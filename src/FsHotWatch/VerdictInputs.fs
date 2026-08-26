/// WHICH FILES DECIDE THE ANSWER — the set `TreeHash` content-addresses.
///
/// THE DERIVATION RULE, stated once so the next addition is derived rather than
/// remembered:
///
///     A FILE BELONGS IN THE TREE HASH IFF CHANGING IT CAN CHANGE WHAT A CHECK
///     CONCLUDES.
///
/// Before AUTOMATION-165 the hashed set was `Discovery`'s walk (`src/`, `tests/`)
/// with `.fshw.json` bolted on — a list inherited from the WATCHER, whose job is a
/// different one, plus one file someone remembered. Everything that decides an
/// answer from OUTSIDE those roots was omitted: the coverage floors, the analyzer
/// rules, `Directory.Build.props`. Lower a floor or weaken a rule and a green
/// earned under the STRONGER check still reported `Applies` — a fail-open in the
/// worst possible direction.
///
/// The rule cannot be applied by the tool alone, because half of its instances are
/// repo-specific: fshw cannot know that `probe-collapse-baseline.json` is a census
/// a finding is measured against. So the set has TWO halves:
///
///   * TOOL-KNOWN (`toolKnownInputs`) — files fshw itself knows are gate-deciding
///     for any repo: its own config, and the root-level toolchain/dependency files
///     that decide what the compiler even does.
///   * DECLARED (`verdictInputs.hashed` in `.fshw.json`) — everything repo-specific,
///     named by the repo, each with a `why` a reviewer can check.
///
/// A DECLARATION IS NEVER SILENTLY SKIPPED. That is the whole point: the defect this
/// module closes was a declaration with no consumer, and replacing it with one that
/// is honoured only when convenient would be the same bug wearing a fix. So:
///
///   * a declaration that cannot be honoured as written (no `path`, no `why`,
///     escaping the repo, declared as both hashed and not-an-input) is a hard
///     `ConfigError` at load — the daemon refuses to start;
///   * a declaration that resolves to NO FILE contributes a SENTINEL entry to the
///     hash rather than contributing zero, so a typo cannot quietly restore the
///     old fail-open, and the hash still moves when the file appears.
///
/// `notInputs` exists so that "not hashed" can be a STATED DECISION with a reason
/// rather than an omission nobody noticed. A repo's changelog is the canonical
/// case: prose about already-gated work should not cost a full re-gate. Declaring
/// it makes the exclusion reviewable; leaving it out makes it invisible.
module FsHotWatch.VerdictInputs

open System
open System.IO
open System.Text.Json
open Ignore

/// Names of the dependency-declaring files that decide what the compiler sees.
/// Canonical here rather than in `DepsFreshness` because two questions read this
/// same list — "is the restore stale?" and "does this file decide the verdict?" —
/// and two spellings of it is how they come to disagree.
let DependencyFileNames =
    [ "Directory.Packages.props"
      "Directory.Build.props"
      "paket.lock"
      "paket.dependencies" ]

/// Root-level files fshw itself knows are gate-deciding, whatever a repo declares.
///
/// ROOT LEVEL ONLY, and that is not an oversight: MSBuild's ancestor copies inside
/// `src/` and `tests/` are already in the discovery walk, so hashing them again
/// here would only duplicate entries. The repo root is the one directory the walk
/// never reaches.
///
/// Compared case-INSENSITIVELY and resolved against the directory listing rather
/// than by probing each spelling, so `NuGet.Config` and `nuget.config` contribute
/// one entry under the name actually on disk instead of two on a case-insensitive
/// filesystem and one on Linux.
let ToolKnownRootFileNames =
    Set.ofList (
        DependencyFileNames
        @ [ "Directory.Build.targets"; "global.json"; "nuget.config" ]
        |> List.map (fun n -> n.ToLowerInvariant())
    )

/// Prefix of a tree-hash entry that stands for a DECLARATION rather than a file.
/// A repo-relative path produced by the walk never begins with this, so a sentinel
/// and a real file cannot collide — the same trick the hole entries use with their
/// trailing `/`.
[<Literal>]
let SentinelPrefix = "!verdict-input:"

/// Hashed in place of a declared input's content when the declaration matched no
/// file at all. Distinct from `ContentHash.UnhashableContent`: "I could not read
/// this file" and "there is no such file to read" are different facts, and a
/// reader of a manifest should not have to guess which one happened.
[<Literal>]
let AbsentDeclaration = "declared-but-absent"

/// One file (or glob) a repo declares can change what a check concludes.
type Declared =
    {
        /// Repo-relative path or gitignore-style glob.
        Path: string
        /// Why changing it can change an answer. REQUIRED — a declaration nobody can
        /// review is a declaration nobody will notice going wrong.
        Why: string
    }

/// One file a repo deliberately declares is NOT an input, with the reason. The
/// point of the type is that the exclusion is stated and reviewable, so "not
/// hashed" stops being indistinguishable from "nobody thought about it".
type NotInput =
    {
        Path: string
        /// Why changing it CANNOT change an answer. REQUIRED, same argument.
        Reason: string
    }

/// `.fshw.json`'s `verdictInputs` block, as parsed.
[<NoComparison>]
type Declaration =
    {
        Hashed: Declared list
        NotInputs: NotInput list
        /// Reasons this declaration cannot be honoured AS WRITTEN. Non-empty makes
        /// the config load fail: a declaration that is half-understood is the
        /// fail-open this module exists to close.
        Errors: string list
    }

/// No `verdictInputs` block at all — the correct reading of a repo that has not
/// declared anything, and NOT the same thing as a block that failed to parse.
let empty =
    { Hashed = []
      NotInputs = []
      Errors = [] }

/// What a `Declaration` resolved to against the tree on disk.
[<NoComparison>]
type Resolution =
    {
        /// Absolute paths of declared inputs that exist. Sorted, deduplicated.
        Files: string list
        /// Declared paths that matched NOTHING. Each becomes a sentinel entry in the
        /// hash — never a silent zero, which is exactly how a typo would restore the
        /// defect this closes.
        Absent: string list
    }

/// Run a filesystem read, answering `fallback` for the two ways of not being allowed
/// to look. ONE such arm in this module rather than one per call site: they mean the
/// same thing here and get the same answer, and only `UnauthorizedAccessException` is
/// forceable from a test (a mode-000 path). Repeated per site, the other half becomes
/// a permanent coverage hole standing in for a distinction this module does not make.
///
/// Answering `fallback` is not failing open. Every caller's absence is caught
/// elsewhere and fails CLOSED: an unreadable `.fshw.json` is itself a hashed input, so
/// `ContentHash` marks it unhashable and no prior verdict matches; a root that cannot
/// be listed is reported as a hole by the discovery walk. Throwing here would only make
/// hashing a tree a new way to crash.
let private tryRead (fallback: 'a) (read: unit -> 'a) : 'a =
    try
        read ()
    with ex when (ex :? IOException) || (ex :? UnauthorizedAccessException) ->
        fallback

let private normalizeSlashes (p: string) = p.Replace('\\', '/')

let private isGlob (p: string) = p.Contains '*' || p.Contains '?'

/// True when `path` (repo-relative, already slash-normalized) points outside the
/// repo, or is rooted. Such a declaration cannot be content-addressed as part of
/// THIS tree, so it is refused rather than silently resolved somewhere else.
let private escapesRepo (path: string) =
    Path.IsPathRooted path
    || path.Split('/') |> Array.exists (fun seg -> seg = "..")

// ---------------------------------------------------------------------------
// Parsing
// ---------------------------------------------------------------------------

let private tryStringProp (el: JsonElement) (name: string) : string option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String ->
        // `IsNullOrWhiteSpace` covers null too. A separate `| null ->` arm would be a
        // branch nothing can reach — `GetString()` returns null only for a JSON `null`,
        // which the `ValueKind = String` guard has already excluded — and an unreachable
        // arm is a permanent coverage hole standing in for a distinction not being made.
        match v.GetString() with
        | s when String.IsNullOrWhiteSpace s -> None
        | s -> Some s
    | _ -> None

/// Parse one `{ path, <reasonKey> }` entry. Both fields are required, and the
/// error names WHICH entry so a 29-entry declaration is fixable without bisecting.
let private parseEntry (reasonKey: string) (index: int) (el: JsonElement) : Result<string * string, string> =
    if el.ValueKind <> JsonValueKind.Object then
        Error $"entry %d{index} is not an object — each entry is {{ \"path\": …, \"%s{reasonKey}\": … }}"
    else
        match tryStringProp el "path", tryStringProp el reasonKey with
        | None, _ -> Error $"entry %d{index} has no non-empty 'path'"
        | Some path, None ->
            Error
                $"'%s{path}' has no non-empty '%s{reasonKey}' — a declaration without a stated reason is one no reviewer can check"
        | Some path, Some reason ->
            let normalized = normalizeSlashes (path.Trim())

            if escapesRepo normalized then
                Error $"'%s{path}' is not inside the repo — a verdict input must be part of the tree being addressed"
            else
                Ok(normalized, reason)

let private parseArray
    (root: JsonElement)
    (arrayKey: string)
    (reasonKey: string)
    : Result<(string * string) list, string> list =
    match root.TryGetProperty arrayKey with
    | false, _ -> []
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray()
        |> Seq.mapi (fun i el -> parseEntry reasonKey i el |> Result.mapError (fun e -> $"%s{arrayKey}: %s{e}"))
        |> Seq.toList
        |> List.map (Result.map List.singleton)
    | true, _ -> [ Error $"'%s{arrayKey}' must be an array" ]

/// Parse the `verdictInputs` block out of `.fshw.json`'s text.
///
/// PURE — takes the JSON, not a path — so every rejection below is testable
/// without a filesystem. Text that is not valid JSON at all is NOT this function's
/// error to report (the config loader already refuses it); it yields `empty`,
/// because inventing a second complaint about the same broken file only makes the
/// real one harder to find.
let parse (json: string) : Declaration =
    let parsed =
        try
            Some(JsonDocument.Parse json)
        with :? JsonException ->
            None

    match parsed with
    | None -> empty
    | Some doc ->
        use doc = doc
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            empty
        else
            match root.TryGetProperty "verdictInputs" with
            | false, _ -> empty
            | true, v when v.ValueKind <> JsonValueKind.Object ->
                { empty with
                    Errors = [ "'verdictInputs' must be an object with 'hashed' and/or 'notInputs' arrays" ] }
            | true, v ->
                let hashedResults = parseArray v "hashed" "why"
                let notInputResults = parseArray v "notInputs" "reason"

                let collect results =
                    results
                    |> List.choose (function
                        | Ok xs -> Some xs
                        | Error _ -> None)
                    |> List.concat

                let errorsOf results =
                    results
                    |> List.choose (function
                        | Error e -> Some e
                        | Ok _ -> None)

                let hashed = collect hashedResults
                let notInputs = collect notInputResults

                let duplicatesIn (label: string) (entries: (string * string) list) =
                    entries
                    |> List.countBy fst
                    |> List.filter (fun (_, n) -> n > 1)
                    |> List.map (fun (p, n) ->
                        $"%s{label}: '%s{p}' is declared %d{n} times — one path, one stated reason")

                // A path declared as BOTH an input and a not-an-input is not a
                // conflict to resolve by precedence; it is two statements that
                // cannot both be true, and picking one silently is how a stated
                // exclusion comes to override a stated inclusion unnoticed.
                let contradictions =
                    let excluded = notInputs |> List.map fst |> Set.ofList

                    hashed
                    |> List.map fst
                    |> List.filter excluded.Contains
                    |> List.map (fun p ->
                        $"'%s{p}' is declared in BOTH 'hashed' and 'notInputs' — it cannot be an input and not an input")

                { Hashed = hashed |> List.map (fun (p, w) -> { Path = p; Why = w })
                  NotInputs = notInputs |> List.map (fun (p, r) -> { Path = p; Reason = r })
                  Errors =
                    errorsOf hashedResults
                    @ errorsOf notInputResults
                    @ duplicatesIn "hashed" hashed
                    @ duplicatesIn "notInputs" notInputs
                    @ contradictions }

/// Read the `verdictInputs` block from `repoRoot`'s `.fshw.json`. A repo with no
/// config declares nothing — that is a valid state, not an error.
let read (repoRoot: string) : Declaration =
    let path = FsHwPaths.configFile repoRoot

    if not (File.Exists path) then
        empty
    else
        tryRead empty (fun () -> parse (File.ReadAllText path))

// ---------------------------------------------------------------------------
// Resolution against the tree
// ---------------------------------------------------------------------------

/// The longest leading directory prefix of a glob that contains no wildcard —
/// the shallowest directory a walk has to start from to find every match.
/// `analyzers/**/*.fs` → `analyzers`; `*.json` → `""` (the repo root).
/// Only ever called with a pattern that HAS a wildcard, so some segment is a glob and
/// the literal prefix is always shorter than the whole — every segment it keeps is a
/// directory, never the filename.
let internal globWalkPrefix (pattern: string) : string =
    pattern.Split('/')
    |> Array.takeWhile (fun s -> not (isGlob s))
    |> String.concat "/"

/// Resolve a declaration against the tree at `repoRoot`.
///
/// DELIBERATELY NOT SUBJECT TO `exclude`, and a declared FILE is not subject to the
/// `bin/`-`obj/` filter either. Those are heuristics about what the walk should
/// bother looking at; a declaration is an explicit statement that this file decides
/// an answer, and the specific statement wins. It is what makes the analyzer DLLs a
/// check actually loads declarable at all — they live under `bin/`, and the walk
/// will never offer them.
///
/// A declared DIRECTORY or GLOB does not DESCEND into build output, but may be
/// POINTED AT it. `analyzers/Rules` means the project, not the project plus every
/// artifact of every configuration ever built there — which would churn the tree
/// hash on every rebuild and invalidate the verdict a run had just earned.
/// `analyzers/Rules/bin/Debug/net10.0` still resolves to the assemblies in it,
/// because `SafeWalk` prunes by directory name during recursion and never prunes the
/// root it was handed. One rule: you may point at build output, you may not sweep
/// into it. A glob that would have to descend (`analyzers/**/*.dll`) matches nothing
/// and is reported ABSENT — loudly wrong rather than quietly narrow.
let resolve (repoRoot: string) (declaration: Declaration) : Resolution =
    let root = Path.GetFullPath repoRoot

    let relativeTo (abs: string) =
        normalizeSlashes (Path.GetRelativePath(root, abs))

    let literals, globs =
        declaration.Hashed
        |> List.map (fun d -> d.Path)
        |> List.partition (isGlob >> not)

    // --- literal paths: a file, or a directory taken whole -------------------
    let literalMatches =
        literals
        |> List.map (fun rel ->
            let abs = Path.GetFullPath(Path.Combine(root, rel))

            if File.Exists abs then
                rel, [ abs ]
            elif Directory.Exists abs then
                rel, SafeWalk.bestEffortFilePaths SafeWalk.SourceExcludedDirs "*" abs |> List.ofSeq
            else
                rel, [])

    // --- globs: ONE walk per distinct prefix, ONE combined matcher -----------
    // Matching every pattern against every file would be O(patterns x files) with
    // a regex apiece — seconds of latency on a large repo, on a code path that
    // runs on every `fshw verdict`. So the walk is filtered by a single combined
    // matcher first, and the per-pattern matchers only ever see the handful of
    // files that already matched something.
    let globMatches =
        if List.isEmpty globs then
            []
        else
            let prefixes = globs |> List.map globWalkPrefix |> List.distinct

            let walkRoots =
                if prefixes |> List.contains "" then
                    [ root ]
                else
                    prefixes |> List.map (fun p -> Path.Combine(root, p))

            let combined = (Ignore(), globs) ||> List.fold (fun (ig: Ignore) pat -> ig.Add pat)

            let candidates =
                walkRoots
                |> List.collect (fun r -> SafeWalk.bestEffortFilePaths SafeWalk.SourceExcludedDirs "*" r |> List.ofSeq)
                |> List.distinct
                |> List.filter (fun abs -> combined.IsIgnored(relativeTo abs))

            globs
            |> List.map (fun pat ->
                let one = Ignore().Add pat
                pat, candidates |> List.filter (fun abs -> one.IsIgnored(relativeTo abs)))

    let all = literalMatches @ globMatches

    { Files = all |> List.collect snd |> List.distinct |> List.sort
      // A declaration that resolved to zero files is ABSENT whichever way it got
      // there — a missing file, an empty directory, a glob that matches nothing.
      // One rule, because all three are the same failure: a declaration that
      // contributes nothing while looking like it contributes something.
      Absent = all |> List.filter (snd >> List.isEmpty) |> List.map fst |> List.sort }

/// The files fshw itself knows decide a check's answer for ANY repo: its own
/// config, and the root-level toolchain/dependency files. Absolute paths, sorted.
///
/// `.fshw.json` is here rather than special-cased at the hashing site. That
/// special case — `let all = if File.Exists config then config :: walked else walked`
/// — was the tell AUTOMATION-165 was filed on: one file bolted onto a list that
/// was otherwise "whatever the watcher happens to watch", with no rule saying what
/// else belonged. There is a rule now, and this is where it is applied.
let toolKnownInputs (repoRoot: string) : string list =
    let root = Path.GetFullPath repoRoot

    let rootFiles =
        tryRead [] (fun () ->
            Directory.GetFiles root
            |> Array.filter (fun f -> ToolKnownRootFileNames.Contains((Path.GetFileName f).ToLowerInvariant()))
            |> Array.toList)

    let config = FsHwPaths.configFile root

    let withConfig =
        if File.Exists config then
            config :: rootFiles
        else
            rootFiles

    withConfig |> List.distinct |> List.sort
