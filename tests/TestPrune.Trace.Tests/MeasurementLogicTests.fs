/// The pure halves of the process-driving measurements: what the audit, overhead and
/// file census compute from dumps, CTRF outcomes and CPU samples once the processes ran.
module TestPrune.Trace.Tests.MeasurementLogicTests

open System
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

let private noCounters =
    { Test = 0L
      Class = 0L
      Collection = 0L
      Assembly = 0L
      Override = 0L
      StaticInit = 0L
      Ambient = 0L
      Overflow = 0L }

let private dump pid parent (scopes: RecordedScope list) : ProcessDump =
    { Pid = pid
      ParentScope = parent
      Runtime = ".NET 10.0.0"
      Os = "OSX"
      Arch = "Arm64"
      IdCount = 100
      CpuMs = 0L
      Counters = noCounters
      Scopes = scopes }

let private scope key (ids: int list) : RecordedScope =
    { Key = key
      Test = None
      Parents = []
      Links = []
      Ids = Array.ofList ids
      Inputs = []
      Children = [] }

let private testScope key (cls: string) (meth: string) (display: string) ids =
    { scope key ids with
        Test =
            Some
                { Class = cls
                  Method = meth
                  Display = display } }

let private row id kind typeName memberName : ManifestRow =
    { Id = id
      Kind = kind
      Assembly = "A"
      TypeName = typeName
      Member = memberName
      Document = None
      FirstLine = 1
      LastLine = 1 }

// ---------------------------------------------------------------- audit

[<Fact>]
let ``own ids merge a test's scope across processes, re-keying a child's static init`` () =
    let parent =
        dump 1 None [ testScope "T:1" "N.C" "m" "N.C.m" [ 1; 2 ]; scope "C:N.C" [ 9 ] ]

    let child = dump 2 (Some "T:1") [ scope "T:1" [ 3 ]; scope "S:static-init" [ 4 ] ]

    test <@ Audit.ownIds [ parent; child ] = Map [ "N.C.m", set [ 1; 2; 3; 4 ] ] @>

[<Fact>]
let ``own ids keep a parentless process's static init out of every test`` () =
    let d =
        dump 1 None [ testScope "T:1" "N.C" "m" "N.C.m" [ 1 ]; scope "S:static-init" [ 4 ] ]

    test <@ Audit.ownIds [ d ] = Map [ "N.C.m", set [ 1 ] ] @>

[<Fact>]
let ``the sample is seeded, sorted first, at least one test and never more than all`` () =
    let all = [ "e"; "d"; "c"; "b"; "a" ]
    let pick = Audit.choose 0.4 7 all
    test <@ pick.Length = 2 && pick = Audit.choose 0.4 7 (List.rev all) @>
    test <@ Audit.choose 0.01 7 all |> List.length = 1 @>
    test <@ Audit.choose 1.0 7 all |> List.sort = List.sort all @>
    test <@ Audit.choose 5.0 7 all |> List.length = 5 @>
    test <@ List.isEmpty (Audit.choose 0.5 7 []) @>

[<Fact>]
let ``a test's audit is its extra ids and its missing ids by kind`` () =
    let rows =
        Map
            [ 1, row 1 UserMethod "N.M" "f"
              2, row 2 StaticCtor "N.M" ".cctor"
              3, row 3 GeneratedMethod "N.M+f@1" "Invoke"
              4, row 4 UnionCase "N.U" "A"
              5, row 5 TypeUse "N.R" "" ]

    let a =
        Audit.compareTest rows "N.C.m" (set [ 1; 6 ]) (Some(set [ 1; 2; 3; 4; 5; 7 ]))

    test <@ a.Display = "N.C.m" && a.IsolatedFound @>
    test <@ a.Extra = [ 6 ] @>

    test <@ a.MissingByKind = Map [ "case", 1; "cctor", 1; "gen", 1; "type", 1; "unknown", 1 ] @>
    // cctor and gen are the expected once-per-process kinds; the rest need a human.
    test <@ a.MissingUser = [ "N.U::A"; "N.R::"; "?::7" ] @>

[<Fact>]
let ``a test the isolated run never recorded is all extra and not found`` () =
    let a = Audit.compareTest Map.empty "N.C.m" (set [ 1 ]) None
    test <@ not a.IsolatedFound && a.Extra = [ 1 ] && a.MissingByKind.IsEmpty @>

[<Fact>]
let ``incompleteness is broken down by reason, one count per test`` () =
    let echo =
        { Pid = 10
          FileName = "echo"
          EnvInjected = false }

    let git =
        { Pid = 11
          FileName = "git"
          EnvInjected = true }

    let dotnet =
        { Pid = 12
          FileName = "dotnet"
          EnvInjected = true }

    let t1 =
        { testScope "T:1" "N.C" "a" "N.C.a" [ 1 ] with
            Children = [ echo; git; git ] }

    let t2 =
        { testScope "T:2" "N.C" "b" "N.C.b" [ 1 ] with
            Children = [ dotnet ] }

    let t3 = testScope "T:3" "N.C" "c" "N.C.c" [ 1 ]
    // dotnet (pid 12) left a dump under T:2, so only echo and git are untraced.
    let dumps =
        [ dump 1 None [ t1; t2; t3 ]; dump 12 (Some "T:2") [ scope "T:2" [ 2 ] ] ]

    test <@ Audit.incomplete dumps [] = Map [ "child-process-untraced:echo", 1; "child-process-untraced:git", 1 ] @>

    let overflowed =
        [ { dump 1 None [ t3 ] with
              Counters = { noCounters with Overflow = 1L } } ]

    test
        <@
            Audit.incomplete overflowed [ "/d/trace-9.ndjson", "truncated" ] = Map
                [ "dump-rejected:trace-9.ndjson: truncated", 1; "recorder-overflow", 1 ]
        @>

let private audited display extra found : Audit.TestAudit =
    { Display = display
      Extra = extra
      MissingByKind = Map.empty
      MissingUser = []
      IsolatedFound = found }

[<Fact>]
let ``the audit passes only with tests sampled, no extra id and every test found alone`` () =
    let ok = Audit.summarize [ audited "a" [] true ] Map.empty
    test <@ ok.Sampled = 1 && ok.ExtraTotal = 0 && Audit.passes ok @>

    let extra =
        Audit.summarize [ audited "a" [ 1; 2 ] true; audited "b" [ 3 ] true ] Map.empty

    test <@ extra.ExtraTotal = 3 && not (Audit.passes extra) @>
    test <@ not (Audit.passes (Audit.summarize [ audited "a" [] false ] Map.empty)) @>
    test <@ not (Audit.passes (Audit.summarize [] Map.empty)) @>

[<Fact>]
let ``the audit table names every extra id, missing user id and incomplete reason`` () =
    let t =
        { audited "N.C.m" [ 6 ] true with
            MissingByKind = Map [ "cctor", 2; "user", 1 ]
            MissingUser = [ "N.M::f" ] }

    let text =
        Audit.render (Audit.summarize [ t; audited "N.C.n" [] false ] (Map [ "child-process-untraced:git", 3 ]))

    test
        <@
            text = String.concat
                "\n"
                [ "audit  FAIL  sampled 2  extra-in-parallel 1"
                  "  N.C.m  extra 1  missing cctor=2 user=1"
                  "    extra id 6"
                  "    missing user N.M::f"
                  "  N.C.n  extra 0  missing -"
                  "    not recorded when run alone"
                  "  incomplete child-process-untraced:git  3"
                  "" ]
        @>

// ---------------------------------------------------------------- overhead

let private ms (n: float) = TimeSpan.FromMilliseconds n

let private sample traced cpu rss : Overhead.Sample =
    { Traced = traced
      Cpu = ms cpu
      MaxRssBytes = rss
      ExitCode = 0 }

[<Fact>]
let ``the median of an odd count is the middle, of an even count the mean of the middle two`` () =
    test <@ Overhead.median [ ms 30.0; ms 10.0; ms 20.0 ] = ms 20.0 @>
    test <@ Overhead.median [ ms 40.0; ms 10.0; ms 20.0; ms 30.0 ] = ms 25.0 @>

[<Fact>]
let ``the overhead report has both medians, their spread, the ratio and the traced peak`` () =
    let r =
        Overhead.summarize
            [ sample false 100.0 5L
              sample true 110.0 7L
              sample false 120.0 9L
              sample true 150.0 8L
              sample false 90.0 1L
              sample true 115.0 6L ]

    test <@ r.BaseMedianCpu = ms 100.0 && r.TracedMedianCpu = ms 115.0 @>
    test <@ (r.BaseMinCpu, r.BaseMaxCpu, r.TracedMinCpu, r.TracedMaxCpu) = (ms 90.0, ms 120.0, ms 110.0, ms 150.0) @>
    test <@ abs (r.Ratio - 1.15) < 1e-9 && r.TracedMaxRssBytes = 8L && r.Samples.Length = 6 @>
    test <@ Overhead.passes r @>

[<Fact>]
let ``overhead fails over the bar or when tracing changes an exit code`` () =
    test <@ not (Overhead.passes (Overhead.summarize [ sample false 100.0 0L; sample true 116.0 0L ])) @>

    let changed =
        Overhead.summarize
            [ sample false 100.0 0L
              { sample true 100.0 0L with
                  ExitCode = 1 } ]

    test <@ not (Overhead.passes changed) @>

[<Fact>]
let ``the overhead table prints medians, spread, ratio and every sample`` () =
    let text =
        Overhead.render (Overhead.summarize [ sample false 100.0 0L; sample true 105.0 (3L * 1024L * 1024L) ])

    test
        <@
            text = String.concat
                "\n"
                [ "overhead  PASS  ratio 1.050 (bar <= 1.15)"
                  "  untraced  median 100 ms  range 100-100 ms"
                  "  traced    median 105 ms  range 105-105 ms  peak rss 3.0 MiB"
                  "  sample untraced  100 ms  exit 0"
                  "  sample traced    105 ms  exit 0"
                  "" ]
        @>

// ---------------------------------------------------------------- file census

[<Fact>]
let ``names compare on dotted class and method, theory arguments stripped`` () =
    test <@ FileCensus.normalise "N.M+C.t(x: 1)" = "N.M.C.t" @>
    test <@ FileCensus.normalise "N.M+C.t" = "N.M.C.t" @>

[<Fact>]
let ``a test reads the repository through its own scope or any scope it inherits`` () =
    let read p = { Kind = FileRead; Path = p }

    let own =
        { testScope "T:1" "N.M+C" "own" "N.M+C.own" [] with
            Inputs = [ read "/r/x" ] }

    let viaClass =
        { testScope "T:2" "N.M+D" "inherits(x: 1)" "N.M+D.inherits(x: 1)" [] with
            Parents = [ "C:N.M+D" ] }

    let viaPool =
        { testScope "T:3" "N.M+E" "linked" "N.M+E.linked" [] with
            Links = [ "P:web" ]
            Parents = [ "C:missing" ] }

    // The ambient and static-init scopes belong to the run, never to a test.
    let none =
        { testScope "T:4" "N.M+F" "none" "N.M+F.none" [] with
            Parents = [ "A:ambient"; "S:static-init" ] }

    let viaChild = testScope "T:5" "N.M+G" "child" "N.M+G.child" []

    // A fixture cycle with no input anywhere ends, reading nothing.
    let cycle =
        { testScope "T:6" "N.M+H" "cycle" "N.M+H.cycle" [] with
            Parents = [ "C:loop" ] }

    let dumps =
        [ dump
              1
              None
              [ own
                viaClass
                viaPool
                none
                viaChild
                cycle
                { scope "C:loop" [] with
                    Parents = [ "L:loop" ] }
                { scope "L:loop" [] with
                    Parents = [ "C:loop" ] }
                { scope "C:N.M+D" [] with
                    Parents = [ "L:coll" ] }
                { scope "L:coll" [] with
                    Inputs =
                        [ { Kind = DirectoryListing
                            Path = "/r/d" } ]
                    Parents = [ "C:N.M+D" ] }
                { scope "P:web" [] with
                    Inputs = [ { Kind = ExistenceProbe; Path = "/r/e" } ] }
                { scope "A:ambient" [] with
                    Inputs = [ read "/r/a" ] }
                { scope "S:static-init" [] with
                    Inputs = [ read "/r/s" ] } ]
          dump
              2
              (Some "T:5")
              [ { scope "T:5" [] with
                    Inputs = [ read "/r/c" ] } ] ]

    test <@ FileCensus.readers dumps = set [ "N.M.C.own"; "N.M.D.inherits"; "N.M.E.linked"; "N.M.G.child" ] @>

[<Fact>]
let ``failures are failed or other outcomes, never passed or skipped`` () =
    let o name outcome = { Name = name; Outcome = outcome }

    test
        <@
            FileCensus.failures
                [ o "N.C.a" Passed
                  o "N.C.b" Failed
                  o "N.C.c(x: 1)" OtherOutcome
                  o "N.C.d" Skipped ] = set [ "N.C.b"; "N.C.c" ]
        @>

[<Fact>]
let ``the ratio is the symmetric difference over the union, outside failures net of in-repo ones`` () =
    let r =
        FileCensus.summarize (set [ "flaky" ]) (set [ "flaky"; "a"; "b" ]) (set [ "b"; "c" ])

    test <@ r.FailInRepo = set [ "flaky" ] && r.FailOutside = set [ "a"; "b" ] @>
    test <@ r.SymmetricDifference = set [ "a"; "c" ] && abs (r.Ratio - 2.0 / 3.0) < 1e-9 @>
    test <@ not (FileCensus.passes r) @>

    let empty = FileCensus.summarize Set.empty Set.empty Set.empty
    test <@ empty.Ratio = 0.0 && FileCensus.passes empty @>

[<Fact>]
let ``the file census table lists both sides of the difference`` () =
    let text =
        FileCensus.render (FileCensus.summarize (set [ "f" ]) (set [ "a"; "f" ]) (set [ "b" ]))

    test
        <@
            text = String.concat
                "\n"
                [ "file-census  FAIL  ratio 1.000 (bar <= 0.05)  fail-outside 1  reads-repo 1  fail-in-repo 1"
                  "  fails outside, reads nothing  a"
                  "  reads the repo, passes outside  b"
                  "" ]
        @>

// ---------------------------------------------------------------- rusage

[<Fact>]
let ``rusage decodes user plus system time and max rss, in bytes on either platform`` () =
    let buf = Array.zeroCreate<byte> 256

    let put (off: int) (v: int64) =
        BitConverter.GetBytes(v).CopyTo(buf, off)

    put 0 2L
    put 8 500_000L
    put 16 1L
    put 24 250_000L
    put 32 4096L

    test <@ Rusage.decode true buf = struct (TimeSpan.FromSeconds 3.75, 4096L) @>
    test <@ Rusage.decode false buf = struct (TimeSpan.FromSeconds 3.75, 4096L * 1024L) @>

[<Fact>]
let ``a failing getrusage raises`` () =
    raises<InvalidOperationException> <@ Rusage.childrenWith (fun _ -> -1) true @>

[<Fact>]
let ``overhead refuses Windows before preparing or reading anything`` () =
    let req: TraceSession.PrepareRequest =
        { RepoRoot = "/nonexistent"
          ProjectDir = "/nonexistent/P"
          AssemblyName = "P"
          TestProject = "P"
          WeaveTests = SitesOnly
          RunDir = "/nonexistent/run"
          VerifyTimeout = TimeSpan.FromMinutes 1.0 }

    let read () : struct (TimeSpan * int64) = failwith "read on Windows"

    test
        <@
            Overhead.runWith true read req [] 1 (TimeSpan.FromMinutes 1.0) = Error
                "cannot measure CPU overhead: getrusage is macOS/Linux only"
        @>

[<Fact>]
let ``an exception reading getrusage is the cannot-measure error`` () =
    let read () : struct (TimeSpan * int64) =
        raise (EntryPointNotFoundException "no getrusage in libc")

    test <@ Overhead.readCpu read = Error "cannot measure CPU overhead: getrusage failed: no getrusage in libc" @>
    test <@ Overhead.readCpu (fun () -> struct (ms 5.0, 7L)) = Ok(struct (ms 5.0, 7L)) @>
