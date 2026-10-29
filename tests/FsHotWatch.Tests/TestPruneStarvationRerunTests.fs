/// The plugin end of `Starvation`: a project that declared "re-run alone" and timed out
/// on a starved box is re-run once after the rest of the run, its second result is the
/// one reported, and the run directory says what happened.
///
/// The box's real load is whatever it is when this runs, so these tests pin the
/// threshold instead: `loadPerCore = 0` makes every sample starved (a load average is
/// never negative), and a threshold no box reaches makes none of them starved.
module FsHotWatch.Tests.TestPruneStarvationRerunTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// A project whose first launch hangs past its 2 s timeout and whose second exits 0:
/// the marker file is the only state the two launches share.
let private hangsOnceThenPasses (marker: string) (starvation: Starvation.Policy option) : TestConfig =
    { Project = "Slow.Tests"
      Command = "sh"
      Args =
        $"-c \"if [ -f '%s{marker}' ]; then echo second; exit 0; else touch '%s{marker}'; echo first; exec sleep 60; fi\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = Some 2
      ReportVerificationFormat = Disabled
      Starvation = starvation
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

let private alwaysStarved: Starvation.Policy =
    { Starvation.Policy.rerunAlone [] with
        LoadPerCore = 0.0 }

let private neverStarved: Starvation.Policy =
    { Starvation.Policy.rerunAlone [] with
        LoadPerCore = 1_000_000.0 }

/// Run the suite once through `run-tests`; the project's status and the run directory.
let private runOnce (config: TestConfig) : string * string =
    let root = isolatedRoot ()
    let host = PluginHost.create (Unchecked.defaultof<_>) root
    let handler = create ":memory:" root (Some [ config ]) None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsSome @>

    use doc = JsonDocument.Parse(result.Value)

    let status =
        doc.RootElement.GetProperty("projects").[0].GetProperty("status").GetString()

    let runDir =
        Directory.GetDirectories(FsHotWatch.Ctrf.reportsDir root) |> Array.exactlyOne

    status, runDir

let private markerIn () =
    Path.Combine(Path.GetTempPath(), $"fshw-starved-marker-{Guid.NewGuid():N}")

[<Fact(Timeout = 120_000)>]
let ``a declared project that timed out on a starved box is re-run alone, and its second result is reported`` () =
    let marker = markerIn ()

    try
        let status, runDir = runOnce (hangsOnceThenPasses marker (Some alwaysStarved))

        test <@ status = "passed" @>

        // The verdict-facing record says it was starved, then passed alone.
        match Starvation.tryRead (Starvation.pathIn runDir "Slow.Tests") with
        | Some stored ->
            test <@ stored.PassedAlone @>
            test <@ stored.Summary.Contains "starved, then passed alone" @>
            test <@ stored.FirstAttempt.Contains "timed out after 2s" @>
        | None -> failwith $"no starvation record in %s{runDir}"

        // The first attempt is kept, out of the run's report set; the run's own log is
        // the re-run's.
        let attempt = Path.Combine(runDir, Starvation.AttemptDirName)
        test <@ File.Exists(Path.Combine(attempt, "Slow.Tests" + TimeoutMarker.Suffix)) @>
        test <@ File.Exists(Path.Combine(attempt, "Slow.Tests" + Profile.Suffix)) @>
        test <@ File.ReadAllText(Path.Combine(attempt, "Slow.Tests.output.log")).Contains "first" @>
        test <@ File.ReadAllText(Path.Combine(runDir, "Slow.Tests.output.log")).Contains "second" @>
        test <@ not (File.Exists(Path.Combine(runDir, "Slow.Tests" + TimeoutMarker.Suffix))) @>
    finally
        File.Delete marker

[<Fact(Timeout = 120_000)>]
let ``a declared project that timed out on a quiet box is reported timed out, and not re-run`` () =
    let marker = markerIn ()

    try
        let status, runDir = runOnce (hangsOnceThenPasses marker (Some neverStarved))

        test <@ status = "timed-out" @>
        test <@ not (File.Exists(Starvation.pathIn runDir "Slow.Tests")) @>
        test <@ not (Directory.Exists(Path.Combine(runDir, Starvation.AttemptDirName))) @>
    finally
        File.Delete marker

[<Fact(Timeout = 120_000)>]
let ``a project that declared nothing is never re-run, however starved the box`` () =
    let marker = markerIn ()

    try
        let status, runDir = runOnce (hangsOnceThenPasses marker None)

        test <@ status = "timed-out" @>
        test <@ not (File.Exists(Starvation.pathIn runDir "Slow.Tests")) @>
    finally
        File.Delete marker

[<Fact(Timeout = 120_000)>]
let ``a red that is not a timeout is never re-run, even when declared and starved`` () =
    let marker = markerIn ()

    let failsOnceThenPasses =
        { hangsOnceThenPasses marker (Some alwaysStarved) with
            Args = $"-c \"if [ -f '%s{marker}' ]; then exit 0; else touch '%s{marker}'; exit 1; fi\"" }

    try
        let status, runDir = runOnce failsOnceThenPasses

        test <@ status = "failed" @>
        test <@ not (File.Exists(Starvation.pathIn runDir "Slow.Tests")) @>
    finally
        File.Delete marker

// ---------------------------------------------------------------------------
// A starved first attempt's coverage controller, killed before it restores
// ---------------------------------------------------------------------------
//
// On macOS a coverage run instruments the assemblies it measures IN the test project's
// output (`Common.dll` becomes the instrumented one, `Common.dll.orig` the backup) and
// its controller puts them back once the test host exits. A starved host that has to be
// killed with its tree (the stop grace ran out) takes the controller with it, so the
// instrumented copies stay. A coverage run over an already-instrumented copy measures
// nothing: measured against xunit.v3 + Microsoft.Testing.Platform + CodeCoverage 18.9.0,
// it passes and writes a well-formed, empty report (`<packages />`, 178 bytes), and the
// next one does the same. That was land entry 224: the re-run alone passed and the
// coverage-count gate read 56,724 -> 0 covered entries for that report.
//
// The stand-in: a runner whose first launch leaves its `Common.dll` copy holding
// different bytes from its origin and then hangs past the timeout, and whose second
// launch writes an empty report when that copy is still the altered one and a covered
// one when it holds the build's bytes.

/// A synthetic repo: `Tests` references `Common`; both are built, the copy of
/// `Common.dll` in the test output holds its origin's bytes, and every source is older
/// than every output.
type private Built =
    { Root: string
      Fsproj: string
      TestsSrc: string
      CommonDll: string
      CommonDllCopy: string }

let private builtRepo (root: string) : Built =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)
    let p (parts: string list) = Path.Combine(Array.ofList parts)

    let writeAt (path: string) (content: string) (at: DateTime) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)
        File.SetLastWriteTimeUtc(path, at)

    let commonDir = p [ root; "Common" ]
    let testsDir = p [ root; "Tests" ]
    let commonOut = p [ commonDir; "bin"; "Debug"; "net10.0" ]
    let testsOut = p [ testsDir; "bin"; "Debug"; "net10.0" ]
    let fsproj = p [ testsDir; "Tests.fsproj" ]

    writeAt (p [ commonDir; "Common.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ commonDir; "Common.fs" ]) "module Common" sourcedAt
    writeAt (p [ commonOut; "Common.dll" ]) "built" builtAt

    writeAt
        fsproj
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Common/Common.fsproj\" />\n  </ItemGroup>\n</Project>"
        sourcedAt

    writeAt (p [ testsDir; "Tests.fs" ]) "module Tests" sourcedAt
    writeAt (p [ testsOut; "Tests" ]) "" builtAt
    writeAt (p [ testsOut; "Tests.dll" ]) "" builtAt
    writeAt (p [ testsOut; "Common.dll" ]) "built" builtAt

    { Root = root
      Fsproj = fsproj
      TestsSrc = p [ testsDir; "Tests.fs" ]
      CommonDll = p [ commonOut; "Common.dll" ]
      CommonDllCopy = p [ testsOut; "Common.dll" ] }

/// The runner: its first launch runs `firstAttempt`, then hangs past the 2 s timeout;
/// its second writes the report to the path after `--coverage-output` — empty when the
/// copy of `Common.dll` still holds the first attempt's bytes, covered when it holds the
/// build's — and passes.
let private coverageRunner (repo: Built) (marker: string) (firstAttempt: string) : TestConfig =
    let script = Path.Combine(repo.Root, "runner.sh")

    File.WriteAllText(
        script,
        $"out=''\n\
          prev=''\n\
          for a in \"$@\"; do [ \"$prev\" = --coverage-output ] && out=\"$a\"; prev=\"$a\"; done\n\
          if [ ! -f '%s{marker}' ]; then\n\
            touch '%s{marker}'\n\
            %s{firstAttempt}\n\
            exec sleep 60\n\
          fi\n\
          if [ \"$(cat '%s{repo.CommonDllCopy}')\" = instrumented ]; then\n\
            printf '<coverage><packages /></coverage>' > \"$out\"\n\
          else\n\
            printf '<coverage><packages><package name=\"Common\"><classes><class name=\"Common\" filename=\"Common/Common.fs\"><lines><line number=\"1\" hits=\"3\" /></lines></class></classes></package></packages></coverage>' > \"$out\"\n\
          fi\n\
          exit 0\n"
    )

    { hangsOnceThenPasses marker (Some alwaysStarved) with
        Project = "Tests"
        Args = $"\"%s{script}\" --project \"%s{repo.Fsproj}\"" }

/// Run `config` once with coverage; its status, the run directory and the baseline path.
let private runOnceWithCoverage (root: string) (config: TestConfig) : string * string * string =
    let baseline = Path.Combine(root, "coverage", "Tests", BaselineName)

    let coveragePaths _ =
        let dir = Path.Combine(root, "coverage", "Tests")

        Some
            { Baseline = baseline
              Partial = Path.Combine(dir, PartialName)
              Cobertura = Path.Combine(root, "coverage", CoberturaName)
              IncludeInRatchet = false
              ArgsTemplate = "--coverage-output \"{output}\"" }

    let host = PluginHost.create (Unchecked.defaultof<_>) root

    let handler =
        create (Path.Combine(root, "tp.db")) root (Some [ config ]) None None None (Some coveragePaths) []

    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsSome @>

    use doc = JsonDocument.Parse(result.Value)

    let status =
        doc.RootElement.GetProperty("projects").[0].GetProperty("status").GetString()

    let runDir =
        Directory.GetDirectories(FsHotWatch.Ctrf.reportsDir root) |> Array.exactlyOne

    status, runDir, baseline

[<Fact(Timeout = 120_000)>]
let ``a re-run alone measures the build's assemblies, not the copies a killed first attempt left instrumented`` () =
    let marker = markerIn ()
    let repo = builtRepo (isolatedRoot ())

    try
        let config =
            coverageRunner repo marker $"printf instrumented > '%s{repo.CommonDllCopy}'"

        let status, runDir, baseline = runOnceWithCoverage repo.Root config

        test <@ status = "passed" @>

        match Starvation.tryRead (Starvation.pathIn runDir "Tests") with
        | Some stored -> test <@ stored.PassedAlone @>
        | None -> failwith $"no starvation record in %s{runDir}"

        // The report the coverage gate reads is the re-run's, and it measured something.
        test <@ (File.ReadAllText baseline).Contains "hits=\"3\"" @>
        test <@ File.ReadAllText repo.CommonDllCopy = File.ReadAllText repo.CommonDll @>
    finally
        File.Delete marker

[<Fact(Timeout = 120_000)>]
let ``a re-run alone over build output it cannot certify is not launched, and is never a pass`` () =
    let marker = markerIn ()
    let repo = builtRepo (isolatedRoot ())

    try
        // An edit lands while the first attempt runs: the test assembly no longer
        // describes its sources, and no file on disk holds the bytes that would.
        let config = coverageRunner repo marker $"touch '%s{repo.TestsSrc}'"

        let status, runDir, baseline = runOnceWithCoverage repo.Root config

        test <@ status = "deferred" @>

        match Starvation.tryRead (Starvation.pathIn runDir "Tests") with
        | Some stored ->
            test <@ not stored.PassedAlone @>
            test <@ stored.Summary.Contains "was deferred" @>
        | None -> failwith $"no starvation record in %s{runDir}"

        test <@ not (File.Exists baseline) @>
    finally
        File.Delete marker
