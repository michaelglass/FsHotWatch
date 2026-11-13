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

            CheckRecords.file store (Some repoRoot) toolchain projectOptions.ProjectFileName file key outputs
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
          UnreadableAudited = 0 }

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
