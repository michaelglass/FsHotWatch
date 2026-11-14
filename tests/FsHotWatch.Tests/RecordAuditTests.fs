/// The clean-recompute audit (ADR-059 §35 Change 1a, AUTOMATION-1633): a stored record that
/// differs from a clean recompute under the same key is a key that missed an input.
///
/// The keys here are the REAL check-record keys (`CheckRecords.Walk`); only the check is a
/// stand-in (its outputs are a function of the file's bytes), because what the audit judges
/// is the key, not FCS. The mandatory control plants a key that leaves an input out.
module FsHotWatch.Tests.RecordAuditTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

let private toolchain = "toolchain-under-test"

let private storeAt (root: string) =
    OutcomeStore.Store(
        { Root = root
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = ignore }
    )

/// One project of `count` files, File1.fs .. FileN.fs, under `root`.
let private project (root: string) (count: int) : FSharpProjectOptions * string list =
    Directory.CreateDirectory root |> ignore

    let files =
        [ for i in 1..count ->
              let path = Path.Combine(root, $"File%d{i}.fs")
              File.WriteAllText(path, $"module File%d{i}\nlet v%d{i} : int = %d{i}\n")
              path ]

    { ProjectFileName = Path.Combine(root, "Audit.fsproj")
      ProjectId = None
      SourceFiles = Array.ofList files
      OtherOptions = [| "--target:library" |]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UnixEpoch
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None },
    files

/// The stand-in check: an error for a file whose bytes say `bad`, and an API signature of
/// its first line (so a body edit keeps the next file's key, as the real cutoff does).
let private checkOf (file: string) : CheckOutputs =
    let text = File.ReadAllText file

    { Diagnostics =
        if text.Contains "bad" then
            [ { FileName = file
                StartLine = 2
                StartColumn = 0
                EndLine = 2
                EndColumn = 3
                ErrorNumber = 1
                Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error
                Subcategory = "typecheck"
                Message = "bad" } ]
        else
            []
      FcsSignature = ContentHash.ofText text
      UsedSignatures = None
      ApiSignature = Some(ContentHash.ofText (text.Split('\n')[0])) }

/// What a daemon leaves behind: every file of `options` checked and filed in `store`,
/// keyed by the real check-record walk over `hashFile`.
let private fileAll (store: OutcomeStore.Store) (repoRoot: string) (hashFile: string -> string) options =
    let filer: CheckRecords.Filer =
        fun (projectOptions: FSharpProjectOptions) file key ->
            let outputs = checkOf file

            CheckRecords.file
                store
                (CheckRecords.LastKeys.at store)
                (Some repoRoot)
                toolchain
                projectOptions.ProjectFileName
                file
                key
                outputs
            |> ignore

            Some outputs

    CheckRecords.Walk(store, toolchain, Some repoRoot, hashFile, filer).Project(options)

let private realHash = CheckCache.FileContentHasher().Hash

/// A key that LEAVES OUT AN INPUT: `planted`'s bytes never reach its key.
let private plantedHash (planted: string) =
    fun (path: string) -> if path = planted then "planted-constant" else realHash path

/// Tree v1 filed into the audited store, then one file edited (`bad` fixed) and tree v2
/// recomputed into an empty clean store, both under `hashFor`. Returns both roots and the
/// edited file.
let private auditedThenClean (dir: string) (hashFor: string -> string -> string) =
    let repo = Path.Combine(dir, "repo")
    let options, files = project repo 6
    let edited = files[3]
    File.WriteAllText(edited, "module File4\nlet v4 : int = bad\n")
    let hash = hashFor edited
    let auditedRoot = Path.Combine(dir, "audited")
    fileAll (storeAt auditedRoot) repo hash options |> ignore
    // The edit a later tree makes: the same first line, so only this file's bytes move.
    File.WriteAllText(edited, "module File4\nlet v4 : int = 4\n")
    let cleanRoot = Path.Combine(dir, "clean")
    fileAll (storeAt cleanRoot) repo hash options |> ignore
    auditedRoot, cleanRoot, edited, repo

let private everyKey =
    { RecordAudit.Options.defaults with
        Sample = None }

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``CONTROL: a planted key that leaves out a file's bytes is caught as a mismatch on that file`` () =
    withTempDir "audit-planted" (fun dir ->
        let auditedRoot, cleanRoot, edited, repo = auditedThenClean dir plantedHash

        let report = RecordAudit.audit auditedRoot cleanRoot everyKey

        test <@ report.Verdict = RecordAudit.Verdict.Mismatch @>
        test <@ RecordAudit.exitCode report.Verdict = 1 @>

        let subject = CachePathIdentity.keyOf (Some repo) edited
        test <@ report.Mismatches |> List.map _.Subject = [ subject ] @>

        let mismatch = report.Mismatches.Head
        test <@ mismatch.Kind = "check" @>
        test <@ mismatch.IsAnswer @>
        test <@ mismatch.Fields = [ "outcome"; "diagnostics"; "fcsSignature" ] @>
        test <@ mismatch.Stored.Outcome = "fail" && mismatch.Clean.Outcome = "pass" @>

        let check = report.Kinds |> List.exactlyOne
        test <@ check.Shared = 6 && check.Audited = 6 && check.Mismatched = 1 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``positive control: the real key moves with the edit, so the same history agrees`` () =
    withTempDir "audit-sound" (fun dir ->
        let auditedRoot, cleanRoot, _, _ = auditedThenClean dir (fun _ -> realHash)

        let report = RecordAudit.audit auditedRoot cleanRoot everyKey

        test <@ List.isEmpty report.Mismatches @>
        test <@ report.Verdict = RecordAudit.Verdict.Agree @>
        test <@ RecordAudit.exitCode report.Verdict = 0 @>
        let check = report.Kinds |> List.exactlyOne
        // The edited file's key moved: five of six keys are shared, and they agree.
        test <@ check.CleanKeys = 6 && check.Shared = 5 && check.Audited = 5 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``sampling is seeded: the same seed audits the same keys, a smaller sample a prefix of them`` () =
    withTempDir "audit-seeded" (fun dir ->
        let repo = Path.Combine(dir, "repo")
        let options, _ = project repo 30
        let auditedRoot = Path.Combine(dir, "audited")
        let cleanRoot = Path.Combine(dir, "clean")
        fileAll (storeAt auditedRoot) repo realHash options |> ignore
        fileAll (storeAt cleanRoot) repo realHash options |> ignore

        let keysWith seed sample =
            let report =
                RecordAudit.audit
                    auditedRoot
                    cleanRoot
                    { RecordAudit.Options.defaults with
                        Seed = seed
                        Sample = Some sample }

            report.AuditedKeys |> List.exactlyOne |> snd

        let first = keysWith "night-1" 10
        test <@ first.Length = 10 @>
        test <@ keysWith "night-1" 10 = first @>
        test <@ keysWith "night-1" 4 = List.truncate 4 first @>
        test <@ keysWith "night-2" 10 <> first @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a kind with no key in both stores is vacuous, never agreement`` () =
    withTempDir "audit-vacuous" (fun dir ->
        let repo = Path.Combine(dir, "repo")
        let options, _ = project repo 3
        let cleanRoot = Path.Combine(dir, "clean")
        fileAll (storeAt cleanRoot) repo realHash options |> ignore
        let empty = Path.Combine(dir, "audited")
        Directory.CreateDirectory empty |> ignore

        let report = RecordAudit.audit empty cleanRoot everyKey

        test <@ report.Verdict = RecordAudit.Verdict.Vacuous @>
        test <@ RecordAudit.exitCode report.Verdict = 3 @>

        let otherKind =
            RecordAudit.audit
                cleanRoot
                cleanRoot
                { everyKey with
                    Kinds = [ "options-inputs" ] }

        test <@ otherKind.Verdict = RecordAudit.Verdict.Vacuous @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a named key is audited on demand, and a key one store lacks is reported, not agreed`` () =
    withTempDir "audit-key" (fun dir ->
        let auditedRoot, cleanRoot, edited, _ = auditedThenClean dir plantedHash

        let plantedKey =
            (RecordAudit.audit auditedRoot cleanRoot everyKey).Mismatches.Head.Key
            |> ActionKey.KeyValue.tryParse
            |> Option.get

        let named =
            RecordAudit.audit auditedRoot cleanRoot { everyKey with Keys = [ plantedKey ] }

        test <@ named.Verdict = RecordAudit.Verdict.Mismatch @>
        test <@ named.Kinds |> List.map _.Audited = [ 1 ] @>
        test <@ named.Sample = None @>
        ignore edited

        let absent = ActionKey.create "check" [ "file", "nowhere" ]

        let missing =
            RecordAudit.audit
                auditedRoot
                cleanRoot
                { everyKey with
                    Keys = [ absent.Value ] }

        test <@ missing.Unmatched = [ absent.Value.Text ] @>
        test <@ missing.Verdict = RecordAudit.Verdict.Vacuous @>)

let private recordAt (key: ActionKey.ActionKey) (outcome: OutcomeStore.Outcome) (at: DateTimeOffset) =
    OutcomeStore.Record.ofAction key "subject" outcome "binary" "/worktree" at

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an older stored record that differs is a mismatch too, flagged as history`` () =
    withTempDir "audit-history" (fun dir ->
        let key = ActionKey.create "check" [ "file", "a" ]
        let audited = storeAt (Path.Combine(dir, "audited"))
        let clean = storeAt (Path.Combine(dir, "clean"))
        let t0 = DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)

        let outputs (signature: string) =
            Some $"""{{"schema":"fshw-check-outputs-v1","diagnostics":[],"fcsSignature":"%s{signature}"}}"""

        audited.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass t0, outputs "old")
        |> ignore

        audited.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass (t0.AddMinutes 1.0), outputs "new")
        |> ignore

        clean.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass t0, outputs "new")
        |> ignore

        let report = RecordAudit.audit audited.Root clean.Root everyKey

        test <@ report.Kinds |> List.map _.Records = [ 2 ] @>
        test <@ report.Mismatches |> List.map (fun m -> m.IsAnswer, m.Fields) = [ false, [ "fcsSignature" ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a kind without a comparison of its own is compared by outcome and outputs digest`` () =
    withTempDir "audit-generic" (fun dir ->
        let key = ActionKey.create "format" [ "file", "a" ]
        let audited = storeAt (Path.Combine(dir, "audited"))
        let clean = storeAt (Path.Combine(dir, "clean"))
        let t0 = DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)

        audited.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass t0, Some """{"x":1}""")
        |> ignore

        clean.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Fail t0, Some """{"x":2}""")
        |> ignore

        let report =
            RecordAudit.audit audited.Root clean.Root { everyKey with Kinds = [ "format" ] }

        test <@ report.Mismatches |> List.map _.Fields = [ [ "outcome"; "outputs" ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the audit reads the audited store without quarantining what it cannot read`` () =
    withTempDir "audit-readonly" (fun dir ->
        let key = ActionKey.create "check" [ "file", "a" ]
        let audited = storeAt (Path.Combine(dir, "audited"))
        let keyDir = audited.DirectoryOf key
        Directory.CreateDirectory keyDir |> ignore
        let torn = Path.Combine(keyDir, "torn.json")
        File.WriteAllText(torn, "{ not a record")

        let records, unreadable = OutcomeStore.readKeyAt audited.Root key.Value

        test <@ List.isEmpty records && unreadable = 1 @>
        test <@ File.Exists torn @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a store is never audited against itself`` () =
    withTempDir "audit-self" (fun dir ->
        test <@ RecordAudit.validateStores dir (dir + "/") |> Result.isError @>
        test <@ RecordAudit.validateStores dir (Path.Combine(dir, "absent")) |> Result.isError @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the JSON report carries the verdict, the exit code, the seed and every mismatch`` () =
    withTempDir "audit-json" (fun dir ->
        let auditedRoot, cleanRoot, _, _ = auditedThenClean dir plantedHash

        let report = RecordAudit.audit auditedRoot cleanRoot { everyKey with Seed = "s1" }

        use doc = JsonDocument.Parse(RecordAudit.toJson report)
        let root = doc.RootElement
        let schema = root.GetProperty("schema").GetString()
        let verdict = root.GetProperty("verdict").GetString()
        let exit = root.GetProperty("exitCode").GetInt32()
        let seed = root.GetProperty("seed").GetString()
        let mismatches = root.GetProperty("mismatches").GetArrayLength()
        let audited = root.GetProperty("auditedKeys").GetProperty("check").GetArrayLength()
        test <@ schema = RecordAudit.ReportSchema @>
        test <@ verdict = "mismatch" && exit = 1 && seed = "s1" @>
        test <@ mismatches = 1 && audited = 6 @>

        let lines = RecordAudit.render report
        test <@ lines |> List.exists (fun l -> l.Contains "MISMATCH check") @>
        test <@ (List.last lines).StartsWith "audit: MISMATCH" @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``keys with a preferred input label are audited first`` () =
    withTempDir "audit-prefer" (fun dir ->
        let audited = storeAt (Path.Combine(dir, "audited"))
        let clean = storeAt (Path.Combine(dir, "clean"))
        let t0 = DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)

        let keys =
            [ for i in 1..20 ->
                  if i % 5 = 0 then
                      ActionKey.create "check" [ "file", string i; "position", "p" ]
                  else
                      ActionKey.create "check" [ "file", string i ] ]

        for key in keys do
            for store in [ audited; clean ] do
                store.Record(recordAt key OutcomeStore.Outcome.Pass t0) |> ignore

        let report =
            RecordAudit.audit
                audited.Root
                clean.Root
                { RecordAudit.Options.defaults with
                    Sample = Some 4
                    PreferInput = Some "position" }

        let preferred =
            keys
            |> List.filter (fun k -> k.Inputs |> List.exists (fun (l, _) -> l = "position"))
            |> List.map _.Value.Text
            |> Set.ofList

        test <@ report.AuditedKeys |> List.exactlyOne |> snd |> Set.ofList = preferred @>)

let private checkOutputs (used: string) (api: string) =
    Some
        $"""{{"schema":"fshw-check-outputs-v1","diagnostics":[],"fcsSignature":"f","usedSignatures":"%s{used}","apiSignature":"%s{api}"}}"""

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a check record names each signature that moved, and outputs it cannot read`` () =
    withTempDir "audit-fields" (fun dir ->
        let audited = storeAt (Path.Combine(dir, "audited"))
        let clean = storeAt (Path.Combine(dir, "clean"))
        let t0 = DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)

        let file (name: string) (stored: string option) (recomputed: string option) =
            let key = ActionKey.create "check" [ "file", name ]

            audited.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass t0, stored)
            |> ignore

            clean.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass t0, recomputed)
            |> ignore

            key.Value.Text

        let used = file "used" (checkOutputs "u1" "a") (checkOutputs "u2" "a")
        let api = file "api" (checkOutputs "u" "a1") (checkOutputs "u" "a2")

        let unreadable =
            file "unreadable" (Some """{"schema":"other"}""") (checkOutputs "u" "a")

        let report = RecordAudit.audit audited.Root clean.Root everyKey

        let fieldsOf key =
            report.Mismatches |> List.filter (fun m -> m.Key = key) |> List.collect _.Fields

        test <@ fieldsOf used = [ "usedSignatures" ] @>
        test <@ fieldsOf api = [ "apiSignature" ] @>
        test <@ fieldsOf unreadable = [ "outputs-unreadable" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``named keys that agree, beside one that is missing, are still not agreement`` () =
    withTempDir "audit-partial-keys" (fun dir ->
        let auditedRoot, cleanRoot, _, _ = auditedThenClean dir (fun _ -> realHash)

        let shared =
            (RecordAudit.audit auditedRoot cleanRoot everyKey).AuditedKeys
            |> List.exactlyOne
            |> snd
            |> List.head

        let request =
            { RecordAudit.Request.empty with
                Clean = Some cleanRoot
                Store = Some auditedRoot
                Keys = [ shared; (ActionKey.create "check" [ "file", "gone" ]).Value.Text ] }

        let setup = RecordAudit.resolve dir request |> Result.toOption |> Option.get
        test <@ setup.Options.Keys.Length = 2 @>

        let report = RecordAudit.execute setup |> Result.toOption |> Option.get

        test <@ List.isEmpty report.Mismatches && report.Kinds |> List.map _.Audited = [ 1 ] @>
        test <@ report.Verdict = RecordAudit.Verdict.Vacuous @>

        let lines = RecordAudit.render report
        test <@ lines |> List.exists (fun l -> l.StartsWith "  not in both stores: ") @>
        test <@ (List.last lines).StartsWith "audit: VACUOUS" @>
        test <@ (RecordAudit.toJson report).Contains "\"verdict\": \"vacuous\"" @>)

// AUTOMATION-1684: a kind that compared nothing is a finding of its own, in the text and the
// JSON, not only the exit code. Its line used to read like a clean kind's ("mismatched 0").
[<Fact(Timeout = 10000)>]
let ``a kind that compared nothing is marked vacuous in the text and the JSON, beside a kind that compared`` () =
    let tally kind cleanKeys shared audited : RecordAudit.KindTally =
        { Kind = kind
          CleanKeys = cleanKeys
          Shared = shared
          Audited = audited
          Records = audited
          Mismatched = 0 }

    let report: RecordAudit.Report =
        { Seed = "s"
          Sample = Some 0
          AuditedStore = "/a"
          CleanStore = "/c"
          Kinds =
            [ tally "check" 5 5 5
              tally "options" 1 0 0
              tally "options-inputs" 0 0 0
              tally "analyze" 2 2 0 ]
          Mismatches = []
          AuditedKeys = []
          Unmatched = []
          UnreadableClean = 0
          UnreadableAudited = 0
          Quarantine = { Noted = []; Unnoted = 0 } }

    test <@ report.Verdict = RecordAudit.Verdict.Vacuous @>
    test <@ RecordAudit.exitCode report.Verdict = 3 @>

    let lines = RecordAudit.render report

    let lineOf (kind: string) =
        lines |> List.find (fun l -> l.StartsWith $"  %-20s{kind} ")

    test <@ not ((lineOf "check").Contains "VACUOUS") @>
    test <@ (lineOf "options").Contains "VACUOUS: clean keys 1, shared 0, nothing compared" @>
    test <@ (lineOf "options-inputs").Contains "VACUOUS: clean keys 0, shared 0, nothing compared" @>

    let json = JsonDocument.Parse(RecordAudit.toJson report).RootElement

    let kinds =
        json.GetProperty("kinds").EnumerateArray()
        |> Seq.map (fun k -> k.GetProperty("kind").GetString(), k.GetProperty("vacuous").GetBoolean())
        |> List.ofSeq

    let vacuousKinds =
        json.GetProperty("vacuousKinds").EnumerateArray()
        |> Seq.map (fun k -> k.GetString())
        |> List.ofSeq

    let reasons =
        json.GetProperty("kinds").EnumerateArray()
        |> Seq.filter (fun k -> k.GetProperty("vacuous").GetBoolean())
        |> Seq.map (fun k -> k.GetProperty("vacuousReason").GetString())
        |> List.ofSeq

    test <@ kinds = [ "check", false; "options", true; "options-inputs", true; "analyze", true ] @>
    test <@ vacuousKinds = [ "options"; "options-inputs"; "analyze" ] @>

    test
        <@
            reasons = [ "the audited store holds none of its clean keys"
                        "the clean store holds no key of this kind"
                        "no shared key holds a readable record" ]
        @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a report with no kind at all is vacuous`` () =
    let report: RecordAudit.Report =
        { Seed = "s"
          Sample = None
          AuditedStore = "/a"
          CleanStore = "/c"
          Kinds = []
          Mismatches = []
          AuditedKeys = []
          Unmatched = []
          UnreadableClean = 0
          UnreadableAudited = 0
          Quarantine = { Noted = []; Unnoted = 0 } }

    test <@ report.Verdict = RecordAudit.Verdict.Vacuous @>
    test <@ (List.last (RecordAudit.render report)).StartsWith "audit: VACUOUS" @>

    let positive =
        RecordAudit.resolve
            "/repo"
            { RecordAudit.Request.empty with
                Clean = Some "/c"
                Sample = Some 5 }

    test <@ positive |> Result.map _.Options.Sample = Ok(Some 5) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``runRequest prints the JSON report or the human one, writes the report, and names the unreadable`` () =
    withTempDir "audit-run" (fun dir ->
        let auditedRoot, cleanRoot, _, _ = auditedThenClean dir (fun _ -> realHash)
        // A torn file in the clean store: counted, never read as a record.
        let torn = Path.Combine(cleanRoot, "records", "00", "00" + String('0', 62))
        Directory.CreateDirectory torn |> ignore
        File.WriteAllText(Path.Combine(torn, "torn.json"), "{")

        let request =
            { RecordAudit.Request.empty with
                Clean = Some cleanRoot
                Store = Some auditedRoot
                Sample = Some 0 }

        let printed = Collections.Generic.List<string>()
        let out = Path.Combine(dir, "reports", "audit.json")

        let json = RecordAudit.runRequest dir request true (Some out) printed.Add ignore

        test <@ json = 0 @>
        test <@ printed.Count = 1 && printed[0].Contains "\"verdict\": \"agree\"" @>
        test <@ File.Exists out @>

        printed.Clear()
        let human = RecordAudit.runRequest dir request false None printed.Add ignore
        test <@ human = 0 @>

        test
            <@
                printed
                |> Seq.exists (fun l -> l.Contains "unreadable files: clean 1, audited 0")
            @>

        test <@ (Seq.last printed).StartsWith "audit: AGREE" @>
        test <@ printed |> Seq.exists (fun l -> l.Contains "sample every shared key") @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a history mismatch is rendered as history`` () =
    withTempDir "audit-history-render" (fun dir ->
        let key = ActionKey.create "check" [ "file", "a" ]
        let audited = storeAt (Path.Combine(dir, "audited"))
        let clean = storeAt (Path.Combine(dir, "clean"))
        let t0 = DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)

        audited.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass t0, checkOutputs "u" "old")
        |> ignore

        audited.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass (t0.AddMinutes 1.0), checkOutputs "u" "new")
        |> ignore

        clean.RecordWithOutputs(recordAt key OutcomeStore.Outcome.Pass t0, checkOutputs "u" "new")
        |> ignore

        let lines = RecordAudit.render (RecordAudit.audit audited.Root clean.Root everyKey)
        test <@ lines |> List.exists (fun l -> l.Contains "(history): apiSignature") @>)

// --- per-kind comparators (AUTOMATION-1633, second slice) ---

/// One key filed in both stores: `stored` by checkout `storedAt`, `clean` by `cleanAt`.
let private pair
    (dir: string)
    (kind: string)
    (stored: OutcomeStore.Record -> OutcomeStore.Record)
    (storedOutputs: string option)
    (clean: OutcomeStore.Record -> OutcomeStore.Record)
    (cleanOutputs: string option)
    =
    let key = ActionKey.create kind [ "file", "a" ]
    let audited = storeAt (Path.Combine(dir, "audited"))
    let cleanStore = storeAt (Path.Combine(dir, "clean"))
    let t0 = DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero)
    let base' = recordAt key OutcomeStore.Outcome.Pass t0
    audited.RecordWithOutputs(stored base', storedOutputs) |> ignore
    cleanStore.RecordWithOutputs(clean base', cleanOutputs) |> ignore

    RecordAudit.audit audited.Root cleanStore.Root { everyKey with Kinds = [ kind ] }
    |> fun report -> report.Mismatches |> List.collect _.Fields, report.Verdict

let private inCheckout (worktree: string) (record: OutcomeStore.Record) =
    { record with
        Producer =
            { record.Producer with
                Worktree = worktree } }

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``CONTROL testprune-analysis: a path-only difference agrees, a planted real one does not`` () =
    let analysis (path: string) =
        Some $"""{{"schema":"fshw-testprune-analysis-v1","symbols":[["%s{path}",26]]}}"""

    withTempDir "audit-tp-path" (fun dir ->
        let fields, verdict =
            pair
                dir
                "testprune-analysis"
                (inCheckout "/w/a")
                (analysis "/w/a/tests/T.fs")
                (inCheckout "/w/b")
                (analysis "/w/b/tests/T.fs")

        test <@ List.isEmpty fields && verdict = RecordAudit.Verdict.Agree @>)

    withTempDir "audit-tp-real" (fun dir ->
        let fields, _ =
            pair
                dir
                "testprune-analysis"
                (inCheckout "/w/a")
                (analysis "/w/a/tests/T.fs")
                (inCheckout "/w/b")
                (analysis "/w/b/tests/U.fs")

        test <@ fields = [ "outputs" ] @>)

    // The shape test-prune files: the path inside a string that holds JSON, so the store
    // escapes its quotes (\u0022) and only the decoded string shows the path's boundary.
    let span (path: string) =
        Some $"""{{"schema":"fshw-testprune-analysis-v1","spans":["[\"%s{path}\",26]"]}}"""

    withTempDir "audit-tp-escaped" (fun dir ->
        let fields, verdict =
            pair dir "testprune-analysis" (inCheckout "/w/a") (span "/w/a/T.fs") (inCheckout "/w/b") (span "/w/b/T.fs")

        test <@ List.isEmpty fields && verdict = RecordAudit.Verdict.Agree @>)

    // A sibling of the root is a different file, not the root: never a token.
    withTempDir "audit-tp-sibling" (fun dir ->
        let fields, _ =
            pair
                dir
                "testprune-analysis"
                (inCheckout "/w/a")
                (analysis "/w/a-old/T.fs")
                (inCheckout "/w/b")
                (analysis "/w/b-old/T.fs")

        test <@ fields = [ "outputs" ] @>)

let private optionsInputs (root: string) (reads: string list) (envNames: string list) =
    let strings (xs: string list) =
        xs |> List.map (sprintf "\"%s\"") |> String.concat ","

    Some
        $"""{{"schema":"s","optionsKey":"k","reads":[%s{strings reads}],"probes":[],"listings":[{{"directory":"%s{root}/src","kind":"files"}}],"envNames":[%s{strings envNames}]}}"""

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``CONTROL options-inputs: its sets in another order, case or checkout agree; a missing read does not`` () =
    withTempDir "audit-oi-order" (fun dir ->
        let fields, verdict =
            pair
                dir
                "options-inputs"
                (inCheckout "/w/a")
                (optionsInputs "/w/a" [ "/w/a/x.fsproj"; "/w/a/Directory.Build.props" ] [ "NoWarn"; "Configuration" ])
                (inCheckout "/w/b")
                (optionsInputs "/w/b" [ "/w/b/Directory.Build.props"; "/w/b/x.fsproj" ] [ "configuration"; "NoWarn" ])

        test <@ List.isEmpty fields && verdict = RecordAudit.Verdict.Agree @>)

    withTempDir "audit-oi-real" (fun dir ->
        let fields, _ =
            pair
                dir
                "options-inputs"
                (inCheckout "/w/a")
                (optionsInputs "/w/a" [ "/w/a/x.fsproj"; "/w/a/Directory.Build.props" ] [ "NoWarn" ])
                (inCheckout "/w/b")
                (optionsInputs "/w/b" [ "/w/b/x.fsproj" ] [ "NoWarn" ])

        test <@ fields = [ "reads" ] @>)

    withTempDir "audit-oi-unreadable" (fun dir ->
        let fields, _ =
            pair dir "options-inputs" id (Some "[1]") id (optionsInputs "/w/b" [] [])

        test <@ fields = [ "outputs-unreadable" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``CONTROL test: a duration differs and agrees; the counts differ and do not`` () =
    let counts passed : OutcomeStore.Counts option =
        Some
            { Total = 10
              Passed = passed
              Failed = 10 - passed
              Skipped = 0 }

    let run passed (ms: int64) (record: OutcomeStore.Record) =
        { record with
            Counts = counts passed
            DurationMs = Some ms }

    withTempDir "audit-test-duration" (fun dir ->
        let fields, verdict = pair dir "test" (run 10 100L) None (run 10 900L) None
        test <@ List.isEmpty fields && verdict = RecordAudit.Verdict.Agree @>)

    withTempDir "audit-test-counts" (fun dir ->
        let fields, _ = pair dir "test" (run 10 100L) None (run 9 100L) None
        test <@ fields = [ "counts" ] @>)

/// A test-evidence entry as `TestEvidenceStore` files it, under `root`.
let private fileEvidence
    (root: string)
    (key: string)
    (worktree: string)
    (runId: string)
    (passed: int)
    (tests: (string * string) list)
    =
    let ctrfTests =
        tests
        |> List.map (fun (name, status) ->
            $"""{{"name":"%s{name}","status":"%s{status}","duration":%d{name.Length * 7},"start":1,"stop":2}}""")
        |> String.concat ","

    let entry: TestEvidenceStore.Entry =
        { Key = key
          Project = "tests/T/T.fsproj"
          KeyInputs = [ "toolchain", "sha256:t" ]
          Producer =
            { Worktree = worktree
              RunId = runId
              TreeHash = "sha256:" + runId
              ProducerHash = "p"
              EarnedAt = DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero) }
          Summary =
            { Tests = tests.Length
              Passed = passed
              Failed = 0
              Skipped = tests.Length - passed
              DurationMs = int64 (runId.Length * 1000) }
          Report =
            $"""{{"reportFormat":"CTRF","reportId":"%s{runId}","timestamp":"%s{runId}","results":{{"tests":[%s{ctrfTests}]}}}}"""
          Coverage =
            Some
                { Path = $".fshw/test-runs/%s{runId}/T.coverage.cobertura.xml"
                  Content = runId } }

    let path = Path.Combine(root, key.Substring(0, 2), key + ".json")
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, TestEvidenceStore.envelope entry)

let private evidenceAudit (dir: string) (fileBoth: string -> string -> unit) =
    let auditedEvidence = Path.Combine(dir, "audited-evidence")
    let cleanEvidence = Path.Combine(dir, "clean-evidence")
    fileBoth auditedEvidence cleanEvidence

    RecordAudit.auditStores
        { Audited = Path.Combine(dir, "audited")
          Clean = Path.Combine(dir, "clean")
          AuditedEvidence = Some auditedEvidence
          CleanEvidence = Some cleanEvidence }
        { everyKey with
            Kinds = [ "test-evidence" ] }

let private evidenceKey = String('e', 64)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``CONTROL test-evidence: another run's producer, durations, report id and coverage agree; a test's status does not``
    ()
    =
    let tests = [ "A.one", "passed"; "A.two", "passed" ]

    withTempDir "audit-ev-run" (fun dir ->
        let report =
            evidenceAudit dir (fun audited clean ->
                fileEvidence audited evidenceKey "/w/a" "run-1" 2 tests
                fileEvidence clean evidenceKey "/w/b" "run-22" 2 (List.rev tests))

        test <@ report.Kinds |> List.map (fun k -> k.Shared, k.Audited) = [ 1, 1 ] @>
        test <@ report.Verdict = RecordAudit.Verdict.Agree @>)

    withTempDir "audit-ev-status" (fun dir ->
        let report =
            evidenceAudit dir (fun audited clean ->
                fileEvidence audited evidenceKey "/w/a" "run-1" 2 tests
                fileEvidence clean evidenceKey "/w/b" "run-2" 1 [ "A.one", "passed"; "A.two", "skipped" ])

        test <@ report.Mismatches |> List.collect _.Fields = [ "counts"; "tests" ] @>
        test <@ report.Verdict = RecordAudit.Verdict.Mismatch @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``test evidence is vacuous without evidence stores, and its unreadable files are counted`` () =
    withTempDir "audit-ev-none" (fun dir ->
        let report =
            RecordAudit.audit
                (Path.Combine(dir, "a"))
                (Path.Combine(dir, "c"))
                { everyKey with
                    Kinds = [ "test-evidence" ] }

        test <@ report.Verdict = RecordAudit.Verdict.Vacuous @>)

    withTempDir "audit-ev-torn" (fun dir ->
        let report =
            evidenceAudit dir (fun audited clean ->
                fileEvidence clean evidenceKey "/w/b" "run-2" 1 [ "A.one", "passed" ]
                let torn = Path.Combine(audited, "ee", evidenceKey + ".json")
                Directory.CreateDirectory(Path.GetDirectoryName torn) |> ignore
                File.WriteAllText(torn, "{")
                let cleanTorn = Path.Combine(clean, "ff", String('f', 64) + ".json")
                Directory.CreateDirectory(Path.GetDirectoryName cleanTorn) |> ignore
                File.WriteAllText(cleanTorn, "{")
                // The project index is not an entry.
                Directory.CreateDirectory(Path.Combine(clean, "projects")) |> ignore)

        test <@ report.UnreadableClean = 1 && report.UnreadableAudited = 1 @>
        test <@ report.Verdict = RecordAudit.Verdict.Vacuous @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a report with an unreadable CTRF report names it`` () =
    withTempDir "audit-ev-report" (fun dir ->
        let report =
            evidenceAudit dir (fun audited clean ->
                fileEvidence audited evidenceKey "/w/a" "run-1" 1 [ "A.one", "passed" ]
                fileEvidence clean evidenceKey "/w/b" "run-2" 1 [ "A.one", "passed" ]
                // Rewrite the clean entry's report as text no CTRF reader can parse.
                let path = Path.Combine(clean, "ee", evidenceKey + ".json")

                match TestEvidenceStore.decode (File.ReadAllText path) with
                | TestEvidenceStore.Decoded.Entry entry ->
                    File.WriteAllText(path, TestEvidenceStore.envelope { entry with Report = "{}" })
                | _ -> failwith "the entry did not read back")

        test <@ report.Mismatches |> List.collect _.Fields = [ "report-unreadable" ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a clean home names its evidence store, --store drops the box-wide one, and --kind all is every kind`` () =
    let fromHome =
        RecordAudit.resolve
            "/repo"
            { RecordAudit.Request.empty with
                CleanHome = Some "/h"
                Kinds = [ "all" ] }
        |> Result.toOption
        |> Option.get

    test <@ fromHome.CleanEvidence = Some(RecordAudit.evidenceUnderCacheHome "/h" "/repo") @>
    test <@ fromHome.AuditedEvidence = Some(TestEvidenceStore.directoryFor "/repo") @>
    test <@ fromHome.Options.Kinds = RecordAudit.AuditKind.all @>

    let named =
        RecordAudit.resolve
            "/repo"
            { RecordAudit.Request.empty with
                Clean = Some "/c"
                Store = Some "/s" }
        |> Result.toOption
        |> Option.get

    test <@ named.CleanEvidence = None && named.AuditedEvidence = None @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a CTRF report that is torn JSON has no results to compare`` () =
    test <@ RecordAudit.AuditKind.ctrfResults "{" = None @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``test evidence names a different project, and evidence stores that do not exist compare nothing`` () =
    let side (project: string) (worktree: string) =
        let entry: TestEvidenceStore.Entry =
            { Key = evidenceKey
              Project = project
              KeyInputs = []
              Producer =
                { Worktree = worktree
                  RunId = "r"
                  TreeHash = "t"
                  ProducerHash = "p"
                  EarnedAt = DateTimeOffset.UnixEpoch }
              Summary =
                { Tests = 0
                  Passed = 0
                  Failed = 0
                  Skipped = 0
                  DurationMs = 0L }
              Report = """{"results":{"tests":[]}}"""
              Coverage = None }

        RecordAudit.evidenceSide entry |> Option.get

    let fields =
        RecordAudit.AuditKind.testEvidence.Differences (side "tests/A/A.fsproj" "/w/a") (side "tests/B/B.fsproj" "/w/b")

    test <@ fields = [ "project" ] @>

    withTempDir "audit-ev-absent" (fun dir ->
        let report =
            RecordAudit.auditStores
                { Audited = Path.Combine(dir, "a")
                  Clean = Path.Combine(dir, "c")
                  AuditedEvidence = Some(Path.Combine(dir, "ae"))
                  CleanEvidence = Some(Path.Combine(dir, "ce")) }
                { everyKey with
                    Kinds = [ "test-evidence" ] }

        test <@ report.Kinds |> List.map _.CleanKeys = [ 0 ] @>
        test <@ report.Verdict = RecordAudit.Verdict.Vacuous @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``outputs are tokenized in every string, keeping nulls, numbers and booleans, and text that is not JSON`` () =
    let side (outputs: string) : RecordAudit.Side =
        { Record =
            OutcomeStore.Record.ofAction
                (ActionKey.create "k" [ "f", "a" ])
                "a"
                OutcomeStore.Outcome.Pass
                "b"
                "/w/a"
                DateTimeOffset.UnixEpoch
          Outputs = Some outputs }

    let normalized text =
        RecordAudit.AuditKind.normalizedOutputs (side text)

    test
        <@
            normalized """{"a":null,"b":[null,1,true,"/w/a/x.fs"],"c":{"d":"/w/a"}}""" = Some
                """{"a":null,"b":[null,1,true,"${repo}/x.fs"],"c":{"d":"${repo}"}}"""
        @>

    test <@ normalized "null" = Some "null" @>
    test <@ normalized "/w/a/x.fs {" = Some "${repo}/x.fs {" @>

/// AUTOMATION-1701 (ADR-072): a quarantined file stays visible. The audit reads the audited store's
/// quarantine and names every file by key, by whom (a read, or a writer of which kind) and when,
/// in the person's report and the JSON the nightly reads; and a moved file with no note still counts.
[<Fact>]
let ``the audit names every quarantined file by kind, key and when`` () =
    let at = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)

    let report: RecordAudit.Report =
        { Seed = "s"
          Sample = Some 0
          AuditedStore = "/a"
          CleanStore = "/c"
          Kinds = []
          Mismatches = []
          AuditedKeys = []
          Unmatched = []
          UnreadableClean = 0
          UnreadableAudited = 0
          Quarantine =
            { Noted =
                [ { File = "f1.json"
                    Key = "ak1:k1"
                    Mover = OutcomeStore.Mover.Writer "check"
                    Why = "could not be read: denied"
                    At = at }
                  { File = "f2.json"
                    Key = "ak1:k2"
                    Mover = OutcomeStore.Mover.Read
                    Why = "checksum mismatch"
                    At = at } ]
              Unnoted = 1 } }

    let lines = RecordAudit.render report

    test
        <@
            lines
            |> List.contains "  quarantined files in the audited store: 3 (1 without a note)"
        @>

    test
        <@
            lines
            |> List.exists (fun l ->
                l.StartsWith "  QUARANTINED ak1:k1 f1.json by writer of check at 2026-11-14T12:00:00")
        @>

    test
        <@
            lines
            |> List.exists (fun l -> l.StartsWith "  QUARANTINED ak1:k2 f2.json by read at")
        @>

    let json = RecordAudit.toJson report
    test <@ json.Contains "\"quarantinedUnnoted\": 1" @>
    test <@ json.Contains "\"kind\": \"check\"" && json.Contains "\"by\": \"writer\"" @>
