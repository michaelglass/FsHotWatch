/// AUTOMATION-1587 step 2: the shadow `test-baseline` record. "Every runnable project passed
/// full-scope on tree T", filed box-wide under T's receipt identity with the manifest a
/// neighbour tree could diff against, and named by ONE per-repository index record that GC
/// keeps alive. Their one reader (`checkoutRecordFor`) lets a restarted daemon diff from it;
/// these tests pin what is written, when nothing is, that the index stays one bounded
/// record, and what `checkoutRecordFor` accepts.
module FsHotWatch.Tests.TestBaselineTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero)

let private storeIn (dir: string) =
    OutcomeStore.Store(
        { Root = dir
          Ttl = TimeSpan.FromDays 14.0
          MaxBytes = 1_000_000_000L
          Log = ignore }
    )

let private producerAt (at: DateTimeOffset) : TestBaseline.Producer =
    { Binary = "fshw-test"
      Worktree = "/ws/a"
      Now = at }

let private projects = Set.ofList [ "TA"; "TB" ]

/// The K_P full-run key of each project at the tree, as the evidence wiring names it.
let private fullKeyOf (project: string) =
    TestEvidenceRun.testActionKey
        { Value = "kp-" + project
          Labels = [ "closure", "c-" + project ] }
        "full"

let private fullKeys () =
    projects |> Seq.map (fun p -> p, Ok (fullKeyOf p).Value.Text) |> Map.ofSeq

/// File `project`'s full-scope `outcome` under `key`, as run `runId` in `worktree` would have.
let private fileFullUnder
    (key: ActionKey.ActionKey)
    (worktree: string)
    (at: DateTimeOffset)
    (store: OutcomeStore.Store)
    (runId: string)
    (outcome: OutcomeStore.Outcome)
    (project: string)
    =
    let record: OutcomeStore.Record =
        { Key = key
          Subject = project
          Outcome = outcome
          Counts = None
          DurationMs = None
          Producer =
            { Binary = "fshw-test"
              Worktree = worktree
              RunId = Some runId }
          RecordedAt = at }

    test <@ store.Record record @>

/// File `project`'s full-scope `outcome` at its K_P, as run `runId` would have.
let private fileFullAt (at: DateTimeOffset) (store: OutcomeStore.Store) (runId: string) outcome project =
    fileFullUnder (fullKeyOf project) "/ws/a" at store runId outcome project

let private fileFull = fileFullAt t0

/// File `project`'s full-scope `outcome` at its K_P as another checkout's run would have:
/// the pass a run here replays (AUTOMATION-1682).
let private fileFullElsewhere (store: OutcomeStore.Store) (runId: string) outcome project =
    fileFullUnder (fullKeyOf project) "/ws/other" t0 store runId outcome project

let private tree =
    "sha256:tree-1",
    [ "src/A/A.fs", "sha256:a"
      "src/A/A.fsproj", "sha256:ap"
      "tests/TA/T.fs", "sha256:t" ]

let private fileKeys () =
    Map.ofList [ "src/A/A.fs", "id-a"; "tests/TA/T.fs", "id-t" ]

let private facts: TestBaseline.Facts =
    { RunId = "run-1"
      EarnsBaseline = true
      RunnableProjects = projects
      SealedTree = Some(fst tree)
      ReceiptTree = Some tree }

[<Fact>]
let ``an all-pass full run over a sealed tree files one baseline and names it in the index`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir
        projects |> Set.iter (fileFull store "run-1" OutcomeStore.Outcome.Pass)

        let written = TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts

        match written with
        | Error refusals -> failwith $"refused: %A{refusals}"
        | Ok w ->
            test <@ w.Tree = fst tree && w.Projects = 2 && w.Files = 3 && w.Identified = 2 @>
            test <@ w.Bytes > 0L && w.IndexEntries = 1 @>

        let key = TestBaseline.keyOf (fst tree)
        test <@ store.History key |> List.length = 1 @>

        let record, outputs = (store.LookupWithOutputs key).Value

        test
            <@
                record.Outcome = OutcomeStore.Outcome.Pass
                && record.Producer.RunId = Some "run-1"
            @>

        use doc = JsonDocument.Parse(outputs.Value)
        let o = doc.RootElement
        let field (name: string) = o.GetProperty(name).GetString()
        let projectKey = o.GetProperty("projects").GetProperty("TA").GetString()
        test <@ field "schema" = TestBaseline.Schema @>
        test <@ field "runId" = "run-1" @>
        test <@ field "tree" = fst tree @>
        test <@ field "analyzer" = AnalysisRecords.analyzerIdentity @>
        test <@ projectKey = (fullKeyOf "TA").Value.Text @>

        let manifest =
            [ for row in o.GetProperty("manifest").EnumerateArray() ->
                  row[0].GetString(),
                  row[1].GetString(),
                  (if row[2].ValueKind = JsonValueKind.Null then
                       None
                   else
                       Some(row[2].GetString())) ]

        test
            <@
                manifest = [ "src/A/A.fs", "sha256:a", Some "id-a"
                             "src/A/A.fsproj", "sha256:ap", None
                             "tests/TA/T.fs", "sha256:t", Some "id-t" ]
            @>

        test
            <@
                TestBaseline.readIndex store |> List.map (fun e -> e.Tree, e.Baseline, e.RunId) = [ fst tree,
                                                                                                    key.Value.Text,
                                                                                                    "run-1" ]
            @>)

[<Fact>]
let ``a run that does not earn the baseline files nothing and says so`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir
        projects |> Set.iter (fileFull store "run-1" OutcomeStore.Outcome.Pass)

        let refused =
            TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) { facts with EarnsBaseline = false }

        test <@ refused = Error [ TestBaseline.Refusal.NotEarned ] @>
        test <@ store.Lookup(TestBaseline.keyOf (fst tree)) = None @>
        test <@ List.isEmpty (TestBaseline.readIndex store) @>
        let line = TestBaseline.describe "run-1" 3L refused
        test <@ line.StartsWith "test-baseline: not written run=run-1" @>
        test <@ line.Contains "reason=not-earned" @>)

[<Fact>]
let ``an index not sealed at the receipt tree files nothing and names both trees`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir
        projects |> Set.iter (fileFull store "run-1" OutcomeStore.Outcome.Pass)

        let moved =
            TestBaseline.file
                (Some store)
                fullKeys
                fileKeys
                (producerAt t0)
                { facts with
                    SealedTree = Some "sha256:tree-0" }

        let unsealed =
            TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) { facts with SealedTree = None }

        test <@ moved = Error [ TestBaseline.Refusal.NotSealed(Some "sha256:tree-0", fst tree) ] @>
        test <@ unsealed = Error [ TestBaseline.Refusal.NotSealed(None, fst tree) ] @>
        test <@ store.Lookup(TestBaseline.keyOf (fst tree)) = None @>

        let line = TestBaseline.describe "run-1" 3L moved
        test <@ line.Contains "reason=not-sealed" @>
        test <@ line.Contains "sealed=sha256:tree-0" && line.Contains $"receipt=%s{fst tree}" @>
        test <@ (TestBaseline.describe "run-1" 3L unsealed).Contains "sealed=none" @>)

[<Fact>]
let ``a project whose full pass is a fail, or is missing, files nothing and is named`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir
        fileFull store "run-1" OutcomeStore.Outcome.Pass "TA"

        let earlier = TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts

        match earlier with
        | Error [ TestBaseline.Refusal.LacksFullPass("TB", whyB) ] -> test <@ whyB.Contains "no full-scope record" @>
        | other -> failwith $"unexpected: %A{other}"

        fileFull store "run-1" OutcomeStore.Outcome.Fail "TB"

        match TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts with
        | Error refusals ->
            test
                <@
                    refusals
                    |> List.exists (function
                        | TestBaseline.Refusal.LacksFullPass("TB", why) -> why.Contains "fail"
                        | _ -> false)
                @>
        | Ok _ -> failwith "filed a baseline over a failed project"

        // A project the model does not name has no entry at all; one whose key failed names why.
        let noKey () =
            Map.ofList [ "TA", Ok (fullKeyOf "TA").Value.Text ]

        let keyFailed () =
            Map.ofList
                [ "TA", Ok (fullKeyOf "TA").Value.Text
                  "TB", Error "project options unknown for tests/TB/TB.fsproj" ]

        match TestBaseline.file (Some store) keyFailed fileKeys (producerAt t0) facts with
        | Error refusals ->
            test
                <@
                    refusals
                    |> List.exists (function
                        | TestBaseline.Refusal.LacksFullPass("TB", why) ->
                            why.Contains "project options unknown for tests/TB/TB.fsproj"
                        | _ -> false)
                @>
        | Ok _ -> failwith "filed a baseline over a project whose key failed"

        match TestBaseline.file (Some store) noKey fileKeys (producerAt t0) facts with
        | Error refusals ->
            test
                <@
                    refusals
                    |> List.exists (function
                        | TestBaseline.Refusal.LacksFullPass("TB", why) -> why.Contains "not in the project model"
                        | _ -> false)
                @>
        | Ok _ -> failwith "filed a baseline over an unkeyed project"

        test <@ store.Lookup(TestBaseline.keyOf (fst tree)) = None @>
        let line = TestBaseline.describe "run-1" 3L earlier

        test <@ line.Contains "reason=lacks-full-pass" && line.Contains "TB" @>)

// ---------------------------------------------------------------------------
// AUTOMATION-1682: a run that REPLAYS a project's full pass from another checkout's run
// filed it at the project's K_P over the inputs as they are now. That record says what a
// pass of this run there would say, so it is a full pass for the baseline. A record at
// another K_P, or a newer fail at this one, is not.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a full pass replayed at the project's current K_P from another checkout's run files the baseline`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir
        fileFullElsewhere store "run-0" OutcomeStore.Outcome.Pass "TA"
        fileFull store "run-1" OutcomeStore.Outcome.Pass "TB"

        let written = TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts

        match written with
        | Ok w ->
            test <@ w.Tree = fst tree && w.Projects = 2 @>

            let expected: TestBaseline.ReplayedFrom =
                { RunId = "run-0"
                  Worktree = "/ws/other" }

            test <@ w.Replayed = Map.ofList [ "TA", expected ] @>
        | Error refusals -> failwith $"refused a baseline every project passed at its K_P: %A{refusals}"

        let line = TestBaseline.describe "run-1" 3L written
        test <@ line.Contains "test-baseline: written" && line.Contains "replayed=1" @>

        let _, outputs = (store.LookupWithOutputs(TestBaseline.keyOf (fst tree))).Value
        use doc = JsonDocument.Parse(outputs.Value)
        let replayed = doc.RootElement.GetProperty("replayed").GetProperty("TA")
        let runId = replayed.GetProperty("runId").GetString()
        let worktree = replayed.GetProperty("worktree").GetString()
        test <@ runId = "run-0" && worktree = "/ws/other" @>)

[<Fact>]
let ``negative control: a replayed pass at another K_P, under a newer fail, or naming no run, files nothing`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir
        // TA passed elsewhere over OTHER inputs (another tree): its key is not TA's K_P now.
        let elsewhere =
            TestEvidenceRun.testActionKey
                { Value = "kp-TA-other-tree"
                  Labels = [ "closure", "c-TA-other" ] }
                "full"

        fileFullUnder elsewhere "/ws/other" t0 store "run-0" OutcomeStore.Outcome.Pass "TA"
        fileFull store "run-1" OutcomeStore.Outcome.Pass "TB"

        match TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts with
        | Error [ TestBaseline.Refusal.LacksFullPass("TA", why) ] -> test <@ why.Contains "no full-scope record" @>
        | other -> failwith $"filed or refused wrongly over a pass at another K_P: %A{other}"

        // A pass at TA's K_P from elsewhere, and then a newer fail there from elsewhere.
        fileFullElsewhere store "run-0" OutcomeStore.Outcome.Pass "TA"

        fileFullUnder (fullKeyOf "TA") "/ws/other" (t0.AddSeconds 1.0) store "run-2" OutcomeStore.Outcome.Fail "TA"

        match TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts with
        | Error [ TestBaseline.Refusal.LacksFullPass("TA", why) ] -> test <@ why.Contains "fail" @>
        | other -> failwith $"filed or refused wrongly under a newer fail: %A{other}"

        // A newer pass there that names no run is no run's pass: refused, not replayed.
        let unnamed: OutcomeStore.Record =
            { Key = fullKeyOf "TA"
              Subject = "TA"
              Outcome = OutcomeStore.Outcome.Pass
              Counts = None
              DurationMs = None
              Producer =
                { Binary = "fshw-test"
                  Worktree = "/ws/other"
                  RunId = None }
              RecordedAt = t0.AddSeconds 1.5 }

        test <@ store.Record unnamed @>

        match TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts with
        | Error [ TestBaseline.Refusal.LacksFullPass("TA", why) ] -> test <@ why.Contains "names no run" @>
        | other -> failwith $"filed or refused wrongly over a pass naming no run: %A{other}"

        // Nor does a run that did not earn the baseline file one from replays.
        fileFullUnder (fullKeyOf "TA") "/ws/other" (t0.AddSeconds 2.0) store "run-3" OutcomeStore.Outcome.Pass "TA"

        test
            <@
                TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) { facts with EarnsBaseline = false } = Error
                    [ TestBaseline.Refusal.NotEarned ]
            @>

        test <@ store.Lookup(TestBaseline.keyOf (fst tree)) = None @>)

[<Fact>]
let ``with no store or no receipt tree nothing is filed, and the reason is named`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir

        test
            <@ TestBaseline.file None fullKeys fileKeys (producerAt t0) facts = Error [ TestBaseline.Refusal.NoStore ] @>

        test
            <@
                TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) { facts with ReceiptTree = None } = Error
                    [ TestBaseline.Refusal.NoReceiptTree ]
            @>

        test <@ (TestBaseline.describe "r" 0L (Error [ TestBaseline.Refusal.NoStore ])).Contains "reason=no-store" @>)

[<Fact>]
let ``a written baseline's log line carries its byte size and the index size, countably`` () =
    let written: TestBaseline.Written =
        { Key = (TestBaseline.keyOf "sha256:t").Value
          Tree = "sha256:t"
          Projects = 7
          Files = 2781
          Identified = 2700
          Bytes = 612345L
          IndexEntries = 20
          IndexBytes = 4096L
          Replayed = Map.empty }

    let line = TestBaseline.describe "run-9" 42L (Ok written)
    test <@ line.StartsWith "test-baseline: written run=run-9 tree=sha256:t" @>

    test
        <@
            [ "projects=7"
              "files=2781"
              "identities=2700"
              "bytes=612345"
              "index=20/20"
              "index-bytes=4096"
              "replayed=0"
              "ms=42" ]
            |> List.forall line.Contains
        @>

[<Fact>]
let ``the index keeps the newest twenty, newest first, as one record that names them all`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir

        for i in 1..25 do
            let tree = $"sha256:tree-%d{i}"

            let entry: TestBaseline.IndexEntry =
                { Tree = tree
                  Baseline = (TestBaseline.keyOf tree).Value.Text
                  RunId = $"run-%d{i}"
                  At = t0.AddMinutes(float i) }

            match TestBaseline.appendIndex store (producerAt entry.At) entry with
            | Some(entries, bytes) -> test <@ entries = min i 20 && bytes > 0L @>
            | None -> failwith "the index was not filed"

        let entries = TestBaseline.readIndex store
        test <@ entries |> List.map _.RunId = [ for i in 25..-1..6 -> $"run-%d{i}" ] @>
        // ONE record under the key: no history grows under it.
        test <@ store.History TestBaseline.indexKey |> List.length = 1 @>

        let _, outputs = (store.LookupWithOutputs TestBaseline.indexKey).Value
        let keeps = OutcomeStore.keepsOf outputs |> List.map _.Text |> Set.ofList
        test <@ keeps = (entries |> List.map _.Baseline |> Set.ofList) @>

        // Re-baselining a tree already named moves it to the front instead of naming it twice.
        let again: TestBaseline.IndexEntry =
            { entries[5] with
                RunId = "run-again"
                At = t0.AddHours 1.0 }

        TestBaseline.appendIndex store (producerAt again.At) again |> ignore
        let after = TestBaseline.readIndex store
        test <@ after.Length = 20 && (List.head after).RunId = "run-again" @>
        test <@ after |> List.filter (fun e -> e.Tree = again.Tree) |> List.length = 1 @>)

[<Fact>]
let ``gc keeps a baseline the live index names and may evict one that fell off it`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir
        projects |> Set.iter (fileFull store "run-0" OutcomeStore.Outcome.Pass)

        // 21 baselines filed at t0: the first falls off the index of twenty.
        let trees = [ for i in 0..20 -> $"sha256:tree-%d{i}" ]

        for i, treeId in List.indexed trees do
            let runId = $"run-%d{i}"

            projects
            |> Set.iter (fileFullAt (t0.AddSeconds(float (i + 1))) store runId OutcomeStore.Outcome.Pass)

            let result =
                TestBaseline.file
                    (Some store)
                    fullKeys
                    fileKeys
                    (producerAt t0)
                    { facts with
                        RunId = runId
                        SealedTree = Some treeId
                        ReceiptTree = Some(treeId, snd tree) }

            test <@ Result.isOk result @>

        // The index is rewritten later than every baseline it names, so it is live at the GC.
        let last: TestBaseline.IndexEntry = (TestBaseline.readIndex store).Head
        TestBaseline.appendIndex store (producerAt (t0.AddDays 10.0)) last |> ignore

        let evicted = store.Gc(t0.AddDays 15.0, Set.empty) |> Set.ofList
        let fellOff = TestBaseline.keyOf (List.head trees)
        test <@ evicted.Contains fellOff.Value @>
        test <@ store.Lookup fellOff = None @>

        for tree in List.tail trees do
            test <@ store.Lookup(TestBaseline.keyOf tree) <> None @>

        test <@ store.Lookup TestBaseline.indexKey <> None @>)

[<Fact>]
let ``an index of another schema, or with entries missing a field or a readable time, reads as what it can vouch for``
    ()
    =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir

        let fileIndex (outputs: string) =
            let record =
                OutcomeStore.Record.ofAction TestBaseline.indexKey "index" OutcomeStore.Outcome.Pass "fshw" "ws" t0

            store.Supersede(record, Some outputs) |> ignore

        fileIndex """{"schema":"fshw-test-baseline-index-v0","entries":[]}"""
        test <@ List.isEmpty (TestBaseline.readIndex store) @>
        fileIndex $"""{{"schema":"%s{TestBaseline.IndexSchema}","entries":{{}}}}"""
        test <@ List.isEmpty (TestBaseline.readIndex store) @>

        fileIndex
            $"""{{"schema":"%s{TestBaseline.IndexSchema}","entries":[
                  {{"tree":"t1","baseline":"b1","runId":"r1","at":"not a time"}},
                  {{"tree":"t2","baseline":"b2","at":"2026-11-12T12:00:00+00:00"}},
                  {{"tree":"t3","baseline":"b3","runId":"r3","at":"2026-11-12T12:00:00+00:00"}}]}}"""

        test <@ TestBaseline.readIndex store |> List.map _.Tree = [ "t3" ] @>)

[<Fact>]
let ``a store that cannot file the baseline refuses it as not-filed, and the line says so`` () =
    withTempDir "baseline" (fun dir ->
        // Records land under a regular file, so no directory can be made for them.
        let blocker = Path.Combine(dir, "blocker")
        File.WriteAllText(blocker, "")
        let readable = storeIn (Path.Combine(dir, "store"))
        projects |> Set.iter (fileFull readable "run-1" OutcomeStore.Outcome.Pass)

        // A store under a regular file cannot file anything: Supersede says so and deletes nothing.
        let unwritable =
            OutcomeStore.Store(
                { Root = Path.Combine(blocker, "store")
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 1_000_000_000L
                  Log = ignore }
            )

        test
            <@
                unwritable.Supersede(
                    OutcomeStore.Record.ofAction TestBaseline.indexKey "i" OutcomeStore.Outcome.Pass "f" "w" t0,
                    None
                ) = None
            @>

        // A store that reads the passes but cannot write the baseline's key directory is the case
        // `not-filed` names.
        let keyDir = readable.DirectoryOf(TestBaseline.keyOf (fst tree))
        Directory.CreateDirectory keyDir |> ignore
        let writable = File.GetUnixFileMode keyDir
        File.SetUnixFileMode(keyDir, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        try
            let refused =
                TestBaseline.file (Some readable) fullKeys fileKeys (producerAt t0) facts

            match refused with
            | Error [ TestBaseline.Refusal.NotFiled _ ] -> ()
            | other -> failwith $"unexpected: %A{other}"

            test <@ List.isEmpty (TestBaseline.readIndex readable) @>
            test <@ (TestBaseline.describe "run-1" 1L refused).Contains "reason=not-filed" @>

            test
                <@
                    (TestBaseline.describe "run-1" 1L (Error [ TestBaseline.Refusal.NoReceiptTree ])).Contains
                        "reason=no-receipt-tree"
                @>
        finally
            File.SetUnixFileMode(keyDir, writable))

[<Fact>]
let ``with no evidence store or no identities every configured project is refused, naming which`` () =
    let configs =
        Some
            [ FsHotWatch.Tests.TestPrunePluginTestSupport.testConfigNamed "TA"
              FsHotWatch.Tests.TestPrunePluginTestSupport.testConfigNamed "TB" ]

    let noStore =
        PluginSteps.currentOutcomeKeyResultsIn None "/repo" configs FsHotWatch.PluginFramework.ProjectGraphAccessor.none

    test
        <@
            noStore = Map.ofList
                [ "TA", Error "no test-evidence store is wired"
                  "TB", Error "no test-evidence store is wired" ]
        @>

    withTempDir "baseline" (fun dir ->
        let wiring: TestEvidenceRun.Wiring =
            { Store =
                TestEvidenceStore.Store(
                    { Root = Path.Combine(dir, "evidence")
                      Ttl = TimeSpan.FromHours 1.0
                      MaxBytes = 1_000_000L
                      Now = fun () -> t0
                      Log = ignore }
                )
              Configs = Map.empty
              Toolchain = lazy "tc"
              Loaded = TestEvidenceKey.LoadedEnvironment.empty
              ProducerHash = "p"
              ExcludePatterns = []
              Outcomes = Some(storeIn dir) }

        let noIdentities =
            PluginSteps.currentOutcomeKeyResultsIn
                (Some wiring)
                dir
                configs
                FsHotWatch.PluginFramework.ProjectGraphAccessor.none

        test
            <@
                noIdentities
                |> Map.forall (fun _ r -> r = Error "the project model publishes no compile-input identities")
                && noIdentities.Count = 2
            @>)

// ---------------------------------------------------------------------------
// `checkoutRecordFor` (AUTOMATION-1587): the one reader. A restarted daemon diffs from its
// checkout's own last full pass only through a passing record for exactly that tree, filed
// by this analyser build, whose manifest it can read. Everything else is a miss.
// ---------------------------------------------------------------------------

[<Fact>]
let ``checkoutRecordFor reads T's run and manifest, and misses every other case`` () =
    withTempDir "baseline" (fun dir ->
        let store = storeIn dir

        test <@ TestBaseline.checkoutRecordFor store (fst tree) = Error "no test-baseline record for that tree" @>

        projects |> Set.iter (fileFull store "run-1" OutcomeStore.Outcome.Pass)

        match TestBaseline.file (Some store) fullKeys fileKeys (producerAt t0) facts with
        | Ok _ -> ()
        | Error refusals -> failwith $"refused: %A{refusals}"

        let key = TestBaseline.keyOf (fst tree)

        match TestBaseline.checkoutRecordFor store (fst tree) with
        | Ok record ->
            test
                <@
                    record.Tree = fst tree
                    && record.Key = key.Value.Text
                    && record.RunId = "run-1"
                    && record.Entries = snd tree
                @>
        | Error why -> failwith $"expected a hit: %s{why}"

        // Another tree is a miss: the lookup is by exact identity.
        test <@ TestBaseline.checkoutRecordFor store "sha256:tree-2" |> Result.isError @>

        let refile (outputs: string option) (outcome: OutcomeStore.Outcome) =
            store.Supersede(OutcomeStore.Record.ofAction key (fst tree) outcome "fshw-test" "/ws/a" t0, outputs)
            |> ignore

        let missWith (outputs: string) =
            refile (Some outputs) OutcomeStore.Outcome.Pass
            TestBaseline.checkoutRecordFor store (fst tree)

        let valid (fields: string) =
            "{\"schema\":\""
            + TestBaseline.Schema
            + "\",\"tree\":\""
            + fst tree
            + "\",\"runId\":\"r\",\"analyzer\":\""
            + AnalysisRecords.analyzerIdentity
            + "\""
            + fields
            + "}"

        test <@ missWith (valid ",\"manifest\":[[\"a\",\"h\",null]]") |> Result.isOk @>
        test <@ missWith (valid ",\"manifest\":[[\"a\"]]") = Error "the record's manifest has a malformed row" @>
        test <@ missWith (valid ",\"manifest\":[[1,2]]") = Error "the record's manifest has a malformed row" @>
        test <@ missWith (valid ",\"manifest\":[\"a\"]") = Error "the record's manifest has a malformed row" @>
        test <@ missWith (valid "") = Error "the record has no run or no manifest" @>
        test <@ missWith (valid ",\"manifest\":{}") = Error "the record has no run or no manifest" @>

        test
            <@
                missWith """{"schema":"other","manifest":[]}""" = Error
                    $"the record is of schema other, not %s{TestBaseline.Schema}"
            @>

        test
            <@
                missWith $"""{{"schema":"%s{TestBaseline.Schema}","tree":"elsewhere","runId":"r","manifest":[]}}""" = Error
                    "the record names tree elsewhere"
            @>

        test
            <@
                missWith
                    $"""{{"schema":"%s{TestBaseline.Schema}","tree":"%s{fst tree}","runId":"r","analyzer":"old","manifest":[]}}""" = Error
                    "the record was filed by a different analyser build"
            @>

        test
            <@
                missWith $"""{{"schema":"%s{TestBaseline.Schema}","tree":"%s{fst tree}","runId":"r","manifest":[]}}""" = Error
                    "the record was filed by a different analyser build"
            @>

        refile None OutcomeStore.Outcome.Pass

        test
            <@
                TestBaseline.checkoutRecordFor store (fst tree) = Error
                    "the test-baseline record for that tree has no outputs"
            @>

        refile None OutcomeStore.Outcome.Fail

        test
            <@
                TestBaseline.checkoutRecordFor store (fst tree) = Error
                    "the test-baseline record for that tree is not a pass"
            @>)
