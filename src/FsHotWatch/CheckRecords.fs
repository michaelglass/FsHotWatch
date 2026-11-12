/// The persisted check-result store (ADR-059 §14, AUTOMATION-1361): one `check` action per
/// file, filed in the one outcome store, so a restarted daemon is told what a file's check
/// reported without asking FCS, for as long as every input the check read is unchanged.
///
/// A RECORD is what a consumer needs without FCS objects (`CheckOutputs`): the
/// diagnostics, `fcsCheckSignature`, `usedSignaturesHash` and the file's own API
/// signature. FCS checker state itself cannot be persisted (the 1361 spike's NO-GO).
///
/// THE KEY is canonical — a function of the bytes on disk, never of what this process
/// happened to check first. It is an ActionKey of kind `check` over:
///   * `toolchain` — the checker build (`CheckCache.toolchainIdentity`);
///   * `options`   — the project's options hash, in-repo paths relative;
///   * `file`      — the file, repository-relative;
///   * `content`   — the file's bytes;
///   * `upstream`  — the CANONICAL API chain: the project's options, its assembly and
///                   non-F# references (`CheckCache.addAssemblyParts`), each referenced F#
///                   project's whole chain, then each earlier file in compile order named
///                   by what its check declared under its own canonical key: its API
///                   signature, or its content when that check declared none (an error, an
///                   `inline`). A generated compile item, never checked, is named by content;
///   * `project-outputs` — every referenced project's output, which FCS types against
///                   instead of the sources whenever it is newer (ADR-037), named by
///                   `OutputIdentity`: what a check reads of it, not the checkout path
///                   the build embedded, so a sibling checkout of the same tree keys its
///                   records alike (AUTOMATION-1478). An in-repo `-r:` assembly in
///                   `upstream` is named the same way.
///
/// An earlier file's name comes from its record, or from a pending check that would be
/// filed as that record (`Filer`). A file the walk can name neither way is UNKNOWN, and so
/// is every file after it, in its project and in every project referencing it: an unknown
/// file's key is provisional, never read and never filed. The key is therefore a function of
/// the bytes and what checks of them declared, never of which records happened to exist
/// when it was filed (AUTOMATION-1606). Naming an unrecorded file by its content instead
/// made a record for file k+1 filed before k's record unreachable the moment k's was filed:
/// a reader then names k by its signature.
///
/// Soundness is by induction over the compile order, as for the in-memory API key: an equal
/// `upstream` means each earlier file was checked under an equal key and declared an equal
/// signature (or had equal bytes, where it declared none), so an API change upstream moves
/// every key after it, while a body edit that keeps the signature moves only its own file's
/// key: the early cutoff (ADR-059 §14), whose pending check names it before it is filed.
///
/// A miss is never wrong, only slow: the files after an unknown one miss until it is
/// checked, and a check behind it waits, pending, to be filed once it is known.
module FsHotWatch.CheckRecords

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events
open FsHotWatch.CheckCache

/// The kind of a file's check record.
[<Literal>]
let CheckKind = "check"

/// Reserved for AUTOMATION-1372: a project's evaluated options, filed in the same store
/// beside its files' `check` records.
[<Literal>]
let OptionsKind = "options"

/// The format of a check record's outputs. Any other is read as a miss.
[<Literal>]
let OutputsSchema = "fshw-check-outputs-v1"

module CheckOutputs =
    /// The outputs of a full check; None for one that did not complete.
    let ofResult (repoRoot: string option) (result: FileCheckResult) : CheckOutputs option =
        match result.CheckResults with
        | ParseOnly _ -> None
        | Replayed outputs -> Some outputs
        | FullCheck(_, results) ->
            let toReal = FileCheckResult.textToReal result

            Some
                { Diagnostics =
                    [ for d in results.Diagnostics ->
                          { FileName = toReal d.FileName
                            StartLine = d.StartLine
                            StartColumn = d.StartColumn
                            EndLine = d.EndLine
                            EndColumn = d.EndColumn
                            ErrorNumber = d.ErrorNumber
                            Severity = d.Severity
                            Subcategory = d.Subcategory
                            Message = toReal d.Message } ]
                  FcsSignature = fcsCheckSignature result.CheckResults
                  UsedSignatures = CacheInputs.usedSignaturesHash repoRoot result.ProjectOptions result.CheckResults
                  ApiSignature = ApiSignature.ofCheck result.Source result.CheckResults }

    /// Whether any diagnostic is an error: the record's outcome.
    let hasErrors (outputs: CheckOutputs) =
        outputs.Diagnostics
        |> List.exists (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

    let private rootOf (repoRoot: string option) =
        repoRoot
        |> Option.map (fun root -> Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))

    /// The outputs as a record files them: paths inside the repository named relatively,
    /// so a sibling checkout reads its own.
    let toJson (repoRoot: string option) (outputs: CheckOutputs) : string =
        let relative = relativizeOption repoRoot
        let o = JsonObject()
        o["schema"] <- OutputsSchema
        let diagnostics = JsonArray()

        for d in outputs.Diagnostics do
            let e = JsonObject()
            e["file"] <- relative d.FileName
            e["startLine"] <- d.StartLine
            e["startColumn"] <- d.StartColumn
            e["endLine"] <- d.EndLine
            e["endColumn"] <- d.EndColumn
            e["number"] <- d.ErrorNumber
            e["severity"] <- StoredDiagnostic.severityToken d.Severity
            e["subcategory"] <- d.Subcategory
            e["message"] <- relative d.Message
            diagnostics.Add e

        o["diagnostics"] <- diagnostics
        o["fcsSignature"] <- outputs.FcsSignature
        outputs.UsedSignatures |> Option.iter (fun used -> o["usedSignatures"] <- used)
        outputs.ApiSignature |> Option.iter (fun api -> o["apiSignature"] <- api)
        o.ToJsonString()

    /// The outputs a record filed, with this checkout's paths; None for anything that is
    /// not exactly an outputs object of this schema.
    let tryOfJson (repoRoot: string option) (text: string) : CheckOutputs option =
        let real (value: string) =
            match rootOf repoRoot with
            | Some root -> value.Replace(RepoRootPlaceholder, root)
            | None -> value

        try
            use doc = JsonDocument.Parse text
            let o = doc.RootElement

            if Json.tryString o "schema" <> Some OutputsSchema then
                None
            else
                let str (el: JsonElement) (name: string) = el.GetProperty(name).GetString()

                let severity (el: JsonElement) =
                    match StoredDiagnostic.severityOfToken (str el "severity") with
                    | Some severity -> severity
                    | None -> raise (FormatException "a diagnostic names a severity FCS does not have")

                let int (el: JsonElement) (name: string) = el.GetProperty(name).GetInt32()

                Some
                    { Diagnostics =
                        [ for e in o.GetProperty("diagnostics").EnumerateArray() ->
                              { FileName = real (str e "file")
                                StartLine = int e "startLine"
                                StartColumn = int e "startColumn"
                                EndLine = int e "endLine"
                                EndColumn = int e "endColumn"
                                ErrorNumber = int e "number"
                                Severity = severity e
                                Subcategory = str e "subcategory"
                                Message = real (str e "message") } ]
                      FcsSignature = str o "fcsSignature"
                      UsedSignatures = Json.tryString o "usedSignatures"
                      ApiSignature = Json.tryString o "apiSignature" }
        with
        | :? JsonException
        | :? KeyNotFoundException
        | :? InvalidOperationException
        | :? FormatException -> None

/// What a walk found for one file: its canonical key, and its record's outputs when the
/// store holds one. Behind an unknown file (see the module) the key is provisional: it is
/// neither read nor filed, so `Outputs` is None.
type FileRecord =
    { File: string
      Key: ActionKey.ActionKey
      Outputs: CheckOutputs option }

/// Called for a file the store has no record of, with the key it would be filed under,
/// when every file before it is known: the outputs it filed there, or None. A file it
/// answers None for is unknown, and so is every file after it.
type Filer = FSharpProjectOptions -> string -> ActionKey.ActionKey -> CheckOutputs option

/// One pass over the canonical chains of some projects: each project's chain is computed
/// once per walk, a referenced project's before the project that references it.
///
/// `reads`, when given, remembers what the store answered for each key, for walks that are
/// taken again and again over the same records (a restart's early cutoff, AUTOMATION-1507).
/// A record is immutable, so a remembered hit stays true; a remembered miss can only cost
/// a check, and whoever files a record clears the memo.
type Walk
    (
        store: OutcomeStore.Store,
        toolchain: string,
        repoRoot: string option,
        hashFile: string -> string,
        filer: Filer,
        ?reads: Collections.Concurrent.ConcurrentDictionary<string, CheckOutputs option>
    ) =
    let relativize = relativizeOption repoRoot
    let outputIdentity = OutputIdentity.identity repoRoot hashFile
    /// Each project's records, its chain, and the first file of it, or of a project it
    /// references, that the walk could not name (None when every file is known).
    let chains = Dictionary<string, FileRecord list * string * string option>()

    let read (key: ActionKey.ActionKey) =
        store.LookupWithOutputs key
        |> Option.bind snd
        |> Option.bind (CheckOutputs.tryOfJson repoRoot)

    let lookup (key: ActionKey.ActionKey) =
        match reads with
        | Some reads -> reads.GetOrAdd(key.Value.Text, fun _ -> read key)
        | None -> read key

    let rec chain (opts: FSharpProjectOptions) : FileRecord list * string * string option =
        let optionsHash = getProjectOptionsHashRelativeTo repoRoot opts
        let memoKey = $"%s{opts.ProjectFileName}|%s{optionsHash}"

        match chains.TryGetValue memoKey with
        | true, walked -> walked
        | false, _ ->
            // Marked before the walk, so a reference cycle names the project instead of
            // recursing without end.
            chains[memoKey] <- ([], $"cycle:%s{relativize opts.ProjectFileName}", None)
            let computed = walkProject opts optionsHash
            chains[memoKey] <- computed
            computed

    and walkProject (opts: FSharpProjectOptions) (optionsHash: string) =
        let parts = List<string>()
        // Without the source list (AUTOMATION-1613): the chain names every file in order.
        let flagsHash = getProjectFlagsHashRelativeTo repoRoot opts
        parts.Add $"options:%s{flagsHash}"
        // An in-repo assembly is built in this checkout too: named as an output is.
        addAssemblyParts parts outputIdentity fileStampText repoRoot opts

        // Only the references the project compiles against: FCS types a project against a
        // referenced project only through its `-r:`, so one listed without it
        // (`ReferenceOutputAssembly="false"`, a build-order dependency) is read by no check
        // (AUTOMATION-1479).
        let compiled =
            opts.OtherOptions
            |> Array.choose (fun o ->
                if o.StartsWith("-r:", StringComparison.Ordinal) then
                    Some(o.Substring 3)
                else
                    None)
            |> Set.ofArray

        let references =
            opts.ReferencedProjects
            |> Array.filter (fun reference -> compiled.Contains reference.OutputFile)

        // Every file of every referenced F# project is known: otherwise so is none of these.
        let mutable unknownAt: string option = None

        for reference in references do
            match reference with
            | FSharpReferencedProject.FSharpReference(_, referenced) ->
                let _, referencedChain, referencedUnknown = chain referenced
                unknownAt <- Option.orElse referencedUnknown unknownAt
                parts.Add $"project:%s{relativize referenced.ProjectFileName}=%s{referencedChain}"
            | FSharpReferencedProject.PEReference(getStamp, _) ->
                parts.Add(otherReferencePart repoRoot reference.OutputFile (getStamp ()))
            | FSharpReferencedProject.ILModuleReference(output, getStamp, _) ->
                parts.Add(otherReferencePart repoRoot output (getStamp ()))

        let projectOutputs =
            references
            |> Array.map (fun reference ->
                $"%s{relativize reference.OutputFile}=%s{outputIdentity reference.OutputFile}")
            |> String.concat "\n"
            |> sha256Hex

        let records = List<FileRecord>()
        let positions = compilePositions repoRoot hashFile opts

        let positionOf (source: string) =
            match positions.TryGetValue source with
            | true, position -> position
            | false, _ -> "list:" + optionsHash

        let partOf = Dictionary<string, string>()
        let mutable running = sha256Hex (String.concat "\n" parts)

        for source in opts.SourceFiles do
            let part =
                match partOf.TryGetValue source with
                | true, part -> part
                | false, _ ->
                    let content = hashFile source

                    let part =
                        // A generated compile item is never checked on its own. It is named
                        // without a commit id SourceLink stamped in (AUTOMATION-1480).
                        if PathFilter.isGeneratedFor opts.ProjectFileName source then
                            "content:" + OutputIdentity.generatedSource hashFile source
                        else
                            let key =
                                ActionKey.create
                                    CheckKind
                                    [ "toolchain", toolchain
                                      // Without the source list, which the upstream
                                      // names up to this file, and the position that
                                      // names what it reads of the rest (AUTOMATION-1613).
                                      "options", flagsHash
                                      "position", positionOf source
                                      "file", CachePathIdentity.keyOf repoRoot source
                                      "content", content
                                      "upstream", running
                                      "project-outputs", projectOutputs ]

                            // Behind an unknown file the key is provisional: read and filed
                            // under it, a record would be orphaned once that file is known.
                            let outputs =
                                if unknownAt.IsSome then
                                    None
                                else
                                    match lookup key with
                                    | Some outputs -> Some outputs
                                    | None -> filer opts source key

                            records.Add
                                { File = source
                                  Key = key
                                  Outputs = outputs }

                            match outputs with
                            | Some { ApiSignature = Some signature } -> "api:" + signature
                            | Some { ApiSignature = None } -> "content:" + content
                            | None ->
                                unknownAt <- Some(defaultArg unknownAt source)
                                "content:" + content

                    partOf[source] <- part
                    part

            running <- sha256Hex $"%s{running}\n%s{relativize source}=%s{part}"

        List.ofSeq records, running, unknownAt

    /// Each file of `options`' project with its canonical key and record, in compile order.
    member _.Project(options: FSharpProjectOptions) : FileRecord list =
        let records, _, _ = chain options
        records

    /// The first file, of `options`' project or of a project it references, the walk could
    /// not name: every key after it is provisional. None when every file is known.
    member _.Unknown(options: FSharpProjectOptions) : string option =
        let _, _, unknownAt = chain options
        unknownAt

    /// `filePath`'s canonical key and record in `options`' project; None for a file the
    /// project does not check on its own.
    member this.File(filePath: string, options: FSharpProjectOptions) : FileRecord option =
        this.Project options |> List.tryFind (fun r -> r.File = filePath)

/// A filer that files nothing: a walk that only reads.
let readOnly: Filer = fun _ _ _ -> None

/// File `outputs` under `key` for `file`. Returns whether it is filed.
let file
    (store: OutcomeStore.Store)
    (repoRoot: string option)
    (toolchain: string)
    (file: string)
    (key: ActionKey.ActionKey)
    (outputs: CheckOutputs)
    : bool =
    let outcome =
        if CheckOutputs.hasErrors outputs then
            OutcomeStore.Outcome.Fail
        else
            OutcomeStore.Outcome.Pass

    store.RecordWithOutputs(
        OutcomeStore.Record.ofAction
            key
            (CachePathIdentity.keyOf repoRoot file)
            outcome
            toolchain
            (defaultArg repoRoot "")
            DateTimeOffset.UtcNow,
        Some(CheckOutputs.toJson repoRoot outputs)
    )
