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
///                   by the API signature IN ITS OWN RECORD under its own canonical key,
///                   or by its content where it has no record or no signature;
///   * `project-outputs` — the bytes of every referenced project's output, which FCS
///                   types against instead of the sources whenever it is newer
///                   (ADR-037). Stricter than the in-memory guard, never looser.
///
/// The in-memory API key cannot be persisted as it is: `UpstreamFingerprints.ForApi` names
/// earlier files by whatever signatures were recorded BEFORE the table was built, so the
/// same bytes get a content key in a cold scan and a signature key later. Here an earlier
/// file is named by its signature exactly when the store holds its record, and a record is
/// filed only in compile order (`Walk` with a filer), so writer and reader compute the same
/// key. Soundness is by induction over the compile order, as for the in-memory API key: an
/// equal `upstream` means each earlier file had equal bytes, or a check under an equal key
/// declared an equal signature.
///
/// A miss is never wrong, only slow: an earlier file with no record names its content, so
/// the files after it miss until it is re-checked and filed. That is the "lazily, in
/// dependency order" of the spike.
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

/// One FCS diagnostic as a record keeps it: every field a consumer reads, paths as the
/// worktree reads them.
type StoredDiagnostic =
    {
        FileName: string
        StartLine: int
        StartColumn: int
        EndLine: int
        EndColumn: int
        ErrorNumber: int
        /// `%A` of `FSharpDiagnosticSeverity`, as `fcsCheckSignature` spells it.
        Severity: string
        Subcategory: string
        Message: string
    }

/// What a file's check produced, without FCS objects.
type CheckOutputs =
    {
        Diagnostics: StoredDiagnostic list
        /// `CheckCache.fcsCheckSignature` of the check.
        FcsSignature: string
        /// `CacheInputs.usedSignaturesHash` of the check; None when it could not be described.
        UsedSignatures: string option
        /// `ApiSignature.ofCheck`: None when the file has no signature of its own.
        ApiSignature: string option
    }

module CheckOutputs =
    /// The outputs of a full check; None for one that did not complete.
    let ofResult (repoRoot: string option) (result: FileCheckResult) : CheckOutputs option =
        match result.CheckResults with
        | ParseOnly -> None
        | FullCheck results when isNull (box results) -> None
        | FullCheck results ->
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
                            Severity = $"%A{d.Severity}"
                            Subcategory = d.Subcategory
                            Message = toReal d.Message } ]
                  FcsSignature = fcsCheckSignature result.CheckResults
                  UsedSignatures = CacheInputs.usedSignaturesHash repoRoot result.ProjectOptions result.CheckResults
                  ApiSignature = ApiSignature.ofCheck result.Source result.CheckResults }

    /// Whether any diagnostic is an error: the record's outcome.
    let hasErrors (outputs: CheckOutputs) =
        outputs.Diagnostics |> List.exists (fun d -> d.Severity = "Error")

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
            e["severity"] <- d.Severity
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
                                Severity = str e "severity"
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
/// store holds one.
type FileRecord =
    { File: string
      Key: ActionKey.ActionKey
      Outputs: CheckOutputs option }

/// Called for a file the store has no record of, with the key it would be filed under:
/// the outputs it filed there, or None.
type Filer = FSharpProjectOptions -> string -> ActionKey.ActionKey -> CheckOutputs option

/// One pass over the canonical chains of some projects: each project's chain is computed
/// once per walk, a referenced project's before the project that references it.
type Walk
    (store: OutcomeStore.Store, toolchain: string, repoRoot: string option, hashFile: string -> string, filer: Filer) =
    let relativize = relativizeOption repoRoot
    let chains = Dictionary<string, FileRecord list * string>()

    let lookup (key: ActionKey.ActionKey) =
        store.LookupWithOutputs key.Value
        |> Option.bind snd
        |> Option.bind (CheckOutputs.tryOfJson repoRoot)

    let rec chain (opts: FSharpProjectOptions) : FileRecord list * string =
        let optionsHash = getProjectOptionsHashRelativeTo repoRoot opts
        let memoKey = $"%s{opts.ProjectFileName}|%s{optionsHash}"

        match chains.TryGetValue memoKey with
        | true, known -> known
        | false, _ ->
            // Marked before the walk, so a reference cycle names the project instead of
            // recursing without end.
            chains[memoKey] <- ([], $"cycle:%s{relativize opts.ProjectFileName}")
            let computed = walkProject opts optionsHash
            chains[memoKey] <- computed
            computed

    and walkProject (opts: FSharpProjectOptions) (optionsHash: string) =
        let parts = List<string>()
        parts.Add $"options:%s{optionsHash}"
        addAssemblyParts parts hashFile fileStampText repoRoot opts

        for reference in opts.ReferencedProjects do
            match reference with
            | FSharpReferencedProject.FSharpReference(_, referenced) ->
                parts.Add $"project:%s{relativize referenced.ProjectFileName}=%s{snd (chain referenced)}"
            | FSharpReferencedProject.PEReference(getStamp, _) ->
                parts.Add(otherReferencePart repoRoot reference.OutputFile (getStamp ()))
            | FSharpReferencedProject.ILModuleReference(output, getStamp, _) ->
                parts.Add(otherReferencePart repoRoot output (getStamp ()))

        let projectOutputs =
            opts.ReferencedProjects
            |> Array.map (fun reference -> $"%s{relativize reference.OutputFile}=%s{hashFile reference.OutputFile}")
            |> String.concat "\n"
            |> sha256Hex

        let records = List<FileRecord>()
        let partOf = Dictionary<string, string>()
        let mutable running = sha256Hex (String.concat "\n" parts)

        for source in opts.SourceFiles do
            let part =
                match partOf.TryGetValue source with
                | true, part -> part
                | false, _ ->
                    let content = hashFile source

                    let part =
                        // A generated compile item is never checked on its own.
                        if PathFilter.isGeneratedPath source then
                            "content:" + content
                        else
                            let key =
                                ActionKey.create
                                    CheckKind
                                    [ "toolchain", toolchain
                                      "options", optionsHash
                                      "file", CachePathIdentity.keyOf repoRoot source
                                      "content", content
                                      "upstream", running
                                      "project-outputs", projectOutputs ]

                            let outputs =
                                match lookup key with
                                | Some outputs -> Some outputs
                                | None -> filer opts source key

                            records.Add
                                { File = source
                                  Key = key
                                  Outputs = outputs }

                            match outputs with
                            | Some { ApiSignature = Some signature } -> "api:" + signature
                            | _ -> "content:" + content

                    partOf[source] <- part
                    part

            running <- sha256Hex $"%s{running}\n%s{relativize source}=%s{part}"

        List.ofSeq records, running

    /// Each file of `options`' project with its canonical key and record, in compile order.
    member _.Project(options: FSharpProjectOptions) : FileRecord list = fst (chain options)

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
    store.RecordWithOutputs(
        { Key = key
          Subject = CachePathIdentity.keyOf repoRoot file
          Outcome =
            if CheckOutputs.hasErrors outputs then
                OutcomeStore.Outcome.Fail
            else
                OutcomeStore.Outcome.Pass
          Counts = None
          DurationMs = None
          Producer =
            { Binary = toolchain
              Worktree = defaultArg repoRoot ""
              RunId = None }
          RecordedAt = DateTimeOffset.UtcNow },
        Some(CheckOutputs.toJson repoRoot outputs)
    )
