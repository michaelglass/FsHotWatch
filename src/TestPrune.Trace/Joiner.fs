/// Maps a weave manifest's probe ids to TestPrune symbols.
///
/// A probe id stands for a CLR method, union case or type. TestPrune's index names F#
/// symbols. The joiner bridges the two, by NAME before LINE: a method with a source
/// document matches a same-named symbol of its own file first, and only then the nearest
/// declaration above its first line. Line matching alone mis-attributes a method whenever
/// the index and the binary disagree about lines (an edit since the last index, or a
/// sequence point that starts past its binding's first line).
///
/// Every row gets exactly one target. A row that names no symbol but a repository file
/// becomes a file-level entry, which is sound: a change to that file invalidates the trace.
module TestPrune.Trace.Joiner

open System
open System.Collections.Concurrent
open System.IO
open System.Text.RegularExpressions
open TestPrune.AstAnalyzer
open TestPrune.Trace.Model

/// The parts of TestPrune's symbol index the joiner reads.
type SymbolIndex =
    {
        /// The symbols declared in a file, by repository-relative path.
        InFile: string -> SymbolInfo list
        /// Whether a symbol full name is indexed.
        Exists: string -> bool
        /// Whether a repository-relative file was indexed.
        IsIndexedFile: string -> bool
        /// The root that index paths are relative to.
        RepoRoot: string
    }

/// Where one probe id's hits are credited.
type JoinTarget =
    /// An indexed symbol, by full name.
    | ToSymbol of fullName: string
    /// A file-level entry: the code is in this repository file but no symbol names it.
    | ToFile of repoRelativePath: string
    /// Compiler-generated code whose own probes carry no meaning; the probes inside it
    /// (or the site probes around it) attribute elsewhere.
    | Dropped
    /// Neither a symbol nor a repository file.
    | Unmapped of reason: string

/// A `SymbolIndex` over a TestPrune symbol store. Per-file lookups are cached, so joining
/// a large manifest reads each file's symbols once.
let ofStore (store: TestPrune.Ports.SymbolStore) (repoRoot: string) : SymbolIndex =
    let names = store.GetAllSymbolNames()
    let inFile = ConcurrentDictionary<string, SymbolInfo list>()
    let indexed = ConcurrentDictionary<string, bool>()

    { InFile = fun f -> inFile.GetOrAdd(f, store.GetSymbolsInFile)
      Exists = names.Contains
      IsIndexedFile = fun f -> indexed.GetOrAdd(f, fun f -> (store.GetFileKey f).IsSome)
      RepoRoot = repoRoot }

let private arity = Regex(@"`\d+", RegexOptions.Compiled)

let private stripModuleSuffix (segment: string) =
    if segment.Length > 6 && segment.EndsWith("Module", StringComparison.Ordinal) then
        segment.Substring(0, segment.Length - 6)
    else
        segment

/// The index names a CLR type could have, most specific first: the dotted name, then
/// without generic arity, then without F#'s implicit `Module` suffix on any segment.
/// Callers check each with `Exists`, so a wrong candidate never matches.
let typeCandidates (clrName: string) : string list =
    let dotted = clrName.Replace('+', '.')
    let noArity = arity.Replace(dotted, "")
    let noSuffix = noArity.Split '.' |> Seq.map stripModuleSuffix |> String.concat "."
    List.distinct [ dotted; noArity; noSuffix ]

/// Split a nested CLR name at its last `+`: (declaring type, last segment).
let private splitNested (clr: string) =
    match clr.LastIndexOf '+' with
    | -1 -> "", clr
    | i -> clr.Substring(0, i), clr.Substring(i + 1)

/// A closure class `N.M+area@6` (or `area@6-1`) → owner `N.M` and binding `area`. The
/// compiler names a closure after the binding it was written in; a closure with no such
/// name (`@_instance`) yields the empty name, which matches nothing.
let private splitClosure (clr: string) : string * string option =
    let owner, last = splitNested clr

    match last.IndexOf '@' with
    | at when at >= 0 && owner <> "" -> owner, Some(last.Substring(0, at))
    | _ -> clr, None

/// The source-level name a CLR member stands for: accessor prefixes stripped, and an
/// explicit interface implementation (`N.IGreeter.Greet`) reduced to its last segment.
/// Entry points a closure or state machine is called through name nothing.
let private memberName (m: string) =
    match m with
    | "Invoke"
    | "MoveNext"
    | "Specialize"
    | ".ctor"
    | ".cctor" -> ""
    | _ ->
        let m =
            match m.LastIndexOf '.' with
            | i when i > 0 -> m.Substring(i + 1)
            | _ -> m

        if
            m.StartsWith("get_", StringComparison.Ordinal)
            || m.StartsWith("set_", StringComparison.Ordinal)
        then
            m.Substring 4
        else
            m

let private lastSegment (clr: string) =
    let dotted = clr.Replace('+', '.')
    dotted.Substring(dotted.LastIndexOf '.' + 1)

/// Every enclosing name of a dotted name, innermost first: `N.M.T` → `N.M`, `N`.
let rec private enclosingNames (name: string) =
    match name.LastIndexOf '.' with
    | -1 -> []
    | i ->
        let outer = name.Substring(0, i)
        outer :: enclosingNames outer

/// Members F# generates on every union type. Their own entry probes mean nothing: the
/// site probes inside them record the compared values' cases.
let private unionPlumbing =
    set
        [ "get_Tag"
          "Equals"
          "CompareTo"
          "GetHashCode"
          "ToString"
          ".ctor"
          ".cctor" ]

let private firstExisting (ix: SymbolIndex) (names: string list) = names |> List.tryFind ix.Exists

let private caseOf (ix: SymbolIndex) (union: string) (case: string) =
    typeCandidates union |> List.map (fun u -> u + "." + case) |> firstExisting ix

/// The case a union-generated member stands for: `NewX`, `get_IsX`, or the static
/// getter `get_X` of a fieldless case.
let private caseOfMember (ix: SymbolIndex) (union: string) (m: string) =
    [ "New"; "get_Is"; "get_" ]
    |> List.tryPick (fun prefix ->
        if m.StartsWith(prefix, StringComparison.Ordinal) && m.Length > prefix.Length then
            caseOf ix union (m.Substring prefix.Length)
        else
            None)

/// Map a row that has a source document: by name in its own file, then by line.
let private byDocument (ix: SymbolIndex) (row: ManifestRow) (doc: string) =
    let rel = Path.GetRelativePath(ix.RepoRoot, doc).Replace('\\', '/')

    if rel.StartsWith("../", StringComparison.Ordinal) then
        Unmapped "outside-repo"
    elif not (ix.IsIndexedFile rel) then
        ToFile rel
    else
        let syms = ix.InFile rel |> List.filter (fun s -> s.Kind <> ExternRef)
        let owner, closureName = splitClosure row.TypeName

        let name =
            match closureName, row.Member with
            | Some n, _ -> n
            | None, ".ctor" -> lastSegment row.TypeName
            | None, m -> memberName m

        let preceding (cands: SymbolInfo list) =
            cands
            |> List.filter (fun s -> row.FirstLine > 0 && s.LineStart <= row.FirstLine)

        let nearest (cands: SymbolInfo list) =
            match preceding cands with
            | [] ->
                cands
                |> List.sortBy (fun s -> abs (s.LineStart - row.FirstLine))
                |> List.tryHead
            | before -> before |> List.maxBy (fun s -> s.LineStart) |> Some

        let named =
            if name = "" then
                []
            else
                syms |> List.filter (fun s -> canonicalShortName s.FullName = name)

        // Among same-named symbols (`Dog.Speak` and `Cat.Speak` in one file), the one
        // declared on the method's own type is exact; the rest fall back to nearness.
        let qualified =
            typeCandidates owner
            |> List.map (fun o -> o + "." + name)
            |> List.tryPick (fun full -> named |> List.tryFind (fun s -> s.FullName = full))

        match qualified |> Option.orElse (nearest named) with
        | Some s -> ToSymbol s.FullName
        | None ->
            match preceding syms with
            | [] -> ToFile rel
            | before -> ToSymbol (before |> List.maxBy (fun s -> s.LineStart)).FullName

/// Map a row without a source document (generated members have no sequence points):
/// the owner's member, then the owner type, then each enclosing module in turn.
let private byName (ix: SymbolIndex) (row: ManifestRow) =
    let owner, closureName = splitClosure row.TypeName
    let name = defaultArg closureName (memberName row.Member)
    let owners = typeCandidates owner

    let enclosing = owners |> List.collect enclosingNames

    [ if name <> "" then
          yield! owners |> List.map (fun o -> o + "." + name)
      yield! owners
      yield! enclosing ]
    |> firstExisting ix
    |> Option.map ToSymbol
    |> Option.defaultValue (Unmapped "no-document")

let private byLocation (ix: SymbolIndex) (row: ManifestRow) =
    match row.Document with
    | Some doc -> byDocument ix row doc
    | None -> byName ix row

/// Map one manifest row. `unionTypes` holds the CLR names of the union types in the
/// manifest (`joinManifest` derives them).
let joinRow (ix: SymbolIndex) (unionTypes: Set<string>) (row: ManifestRow) : JoinTarget =
    match row.Kind with
    | UnionCase ->
        caseOf ix row.TypeName row.Member
        |> Option.map ToSymbol
        |> Option.defaultValue (Unmapped "case-not-indexed")
    | TypeUse ->
        typeCandidates row.TypeName
        |> firstExisting ix
        |> Option.map ToSymbol
        |> Option.defaultValue (Unmapped "type-not-indexed")
    | UserMethod
    | GeneratedMethod
    | StaticCtor ->
        let t = row.TypeName
        let parent, last = splitNested t
        let generated = row.Kind <> UserMethod

        // A case class: `U+X`, `U+_X` or `U+X@DebugTypeProxy`; a closure nested in a
        // union (`U+f@12`) is not one.
        let caseClass () =
            if parent <> "" && unionTypes.Contains parent then
                let case = last.Replace("@DebugTypeProxy", "")

                if case.Contains '@' then
                    None
                else
                    caseOf ix parent (case.TrimStart '_')
            else
                None

        if unionTypes.Contains t && generated && unionPlumbing.Contains row.Member then
            Dropped
        else
            let unionMember =
                if unionTypes.Contains t && generated then
                    caseOfMember ix t row.Member
                else
                    None

            match unionMember |> Option.orElse (caseClass ()) with
            | Some case -> ToSymbol case
            | None when last.Contains '@' && (row.Member = ".ctor" || row.Member = ".cctor") -> Dropped
            | None -> byLocation ix row

/// The union types of a manifest: those with union-case site rows, and those with a
/// generated `NewX`/`get_IsX` member whose case `X` the index knows. Index-checked, so a
/// class with a `Tag` property or a `NewX` method of its own is never mistaken for one.
let private unionTypesOf (ix: SymbolIndex) (m: Manifest) =
    m.Rows
    |> Array.choose (fun r ->
        match r.Kind with
        | UnionCase -> Some r.TypeName
        | GeneratedMethod when
            (r.Member.StartsWith("New", StringComparison.Ordinal)
             || r.Member.StartsWith("get_Is", StringComparison.Ordinal))
            && (caseOfMember ix r.TypeName r.Member).IsSome
            ->
            Some r.TypeName
        | _ -> None)
    |> Set.ofArray

/// Map every row of a manifest; the result is indexed by probe id.
let joinManifest (ix: SymbolIndex) (m: Manifest) : JoinTarget[] =
    let unions = unionTypesOf ix m
    let targets = Array.create m.IdCount (Unmapped "no-row")

    for r in m.Rows do
        targets.[r.Id] <- joinRow ix unions r

    targets
