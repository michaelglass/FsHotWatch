/// An analysis of one file, filed in the box-wide outcome store as an `analyze` record
/// (AUTOMATION-1585, ADR-059 §37).
///
/// The analyzers' task-cache entries live in a box-wide directory shared by every
/// checkout, but that cache keeps ONE entry per file: a write deletes the siblings it
/// knows of. Daemons at two different trees therefore evicted each other's analyses, and
/// each start at the other tree demanded a live check and re-analysed every file the trees
/// differ in. A record store keeps many keys per file, immutably, under one GC.
///
/// The key names everything the analysis is a function of: the analyzer set, its
/// threshold and configuration, the file, its source, the shape of what its check resolved
/// elsewhere and its own diagnostics (the task-cache key's terms), PLUS the checker build
/// and the project options, so a replayed analysis is exactly the analysis its key names.
/// Paths under the filing checkout's root are named relatively, so a sibling reads its own.
module FsHotWatch.Analyzers.AnalyzeRecords

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open FSharp.Compiler.CodeAnalysis
open FsHotWatch
open FsHotWatch.ErrorLedger

/// The kind of an analysis record.
[<Literal>]
let Kind = "analyze"

/// The format of an analysis record's outputs. Any other is read as a miss.
[<Literal>]
let Schema = "fshw-analyze-v1"

/// What fshw's analyzers plugin contributes to an analysis record's key: how it runs the
/// analyzers over a check (the context it builds, how it promotes and renders findings)
/// and how it files one (the key's terms, the record's format).
///
/// BUMP IT BY HAND for any change that makes a record an older fshw filed wrong for this
/// one. It stands where fshw's own assembly identity would, which moves with every build
/// (AUTOMATION-1616). `AnalyzeRecordsTests`' golden key fails on any change to how the key
/// is computed and says to bump this.
[<Literal>]
let AnalyzeRecordSchema = "fshw-analyze-semantics-v1"

/// Whether analyses are filed as records, and whether a record may answer.
type RecordMode =
    /// Nothing is filed or read: the task cache alone, as before AUTOMATION-1585.
    | Off
    /// Analyses are filed, and the task cache answers a file only once its record is
    /// filed (AUTOMATION-1609). A record never replaces a live analysis: where one would
    /// have answered, the live analysis runs and the two are compared and logged.
    | Shadow
    /// As `Shadow`, and a record answers its key in place of a live analysis, a replayed
    /// check included, so the check is never demanded live.
    | On

module RecordMode =
    /// `analyzerRecords` as configured: `off`, `shadow` or `on`.
    let tryParse (text: string) : RecordMode option =
        match text with
        | "off" -> Some Off
        | "shadow" -> Some Shadow
        | "on" -> Some On
        | _ -> None

    let toString (mode: RecordMode) =
        match mode with
        | Off -> "off"
        | Shadow -> "shadow"
        | On -> "on"

    /// SHADOW: the shadow period's evidence is gathered only where records are filed and
    /// compared, and a shadow never changes an answer. Its cost is one analysis of each
    /// file a workspace's task cache answered before the store held its record.
    let Default = Shadow

/// Where and how analyses are filed.
[<NoComparison; NoEquality>]
type Wiring =
    {
        Store: OutcomeStore.Store
        RecordMode: RecordMode
        /// The .NET toolchain the daemon resolves (`DotnetToolchain.identity`).
        Toolchain: string
        /// Where a shadow DISAGREEMENT is said: the evidence the shadow period exists for.
        Log: string -> unit
        /// Where a shadow agreement is said: one line per live analysis of a filed key.
        Trace: string -> unit
    }

/// Every term an analysis record's key is folded from, each a digest or a name.
type KeyParts =
    {
        /// `AnalyzeRecordSchema`.
        RecordSchema: string
        /// The checker build (`CheckCache.CheckerBuild.keyIdentity`): FCS and FSharp.Core
        /// exactly, never fshw's own build.
        Checker: string
        /// The analyzer SDK fshw hosts the analyzers with, by release.
        AnalyzerSdk: string
        /// The .NET toolchain.
        Toolchain: string
        /// The project options, named relative to the repository (`optionsHashOf`).
        Options: string
        AnalyzerPaths: string
        AnalyzerInputs: string
        FailOnSeverity: string
        AnalyzerConfig: string
        /// The file, repo-relative (`CachePathIdentity`).
        File: string
        /// The digest of the file's source.
        Source: string
        UsedSignatures: string
        FcsSignature: string
    }

/// The key an analysis is filed under.
let keyWith (parts: KeyParts) : ActionKey.ActionKey =
    ActionKey.create
        Kind
        [ "recipe", parts.RecordSchema
          "checker", parts.Checker
          "analyzer-sdk", parts.AnalyzerSdk
          "toolchain", parts.Toolchain
          "options", parts.Options
          "analyzer-paths", parts.AnalyzerPaths
          "analyzer-inputs", parts.AnalyzerInputs
          "fail-on-severity", parts.FailOnSeverity
          "analyzer-config", parts.AnalyzerConfig
          "file", parts.File
          "source", parts.Source
          "used-signatures", parts.UsedSignatures
          "fcs-signature", parts.FcsSignature ]

/// This process's checker build, as a key names it.
let checkerIdentity: string = CheckCache.toolchainIdentity

/// An analyzer SDK build as a key names it: `name@release`, the release being its
/// `informational` version without build metadata, or its assembly `version` when it states
/// none. A release, not a module version id: the SDK is a package fshw links, rebuilt only
/// when its version moves (AUTOMATION-1616).
let sdkIdentityOf (name: string) (version: Version) (informational: string option) : string =
    let release =
        match informational with
        | Some text -> text.Split('+').[0]
        | None -> version.ToString()

    $"%s{name}@%s{release}"

/// The analyzer SDK this process hosts analyzers with (`sdkIdentityOf`).
let analyzerSdkIdentity: string =
    let assembly = typeof<FSharp.Analyzers.SDK.CliContext>.Assembly
    let name = assembly.GetName()

    let informational =
        assembly.GetCustomAttributes(typeof<Reflection.AssemblyInformationalVersionAttribute>, false)
        |> Array.tryHead
        |> Option.map (fun attribute ->
            (attribute :?> Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)

    sdkIdentityOf name.Name name.Version informational

/// One digest of a project's options per options object and root: every file of a
/// project shares them, and hashing them names every source and reference.
let private optionsHashes =
    Runtime.CompilerServices.ConditionalWeakTable<
        FSharpProjectOptions,
        Collections.Concurrent.ConcurrentDictionary<string, string>
     >()

/// The project options as a key names them, relative to `repoRoot`
/// (`CheckCache.getProjectOptionsHashRelativeTo`): defines, language version and warnings
/// change what an analyzer sees without moving the source or the check's signatures.
let optionsHashOf (repoRoot: string) (options: FSharpProjectOptions) : string =
    optionsHashes
        .GetValue(options, fun _ -> Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal))
        .GetOrAdd(repoRoot, fun root -> CheckCache.getProjectOptionsHashRelativeTo (Some root) options)

[<Literal>]
let private RootPlaceholder = "${analyze-root}"

let private relativeTo (root: string) (text: string) =
    let prefix = root.TrimEnd('/', '\\') + "/"
    text.Replace(prefix, RootPlaceholder + "/")

let private absoluteUnder (root: string) (text: string) =
    text.Replace(RootPlaceholder + "/", root.TrimEnd('/', '\\') + "/")

/// The entries as a record files them, paths under `root` named relatively.
let toJson (root: string) (entries: ErrorEntry list) : string =
    let o = JsonObject()
    o["schema"] <- Schema
    let rows = JsonArray()

    for e in entries do
        let row = JsonObject()
        row["message"] <- relativeTo root e.Message
        row["severity"] <- DiagnosticSeverity.toString e.Severity
        row["line"] <- e.Line
        row["column"] <- e.Column

        match e.Code with
        | Some code -> row["code"] <- code
        | None -> ()

        match e.Detail with
        | Some detail -> row["detail"] <- relativeTo root detail
        | None -> ()

        rows.Add row

    o["entries"] <- rows
    o.ToJsonString()

/// The entries a record filed, read under `root`; None for anything that is not exactly an
/// outputs object of this schema.
let tryOfJson (root: string) (text: string) : ErrorEntry list option =
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

            Some
                [ for e in o.GetProperty("entries").EnumerateArray() ->
                      { Message = absoluteUnder root (str e "message")
                        Severity =
                          match DiagnosticSeverity.fromString (str e "severity") with
                          | Some severity -> severity
                          | None -> raise (FormatException "an unknown severity")
                        Line = e.GetProperty("line").GetInt32()
                        Column = e.GetProperty("column").GetInt32()
                        Code = Json.tryString e "code"
                        Detail = Json.tryString e "detail" |> Option.map (absoluteUnder root) } ]
    with
    | :? JsonException
    | :? KeyNotFoundException
    | :? InvalidOperationException
    | :? FormatException -> None

/// The analysis filed under `key`, read under `root`; None for no record or one that does
/// not read (a corrupt record file is quarantined by the store).
let lookup (store: OutcomeStore.Store) (root: string) (key: ActionKey.ActionKey) : ErrorEntry list option =
    store.LookupWithOutputs key |> Option.bind snd |> Option.bind (tryOfJson root)

/// File `entries` under `key`, for `relPath`, paths named relative to `root`. A finding at
/// Error severity files a fail, so a cached red is never read as a pass. Returns whether
/// it is filed.
let file
    (store: OutcomeStore.Store)
    (root: string)
    (relPath: string)
    (key: ActionKey.ActionKey)
    (entries: ErrorEntry list)
    : bool =
    let outcome =
        if entries |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Error) then
            OutcomeStore.Outcome.Fail
        else
            OutcomeStore.Outcome.Pass

    store.RecordWithOutputs(
        OutcomeStore.Record.ofAction key relPath outcome CheckCache.producerIdentity root DateTimeOffset.UtcNow,
        Some(toJson root entries)
    )

let private readable (root: string) (store: OutcomeStore.Store) (key: ActionKey.ActionKey) =
    lookup store root key |> Option.isSome

/// `file`, only when `store` holds no readable analysis under `key` (AUTOMATION-1596).
/// Returns whether it filed.
let fileIfAbsent
    (store: OutcomeStore.Store)
    (root: string)
    (relPath: string)
    (key: ActionKey.ActionKey)
    (entries: ErrorEntry list)
    : bool =
    OutcomeStore.FiledWork.fileIfAbsent (readable root) store key (fun () -> file store root relPath key entries)

/// Whether the task cache may answer the analysis keyed `key`: only once `store` holds its
/// record, or when none would be filed (AUTOMATION-1609). A key-only probe (`HoldsKey`),
/// asked on every warm read: a corrupt record is a miss where it is read.
let mayAnswerLocally (store: OutcomeStore.Store option) (key: ActionKey.ActionKey option) : bool =
    OutcomeStore.FiledWork.mayAnswerLocally (fun store key -> store.HoldsKey key) store key

/// The first field in which the `recorded` analysis and the `live` one differ, named; None
/// when they agree entry for entry.
let disagreement (recorded: ErrorEntry list) (live: ErrorEntry list) : string option =
    if recorded.Length <> live.Length then
        Some $"entry count: record %d{recorded.Length}, live %d{live.Length}"
    else
        let optional =
            function
            | Some(text: string) -> text
            | None -> "(none)"

        List.zip recorded live
        |> List.indexed
        |> List.tryPick (fun (i, (r, l)) ->
            let differs (field: string) (a: string) (b: string) =
                if a = b then
                    None
                else
                    Some $"entry %d{i} %s{field}: record %s{a}, live %s{b}"

            differs "message" r.Message l.Message
            |> Option.orElse (
                differs "severity" (DiagnosticSeverity.toString r.Severity) (DiagnosticSeverity.toString l.Severity)
            )
            |> Option.orElse (differs "line" (string r.Line) (string l.Line))
            |> Option.orElse (differs "column" (string r.Column) (string l.Column))
            |> Option.orElse (differs "code" (optional r.Code) (optional l.Code))
            |> Option.orElse (differs "detail" (optional r.Detail) (optional l.Detail)))
