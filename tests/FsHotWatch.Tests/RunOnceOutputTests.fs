module FsHotWatch.Tests.RunOnceOutputTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers


// --- Staleness warning: detect FileCommand plugin inputs newer than last run ---

[<Fact(Timeout = 15000)>]
let ``detectStalePluginInputs flags plugins whose args are newer than last run`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfg = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfg, "{}")
        let lastRun = DateTime.UtcNow.AddMinutes(-5.0)
        // ensure mtime is after lastRun
        System.IO.File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow)

        let plugins =
            [ { Name = "ratchet"
                LastRunStarted = lastRun
                RepoRoot = tmpDir
                Args = "--check cfg.json" } ]

        let result = detectStalePluginInputs plugins

        test
            <@
                result
                |> List.exists (fun (n, files) -> n = "ratchet" && List.contains cfg files)
            @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``detectStalePluginInputs omits plugins with no stale files`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfg = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfg, "{}")
        System.IO.File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow.AddMinutes(-10.0))
        let lastRun = DateTime.UtcNow

        let plugins =
            [ { Name = "ratchet"
                LastRunStarted = lastRun
                RepoRoot = tmpDir
                Args = "--check cfg.json" } ]

        let result = detectStalePluginInputs plugins
        test <@ List.isEmpty result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``formatStalenessWarning is empty for no stale plugins`` () =
    test <@ formatStalenessWarning [] = "" @>

[<Fact(Timeout = 15000)>]
let ``formatStalenessWarning names the plugin, file, and rerun hint`` () =
    let warning = formatStalenessWarning [ "ratchet", [ "/tmp/cfg.json" ] ]
    test <@ warning.Contains("ratchet") @>
    test <@ warning.Contains("/tmp/cfg.json") @>
    test <@ warning.Contains("rerun") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors groups by file with plugin prefix`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 17
                   Column = 0
                   Detail = None })
                ("build",
                 { Message = "type error"
                   Severity = Error
                   Line = 42
                   Column = 5
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("src/Foo.fs") @>
    test <@ result.Contains("[lint]") @>
    test <@ result.Contains("[build]") @>
    test <@ result.Contains("L17") @>
    test <@ result.Contains("L42") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors shows severity labels for error and warning`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("build",
                 { Message = "type error"
                   Severity = Error
                   Line = 42
                   Column = 5
                   Detail = None })
                ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 17
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("error") @>
    test <@ result.Contains("type error") @>
    test <@ result.Contains("warning") @>
    test <@ result.Contains("bad name") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors shows count summary`` () =
    let errors =
        Map.ofList
            [ "src/A.fs",
              [ ("lint",
                 { Message = "x"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ]
              "src/B.fs",
              [ ("build",
                 { Message = "y"
                   Severity = Error
                   Line = 2
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("1 error(s), 1 warning(s) in 2 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors with no errors shows clean message`` () =
    let result = formatErrors Map.empty
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors hides info-severity entries from output`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "XML comment is not placed on a valid language element."
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ not (result.Contains("XML comment")) @>
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors hides hint-severity entries from output`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "some hint"
                   Severity = Hint
                   Line = 5
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ not (result.Contains("some hint")) @>
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors shows warnings but hides info in same file`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "XML comment is not placed on a valid language element."
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None })
                ("format-check",
                 { Message = "File is not formatted"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("File is not formatted") @>
    test <@ not (result.Contains("XML comment")) @>
    test <@ result.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``formatErrors excludes files with only info entries from file count`` () =
    let errors =
        Map.ofList
            [ "src/A.fs",
              [ ("fcs",
                 { Message = "XML comment"
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None }) ]
              "src/B.fs",
              [ ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("1 warning(s) in 1 file(s)") @>

// --- failIfNoProjects: shared fail-fast helper used by every entry path ---

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns Some 2 when no fsproj exists`` () =
    withTempDir "failif-zero-projects" (fun tmpDir ->
        // Empty src/ — no .fsproj files anywhere.
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpDir, "src"))
        |> ignore

        let result = failIfNoProjects tmpDir []
        test <@ result = Some 2 @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns None when at least one fsproj exists`` () =
    withTempDir "failif-has-project" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir []
        test <@ result = None @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns None when fsproj is in tests directory`` () =
    // Exercises the cross-directory search: src/ exists but is empty; the
    // sole fsproj lives under tests/. Both directories must be probed
    // before the helper can declare success.
    withTempDir "failif-tests-only" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        let testsDir = System.IO.Path.Combine(tmpDir, "tests")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore
        System.IO.Directory.CreateDirectory(testsDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(testsDir, "MyTests.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir []
        test <@ result = None @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns None when some fsprojs excluded but at least one remains`` () =
    // Drives Array.exists past the first element: the first fsproj matches
    // the exclude (predicate returns false → keep iterating); the second
    // does not (predicate returns true → match). Without this case the
    // "keep iterating" branch is never exercised.
    withTempDir "failif-mixed-excludes" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(srcDir, "vendor"))
        |> ignore

        // Excluded by `vendor/`.
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "vendor", "Vendored.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )
        // Not excluded.
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir [ "vendor/" ]
        test <@ result = None @>)

[<Fact(Timeout = 15000)>]
let ``failIfNoProjects returns Some 2 when every fsproj is excluded`` () =
    // Stress-test scenario: fsproj exists on disk but the user's exclude
    // pattern matches it. The pre-check must respect repo-relative
    // gitignore semantics (per Bug 1) so this exclude only matches files
    // at repoRoot/.workspaces/...
    withTempDir "failif-all-excluded" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        let result = failIfNoProjects tmpDir [ "src/" ]
        test <@ result = Some 2 @>)

// --- runOnceAndReport: zero-projects exit code ---

[<Fact(Timeout = 30000)>]
let ``runOnceAndReport returns 2 when no projects are discovered`` () =
    withTempDir "runonce-zero-projects" (fun tmpDir ->
        // Empty src/ — no .fsproj files anywhere.
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpDir, "src"))
        |> ignore

        let nullChecker: FSharp.Compiler.CodeAnalysis.FSharpChecker =
            Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

        let createDaemon (root: string) =
            Daemon.createWith nullChecker root Daemon.DaemonOptions.defaults

        let config: DaemonConfiguration =
            { defaultTestConfig () with
                Build = None
                Format = Off
                Lint = false
                Cache = NoCache }

        let exitCode = runOnceAndReport (fun _ -> "") false createDaemon tmpDir config None

        test <@ exitCode = 2 @>)

// ---------------------------------------------------------------------------
// AUTOMATION-117 — `confirm --run-once`: the merge verdict WITHOUT a daemon.
//
// `confirm` used to exist only on the daemon IPC path, and `--run-once` bypasses the
// daemon entirely — which is what CI uses. So our own CI could not invoke the very
// check it is supposed to be judged by. It ran the full suite anyway, but only because a
// CI checkout starts with a COLD impact DB; warm that cache and the same green
// would silently start coming from a subset.
//
// These drive the REAL run-once driver (`RunOnceCheck.runOnceAndVerdict`), not a
// pure helper beside it. A repo with a project but NO test projects has no
// test-prune plugin, so the `test-scope` command does not exist and the scope reads
// `ScopeUnknown` — the "I could not establish what ran" case. `confirm` must
// refuse it (exit 3); the inner loop is allowed to tolerate it (exit 0), because a
// repo with no tests configured has no tests to run and punishing it would be
// nonsense.
// ---------------------------------------------------------------------------

/// A repo with one discoverable, loadable project and NO test projects.
let private withProjectOnlyRepo (name: string) (f: string -> 'a) : 'a =
    withTempDir name (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "MyProject.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>"""
        )

        f tmpDir)

/// The cheapest config that still registers a plugin: the read-only format check —
/// in-process (no `dotnet` spawn), and a no-op on a repo with no `.fs` files.
///
/// It has to register SOMETHING. `waitForAllTerminal` is built on `isAllTerminal`,
/// which is false for an EMPTY plugin map ("no plugins registered yet" is not
/// "everything finished"), so a genuinely plugin-free daemon never settles and
/// `RunOnce` blocks until its 30-minute timeout. Nothing to do with `confirm` — but it
/// is exactly the kind of incidental hang that gets a test quarantined rather than
/// understood, so it is named here.
///
/// No build, no lint, and above all NO TESTS: with no test projects there is no
/// test-prune plugin, hence no `test-scope` command, hence no way to establish what
/// ran. That is the state `confirm` must refuse.
let private noTestProjectsConfig () : DaemonConfiguration =
    { defaultTestConfig () with
        Build = None
        Format = Check
        Lint = false
        Tests = None
        Cache = NoCache }

let private runOnceIn (checkMode: FsHotWatch.Cli.CheckVerdict.CheckMode) (repoRoot: string) : int =
    let createDaemon (root: string) =
        Daemon.createWith
            (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
            root
            Daemon.DaemonOptions.defaults

    FsHotWatch.Cli.RunOnceCheck.runOnceAndVerdict
        (fun _ -> "")
        checkMode
        false
        createDaemon
        repoRoot
        (noTestProjectsConfig ())
        None

[<Fact(Timeout = 60000)>]
let ``confirm --run-once REFUSES a verdict it has no full-suite evidence for`` () =
    // `confirm`'s whole reason to exist. No test-prune plugin ⇒ no `test-scope` command
    // ⇒ the scope cannot be established ⇒ `ScopeUnknown`, which is NOT full-suite and
    // therefore cannot reach a green. Exit 3 = UnearnedScope: nothing is reported
    // broken, and nothing is reported sound either.
    withProjectOnlyRepo "confirm-runonce-refuses" (fun repoRoot ->
        let exitCode = runOnceIn FsHotWatch.Cli.CheckVerdict.Confirmation repoRoot
        test <@ exitCode = 3 @>)

[<Fact(Timeout = 60000)>]
let ``check --run-once tolerates an unknown scope`` () =
    // The inner loop is allowed to test LESS. A repo with no tests configured is not a
    // failure of `check` — only of `confirm`. Same driver, same tree, DIFFERENT mode: this
    // is what pins that the mode is what decides, and not something incidental.
    withProjectOnlyRepo "check-runonce-tolerates" (fun repoRoot ->
        let exitCode = runOnceIn FsHotWatch.Cli.CheckVerdict.InnerLoop repoRoot
        test <@ exitCode = 0 @>)

[<Fact(Timeout = 60000)>]
let ``run-once publishes a verdict file that agrees with its exit code`` () =
    // Before AUTOMATION-117 the run-once path wrote NO verdict at all — so `fshw
    // verdict` after a CI run reported "no verdict on disk". The machine-readable
    // answer was missing from the one place a machine was reading.
    //
    // The file and the exit code are two renderings of ONE `CheckOutcome`, so this
    // pins that they cannot disagree: a refused confirm must not leave a green on disk.
    withProjectOnlyRepo "runonce-verdict-file" (fun repoRoot ->
        let exitCode = runOnceIn FsHotWatch.Cli.CheckVerdict.Confirmation repoRoot

        match FsHotWatch.Cli.Verdict.read repoRoot with
        | FsHotWatch.Cli.Verdict.Reading.Found v ->
            test <@ v.ExitCode = exitCode @>
            test <@ v.Command = FsHotWatch.Cli.Verdict.Confirm @>
            // An unearned scope is `incomplete`, NEVER green.
            test <@ FsHotWatch.Cli.Verdict.Outcome.tag v.Outcome = "incomplete" @>
            test <@ v.Scope = FsHotWatch.Cli.IpcParsing.ScopeUnknown @>
        | other -> failwithf "expected a published verdict, got %A" other)

// ---------------------------------------------------------------------------
// RunOnceCheck — `confirm`'s scope commands over the IN-PROCESS transport.
//
// The daemon path reaches `test-scope`/`set-scope` over a socket; run-once reaches
// the same commands on the same plugin host, directly. These pin the transport and
// the fail-closed reading, without a daemon, a build, or a test run.
// ---------------------------------------------------------------------------

/// A host carrying a test-prune-shaped plugin that answers the given commands.
let private hostWith (commands: (string * (string array -> string)) list) : FsHotWatch.PluginHost.PluginHost =
    let host =
        FsHotWatch.PluginHost.PluginHost.create
            (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>)
            "/tmp/runoncecheck"

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, unit> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "fake-test-prune"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands =
            commands
            |> List.map (fun (name, f) -> name, (fun _ctx _state (args: string array) -> async { return f args }))
          Subscriptions = FsHotWatch.PluginFramework.PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host

[<Fact(Timeout = 15000)>]
let ``readTestRun parses a full-suite reply from the in-process host`` () =
    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"full","ranProjects":3,"totalProjects":3}""" ]

    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun host
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.FullSuite 3 @>

[<Fact(Timeout = 15000)>]
let ``readTestRun reports an impact-filtered run as filtered, never as full`` () =
    // The whole point of `confirm`. A filtered run must not be readable as evidence.
    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.TestScopeCommand,
              fun _ -> """{"scope":"filtered","ranProjects":1,"totalProjects":3}""" ]

    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun host
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.ImpactFiltered(1, 3) @>

[<Fact(Timeout = 15000)>]
let ``readTestRun on a host with NO test-scope command is ScopeUnknown, not full-suite`` () =
    // AUTOMATION-129 in miniature: the command `confirm` asks for did not exist, the host
    // returned nothing, and that silence must read as "I could not establish what ran" —
    // which `confirm` refuses. It must never round up to full-suite.
    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun (hostWith [])
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.ScopeUnknown @>

[<Fact(Timeout = 15000)>]
let ``readTestRun on a THROWING test-scope command is ScopeUnknown`` () =
    // Fail closed: a command that blows up has not established a scope.
    let host =
        hostWith [ FsHotWatch.Cli.IpcParsing.TestScopeCommand, fun _ -> failwith "SQLITE_BUSY" ]

    let report = FsHotWatch.Cli.RunOnceCheck.readTestRun host
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.ScopeUnknown @>

[<Fact(Timeout = 15000)>]
let ``requestFullSuiteScope sends set-scope full to the host`` () =
    // The AUTOMATION-129 regression, pinned: `confirm` must address the COMMAND
    // (`set-scope`), not the plugin (`test-prune`). It once passed the plugin name in the
    // command slot, so the host resolved nothing, the request never landed, and `confirm`
    // could never establish a full-suite scope on any repo, ever. It failed SAFE — which
    // is why nothing caught it for its whole life.
    let mutable received: string list = []

    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.SetScopeCommand,
              fun args ->
                  received <- List.ofArray args
                  """{"scope":"full"}""" ]

    FsHotWatch.Cli.RunOnceCheck.requestFullSuiteScope host

    test <@ received = [ FsHotWatch.Cli.IpcParsing.FullSuiteScopeArgs ] @>

[<Fact(Timeout = 15000)>]
let ``requestFullSuiteScope survives a host that has no set-scope command`` () =
    // A repo with no test projects has no such command. That is not a crash — it is a
    // a check with nothing to judge, and `readTestRun` will refuse it on the evidence.
    FsHotWatch.Cli.RunOnceCheck.requestFullSuiteScope (hostWith [])

[<Fact(Timeout = 15000)>]
let ``requestFullRun asks the host to run EVERY project — no filter, no selection`` () =
    // `confirm`'s teeth. `run-tests` with an empty payload means "all configured projects,
    // unfiltered" — a filter or a project list here would be `confirm` quietly narrowing
    // the very thing it exists to widen.
    let mutable received: string list = []

    let host =
        hostWith
            [ FsHotWatch.Cli.IpcParsing.RunTestsCommand,
              fun args ->
                  received <- List.ofArray args
                  """{"status":"ok"}""" ]

    FsHotWatch.Cli.RunOnceCheck.requestFullRun host

    test <@ received = [ "{}" ] @>

[<Fact(Timeout = 15000)>]
let ``requestFullRun survives a host with no run-tests command`` () =
    // Nothing to force. Not a crash — `readTestRun` refuses it on the evidence instead.
    FsHotWatch.Cli.RunOnceCheck.requestFullRun (hostWith [])

[<Fact(Timeout = 15000)>]
let ``requestFullRun survives a THROWING run-tests command`` () =
    // A forced run that blows up has produced no evidence. `confirm` must still reach its
    // verdict (and refuse), never die with a stack trace on the way there.
    let host =
        hostWith [ FsHotWatch.Cli.IpcParsing.RunTestsCommand, fun _ -> failwith "runner exploded" ]

    FsHotWatch.Cli.RunOnceCheck.requestFullRun host
