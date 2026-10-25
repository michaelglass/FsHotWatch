/// Tree-bound analyzer findings: what the daemon's analyzers concluded about each file,
/// stamped with the inputs the file was analyzed FROM, so a reader can tell an answer
/// about the tree on disk from one about an older tree.
///
/// The plugin's `status` line says `analyzers: ok` as soon as the last event it folded
/// was clean. That is a statement about the events it has seen, not about the tree: an
/// edit the daemon has not picked up yet is invisible to it, and on a busy daemon that
/// window was measured at 296 s. A reader that took `ok` for "this tree is clean" passed
/// a tree with a finding in it. So every per-file answer here carries the hash of the
/// source it was computed from, the analyzer set and the config chain that applied, and
/// `judge` refuses an answer whose stamps do not match what is on disk now.
///
/// The daemon side (`AnalyzersPlugin`) records the stamps and serves `Reply` through the
/// `analyzer-findings` command; the CLI side (`fshw findings`) reads the disk and calls
/// `judge`. Both halves use the functions below, so the two cannot hash differently.
module FsHotWatch.Analyzers.AnalyzerFindings

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch.ErrorLedger
open FsHotWatch.Events

/// The IPC command the analyzers plugin serves the reply under.
[<Literal>]
let CommandName = "analyzer-findings"

/// The daemon → CLI wire format of `Reply`. Internal to one fshw build pair, but
/// versioned so a CLI talking to a daemon built from other code refuses rather than
/// misreads.
[<Literal>]
let ReplySchema = "fshw-analyzer-findings-reply-v1"

/// The inputs one file's findings were computed from.
type AnalyzedFile =
    {
        /// `sourceHashOf` the text the analyzers ran over.
        SourceHash: string
        /// The project-model generation the check was published against. Informational:
        /// a file in a project a model change did not touch keeps its earlier analysis,
        /// which is still an answer about its current bytes.
        ModelGeneration: int64 option
        /// The analyzer set's semantic identity (`analyzer-inputs`) when it ran; `None`
        /// when the set had no identity.
        AnalyzerSet: string option
        /// `configDigestOf` the config chain that applied to the file when it ran.
        ConfigDigest: string
        /// Whether the analyzers saw a typed check. An aborted check gives the typed
        /// rules nothing to look at, so a parse-only analysis is not a complete one.
        Typed: bool
    }

/// The hash a file's analyzed text is stamped with. The text is what `File.ReadAllText`
/// returns (the checker reads it that way), hashed as UTF-8.
let sourceHashOf (text: string) : string = FsHotWatch.ContentHash.ofText text

/// The configuration files analyzers find by walking up from the analyzed file, as
/// (file name, cache-key label).
let configFiles =
    [ ".editorconfig", "editorconfig"; "fsharplint.json", "fsharplint" ]

/// The config chain that applies to `file`: every candidate between the scope root (the
/// filesystem root when there is none) and the file's directory, with its content.
let configChain (scopeRoot: string option) (file: string) : (string * (string * string option) list) list =
    let root =
        scopeRoot
        |> Option.defaultWith (fun () -> Path.GetPathRoot(Path.GetFullPath file))

    configFiles
    |> List.map (fun (fileName, label) ->
        label, FsHotWatch.CacheInputs.configChainCandidates root fileName label [ file ])

/// The identity of a config chain: the present files of it, by key and content. The same
/// value is the analyzers' `analyzer-config` cache-key slot.
let configDigestOf (chain: (string * (string * string option) list) list) : string =
    chain
    |> List.collect snd
    |> List.choose (fun (key, content) ->
        content
        |> Option.map (fun text -> $"%s{key} %s{FsHotWatch.CheckCache.sha256Hex text}"))
    |> String.concat "\n"
    |> FsHotWatch.CheckCache.sha256Hex

/// One file's answer: the inputs it was analyzed from and the ledger entries it left.
type FileReply =
    { File: string
      Analyzed: AnalyzedFile
      Entries: ErrorEntry list }

/// What the analyzers plugin holds, as the `analyzer-findings` command serves it.
type Reply =
    {
        /// The generation of the available project model; `None` while none is available
        /// (rediscovering, or discovery failed).
        ModelGeneration: int64 option
        /// The checkable in-scope files of that model, absolute, sorted: the files an
        /// answer about the whole tree must account for.
        Expected: string list
        /// The analyzer set on disk at the moment of the reply: its semantic identity, or
        /// why it has none.
        AnalyzerSet: Result<string, string>
        /// The configured failure threshold. Entries are stored AFTER promotion.
        FailOnSeverity: DiagnosticSeverity
        /// The root the config chain walks up to (`None`: the filesystem root).
        ScopeRoot: string option
        /// How many analyzers are loaded.
        AnalyzerCount: int
        /// Every file the plugin holds an analysis for, in or out of `Expected`.
        Files: FileReply list
    }

let private str (node: JsonNode) (name: string) : string option =
    match node[name] with
    | null -> None
    | value -> Some(value.GetValue<string>())

let private entryNode (e: ErrorEntry) : JsonNode =
    let o = JsonObject()
    o["message"] <- e.Message
    o["severity"] <- DiagnosticSeverity.toString e.Severity
    o["line"] <- e.Line
    o["column"] <- e.Column

    match e.Code with
    | Some code -> o["code"] <- code
    | None -> ()

    o

let private entryOf (node: JsonNode) : ErrorEntry =
    { Message = node["message"].GetValue<string>()
      Severity =
        match DiagnosticSeverity.fromString (node["severity"].GetValue<string>()) with
        | Some severity -> severity
        | None -> failwith "analyzer-findings: unknown severity"
      Line = node["line"].GetValue<int>()
      Column = node["column"].GetValue<int>()
      Detail = None
      Code = str node "code" }

/// The reply as the command sends it. Entry details (stack traces) are not carried: the
/// reply is about WHICH findings exist, and the ledger keeps the rest.
let serializeReply (reply: Reply) : string =
    let root = JsonObject()
    root["schema"] <- ReplySchema

    match reply.ModelGeneration with
    | Some generation -> root["modelGeneration"] <- generation
    | None -> root["modelGeneration"] <- null

    let expected = JsonArray()

    for file in reply.Expected do
        expected.Add(JsonValue.Create file)

    root["expected"] <- expected

    match reply.AnalyzerSet with
    | Result.Ok key -> root["analyzerSet"] <- key
    | Result.Error why -> root["analyzerSetRefusal"] <- why

    root["failOnSeverity"] <- DiagnosticSeverity.toString reply.FailOnSeverity

    match reply.ScopeRoot with
    | Some scope -> root["scopeRoot"] <- scope
    | None -> ()

    root["analyzers"] <- reply.AnalyzerCount

    let files = JsonArray()

    for file in reply.Files do
        let f = JsonObject()
        f["file"] <- file.File
        f["sourceHash"] <- file.Analyzed.SourceHash

        match file.Analyzed.ModelGeneration with
        | Some generation -> f["modelGeneration"] <- generation
        | None -> ()

        match file.Analyzed.AnalyzerSet with
        | Some key -> f["analyzerSet"] <- key
        | None -> ()

        f["configDigest"] <- file.Analyzed.ConfigDigest
        f["typed"] <- file.Analyzed.Typed
        let entries = JsonArray()

        for e in file.Entries do
            entries.Add(entryNode e)

        f["entries"] <- entries
        files.Add(f)

    root["files"] <- files
    root.ToJsonString()

/// Read a reply. `Error` for anything that is not one this build understands, including
/// the unknown-command answer of a daemon whose analyzers plugin predates the command.
let tryParseReply (json: string) : Result<Reply, string> =
    try
        let root = JsonNode.Parse(json)

        match str root "schema" with
        | Some ReplySchema ->
            let generation =
                match root["modelGeneration"] with
                | null -> None
                | node -> Some(node.GetValue<int64>())

            let files =
                root["files"].AsArray()
                |> Seq.map (fun f ->
                    { File = f["file"].GetValue<string>()
                      Analyzed =
                        { SourceHash = f["sourceHash"].GetValue<string>()
                          ModelGeneration =
                            match f["modelGeneration"] with
                            | null -> None
                            | node -> Some(node.GetValue<int64>())
                          AnalyzerSet = str f "analyzerSet"
                          ConfigDigest = f["configDigest"].GetValue<string>()
                          Typed = f["typed"].GetValue<bool>() }
                      Entries = f["entries"].AsArray() |> Seq.map entryOf |> Seq.toList })
                |> Seq.toList

            Result.Ok
                { ModelGeneration = generation
                  Expected = root["expected"].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList
                  AnalyzerSet =
                    match str root "analyzerSet", str root "analyzerSetRefusal" with
                    | Some key, _ -> Result.Ok key
                    | None, Some why -> Result.Error why
                    | None, None -> Result.Error "the reply names no analyzer set"
                  FailOnSeverity =
                    match DiagnosticSeverity.fromString (root["failOnSeverity"].GetValue<string>()) with
                    | Some severity -> severity
                    | None -> failwith "unknown failOnSeverity"
                  ScopeRoot = str root "scopeRoot"
                  AnalyzerCount = root["analyzers"].GetValue<int>()
                  Files = files }
        | Some other -> Result.Error $"the daemon answered with schema %s{other}, this fshw reads %s{ReplySchema}"
        | None -> Result.Error "the daemon's reply carries no schema"
    with ex ->
        Result.Error $"the daemon's reply could not be read (%s{ex.GetType().Name}: %s{ex.Message})"

/// What the reader sees on disk now, for the files a reply names.
type DiskView =
    {
        /// `sourceHashOf` the file's current text; `None` when it cannot be read.
        SourceHash: string -> string option
        /// `configDigestOf` the config chain that applies to the file now.
        ConfigDigest: string -> string
    }

/// The real disk, under the reply's scope root. The config chain depends only on a
/// file's directory, so it is read once per directory.
let diskView (scopeRoot: string option) : DiskView =
    let byDirectory =
        Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal)

    { SourceHash =
        fun file ->
            try
                Some(sourceHashOf (File.ReadAllText file))
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None
      ConfigDigest =
        fun file ->
            let directory = Path.GetDirectoryName(Path.GetFullPath file)

            byDirectory.GetOrAdd(directory, fun _ -> configDigestOf (configChain scopeRoot file)) }

/// Why an answer is not about the tree on disk. Each one names the file.
type StaleReason =
    | SourceChanged of file: string
    | Unreadable of file: string
    | AnalyzerSetChanged of file: string
    | ConfigChanged of file: string
    | ModelUnavailable

module StaleReason =
    let describe (reason: StaleReason) : string =
        match reason with
        | SourceChanged file -> $"%s{file}: its source changed since it was analyzed"
        | Unreadable file -> $"%s{file}: cannot be read"
        | AnalyzerSetChanged file -> $"%s{file}: analyzed by a different analyzer set than the one on disk"
        | ConfigChanged file ->
            $"%s{file}: its analyzer config (.editorconfig / fsharplint.json) changed since it was analyzed"
        | ModelUnavailable -> "the daemon has no available project model (it is rediscovering, or discovery failed)"

    /// The file a reason names, when it names one.
    let file (reason: StaleReason) : string option =
        match reason with
        | SourceChanged file
        | Unreadable file
        | AnalyzerSetChanged file
        | ConfigChanged file -> Some file
        | ModelUnavailable -> None

/// The verdict on a reply.
type Judgement =
    /// Every expected file was analyzed from what is on disk now. The findings are the
    /// expected files' entries, sorted by file, line and column.
    | Current of findings: (string * ErrorEntry) list
    /// Some answer is about an older tree. Waiting can fix this: the daemon may simply
    /// not have picked the change up yet.
    | Stale of reasons: StaleReason list
    /// Expected files with no complete analysis: never analyzed, analyzed from an aborted
    /// check, or timed out. The set the answer covers is not the set it must cover.
    | Incomplete of missing: string list
    /// No answer can be about any tree: the analyzer set has no identity.
    | Refused of reason: string

/// Judge `reply` against `disk`. Pure apart from `disk`'s reads.
///
/// A file outside `Expected` is ignored: the model no longer compiles it, so its entries
/// say nothing about this tree. A missing expected file is incompleteness; a present one
/// whose stamps disagree with the disk is staleness, and staleness wins, because it is
/// the one a wait can resolve.
let judge (disk: DiskView) (reply: Reply) : Judgement =
    match reply.AnalyzerSet, reply.ModelGeneration with
    | Result.Error why, _ ->
        Refused $"the analyzer set on disk has no identity, so no answer can be tied to it: %s{why}"
    | _, None -> Stale [ ModelUnavailable ]
    | Result.Ok analyzerSet, Some _ ->
        let byFile = reply.Files |> List.map (fun f -> f.File, f) |> Map.ofList

        let analyzed, missing =
            reply.Expected
            |> List.partition (fun file ->
                match Map.tryFind file byFile with
                | Some f -> f.Analyzed.Typed
                | None -> false)

        let stale =
            analyzed
            |> List.choose (fun file ->
                let f = byFile[file]

                match disk.SourceHash file with
                | None -> Some(Unreadable file)
                | Some hash when hash <> f.Analyzed.SourceHash -> Some(SourceChanged file)
                | Some _ when f.Analyzed.AnalyzerSet <> Some analyzerSet -> Some(AnalyzerSetChanged file)
                | Some _ when disk.ConfigDigest file <> f.Analyzed.ConfigDigest -> Some(ConfigChanged file)
                | Some _ -> None)

        match stale, missing with
        | [], [] ->
            analyzed
            |> List.collect (fun file -> byFile[file].Entries |> List.map (fun e -> file, e))
            |> List.sortBy (fun (file, e) -> file, e.Line, e.Column)
            |> Current
        | [], missing -> Incomplete missing
        | stale, _ -> Stale stale
