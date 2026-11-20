/// AUTOMATION-1816: what each stored check-result entry was typed against under two
/// ALTERNATIVE keys, persisted beside the cache so a later daemon (a land's starts cold every
/// land) can say whether its `NoEntry` misses would have hit under them. Nothing here is a
/// key and nothing here serves a result.
///
/// A record is filed per (file, project, the file's bytes, its keyed options, the
/// toolchain): the inputs every key names alike. It holds the upstreams the entry stored
/// for exactly those inputs had (`AlternativeUpstreams`). A later miss with the same inputs
/// reads it and compares the upstreams it has now (`classify`).
///
/// Step 2, with the references key in shadow (`InMemoryCheckCache.ReferencesKey.Shadow`):
/// the record also holds the stored check's verdict (`CheckVerdict`), and a miss the
/// references key would have served compares it with the verdict of the cold check that
/// answered the miss instead (`verify`).
module internal FsHotWatch.AlternativeKeys

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.CheckCacheTally
open FsHotWatch.Events

/// What a stored entry was typed against, beyond its own bytes and options.
type AlternativeUpstreams =
    {
        /// `UpstreamFingerprints.ForApi`: every earlier file and every source of every
        /// referenced F# project, by API signature (by content where it declares none).
        Signature: string
        /// The files its check resolved symbols in (`CacheInputs.usedDeclarationFiles`),
        /// repository-relative.
        References: string list
        /// `UpstreamFingerprints.ReferencesFor` over `References` when it was stored.
        ReferencesFingerprint: string
    }

/// A check's verdict as the references key would serve it: each `VerdictFacet` of its
/// outputs (`CheckOutputs`), rendered so two verdicts compare by string equality.
type CheckVerdict =
    {
        /// SHA-256 of the file's own diagnostics, paths inside the repository relative.
        Diagnostics: string
        FcsSignature: string
        /// `some:<hash>`, or `none` when the shape could not be described.
        UsedSignatures: string
        /// `some:<signature>`, or `none` when the file declares no signature of its own.
        ApiSignature: string
    }

module CheckVerdict =
    let ofParts (diagnostics: string) (fcsSignature: string) (usedSignatures: string) (apiSignature: string) =
        { Diagnostics = diagnostics
          FcsSignature = fcsSignature
          UsedSignatures = usedSignatures
          ApiSignature = apiSignature }

    let private optional (value: string option) =
        match value with
        | Some value -> "some:" + value
        | None -> "none"

    /// The verdict of a check whose outputs are `outputs`.
    let ofOutputs (repoRoot: string option) (outputs: CheckOutputs) : CheckVerdict =
        let relative = CheckCache.relativizeOption repoRoot

        let diagnostics =
            outputs.Diagnostics
            |> List.map (fun d ->
                String.Join(
                    "\u001f",
                    relative d.FileName,
                    string d.StartLine,
                    string d.StartColumn,
                    string d.EndLine,
                    string d.EndColumn,
                    string d.ErrorNumber,
                    StoredDiagnostic.severityToken d.Severity,
                    d.Subcategory,
                    relative d.Message
                ))
            |> String.concat "\u001e"

        ofParts
            (CheckCache.sha256Hex diagnostics)
            outputs.FcsSignature
            (optional outputs.UsedSignatures)
            (optional outputs.ApiSignature)

    let private facet (verdict: CheckVerdict) (facet: VerdictFacet) =
        match facet with
        | VerdictFacet.Diagnostics -> verdict.Diagnostics
        | VerdictFacet.FcsSignature -> verdict.FcsSignature
        | VerdictFacet.UsedSignatures -> verdict.UsedSignatures
        | VerdictFacet.ApiSignature -> verdict.ApiSignature

    /// The facets on which `cached` and `cold` differ, in `VerdictFacet.all` order.
    let differing (cached: CheckVerdict) (cold: CheckVerdict) : VerdictFacet list =
        VerdictFacet.all |> List.filter (fun f -> facet cached f <> facet cold f)

    /// Bytes a 64-bit CLR holds for one string of `length` characters: a 20-byte header and
    /// length, two bytes per character, rounded up to 8.
    let private stringBytes (length: int) = int64 ((20 + 2 * length + 7) / 8 * 8)

    /// What the shadow holds in memory for one cold verdict until the next settle: the
    /// verdict object (header and four references) and its four strings, and the
    /// dictionary node that holds it with its slot and content hash (header, the struct key's
    /// two references, the struct value's two, the next-node reference and the hash code).
    /// The slot's path, project and content hash are the pipeline's own strings, not copies.
    let heldBytes (verdict: CheckVerdict) : int64 =
        let node = 16L + 16L + 16L + 8L + 8L
        let record = 16L + 4L * 8L

        node
        + record
        + stringBytes verdict.Diagnostics.Length
        + stringBytes verdict.FcsSignature.Length
        + stringBytes verdict.UsedSignatures.Length
        + stringBytes verdict.ApiSignature.Length

/// One filed record.
[<RequireQualifiedAccess>]
type AlternativeRecord =
    /// The upstreams only: filed with the references key off, or by step 1's fshw.
    | Upstreams of AlternativeUpstreams
    /// The upstreams and the stored check's verdict: filed with the references key in shadow.
    | WithVerdict of AlternativeUpstreams * CheckVerdict

module AlternativeRecord =
    let upstreams (record: AlternativeRecord) : AlternativeUpstreams =
        match record with
        | AlternativeRecord.Upstreams upstreams
        | AlternativeRecord.WithVerdict(upstreams, _) -> upstreams

/// Where records are kept.
type IAlternativeKeyStore =
    /// The record filed under `key`; None when there is none, or it cannot be read.
    abstract TryRead: key: string -> AlternativeRecord option
    /// File `record` under `key`, replacing what was there.
    abstract Write: key: string -> record: AlternativeRecord -> unit

/// The key a record is filed under: what every key names alike for one file's entry.
let recordKey
    (file: string)
    (project: string)
    (contentHash: string)
    (keyedOptions: string)
    (toolchain: string)
    : string =
    CheckCache.sha256Hex $"%s{file}|%s{project}|%s{contentHash}|%s{keyedOptions}|%s{toolchain}"

/// What a `NoEntry` miss would have done: `recorded` is the record for its inputs,
/// `signatureNow` its signature upstream now, and `referencesNow` the references upstream
/// now over a recorded reference set.
let classify
    (recorded: AlternativeUpstreams option)
    (signatureNow: string)
    (referencesNow: string list -> string)
    : NoEntryAlternative =
    match recorded with
    | None -> NoEntryAlternative.Unknown
    | Some recorded when recorded.Signature = signatureNow -> NoEntryAlternative.SignatureUnchanged
    | Some recorded when recorded.ReferencesFingerprint = referencesNow recorded.References ->
        NoEntryAlternative.OnlyReferencesUnchanged
    | Some _ -> NoEntryAlternative.NeitherUnchanged

/// Whether the references key would have served a `NoEntry` miss of this outcome.
let referencesKeyServes (outcome: NoEntryAlternative) : bool =
    match outcome with
    | NoEntryAlternative.SignatureUnchanged
    | NoEntryAlternative.OnlyReferencesUnchanged -> true
    | NoEntryAlternative.Unknown
    | NoEntryAlternative.NeitherUnchanged -> false

/// The shadow's finding for a miss of `file` the references key would have served from
/// `recorded`, against `cold`, the verdict of the check that answered it instead (None when
/// that check left none).
let verify (file: string) (recorded: AlternativeRecord) (cold: CheckVerdict option) : ShadowOutcome =
    match recorded, cold with
    | AlternativeRecord.Upstreams _, _ -> ShadowOutcome.Unverified ShadowUnverifiedReason.NoCachedVerdict
    | AlternativeRecord.WithVerdict _, None -> ShadowOutcome.Unverified ShadowUnverifiedReason.NoColdVerdict
    | AlternativeRecord.WithVerdict(_, cached), Some cold ->
        match CheckVerdict.differing cached cold with
        | [] -> ShadowOutcome.Agreed
        | facets -> ShadowOutcome.Disagreed { File = file; Facets = facets }

/// The format of an upstreams-only record.
[<Literal>]
let Schema = "fshw-alternative-upstreams-v1"

/// The format of a record with a verdict.
[<Literal>]
let VerdictSchema = "fshw-alternative-upstreams-v2"

let toJson (record: AlternativeRecord) : string =
    let o = JsonObject()
    let upstreams = AlternativeRecord.upstreams record

    o["schema"] <-
        match record with
        | AlternativeRecord.Upstreams _ -> Schema
        | AlternativeRecord.WithVerdict _ -> VerdictSchema

    o["signature"] <- upstreams.Signature
    let references = JsonArray()

    for file in upstreams.References do
        references.Add file

    o["references"] <- references
    o["referencesFingerprint"] <- upstreams.ReferencesFingerprint

    match record with
    | AlternativeRecord.Upstreams _ -> ()
    | AlternativeRecord.WithVerdict(_, verdict) ->
        let v = JsonObject()
        v["diagnostics"] <- verdict.Diagnostics
        v["fcsSignature"] <- verdict.FcsSignature
        v["usedSignatures"] <- verdict.UsedSignatures
        v["apiSignature"] <- verdict.ApiSignature
        o["verdict"] <- v

    o.ToJsonString()

/// The record in `text`; None for anything that is not exactly one of these schemas.
// MGA-ERROR-REPORT-001:ok — a record that does not parse is a normal input here (an older
// or damaged file), read as no record: the miss it was for counts as unknown.
let tryOfJson (text: string) : AlternativeRecord option =
    // `using`, not `use`: the document is never null, and `use` compiles a null check whose
    // other arm no input can reach.
    try
        using (JsonDocument.Parse text) (fun doc ->
            let o = doc.RootElement
            let str (name: string) = o.GetProperty(name).GetString()

            let upstreams () =
                { Signature = str "signature"
                  References = o.GetProperty("references").Deserialize<string array>() |> List.ofArray
                  ReferencesFingerprint = str "referencesFingerprint" }

            match str "schema" with
            | Schema -> Some(AlternativeRecord.Upstreams(upstreams ()))
            | VerdictSchema ->
                let v = o.GetProperty "verdict"
                let facet (name: string) = v.GetProperty(name).GetString()

                Some(
                    AlternativeRecord.WithVerdict(
                        upstreams (),
                        CheckVerdict.ofParts
                            (facet "diagnostics")
                            (facet "fcsSignature")
                            (facet "usedSignatures")
                            (facet "apiSignature")
                    )
                )
            | _ -> None)
    with _ ->
        None

/// Records in memory: for tests, and for a daemon that keeps none.
type InMemoryAlternativeKeyStore() =
    let records =
        Collections.Concurrent.ConcurrentDictionary<string, AlternativeRecord>()

    interface IAlternativeKeyStore with
        member _.TryRead key =
            match records.TryGetValue key with
            | true, record -> Some record
            | false, _ -> None

        member _.Write key record = records[key] <- record

/// One JSON file per record in `dir`, written whole and then renamed into place, so a
/// reader sees the old record or the new one. Measurement must not take the daemon down:
/// a failed write is logged and dropped (the next miss on those inputs reads as unknown),
/// and a missing or unreadable record reads as none.
type FileAlternativeKeyStore(dir: string) =
    let pathOf (key: string) = Path.Combine(dir, key + ".json")

    interface IAlternativeKeyStore with
        // MGA-ERROR-REPORT-001:ok — no record and an unreadable one both mean nothing to
        // compare with: the miss counts as unknown, which is what the count is for.
        member _.TryRead key =
            try
                tryOfJson (File.ReadAllText(pathOf key))
            with _ ->
                None

        // MGA-ERROR-REPORT-001:ok — measurement is never load-bearing: a record not written
        // is logged, and a later miss on these inputs counts as unknown.
        member _.Write key record =
            let path = pathOf key
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"

            try
                Directory.CreateDirectory dir |> ignore
                File.WriteAllText(temporary, toJson record)
                File.Move(temporary, path, true)
            with ex ->
                Logging.debug "cache" $"alternative upstreams not written to %s{path}: %s{ex.Message}"

                if File.Exists temporary then
                    File.Delete temporary
