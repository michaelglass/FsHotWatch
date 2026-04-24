module TestPrune.ImpactAnalysis

open System.Text.Json
open System.Text.RegularExpressions
open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.Ports
open TestPrune.SymbolDiff

/// Parse a DependsOnFile/DependsOnGlob args_json payload (always `["<string>"]`).
/// Returns None for malformed or non-single-string payloads rather than throwing —
/// attribute args that aren't the shape we expect silently don't match, which is
/// strictly safer than crashing the selection pipeline.
let private firstStringFromArgsJson (argsJson: string) : string option =
    try
        use doc = JsonDocument.Parse(argsJson)
        let root = doc.RootElement

        if root.ValueKind = JsonValueKind.Array && root.GetArrayLength() >= 1 then
            let first = root[0]

            if first.ValueKind = JsonValueKind.String then
                Some(first.GetString())
            else
                None
        else
            None
    with _ ->
        None

/// Translate a TestPrune glob into a regex. Supported tokens:
///   `**` — any number of path segments (including zero)
///   `*`  — any sequence of chars except `/`
///   `?`  — any single char except `/`
/// All other chars are literal. Match is anchored at both ends; paths are repo-relative
/// forward-slash strings. This is deliberately a tiny dialect — negations, character
/// classes, and brace expansion are out of scope.
let private globToRegex (pattern: string) : Regex =
    let sb = System.Text.StringBuilder()
    sb.Append('^') |> ignore
    let mutable i = 0

    while i < pattern.Length do
        let c = pattern[i]

        if c = '*' && i + 1 < pattern.Length && pattern[i + 1] = '*' then
            sb.Append(".*") |> ignore
            i <- i + 2
            // eat a trailing '/' so "**/foo" matches "foo" as well as "a/foo"
            if i < pattern.Length && pattern[i] = '/' then
                i <- i + 1
        elif c = '*' then
            sb.Append("[^/]*") |> ignore
            i <- i + 1
        elif c = '?' then
            sb.Append("[^/]") |> ignore
            i <- i + 1
        else
            sb.Append(Regex.Escape(string c)) |> ignore
            i <- i + 1

    sb.Append('$') |> ignore
    Regex(sb.ToString(), RegexOptions.CultureInvariant ||| RegexOptions.Compiled)

/// Normalize a changed-file path to the same shape used in attribute arguments:
/// forward slashes, no leading `./`. Callers already produce repo-relative paths,
/// so this is just a safety filter.
let private normalizePath (path: string) : string =
    let p = path.Replace('\\', '/')

    if p.StartsWith("./", System.StringComparison.Ordinal) then
        p.Substring(2)
    else
        p

/// A compiled file-dependency declaration: either an exact-path string or a
/// precompiled regex. Keeping the regex here avoids recompiling once per
/// (pattern, changed-file) pair inside the selection loop.
type private FileMatcher =
    | ExactPath of string
    | GlobRegex of Regex

/// Build a matcher for a declared pattern. Normalization (forward slashes,
/// strip `./`) is applied once here so it doesn't run per-candidate.
let private compileMatcher (pattern: string) (isGlob: bool) : FileMatcher =
    let p = normalizePath pattern
    if isGlob then GlobRegex(globToRegex p) else ExactPath p

let private matches (matcher: FileMatcher) (changed: string) : bool =
    let changed = normalizePath changed

    match matcher with
    | ExactPath p -> System.String.Equals(p, changed, System.StringComparison.Ordinal)
    | GlobRegex r -> r.IsMatch(changed)

/// Walk the attribute index to produce (symbolFullName, pattern, isGlob) triples for
/// every DependsOnFile/DependsOnGlob annotation. Attribute names are matched loosely
/// so both `TestPrune.DependsOnFileAttribute` and plain `DependsOnFileAttribute`
/// resolve — the stored DisplayName sheds the namespace.
let private fileDependencyDeclarations
    (allAttributes: Map<string, (string * string) list>)
    : (string * string * bool) list =
    [ for KeyValue(symbolFullName, attrs) in allAttributes do
          for (attrName, argsJson) in attrs do
              let isFile = attrName = "DependsOnFileAttribute" || attrName = "DependsOnFile"

              let isGlob = attrName = "DependsOnGlobAttribute" || attrName = "DependsOnGlob"

              if isFile || isGlob then
                  match firstStringFromArgsJson argsJson with
                  | Some pattern -> yield (symbolFullName, pattern, isGlob)
                  | None -> () ]

/// Result of test impact analysis: either a subset of affected tests or run-all with a reason.
type TestSelection =
    | RunSubset of TestMethodInfo list
    | RunAll of reason: SelectionReason

/// Per-file outcome of comparing the current file's symbols to what's in the store.
/// `NotIndexedButHasSymbols` forces RunAll: we have no baseline, so we can't prove
/// what's affected. `NoBaseline` (both sides empty) and `Diffed []` are both benign
/// and produce no events or changes.
type private FileDiff =
    | NotIndexedButHasSymbols of file: string
    | NoBaseline
    | Diffed of changes: SymbolChange list * events: AnalysisEvent list

let private diffFile
    (getStoredSymbols: string -> SymbolInfo list)
    (currentSymbolsByFile: Map<string, SymbolInfo list>)
    (file: string)
    : FileDiff =
    let storedSymbols = getStoredSymbols file

    let currentSymbols =
        currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

    if storedSymbols.IsEmpty then
        if currentSymbols.IsEmpty then
            NoBaseline
        else
            NotIndexedButHasSymbols file
    else
        let changes, events = detectChanges currentSymbols storedSymbols
        Diffed(changes, events)

/// Given changed files, determine which tests to run. The store supplies stored
/// symbols (for diffing), the reverse-walk query, and the attribute index used to
/// resolve `[<DependsOnFile>]` / `[<DependsOnGlob>]` declarations.
let selectTests
    (store: SymbolStore)
    (changedFiles: string list)
    (currentSymbolsByFile: Map<string, SymbolInfo list>)
    : TestSelection * AnalysisEvent list =
    let getStoredSymbols = store.GetSymbolsInFile
    let queryAffectedTests = store.QueryAffectedTests
    let allAttributes = store.GetAllAttributes()

    if changedFiles.IsEmpty then
        RunSubset [], []
    elif DiffParser.hasFsprojChanges changedFiles then
        RunAll(FsprojChanged(changedFiles |> List.find DiffParser.isFsproj)), []
    else
        let diffs =
            changedFiles
            |> List.filter (DiffParser.isFsproj >> not)
            |> List.map (diffFile getStoredSymbols currentSymbolsByFile)

        let firstNewFile =
            diffs
            |> List.tryPick (function
                | NotIndexedButHasSymbols f -> Some f
                | _ -> None)

        let allChanges =
            diffs
            |> List.collect (function
                | Diffed(c, _) -> c
                | _ -> [])

        let symbolEvents =
            diffs
            |> List.collect (function
                | Diffed(_, e) -> e
                | _ -> [])

        match firstNewFile with
        | Some file -> RunAll(NewFileNotIndexed file), symbolEvents
        | None ->
            // File-attribute seeds: every (symbol, changedFile) pair where a declared
            // DependsOnFile/Glob pattern matches. Each such symbol becomes an extra
            // changed-symbol input to the transitive walk, plus a per-test event carrying
            // a FileDependencyChanged reason. Compile matchers once, not per pair.
            let compiledFileDeps =
                fileDependencyDeclarations allAttributes
                |> List.map (fun (symbol, pattern, isGlob) -> symbol, compileMatcher pattern isGlob)

            let fileSeeds =
                [ for (symbol, matcher) in compiledFileDeps do
                      for changed in changedFiles do
                          if matches matcher changed then
                              yield (symbol, changed) ]

            let hashChangedNames = changedSymbolNames allChanges
            let fileSeedNames = fileSeeds |> List.map fst |> List.distinct
            let seedNames = (hashChangedNames @ fileSeedNames) |> List.distinct

            let affectedTests = queryAffectedTests seedNames

            // Pick the single reason reported with every TestSelectedEvent this batch.
            // Hash changes take precedence when present (preserves the pre-file-dep
            // event shape). Otherwise, a single file-dep seed yields a specific
            // FileDependencyChanged; plural seeds collapse to MultipleChanges, matching
            // how the hash path treats singleton vs plural.
            let distinctFileSeeds = fileSeeds |> List.distinct

            let reason =
                match hashChangedNames, distinctFileSeeds with
                | [], [ (symbol, path) ] -> FileDependencyChanged(path, symbol)
                | [], (_ :: _ :: _) -> MultipleChanges(distinctFileSeeds |> List.map fst |> List.distinct)
                | _, _ ->
                    match allChanges with
                    | [ change ] -> SymbolChanged(SymbolDiff.symbolName change, SymbolDiff.changeKind change)
                    | _ -> MultipleChanges(allChanges |> List.map SymbolDiff.symbolName)

            let testEvents =
                affectedTests
                |> List.map (fun testMethod -> TestSelectedEvent(testMethod.SymbolFullName, reason))

            RunSubset affectedTests, symbolEvents @ testEvents
