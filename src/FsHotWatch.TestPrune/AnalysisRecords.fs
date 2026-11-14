/// TestPrune's symbol analysis of a file, filed in the box-wide outcome store
/// (AUTOMATION-1482, ADR-059 §27).
///
/// A replayed check (ADR-059 §17) carries no FCS objects, and the analysis needs the typed
/// tree, so a workspace whose impact index lacks a file's analysis demanded a live check of
/// it: in a fresh workspace of a tree another checkout had already checked, all 704 of
/// FsHotWatch's. The analysis is a function of the check it reads, so it is filed under
/// the identity the index already trusts to skip re-analysis (`IndexedAnalysis.identity`:
/// the source, the compiler signature and the public shape of everything the check
/// resolved outside the file), with the file, its project and the analyser build. A
/// replayed check whose analysis is filed is answered from the record; any other is
/// demanded live, as before.
///
/// The record's paths are named relative to the root they were analysed under, so a
/// sibling checkout reads its own.
module FsHotWatch.TestPrune.AnalysisRecords

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch
open TestPrune.AstAnalyzer

/// The kind of an analysis record.
[<Literal>]
let Kind = "testprune-analysis"

/// The format of an analysis record's outputs. Any other is read as a miss.
[<Literal>]
let Schema = "fshw-testprune-analysis-v1"

/// What fshw's TestPrune plugin contributes to an analysis record's key: which analysis it
/// asks TestPrune.Core for and how it files one (`keyOf`'s terms, the record's format).
///
/// BUMP IT BY HAND for any change that makes a record an older fshw filed wrong for this
/// one. It replaces the plugin assembly's identity (AUTOMATION-1616), whose module version
/// id moved with every build, so every release re-analysed every file.
/// `RecordKeyGoldenTests` pins the key and fails on any change to how it is computed.
[<Literal>]
let AnalysisSchema = "fshw-testprune-analysis-semantics-v1"

/// The analyser identity, from its parts: TestPrune.Core's release (`name@version`) and
/// the plugin's analysis `schema`.
let analyzerIdentityOf (testPruneCore: Reflection.AssemblyName) (schema: string) : string =
    $"%s{testPruneCore.Name}@%O{testPruneCore.Version};fshw-testprune-analysis-schema=%s{schema}"

/// The analyser build: TestPrune.Core, which computes what an analysis contains, and
/// `AnalysisSchema` for the plugin's part.
///
/// TestPrune.Core is named by its release, its assembly version being its package version:
/// a release that changes it publishes a new version. Not by module version id: every fshw
/// release rebuilds it with the release commit in its informational version, so its id moved
/// with every release whether or not the analyser had changed. Between releases a local build
/// carries the last version, so a change to what TestPrune.Core analyses that must reach
/// records before it is released bumps `AnalysisSchema` too.
let analyzerIdentity: string =
    analyzerIdentityOf (typeof<AnalysisResult>.Assembly.GetName()) AnalysisSchema

/// The key an analysis is filed under by the analyser `analyzer` (`analyzerIdentityOf`).
let keyWith (analyzer: string) (identity: string) (relPath: string) (projectName: string) : ActionKey.ActionKey =
    ActionKey.create
        Kind
        [ "analyzer", analyzer
          "analysis", identity
          "file", relPath
          "project", projectName ]

/// The key an analysis is filed under by this process's analyser.
let keyOf (identity: string) (relPath: string) (projectName: string) : ActionKey.ActionKey =
    keyWith analyzerIdentity identity relPath projectName

[<Literal>]
let private RootPlaceholder = "${analysis-root}"

let private relativeTo (root: string) (path: string) =
    let prefix = root.TrimEnd('/', '\\') + "/"

    if path.StartsWith(prefix, StringComparison.Ordinal) then
        RootPlaceholder + "/" + path.Substring prefix.Length
    else
        path

let private absoluteUnder (root: string) (path: string) =
    if path.StartsWith(RootPlaceholder + "/", StringComparison.Ordinal) then
        root.TrimEnd('/', '\\') + "/" + path.Substring(RootPlaceholder.Length + 1)
    else
        path

let private kindToken (kind: SymbolKind) =
    match kind with
    | Function -> "function"
    | Type -> "type"
    | DuCase -> "du-case"
    | Module -> "module"
    | Value -> "value"
    | Property -> "property"
    | ExternRef -> "extern-ref"

let private kindOf (token: string) =
    match token with
    | "function" -> Function
    | "type" -> Type
    | "du-case" -> DuCase
    | "module" -> Module
    | "value" -> Value
    | "property" -> Property
    | "extern-ref" -> ExternRef
    | other -> raise (FormatException $"a symbol kind TestPrune does not have: %s{other}")

let private dependencyToken (kind: DependencyKind) =
    match kind with
    | Calls -> "calls"
    | UsesType -> "uses-type"
    | PatternMatches -> "pattern-matches"
    | References -> "references"
    | SharedState -> "shared-state"
    | SharedLiteral -> "shared-literal"

let private dependencyOf (token: string) =
    match token with
    | "calls" -> Calls
    | "uses-type" -> UsesType
    | "pattern-matches" -> PatternMatches
    | "references" -> References
    | "shared-state" -> SharedState
    | "shared-literal" -> SharedLiteral
    | other -> raise (FormatException $"a dependency kind TestPrune does not have: %s{other}")

let private strings (values: string list) =
    let array = JsonArray()

    for value in values do
        array.Add(JsonValue.Create value)

    array

/// The analysis as a record files it, its paths under `root` named relatively.
let toJson (root: string) (analysis: AnalysisResult) : string =
    let o = JsonObject()
    o["schema"] <- Schema
    let symbols = JsonArray()

    for s in analysis.Symbols do
        let e = JsonObject()
        e["name"] <- s.FullName
        e["kind"] <- kindToken s.Kind
        e["file"] <- relativeTo root s.SourceFile
        e["lineStart"] <- s.LineStart
        e["lineEnd"] <- s.LineEnd
        e["hash"] <- s.ContentHash
        e["extern"] <- s.IsExtern
        symbols.Add e

    o["symbols"] <- symbols
    let dependencies = JsonArray()

    for d in analysis.Dependencies do
        dependencies.Add(strings [ d.FromSymbol; d.ToSymbol; dependencyToken d.Kind; d.Source ])

    o["dependencies"] <- dependencies
    let tests = JsonArray()

    for t in analysis.TestMethods do
        tests.Add(strings [ t.SymbolFullName; t.TestProject; t.TestClass; t.TestMethod ])

    o["tests"] <- tests
    let attributes = JsonArray()

    for a in analysis.Attributes do
        attributes.Add(strings [ a.SymbolFullName; a.AttributeName; a.ArgsJson ])

    o["attributes"] <- attributes
    let parents = JsonArray()

    for p in analysis.ParentLinks do
        parents.Add(strings [ p.Child; p.Parent ])

    o["parents"] <- parents
    let diagnostics = JsonObject()
    diagnostics["droppedEdges"] <- analysis.Diagnostics.DroppedEdges
    diagnostics["filteredSymbols"] <- analysis.Diagnostics.FilteredSymbols
    diagnostics["totalDefinitions"] <- analysis.Diagnostics.TotalDefinitions
    o["diagnostics"] <- diagnostics
    o.ToJsonString()

/// The analysis a record filed, with paths under `root`; None for anything that is not
/// exactly an outputs object of this schema.
let tryOfJson (root: string) (text: string) : AnalysisResult option =
    try
        use doc = JsonDocument.Parse text
        let o = doc.RootElement

        if Json.tryString o "schema" <> Some Schema then
            None
        else
            let str (e: JsonElement) (name: string) =
                match e.GetProperty(name).GetString() with
                | null -> raise (FormatException $"%s{name} is null")
                | value -> value

            let int (e: JsonElement) (name: string) = e.GetProperty(name).GetInt32()

            let tuple (e: JsonElement) (arity: int) =
                let values =
                    [ for v in e.EnumerateArray() ->
                          match v.GetString() with
                          | null -> raise (FormatException "a null field")
                          | value -> value ]

                if values.Length <> arity then
                    raise (FormatException $"a row of %d{values.Length} fields, not %d{arity}")

                values

            let rows (name: string) (arity: int) =
                [ for e in o.GetProperty(name).EnumerateArray() -> tuple e arity ]

            let diagnostics = o.GetProperty "diagnostics"

            Some
                { Symbols =
                    [ for e in o.GetProperty("symbols").EnumerateArray() ->
                          { FullName = str e "name"
                            Kind = kindOf (str e "kind")
                            Location =
                              SymbolLocation.ofRow
                                  (absoluteUnder root (str e "file"))
                                  (int e "lineStart")
                                  (int e "lineEnd")
                                  (str e "hash")
                                  (e.GetProperty("extern").GetBoolean()) } ]
                  Dependencies =
                    [ for row in rows "dependencies" 4 ->
                          { FromSymbol = row[0]
                            ToSymbol = row[1]
                            Kind = dependencyOf row[2]
                            Source = row[3] } ]
                  TestMethods =
                    [ for row in rows "tests" 4 ->
                          { SymbolFullName = row[0]
                            TestProject = row[1]
                            TestClass = row[2]
                            TestMethod = row[3] } ]
                  Attributes =
                    [ for row in rows "attributes" 3 ->
                          { SymbolFullName = row[0]
                            AttributeName = row[1]
                            ArgsJson = row[2] } ]
                  ParentLinks = [ for row in rows "parents" 2 -> { Child = row[0]; Parent = row[1] } ]
                  Diagnostics =
                    { DroppedEdges = int diagnostics "droppedEdges"
                      FilteredSymbols = int diagnostics "filteredSymbols"
                      TotalDefinitions = int diagnostics "totalDefinitions" } }
    with
    | :? JsonException
    | :? KeyNotFoundException
    | :? InvalidOperationException
    | :? FormatException -> None

/// The analysis filed under `key`, read under `root`; None for no record or one that does
/// not read.
let lookup (store: OutcomeRecords.IOutcomeStore) (root: string) (key: ActionKey.ActionKey) : AnalysisResult option =
    OutcomeStore.Through.lookupWithOutputs store key.Value
    |> Option.bind snd
    |> Option.bind (tryOfJson root)

/// File `analysis` under `key`, for `relPath`, its paths named relative to `root`. Returns
/// whether it is filed.
let file
    (store: OutcomeRecords.IOutcomeStore)
    (root: string)
    (relPath: string)
    (key: ActionKey.ActionKey)
    (analysis: AnalysisResult)
    : bool =
    OutcomeStore.Through.fileComputed
        store
        (OutcomeStore.justRan
            (OutcomeStore.Record.ofAction
                key
                relPath
                OutcomeStore.Outcome.Pass
                analyzerIdentity
                root
                DateTimeOffset.UtcNow)
            (Some(toJson root analysis)))

/// The project identity an analysis is made under: the project file's name, with a
/// script's synthesized `.fsx` dropped, so `"Lib"` names `Lib.fsproj` and `Lib.fsx` alike.
let projectNameOf (projectFileName: string) : string =
    let raw = IO.Path.GetFileNameWithoutExtension projectFileName

    if raw.EndsWith(".fsx", StringComparison.Ordinal) then
        raw.Substring(0, raw.Length - 4)
    else
        raw

/// The key `result`'s analysis is filed under in a repository at `repoRoot`; None when its
/// identity cannot be described (`IndexedAnalysis.identityOf`), so it is never filed or read.
let keyFor (repoRoot: string) (result: FsHotWatch.Events.FileCheckResult) : ActionKey.ActionKey option =
    IndexedAnalysis.identityOf repoRoot result
    |> Option.map (fun identity ->
        let relPath =
            IO.Path.GetRelativePath(repoRoot, FsHotWatch.Events.AbsFilePath.value result.File).Replace('\\', '/')

        keyOf identity relPath (projectNameOf result.ProjectOptions.ProjectFileName))

/// Whether `store` holds a readable analysis for `result`.
let holds (store: OutcomeRecords.IOutcomeStore) (repoRoot: string) (result: FsHotWatch.Events.FileCheckResult) : bool =
    keyFor repoRoot result
    |> Option.bind (fun key -> lookup store repoRoot key)
    |> Option.isSome

/// Whether `store` holds an analysis under `key` that reads under `root`.
let private readable (root: string) (store: OutcomeRecords.IOutcomeStore) (key: ActionKey.ActionKey) : bool =
    lookup store root key |> Option.isSome

/// Whether TestPrune's task-cache entry for `result` may stand in for analysing it: only
/// once its analysis is filed in `store`, or when none would be (`OutcomeStore.FiledWork`,
/// AUTOMATION-1609). A refusal re-analyses the check, which files it. A key-only probe
/// (`HoldsKey`): asked on every warm read, it reads no payload; a corrupt record is a miss
/// where it is read.
let mayAnswerLocally
    (store: OutcomeRecords.IOutcomeStore option)
    (repoRoot: string)
    (result: FsHotWatch.Events.FileCheckResult)
    =
    OutcomeStore.FiledWork.mayAnswerLocally
        (fun (store: OutcomeRecords.IOutcomeStore) key -> store.HoldsKey key.Value)
        store
        (keyFor repoRoot result)

/// `file`, only when `store` holds no readable analysis under `key` (AUTOMATION-1596,
/// AUTOMATION-1609). Returns whether it filed.
let fileIfAbsent
    (store: OutcomeRecords.IOutcomeStore)
    (root: string)
    (relPath: string)
    (key: ActionKey.ActionKey)
    (analysis: AnalysisResult)
    : bool =
    OutcomeStore.FiledWork.fileIfAbsent (readable root) store key (fun () -> file store root relPath key analysis)
