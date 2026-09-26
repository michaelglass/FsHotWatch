module TestPrune.Trace.Tests.TraceIngestTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.TraceIngest

let private sym name file line hash =
    { FullName = name
      Kind = Function
      SourceFile = file
      LineStart = line
      LineEnd = line
      ContentHash = hash
      IsExtern = false }

let private row id kind typ mem doc line =
    { Id = id
      Kind = kind
      Assembly = "A"
      TypeName = typ
      Member = mem
      Document = doc
      FirstLine = line
      LastLine = line }

/// A miniature world: a repo dir with source files, an index holding their symbols, a
/// manifest whose rows point at them, and dumps written by a real RecorderState.
///
/// Probe ids: 0 = N.M.f, 1 = N.M.g (both src/M.fs), 2 = a generated member with no
/// document and no symbol (unmapped), 3 = a closure constructor (dropped), 4 = code in
/// src/Plain.fs, which exists but is not indexed (a file-level entry).
type World() =
    let root = Directory.CreateTempSubdirectory().FullName
    let src = Path.Combine(root, "src", "M.fs")
    let plain = Path.Combine(root, "src", "Plain.fs")
    do Directory.CreateDirectory(Path.GetDirectoryName src) |> ignore
    do File.WriteAllText(src, "module N.M\nlet f () = 1\nlet g () = 2\n")
    do File.WriteAllText(plain, "module N.Plain\nlet p () = 0\n")
    let db = Database.create (Path.Combine(root, "index.db"))

    do
        db.RebuildProjects(
            [ { Symbols = [ sym "N.M.f" "src/M.fs" 2 "hf"; sym "N.M.g" "src/M.fs" 3 "hg" ]
                Dependencies = []
                TestMethods = []
                Attributes = []
                ParentLinks = []
                Diagnostics = AnalysisDiagnostics.Zero } ],
            fileKeys = [ "src/M.fs", "k" ]
        )

    member _.Root = root
    member _.Src = src
    member _.Plain = plain
    member _.Store = TestPrune.Ports.toSymbolStore db
    member val DumpDir = Path.Combine(root, "dumps")
    member val TraceDb = Path.Combine(root, "traces.db")

    member val Manifest =
        { Rows =
            [| row 0 UserMethod "N.M" "f" (Some src) 2
               row 1 UserMethod "N.M" "g" (Some src) 3
               row 2 GeneratedMethod "Q.Nowhere" "x" None 0
               row 3 GeneratedMethod "N.M+f@2" ".ctor" None 0
               row 4 UserMethod "N.Plain" "p" (Some plain) 2 |]
          Documents =
            Map.ofList
                [ src, Fingerprint.hashFile root "src/M.fs"
                  plain, Fingerprint.hashFile root "src/Plain.fs" ]
          IdCount = 5 } with get, set

    member this.Shadow: ShadowBin.Shadow =
        { Dir = root
          Apphost = ""
          ManifestDir = ""
          Manifest = this.Manifest
          WeaveKey = "k"
          Reused = false
          OriginalDepsJsonSha256 = "d"
          Verify =
            { Prepared = 0
              Invalid = []
              SkippedGeneric = 0
              Other = 0 } }

    /// Record one process: `f` drives a RecorderState, then it is dumped as pid `pid`.
    /// The recorder's counters are process-wide (other tests in this process hit and
    /// overflow too), so the dump's counters are replaced: `test` = 1, `overflow` as given.
    member this.ProcessWith(idCount: int, pid: int, parent: string, overflow: int64, f: RecorderState -> unit) =
        let st = RecorderState(idCount, None, parent)
        st.RepoRoot <- root
        f st
        Directory.CreateDirectory this.DumpDir |> ignore
        let path = Path.Combine(this.DumpDir, $"trace-%d{pid}.ndjson")
        DumpWriter.write path st
        let lines = File.ReadAllLines path
        let header = Nodes.JsonNode.Parse(lines.[0])
        // The recorder stamps this test process's own pid; a child is matched by the one given.
        header.["pid"] <- Nodes.JsonValue.Create pid

        header.["counters"] <-
            Nodes.JsonNode.Parse(
                $"""{{"test":1,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":%d{overflow}}}"""
            )

        lines.[0] <- header.ToJsonString()
        File.WriteAllLines(path, lines)

    member this.Process(pid: int, parent: string, f: RecorderState -> unit) =
        this.ProcessWith(this.Manifest.IdCount, pid, parent, 0L, f)

    member this.Request(outcomes: TestOutcome list) : IngestRequest =
        { RunId = "run1"
          TestProject = "P"
          RepoRoot = root
          InputRoot = root
          Kind = TraceStore.FullRun
          LaunchTreeHash = "t"
          CurrentTreeHash = "t"
          DumpDir = this.DumpDir
          Shadow = this.Shadow
          Outcomes = outcomes
          Symbols = this.Store
          FingerprintFiles = []
          FingerprintEnv = []
          RecordedAt = DateTimeOffset.UtcNow }

/// Put a test identity on a scope the way the xUnit source would.
let private asTest (st: RecorderState) (key: string) (cls: string) (meth: string) (display: string) =
    st.EnterScope key
    let s = st.CurrentScope()
    s.TestClass <- cls
    s.TestMethod <- meth
    s.TestDisplay <- display
    s.Parents <- [| "C:" + cls; "A:assembly" |]

let private passed name = { Name = name; Outcome = Passed }

let private read (store: TraceStore.Store) (s: IngestSummary) (cls: string) (meth: string) =
    store.TryRead(testKey "P" cls meth, s.EnvFingerprint.Value) |> Option.get

let private stats (store: TraceStore.Store) =
    JsonDocument.Parse((store.Runs "P" |> List.head).StatsJson).RootElement

[<Fact>]
let ``a passing test is stored complete with its symbols at their current hashes`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.Hit 0
    )

    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    test <@ (s.Executed, s.Traced, s.Complete, s.Status, s.Reason) = (1, 1, 1, TraceStore.Recorded, "") @>
    let t = read store s "N.Tests" "t"
    test <@ t.Complete && t.Symbols = set [ "N.M.f", "hf" ] @>
    let run = store.Runs "P" |> List.head
    test <@ run.Status = TraceStore.Recorded && run.EnvFingerprint = s.EnvFingerprint.Value @>
    let st = stats store

    let values =
        st.GetProperty("executed").GetInt32(),
        st.GetProperty("traced").GetInt32(),
        st.GetProperty("complete").GetInt32(),
        st.GetProperty("counters").GetProperty("Test").GetInt64()

    test <@ values = (1, 1, 1, 1L) @>

[<Fact>]
let ``theory rows union, the worst outcome wins, and a class scope is inherited`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            st.EnterScope "C:N.Tests"
            st.Hit 1
            asTest st "T:row1" "N.Tests" "th" "N.Tests.th(x: 1)"
            st.Hit 0
            asTest st "T:row2" "N.Tests" "th" "N.Tests.th(x: 2)"
    )

    use store = TraceStore.Store.Open w.TraceDb

    let s =
        ingest
            store
            (w.Request
                [ passed "N.Tests.th(x: 1)"
                  { Name = "N.Tests.th(x: 2)"
                    Outcome = Failed } ])

    let t = read store s "N.Tests" "th"
    test <@ t.Symbols = set [ "N.M.f", "hf"; "N.M.g", "hg" ] @>
    test <@ t.Reasons = [ "not-passed:failed" ] @>
    test <@ s.ReasonCounts = Map.ofList [ "not-passed", 1 ] @>

[<Fact>]
let ``outcomes are matched by display name, else by class and method with nested classes dotted`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Outer+Tests" "a" "renamed a"
            asTest st "T:2" "N.Outer+Tests" "b" "renamed b"
            asTest st "T:3" "N.Outer+Tests" "c" "renamed c"
            asTest st "T:4" "N.Outer+Tests" "d" "renamed d"
    )

    use store = TraceStore.Store.Open w.TraceDb

    let s =
        ingest
            store
            (w.Request
                [ { Name = "N.Outer.Tests.a(1)"
                    Outcome = Skipped }
                  { Name = "N.Outer.Tests.a(2)"
                    Outcome = OtherOutcome }
                  { Name = "N.Outer.Tests.b"
                    Outcome = Passed }
                  { Name = "N.Outer.Tests.b(1)"
                    Outcome = Skipped }
                  passed "renamed c"
                  passed "N.Outer.Tests.c"
                  passed "N.Outer.Tests.cc"
                  passed "unrelated" ])

    test <@ (read store s "N.Outer+Tests" "a").Reasons = [ "not-passed:other" ] @>
    test <@ (read store s "N.Outer+Tests" "b").Reasons = [ "not-passed:skipped" ] @>
    // An exact display match wins over the class-and-method fallback.
    test <@ (read store s "N.Outer+Tests" "c").Complete @>
    test <@ (read store s "N.Outer+Tests" "d").Reasons = [ "no-outcome" ] @>
    // Skipped rows never ran; "N.Outer.Tests.c" and "cc" match no scope.
    test <@ s.Executed = 6 && s.Traced = 3 @>
    test <@ s.UntracedExecuted = [ "N.Outer.Tests.c"; "N.Outer.Tests.cc"; "unrelated" ] @>

[<Fact>]
let ``a test with no outcome, unmapped code, or an untraced child is incomplete and says why`` () =
    let w = World()
    // Id 5 has no manifest row.
    w.Manifest <- { w.Manifest with IdCount = 6 }

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "a" "N.Tests.a"
            st.Hit 2
            st.Hit 5
            asTest st "T:2" "N.Tests" "b" "N.Tests.b"
            st.CurrentScope().Children.Enqueue(struct (4242, "dotnet", true))
            // Shell-executed: it got no parent scope, so it can never have left a dump.
            st.CurrentScope().Children.Enqueue(struct (4243, "open", false))
    )

    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.b" ])

    test
        <@
            (read store s "N.Tests" "a").Reasons = [ "no-outcome"
                                                     "unmapped-code:Q.Nowhere::x (no-document)"
                                                     "unmapped-code:#5 (no-row)" ]
        @>

    test <@ (read store s "N.Tests" "b").Reasons = [ "child-process-untraced:dotnet"; "child-process-untraced:open" ] @>

    test <@ s.UnmappedIds = 2 && s.Complete = 0 @>

[<Fact>]
let ``a traced child's hits, static init and children merge into the parent test`` () =
    let w = World()

    w.Process(
        20,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.CurrentScope().Children.Enqueue(struct (100, "app", true))
    )

    // pid 100 sorts before 20, so the identity-less child scope is merged first.
    w.Process(
        100,
        "T:1",
        fun st ->
            st.Hit 1
            st.EnterStatic()
            st.Hit 0
            st.ExitStatic()
            st.NoteScope().Children.Enqueue(struct (101, "grandchild", true))
    )

    w.Process(101, "T:1", ignore)
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    let t = read store s "N.Tests" "t"
    test <@ List.isEmpty t.Reasons @>
    test <@ t.Complete && t.Symbols = set [ "N.M.f", "hf"; "N.M.g", "hg" ] @>
    test <@ s.Counters.Test = 3L @>

[<Fact>]
let ``a child dump for another scope does not complete a test`` () =
    let w = World()

    w.Process(
        20,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.CurrentScope().Children.Enqueue(struct (30, "app", true))
    )

    w.Process(30, "T:other", ignore)
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    test <@ (read store s "N.Tests" "t").Reasons = [ "child-process-untraced:app" ] @>

[<Fact>]
let ``a source file edited since the build marks its executors as drifted`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.Hit 0
    )

    File.AppendAllText(w.Src, "let h () = 3\n")
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    test <@ (read store s "N.Tests" "t").Reasons = [ "source-drift:src/M.fs" ] @>

[<Fact>]
let ``a source file deleted since the build is not indexed; unhashed and outside documents are not checked`` () =
    let w = World()
    let outside = Path.Combine(Path.GetTempPath(), "elsewhere", "X.fs")

    w.Manifest <-
        { w.Manifest with
            Documents = w.Manifest.Documents |> Map.add w.Plain "" |> Map.add outside "abc" }

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.Hit 0
            asTest st "T:2" "N.Tests" "u" "N.Tests.u"
            st.Hit 4
    )

    File.Delete w.Src
    // Edited, but the PDB recorded no hash for it: it cannot be said to have drifted.
    File.AppendAllText(w.Plain, "let q () = 1\n")
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t"; passed "N.Tests.u" ])
    test <@ (read store s "N.Tests" "t").Reasons = [ "not-indexed:src/M.fs" ] @>
    let u = read store s "N.Tests" "u"
    // src/Plain.fs is not indexed: a file-level entry at its current hash.
    test <@ u.Complete @>
    test <@ u.Inputs = set [ "file-level", "src/Plain.fs", Fingerprint.hashFile w.Root "src/Plain.fs" ] @>

[<Fact>]
let ``a dropped probe records nothing and a symbol with no version is unmapped`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.Hit 3
            asTest st "T:2" "N.Tests" "u" "N.Tests.u"
            st.Hit 0
    )

    use store = TraceStore.Store.Open w.TraceDb

    let s =
        ingest
            store
            { w.Request [ passed "N.Tests.t"; passed "N.Tests.u" ] with
                Symbols =
                    { w.Store with
                        GetAllSymbols = fun () -> [] }
                // Documents the manifest lists no hash for are never checked for drift.
                Shadow =
                    { w.Shadow with
                        Manifest =
                            { w.Manifest with
                                Documents = Map.empty } } }

    let t = read store s "N.Tests" "t"
    test <@ t.Complete && t.Symbols.IsEmpty && t.Inputs.IsEmpty @>
    test <@ (read store s "N.Tests" "u").Reasons = [ "unmapped-code:N.M::f (no-version)" ] @>

[<Fact>]
let ``file inputs are stored repo-relative with their current state`` () =
    let w = World()
    let data = Path.Combine(w.Root, "data")
    Directory.CreateDirectory(Path.Combine(data, "sub")) |> ignore
    File.WriteAllText(Path.Combine(data, "a.json"), "{}")
    let traced = Path.Combine(w.Root, "bin", "Traced", "net10.0", "cfg.json")
    Directory.CreateDirectory(Path.GetDirectoryName traced) |> ignore
    let debug = Path.Combine(w.Root, "bin", "Debug", "net10.0", "cfg.json")
    Directory.CreateDirectory(Path.GetDirectoryName debug) |> ignore
    File.WriteAllText(debug, "debug")

    let note (st: RecorderState) kind path =
        st.CurrentScope().Inputs.TryAdd(struct (kind, path), 0uy) |> ignore

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            note st "read" (Path.Combine(data, "a.json"))
            note st "read" traced
            note st "exists" (Path.Combine(data, "a.json"))
            note st "exists" (Path.Combine(data, "sub"))
            note st "exists" (Path.Combine(data, "gone.json"))
            note st "list" data
            note st "list" (Path.Combine(data, "nowhere"))
            note st "read" (Path.Combine(Path.GetTempPath(), "outside.json"))
    )

    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])

    let listing =
        Convert
            .ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes "a.json\nsub"))
            .ToLowerInvariant()

    test
        <@
            (read store s "N.Tests" "t").Inputs = set
                [ "read", "data/a.json", Fingerprint.hashFile w.Root "data/a.json"
                  "read", "bin/Debug/net10.0/cfg.json", Fingerprint.hashFile w.Root "bin/Debug/net10.0/cfg.json"
                  "exists", "data/a.json", "present"
                  "exists", "data/sub", "present"
                  "exists", "data/gone.json", "absent"
                  "list", "data", listing
                  "list", "data/nowhere", "absent" ]
        @>

[<Fact>]
let ``repoRelative maps under-root paths and the traced bin to the debug bin`` () =
    let root = Path.Combine(Path.GetTempPath(), "repo")

    let under (parts: string list) =
        Path.Combine(root :: parts |> Array.ofList)

    test <@ repoRelative root (under [ "src"; "M.fs" ]) = Some "src/M.fs" @>
    test <@ repoRelative (root + string Path.DirectorySeparatorChar) (under [ "a" ]) = Some "a" @>
    test <@ repoRelative root (under [ "bin"; "Traced"; "x.json" ]) = Some "bin/Debug/x.json" @>

    test
        <@ repoRelative root (under [ "p"; "bin"; "Traced"; "net10.0"; "x.json" ]) = Some "p/bin/Debug/net10.0/x.json" @>

    test <@ repoRelative root (root + "-sibling") = None @>
    test <@ repoRelative root root = None @>
    test <@ repoRelative root (Path.Combine(root, "..", "x")) = None @>

[<Fact>]
let ``fixture and pool scopes are inherited through parents and links, and run scopes are kept`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            st.EnterScope "P:pool"
            st.Hit 1
            st.EnterScope "C:N.Tests"
            st.CurrentScope().Links.TryAdd("P:pool", 0uy) |> ignore
            st.CurrentScope().Links.TryAdd("P:never-recorded", 0uy) |> ignore
            st.CurrentScope().Links.TryAdd("A:ambient", 0uy) |> ignore
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.ExitScope()
            // Ambient and static-init hits are stored as run scopes, never linked.
            st.Hit 0
            st.EnterStatic()
            st.Hit 0
            st.ExitStatic()
            // A T scope with no test identity (a host's own `Scopes.Enter`) is not a test.
            st.EnterScope "T:orphan"
            st.Hit 0
    )

    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    let t = read store s "N.Tests" "t"
    test <@ t.Complete && t.Symbols = set [ "N.M.g", "hg" ] @>
    test <@ store.TestKeysOf("P", s.EnvFingerprint.Value) = set [ testKey "P" "N.Tests" "t" ] @>
    test <@ store.RunScopeKeys("run1", "P") = [ "A:ambient"; "C:N.Tests"; "P:pool"; "S:static-init"; "T:1" ] @>

[<Fact>]
let ``no usable dump records a failed run and no tests; executed tests are listed as untraced`` () =
    let w = World()
    Directory.CreateDirectory w.DumpDir |> ignore
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    test <@ s.Traced = 0 && s.UntracedExecuted = [ "N.Tests.t" ] && s.EnvFingerprint = None @>
    test <@ (s.Status, s.Reason) = (TraceStore.FailedToRecord, "recorder-no-output") @>
    let run = store.Runs "P" |> List.head
    test <@ (run.Status, run.Reason) = (TraceStore.FailedToRecord, "recorder-no-output") @>

[<Fact>]
let ``no usable dump names the rejected dumps`` () =
    let w = World()
    Directory.CreateDirectory w.DumpDir |> ignore
    File.WriteAllText(Path.Combine(w.DumpDir, "trace-7.ndjson"), "{\"format\":\"testprune-trace/1\"}\n")
    // A child's dump alone is not a run's output.
    w.Process(8, "T:1", ignore)
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    test <@ s.Status = TraceStore.FailedToRecord @>
    test <@ s.Reason = "recorder-no-output; rejected: trace-7.ndjson: truncated" @>
    test <@ s.RejectedDumps |> List.map (fst >> Path.GetFileName) = [ "trace-7.ndjson" ] @>

[<Fact>]
let ``an empty weave set is a loud refusal, never an empty verified trace`` () =
    let w = World()

    w.Manifest <-
        { Rows = [||]
          Documents = Map.empty
          IdCount = 0 }

    w.Process(10, null, fun st -> asTest st "T:1" "N.Tests" "t" "N.Tests.t")
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])

    test
        <@
            s.Status = TraceStore.Refused
            && s.Traced = 0
            && s.UntracedExecuted = [ "N.Tests.t" ]
        @>

    test <@ s.Reason = nothingWovenReason w.Root @>

    test
        <@
            s.Reason.StartsWith "no-woven-assembly: "
            && s.Reason.Contains "ContinuousIntegrationBuild"
        @>

    let run = store.Runs "P" |> List.head
    test <@ (run.Status, run.Reason) = (TraceStore.Refused, s.Reason) @>

[<Fact>]
let ``a tree that moved during the run makes every trace incomplete`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.Hit 0
    )

    use store = TraceStore.Store.Open w.TraceDb

    let s =
        ingest
            store
            { w.Request [ passed "N.Tests.t" ] with
                CurrentTreeHash = "moved" }

    test <@ (read store s "N.Tests" "t").Reasons = [ "tree-moved" ] @>
    test <@ s.Status = TraceStore.TreeMovedDuringRun @>
    test <@ (store.Runs "P" |> List.head).Status = TraceStore.TreeMovedDuringRun @>

[<Fact>]
let ``an overflowing recorder and a rejected dump make every trace incomplete`` () =
    let w = World()

    w.ProcessWith(
        5,
        10,
        null,
        2L,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.Hit 0
    )

    File.WriteAllText(Path.Combine(w.DumpDir, "trace-11.ndjson"), "not json\n")
    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])
    let t = read store s "N.Tests" "t"
    test <@ t.Reasons.Length = 2 && t.Reasons.[0] = "recorder-overflow" @>
    test <@ t.Reasons.[1].StartsWith "dump-rejected:trace-11.ndjson: " @>
    test <@ s.Counters.Overflow = 2L @>

    let rejected =
        [ for e in stats(store).GetProperty("rejected").EnumerateArray() -> e.GetString() ]

    test <@ rejected.Length = 1 && rejected.[0].StartsWith "trace-11.ndjson: " @>

[<Fact>]
let ``a dump from another weave is rejected rather than joined against the wrong manifest`` () =
    let w = World()

    w.Process(
        10,
        null,
        fun st ->
            asTest st "T:1" "N.Tests" "t" "N.Tests.t"
            st.Hit 0
    )

    w.ProcessWith(9, 11, "T:1", 0L, (fun st -> st.Hit 8))
    // An id outside the manifest in a dump that claims the right id count.
    File.WriteAllLines(
        Path.Combine(w.DumpDir, "trace-12.ndjson"),
        [ """{"format":"testprune-trace/1","pid":12,"parentScope":"T:1","runtime":"r","os":"o","arch":"a","ids":5,"cpuMs":0,"counters":{"test":0,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":0}}"""
          """{"key":"T:1","test":null,"parents":[],"links":[],"ids":[7],"inputs":[],"children":[]}"""
          """{"end":true}""" ]
    )

    use store = TraceStore.Store.Open w.TraceDb
    let s = ingest store (w.Request [ passed "N.Tests.t" ])

    let rejected = s.RejectedDumps |> List.map (fun (f, why) -> Path.GetFileName f, why)

    test
        <@
            rejected = [ "trace-11.ndjson", "id count 9, manifest 5"
                         "trace-12.ndjson", "probe id 7 outside the manifest's 5" ]
        @>

    let t = read store s "N.Tests" "t"
    test <@ t.Symbols = set [ "N.M.f", "hf" ] @>

    test
        <@
            t.Reasons = [ "dump-rejected:trace-11.ndjson: id count 9, manifest 5"
                          "dump-rejected:trace-12.ndjson: probe id 7 outside the manifest's 5" ]
        @>

[<Fact>]
let ``a full run drops traces of tests it ran untraced; a partial run keeps them`` () =
    let w = World()

    let record (pid: int) (tests: string list) =
        for f in Directory.GetFiles w.DumpDir do
            File.Delete f

        w.Process(
            pid,
            null,
            fun st ->
                for m in tests do
                    asTest st ("T:" + m) "N.Tests" m ("N.Tests." + m)
                    st.Hit 0
        )

    Directory.CreateDirectory w.DumpDir |> ignore
    use store = TraceStore.Store.Open w.TraceDb
    record 10 [ "a"; "b" ]
    let s1 = ingest store (w.Request [ passed "N.Tests.a"; passed "N.Tests.b" ])

    let keys () =
        store.TestKeysOf("P", s1.EnvFingerprint.Value)

    test <@ keys () = set [ testKey "P" "N.Tests" "a"; testKey "P" "N.Tests" "b" ] @>
    record 11 [ "a" ]

    ingest
        store
        { w.Request [ passed "N.Tests.a" ] with
            RunId = "run2"
            Kind = TraceStore.PartialRun }
    |> ignore

    test <@ keys () = set [ testKey "P" "N.Tests" "a"; testKey "P" "N.Tests" "b" ] @>

    ingest
        store
        { w.Request [ passed "N.Tests.a" ] with
            RunId = "run3" }
    |> ignore

    test <@ keys () = set [ testKey "P" "N.Tests" "a" ] @>

[<Fact>]
let ``version hashes use the content hash of a single occurrence and a digest of several`` () =
    let w = World()
    let store = w.Store

    let two =
        { store with
            GetAllSymbols =
                fun () ->
                    [ sym "N.S.x" "src/S.fsi" 1 "h1"
                      sym "N.S.x" "src/S.fs" 1 "h2"
                      sym "N.S.y" "src/S.fs" 2 "h3" ] }

    let v = versionHashes two
    test <@ v.["N.S.y"] = "h3" @>
    test <@ v.["N.S.x"].Length = 64 && v.["N.S.x"] <> "h1" && v.["N.S.x"] <> "h2" @>

    let swapped =
        { store with
            GetAllSymbols = fun () -> List.rev (two.GetAllSymbols()) }

    test <@ versionHashes swapped = v @>
    test <@ versionHashes store = Map.ofList [ "N.M.f", "hf"; "N.M.g", "hg" ] @>
