/// AUTOMATION-1816 step 1, measurement only: what each stored check-result entry was typed
/// against under two ALTERNATIVE keys, persisted beside the cache so a later daemon (a
/// land's starts cold every land) can say whether its `NoEntry` misses would have hit under
/// them. Nothing here is a key and nothing here serves a result.
///
/// A record is filed per (file, project, the file's bytes, its keyed options, the
/// toolchain): the inputs every key names alike. It holds the upstreams the entry stored
/// for exactly those inputs had (`AlternativeUpstreams`). A later miss with the same inputs
/// reads it and compares the upstreams it has now (`classify`).
module internal FsHotWatch.AlternativeKeys

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.CheckCacheTally

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

/// Where records are kept.
type IAlternativeKeyStore =
    /// The record filed under `key`; None when there is none, or it cannot be read.
    abstract TryRead: key: string -> AlternativeUpstreams option
    /// File `upstreams` under `key`, replacing what was there.
    abstract Write: key: string -> upstreams: AlternativeUpstreams -> unit

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

/// The format of a record. Any other reads as none.
[<Literal>]
let Schema = "fshw-alternative-upstreams-v1"

let toJson (upstreams: AlternativeUpstreams) : string =
    let o = JsonObject()
    o["schema"] <- Schema
    o["signature"] <- upstreams.Signature
    let references = JsonArray()

    for file in upstreams.References do
        references.Add file

    o["references"] <- references
    o["referencesFingerprint"] <- upstreams.ReferencesFingerprint
    o.ToJsonString()

/// The record in `text`; None for anything that is not exactly one of this schema.
// MGA-ERROR-REPORT-001:ok — a record that does not parse is a normal input here (an older
// or damaged file), read as no record: the miss it was for counts as unknown.
let tryOfJson (text: string) : AlternativeUpstreams option =
    // `using`, not `use`: the document is never null, and `use` compiles a null check whose
    // other arm no input can reach.
    try
        using (JsonDocument.Parse text) (fun doc ->
            let o = doc.RootElement
            let str (name: string) = o.GetProperty(name).GetString()

            if str "schema" <> Schema then
                None
            else
                Some
                    { Signature = str "signature"
                      References = o.GetProperty("references").Deserialize<string array>() |> List.ofArray
                      ReferencesFingerprint = str "referencesFingerprint" })
    with _ ->
        None

/// Records in memory: for tests, and for a daemon that keeps none.
type InMemoryAlternativeKeyStore() =
    let records =
        Collections.Concurrent.ConcurrentDictionary<string, AlternativeUpstreams>()

    interface IAlternativeKeyStore with
        member _.TryRead key =
            match records.TryGetValue key with
            | true, upstreams -> Some upstreams
            | false, _ -> None

        member _.Write key upstreams = records[key] <- upstreams

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
        member _.Write key upstreams =
            let path = pathOf key
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"

            try
                Directory.CreateDirectory dir |> ignore
                File.WriteAllText(temporary, toJson upstreams)
                File.Move(temporary, path, true)
            with ex ->
                Logging.debug "cache" $"alternative upstreams not written to %s{path}: %s{ex.Message}"

                if File.Exists temporary then
                    File.Delete temporary
