module FsHotWatch.Tests.FormatCheckPluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.ProcessHelper
open FsHotWatch.Fantomas.FantomasTool
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------
//
// Two kinds of test live here. The PINNED-REPO tests run the real `dotnet tool run
// fantomas` from a temp repository whose manifest is a copy of this repository's — so
// the tool resolves to the exact version the repo's own `dotnet tool restore` put in
// the NuGet cache, and what the plugin says is what CI's `dotnet fantomas --check`
// says. The FAKE-RUNNER tests substitute a recorder for the process, to pin the
// contract at the seam (which pin, which arguments, how each outcome is rendered)
// without a NuGet cache in the loop.

/// This checkout's root — the manifest the pinned-repo fixtures copy.
let private thisRepoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

/// The version this repository pins, read the same way the plugin reads it.
let private thisRepoPin =
    match readPin thisRepoRoot with
    | Ok p -> p
    | Error e -> failwith $"this repository must pin fantomas for the format tests to run: %A{e}"

/// A temp repository that pins the SAME fantomas this repository pins.
let private withPinnedRepo (prefix: string) (body: string -> 'a) : 'a =
    withTempDir prefix (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".config")) |> ignore

        File.Copy(
            Path.Combine(thisRepoRoot, ".config", "dotnet-tools.json"),
            Path.Combine(dir, ".config", "dotnet-tools.json")
        )

        body dir)

/// A temp repository whose manifest pins a version that exists nowhere — for the
/// fake-runner tests, where the pin is an input to be echoed, not a tool to run.
let private withFakePin (prefix: string) (version: string) (body: string -> 'a) : 'a =
    withTempDir prefix (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".config")) |> ignore

        File.WriteAllText(
            Path.Combine(dir, ".config", "dotnet-tools.json"),
            $"""{{ "version": 1, "isRoot": true, "tools": {{ "fantomas": {{ "version": "%s{version}", "commands": ["fantomas"] }} }} }}"""
        )

        body dir)

/// The evidence line the plugin must print for a repository at `dir`.
let private evidenceFor (_dir: string) (version: string) =
    let manifest = Path.Combine(".config", "dotnet-tools.json")
    $"dotnet fantomas %s{version} (pinned in %s{manifest})"

/// `dotnet tool run fantomas --check <file>` run DIRECTLY — the oracle the plugin
/// must agree with. Exit 0 = clean, 99 = needs formatting.
let private directCheck (repoRoot: string) (file: string) : int =
    match
        runProcess
            "dotnet"
            $"tool run fantomas --check \"%s{file}\""
            repoRoot
            []
            (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))
    with
    | Succeeded _ -> 0
    | Failed(code, _) -> code
    | TimedOut _ -> failwith "direct fantomas check timed out"

/// Runner that records every invocation and answers with `outcome`.
let private recorder (outcome: ProcessOutcome) =
    let calls = ResizeArray<FantomasPin * string * string * TimeSpan>()

    let runner: Runner =
        fun pin args workDir timeout ->
            calls.Add((pin, args, workDir, timeout))
            outcome

    runner, (fun () -> List.ofSeq calls)

let private waitCompleted (host: PluginHost) (timeoutMs: int) =
    waitUntil
        (fun () ->
            match host.GetStatus("format-check") with
            | Some(Completed _) -> true
            | _ -> false)
        timeoutMs

let private waitTerminal (host: PluginHost) (timeoutMs: int) =
    waitUntil
        (fun () ->
            match host.GetStatus("format-check") with
            | Some(Completed _)
            | Some(PluginStatus.Failed _) -> true
            | _ -> false)
        timeoutMs

let private summaryOf (host: PluginHost) : string =
    match host.GetStatus("format-check") with
    | Some(Completed(_, verdict)) -> verdict.Summary
    | other -> failwith $"expected format-check Completed, got %A{other}"

let private unformattedCount (host: PluginHost) : string =
    (host.RunCommand("unformatted", [||]) |> Async.RunSynchronously).Value

/// The shape from the AUTOMATION-447 report: a fully-applied call split one argument
/// per line although it fits in 120 columns. Pinned Fantomas joins it back onto one
/// line; anything that leaves it alone is not the pinned Fantomas.
let private reflowFixture =
    String.concat
        "\n"
        [ "module Fixture"
          ""
          "let runOnceAndVerdictWith a b c d e f g h = a + b + c + d + e + f + g + h"
          ""
          "let run (runOnce: int) (render: int) (mode: int) (warn: int) (create: int) (root: int) (config: int) ="
          "    runOnceAndVerdictWith"
          "        runOnce"
          "        render"
          "        mode"
          "        warn"
          "        create"
          "        root"
          "        config"
          "        0"
          "" ]

// ---------------------------------------------------------------------------
// Basics
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = createFormatCheck "/tmp" None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "format-check" @>

[<Fact(Timeout = 15000)>]
let ``unformatted command returns zero count when no files processed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    host.RegisterHandler(createFormatCheck "/tmp" None)
    test <@ (unformattedCount host).Contains("\"count\": 0") @>

[<Fact(Timeout = 20000)>]
let ``format check handles non-source change events without crashing`` () =
    // No manifest under /tmp — and none is needed: an event with nothing to check
    // completes as `no files to check` without consulting a formatter.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    host.RegisterHandler(createFormatCheck "/tmp" None)

    host.EmitFileChanged(ProjectChanged [ "/tmp/Test.fsproj" ])
    host.EmitFileChanged(SolutionChanged)

    waitUntil
        (fun () ->
            (not (host.AnyPluginBusy()))
            && (match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false))
        17000

    test <@ summaryOf host = "no files to check" @>

[<Fact(Timeout = 15000)>]
let ``format check handles non-existent source file gracefully`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    host.RegisterHandler(createFormatCheck "/tmp" None)

    host.EmitFileChanged(SourceChanged [ "/tmp/nonexistent/Fake.fs" ])
    waitCompleted host 5000

    test <@ summaryOf host = "no files to check" @>
    test <@ (unformattedCount host).Contains("\"count\": 0") @>

// ---------------------------------------------------------------------------
// AUTOMATION-447 — the plugin runs the PINNED Fantomas, says so, or refuses
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``format-check hands the manifest's version to the runner and names it in the summary`` () =
    withFakePin "fmt-agree" "9.9.9-agreement" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\n\nlet x = 1\n")
        let runner, calls = recorder (Succeeded(ProcessOutput.Drained ""))

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheckWith runner dir (Some 9))
        host.EmitFileChanged(SourceChanged [ file ])
        waitCompleted host 10000

        // What was run: the pin the manifest names, `--check`, this file, from the
        // repo root, under the configured budget.
        test <@ calls () |> List.map (fun (p, _, _, _) -> p.Version) = [ "9.9.9-agreement" ] @>
        test <@ calls () |> List.map (fun (_, args, _, _) -> args) = [ $"--check \"%s{file}\"" ] @>
        test <@ calls () |> List.map (fun (_, _, wd, _) -> wd) = [ dir ] @>
        test <@ calls () |> List.map (fun (_, _, _, t) -> t) = [ TimeSpan.FromSeconds 9.0 ] @>

        // And the evidence says so.
        let evidence = evidenceFor dir "9.9.9-agreement"
        test <@ summaryOf host = $"format OK (1 checked) — %s{evidence}" @>)

[<Fact(Timeout = 15000)>]
let ``format-check refuses with a typed reason when the repository pins no fantomas`` () =
    withTempDir "fmt-nopin" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\nlet   x=1\n")
        let runner, calls = recorder (Succeeded(ProcessOutput.Drained ""))

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheckWith runner dir None)
        host.EmitFileChanged(SourceChanged [ file ])
        waitTerminal host 10000

        // Nothing ran, and the status says why — naming the file to fix.
        test <@ List.isEmpty (calls ()) @>

        match host.GetStatus("format-check") with
        | Some(PluginStatus.Failed(reason, _, verdict)) ->
            test <@ reason.Contains(Path.Combine(dir, ".config", "dotnet-tools.json")) @>
            test <@ reason.Contains "dotnet tool install fantomas" @>
            test <@ verdict.Summary.StartsWith "format check refused:" @>
        | other -> failwith $"expected a refusal, got %A{other}"

        // A refusal is not a verdict about the file: no ledger entry either way.
        test <@ host.GetErrors() |> Map.tryFind file = None @>
        test <@ (unformattedCount host).Contains("\"count\": 0") @>)

[<Fact(Timeout = 15000)>]
let ``format-check fails, naming dotnet tool restore, when the pinned version is not restored`` () =
    withFakePin "fmt-unrestored" "1.2.3-nowhere" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\n\nlet x = 1\n")

        let runner, _ =
            recorder (
                Failed(
                    1,
                    ProcessOutput.Drained "Run \"dotnet tool restore\" to make the \"fantomas\" command available."
                )
            )

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheckWith runner dir None)
        host.EmitFileChanged(SourceChanged [ file ])
        waitTerminal host 10000

        match host.GetStatus("format-check") with
        | Some(PluginStatus.Failed(reason, _, _)) ->
            test <@ reason.Contains "1.2.3-nowhere" @>
            test <@ reason.Contains "dotnet tool restore" @>
        | other -> failwith $"expected a failure, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``format-check records TimedOut when the tool is killed at its budget`` () =
    withFakePin "fmt-to" "7.0.5" (fun dir ->
        let file = Path.Combine(dir, "Slow.fs")
        File.WriteAllText(file, "module Slow\n")

        let runner, _ =
            recorder (
                TimedOut(
                    TimeSpan.FromSeconds 1.0,
                    ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0),
                    KillOutcome.Killed
                )
            )

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheckWith runner dir (Some 1))
        host.EmitFileChanged(SourceChanged [ file ])
        waitForTerminalStatus host "format-check" 5000

        match host.GetActivitySnapshot("format-check").LastRun with
        | Some r ->
            match r.Outcome with
            | RunOutcome.TimedOut _ -> ()
            | other -> Assert.Fail($"Expected TimedOut, got {other}")
        | None -> Assert.Fail "Expected LastRun record")

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor hands the manifest's version to the runner and reports it as evidence`` () =
    withFakePin "pre-agree" "9.9.9-agreement" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\n\nlet x = 1\n")
        let runner, calls = recorder (Succeeded(ProcessOutput.Drained ""))

        let preprocessor =
            FormatPreprocessor(timeoutSec = 9, runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Ok result ->
            test <@ List.isEmpty result.Modified @>
            test <@ result.Considered = 1 @>
            test <@ result.Evidence = evidenceFor dir "9.9.9-agreement" @>
        | Error e -> failwith $"expected a run, got %s{e}"

        test
            <@
                calls () |> List.map (fun (p, args, wd, t) -> p.Version, args, wd, t) = [ "9.9.9-agreement",
                                                                                          $"\"%s{file}\"",
                                                                                          dir,
                                                                                          TimeSpan.FromSeconds 9.0 ]
            @>)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor refuses, naming dotnet tool restore, when the pinned version is not restored`` () =
    withFakePin "pre-unrestored" "1.2.3-nowhere" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\n\nlet x = 1\n")

        let runner, _ =
            recorder (
                Failed(
                    1,
                    ProcessOutput.Drained "Run \"dotnet tool restore\" to make the \"fantomas\" command available."
                )
            )

        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor
        test <@ preprocessor.Name = "format" @>

        match preprocessor.Process [ file ] dir with
        | Error reason ->
            test <@ reason.Contains "1.2.3-nowhere" @>
            test <@ reason.Contains "dotnet tool restore" @>
        | Ok r -> failwith $"expected a refusal, got %A{r}")

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor refuses when the repository pins no fantomas`` () =
    withTempDir "pre-nopin" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        let original = "module A\nlet   x=1\n"
        File.WriteAllText(file, original)
        let runner, calls = recorder (Succeeded(ProcessOutput.Drained ""))

        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Error reason ->
            test <@ reason.Contains "dotnet-tools.json" @>
            test <@ reason.Contains "fantomas" @>
        | Ok r -> failwith $"expected a refusal, got %A{r}"

        test <@ List.isEmpty (calls ()) @>
        test <@ File.ReadAllText file = original @>)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor leaves a file alone when the tool exceeds its timeout`` () =
    // A timed-out format must never write a half-formatted document — and must not
    // stop the batch: the daemon's change agent runs inside this call.
    withFakePin "pre-to" "7.0.5" (fun dir ->
        let file = Path.Combine(dir, "Bad.fs")
        let original = "module Bad\nlet   x=1\nlet   y   =   2\n"
        File.WriteAllText(file, original)

        let runner, _ =
            recorder (
                TimedOut(
                    TimeSpan.FromSeconds 1.0,
                    ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0),
                    KillOutcome.Killed
                )
            )

        let preprocessor =
            FormatPreprocessor(timeoutSec = 1, runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Ok result ->
            test <@ List.isEmpty result.Modified @>
            test <@ result.Considered = 1 @>
        | Error e -> failwith $"a timeout is not a refusal: %s{e}"

        test <@ File.ReadAllText(file) = original @>)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor skips non-fs files without consulting the tool`` () =
    withFakePin "pre-nonfs" "7.0.5" (fun dir ->
        let file = Path.Combine(dir, "readme.txt")
        File.WriteAllText(file, "hello world")
        let runner, calls = recorder (Succeeded(ProcessOutput.Drained ""))

        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Ok result ->
            test <@ List.isEmpty result.Modified @>
            test <@ result.Considered = 0 @>
        | Error e -> failwith e

        test <@ List.isEmpty (calls ()) @>)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor handles a non-existent file as nothing to consider`` () =
    withFakePin "pre-missing" "7.0.5" (fun dir ->
        let runner, calls = recorder (Succeeded(ProcessOutput.Drained ""))
        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ Path.Combine(dir, "nonexistent-file-xyz.fs") ] dir with
        | Ok result -> test <@ result.Considered = 0 @>
        | Error e -> failwith e

        test <@ List.isEmpty (calls ()) @>)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor dispose is callable`` () =
    let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
    preprocessor.Dispose()

// ---------------------------------------------------------------------------
// AUTOMATION-447 — the regression fixture: the plugin AGREES with the pinned tool
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``format-check and the preprocessor agree with a direct pinned fantomas --check on a shape it reflows`` () =
    withPinnedRepo "fmt-oracle" (fun dir ->
        let file = Path.Combine(dir, "Fixture.fs")
        File.WriteAllText(file, reflowFixture)

        // The oracle: the pinned tool, run the way CI runs it, rejects the shape.
        test <@ directCheck dir file = NeedsFormattingExitCode @>

        // The plugin says the same, and says which tool it asked.
        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheck dir None)
        host.EmitFileChanged(SourceChanged [ file ])
        waitCompleted host 30000

        test <@ summaryOf host = $"1 of 1 files need formatting — %s{evidenceFor dir thisRepoPin.Version}" @>
        test <@ (unformattedCount host).Contains("\"count\": 1") @>

        // The preprocessor rewrites it with the same tool …
        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Ok result ->
            test <@ result.Modified = [ file ] @>
            test <@ result.Evidence = evidenceFor dir thisRepoPin.Version @>
        | Error e -> failwith e

        // … after which the oracle is clean, and so is the plugin. The reflow the
        // report describes (one argument per line → one line) is what happened.
        test <@ directCheck dir file = 0 @>

        test
            <@ File.ReadAllText(file).Contains "runOnceAndVerdictWith runOnce render mode warn create root config 0" @>

        let second = beginAwaitNextTerminal host "format-check"
        host.EmitFileChanged(SourceChanged [ file ])
        test <@ second.Wait(TimeSpan.FromSeconds 30.0) @>
        test <@ summaryOf host = $"format OK (1 checked) — %s{evidenceFor dir thisRepoPin.Version}" @>
        test <@ (unformattedCount host).Contains("\"count\": 0") @>)

[<Fact(Timeout = 60000)>]
let ``the pinned tool the plugin runs reports the version the manifest pins`` () =
    // `dotnet tool run` resolves the version from the manifest by construction; this
    // pins that construction against the binary's own answer, so a future resolver
    // change (a global fallback, a roll-forward) would surface here.
    withPinnedRepo "fmt-version" (fun dir ->
        match
            runProcess "dotnet" "tool run fantomas --version" dir [] (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))
        with
        | Succeeded output -> test <@ (ProcessOutput.text output).Contains $"v%s{thisRepoPin.Version}" @>
        | other -> failwith $"fantomas --version failed: %A{other}")

// ---------------------------------------------------------------------------
// Behaviour over the real pinned tool
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``FormatPreprocessor formats unformatted file`` () =
    withPinnedRepo "fmt" (fun dir ->
        let file = Path.Combine(dir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Ok result -> test <@ result.Modified = [ file ] @>
        | Error e -> failwith e

        test <@ File.ReadAllText(file) = "module Bad\n\nlet x = 1\nlet y = 2\n" @>)

[<Fact(Timeout = 30000)>]
let ``FormatPreprocessor skips already formatted file`` () =
    withPinnedRepo "fmt" (fun dir ->
        let file = Path.Combine(dir, "Good.fs")
        File.WriteAllText(file, "module Good\n\nlet x = 1\nlet y = 2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Ok result ->
            test <@ List.isEmpty result.Modified @>
            test <@ result.Considered = 1 @>
        | Error e -> failwith e)

[<Fact(Timeout = 30000)>]
let ``FormatPreprocessor reports a file the tool cannot parse and leaves it alone`` () =
    withPinnedRepo "fmt-err" (fun dir ->
        let file = Path.Combine(dir, "Bad.fs")
        let original = "module \x00\x00\x00"
        File.WriteAllText(file, original)

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] dir with
        | Ok result -> test <@ List.isEmpty result.Modified @>
        | Error e -> failwith $"a parse error is a per-file finding, not a refusal: %s{e}"

        test <@ File.ReadAllText(file) = original @>)

[<Fact(Timeout = 30000)>]
let ``format check reports a file the tool cannot parse as a ledger error`` () =
    withPinnedRepo "fmtchk-err" (fun dir ->
        let file = Path.Combine(dir, "Bad.fs")
        File.WriteAllText(file, "module \x00\x00\x00")

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheck dir None)
        host.EmitFileChanged(SourceChanged [ file ])
        waitTerminal host 25000

        test <@ (summaryOf host).StartsWith "1 of 1 files could not be formatted" @>

        let entries =
            host.GetErrors()
            |> Map.tryFind file
            |> Option.defaultValue []
            |> List.filter (fun (plugin, _) -> plugin = "format-check")

        test
            <@
                entries
                |> List.exists (fun (_, e) ->
                    e.Severity = FsHotWatch.ErrorLedger.Error
                    && e.Message.Contains "could not format")
            @>)

[<Fact(Timeout = 30000)>]
let ``format check detects formatting change even with same commit ID`` () =
    withPinnedRepo "fmtchk-cache" (fun dir ->
        let file = Path.Combine(dir, "Test.fs")

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheck dir None)

        // First: file is unformatted
        File.WriteAllText(file, "module Test\nlet   x = 1\n")
        host.EmitFileChanged(SourceChanged [ file ])
        waitCompleted host 25000
        test <@ (unformattedCount host).Contains("\"count\": 1") @>

        // Second: file is now formatted, but commit ID hasn't changed
        let second = beginAwaitNextTerminal host "format-check"
        File.WriteAllText(file, "module Test\n\nlet x = 1\n")
        host.EmitFileChanged(SourceChanged [ file ])
        test <@ second.Wait(TimeSpan.FromSeconds 25.0) @>
        test <@ (unformattedCount host).Contains("\"count\": 0") @>)

[<Fact(Timeout = 30000)>]
let ``format check reports unformatted files to error ledger`` () =
    withPinnedRepo "fmtchk-ledger" (fun dir ->
        let file = Path.Combine(dir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheck dir None)
        host.EmitFileChanged(SourceChanged [ file ])
        waitCompleted host 25000

        let formatErrors =
            host.GetErrors()
            |> Map.tryFind file
            |> Option.defaultValue []
            |> List.filter (fun (plugin, _) -> plugin = "format-check")

        test <@ not formatErrors.IsEmpty @>
        // The ledger entry names the formatter whose opinion it is.
        test
            <@
                formatErrors
                |> List.forall (fun (_, e) -> e.Message.Contains $"dotnet fantomas %s{thisRepoPin.Version}")
            @>)

[<Fact(Timeout = 40000)>]
let ``format check clears errors when file becomes formatted`` () =
    withPinnedRepo "fmtchk-clear" (fun dir ->
        let file = Path.Combine(dir, "Fix.fs")
        File.WriteAllText(file, "module Fix\nlet   x=1\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(createFormatCheck dir None)

        let firstTerminal = beginAwaitTerminal host "format-check"
        host.EmitFileChanged(SourceChanged [ file ])
        test <@ firstTerminal.Wait(TimeSpan.FromSeconds 25.0) @>
        test <@ not (host.GetErrors()).IsEmpty @>

        // Subscribe before emitting: this small clean-file run can otherwise pass
        // through Running to Completed between two status polls.
        let secondTerminal = beginAwaitNextTerminal host "format-check"
        File.WriteAllText(file, "module Fix\n\nlet x = 1\n")
        host.EmitFileChanged(SourceChanged [ file ])
        test <@ secondTerminal.Wait(TimeSpan.FromSeconds 25.0) @>

        let fileErrors = host.GetErrors() |> Map.tryFind file

        test
            <@
                fileErrors.IsNone
                || fileErrors.Value
                   |> List.filter (fun (p, _) -> p = "format-check")
                   |> List.isEmpty
            @>)

// ---------------------------------------------------------------------------
// Cache key — content, pin and config
// ---------------------------------------------------------------------------

let private cacheKeyOf (dir: string) =
    (createFormatCheck dir None).CacheKey
    |> Option.defaultWith (fun () -> failwith "expected CacheKey")

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns None when any input file is unreadable (F2)`` () =
    // F2 — the cacheKey lambda used to substitute "" for an unreadable file, which
    // collided with a real empty file and across distinct read-failure causes,
    // producing stale "format OK" hits on transient locks. None bypasses the cache.
    withFakePin "f2-missing" "7.0.5" (fun dir ->
        let nonExistentFile = Path.Combine(dir, "missing.fs")
        test <@ cacheKeyOf dir (FileChanged(SourceChanged [ nonExistentFile ])) = None @>)

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns None when one of multiple files is unreadable (F2 short-circuit)`` () =
    withFakePin "f2-mixed" "7.0.5" (fun dir ->
        let goodFile = Path.Combine(dir, "Good.fs")
        File.WriteAllText(goodFile, "module Good\n")
        let missingFile = Path.Combine(dir, "missing.fs")
        // "AAA-missing" sorts BEFORE "ZZZ-good", so Option.bind sees None first and
        // skips the still-readable successor.
        let sortedFirstMissing = Path.Combine(dir, "AAA-still-missing.fs")
        let sortedSecondGood = Path.Combine(dir, "ZZZ-good.fs")
        File.WriteAllText(sortedSecondGood, "module ZZZ\n")

        test <@ cacheKeyOf dir (FileChanged(SourceChanged [ goodFile; missingFile ])) = None @>
        test <@ cacheKeyOf dir (FileChanged(SourceChanged [ sortedFirstMissing; sortedSecondGood ])) = None @>)

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns Some for readable files under a pinned repository`` () =
    withFakePin "f2-ok" "7.0.5" (fun dir ->
        let file = Path.Combine(dir, "F2Ok.fs")
        File.WriteAllText(file, "module F2Ok\n")
        test <@ (cacheKeyOf dir (FileChanged(SourceChanged [ file ]))).IsSome @>)

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey is None when the repository pins no fantomas`` () =
    // A refusal is re-earned on every event; it must never be replayed as a verdict.
    withTempDir "key-nopin" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\n")
        test <@ cacheKeyOf dir (FileChanged(SourceChanged [ file ])) = None @>)

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey changes with the pinned version and with the editorconfig`` () =
    // Same bytes, different formatter or different settings = a different answer, so
    // a replayed `format OK` across either edit would be the AUTOMATION-447 defect in
    // cached form.
    withFakePin "key-inputs" "7.0.5" (fun dir ->
        let file = Path.Combine(dir, "A.fs")
        File.WriteAllText(file, "module A\n")
        let event = FileChanged(SourceChanged [ file ])
        let baseline = cacheKeyOf dir event

        File.WriteAllText(
            Path.Combine(dir, ".config", "dotnet-tools.json"),
            """{ "version": 1, "tools": { "fantomas": { "version": "7.0.6", "commands": ["fantomas"] } } }"""
        )

        let bumped = cacheKeyOf dir event
        test <@ bumped.IsSome && bumped <> baseline @>

        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")
        let reconfigured = cacheKeyOf dir event
        test <@ reconfigured.IsSome && reconfigured <> bumped @>)

// ---------------------------------------------------------------------------
// AUTOMATION-191 — a cached format-check verdict may only assert what its key covers.
// ---------------------------------------------------------------------------
//
// Format-check subscribes to `FileChanged`, which the framework keys as a
// WHOLE-RUN entry (`File = None`), so its stored verdict replays VERBATIM — the
// AUTOMATION-186 derive-from-ledger path only ever reached per-file entries.
// A verbatim replay is only honest if the summary is a function of the key, and
// the key is a content merkle of THIS event's files. So the invariant pinned
// here is the one AUTOMATION-245 stated for the build cache: a cache hit must be
// indistinguishable from having run.
//
// Both tests below compare a REPLAYED summary against the summary a cold-cache
// run over the same inputs produces. The pair is deliberate: the first proves a
// stale claim cannot survive the replay, the second proves the same comparison
// still SEES a genuine finding — an equality that a summary frozen to one
// constant would also satisfy is not a detector.

/// Run one format-check session over `batches`, one `SourceChanged` per batch,
/// waiting for each to land before the next is emitted (the summary observed
/// otherwise belongs to whichever batch finished last). Returns the host so the
/// caller can read the terminal summary and the ledger it must agree with.
let private runFormatCheckBatches
    (tmpDir: string)
    (taskCache: FsHotWatch.TaskCache.ITaskCache option)
    (batches: string list list)
    : PluginHost =
    let host =
        match taskCache with
        | Some cache -> PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)
        | None -> PluginHost(Unchecked.defaultof<_>, tmpDir)

    host.RegisterHandler(createFormatCheck tmpDir None)

    for batch in batches do
        host.EmitFileChanged(SourceChanged batch)

        // `AnyPluginBusy` is raised synchronously by the emit, so this cannot
        // pass on the PREVIOUS batch's still-standing `Completed`.
        waitUntil
            (fun () ->
                (not (host.AnyPluginBusy()))
                && (match host.GetStatus("format-check") with
                    | Some(Completed _) -> true
                    | _ -> false))
            30000

    host

/// How many files the plugin's LIVE ledger — the set the verdict gates on —
/// currently holds a format-check finding for.
let private formatCheckLedgerCount (host: PluginHost) : int =
    host.GetErrors()
    |> Map.toList
    |> List.sumBy (fun (_, entries) ->
        entries
        |> List.filter (fun (plugin, _) -> plugin = "format-check")
        |> List.length)

[<Fact(Timeout = 120000)>]
let ``a replayed format-check verdict cannot claim files its cache key never covered`` () =
    withPinnedRepo "fmtchk-stale" (fun tmpDir ->
        let bad = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(bad, "module Bad\nlet   x=1\nlet   y   =   2\n")
        let good = Path.Combine(tmpDir, "Good.fs")
        File.WriteAllText(good, "module Good\n\nlet x = 1\n")

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        // Session 1 mints the entry for `Good` while ANOTHER file is unformatted.
        // `Good`'s key covers `Good`'s bytes and nothing else, so anything the
        // stored summary says about `Bad` is outside its scope.
        runFormatCheckBatches tmpDir (Some cache) [ [ bad ]; [ good ] ] |> ignore

        // What a run over `Good` alone actually finds, with no cache in play.
        let fresh = runFormatCheckBatches tmpDir None [ [ good ] ]
        let freshSummary = summaryOf fresh
        test <@ formatCheckLedgerCount fresh = 0 @>

        // Same inputs, but served from session 1's entry.
        let replayed = runFormatCheckBatches tmpDir (Some cache) [ [ good ] ]
        let replayedSummary = summaryOf replayed

        // The replay landed a green ledger; a summary claiming otherwise would be
        // a `summary:` line contradicting the verdict it sits beside.
        test <@ formatCheckLedgerCount replayed = 0 @>
        test <@ replayedSummary = freshSummary + " (cached)" @>)

[<Fact(Timeout = 120000)>]
let ``a replayed format-check verdict still reports a finding that is genuinely current`` () =
    // The positive control for the test above. Same plugin, same replay path, same
    // comparison — but the cached entry's claim is TRUE at replay time, so it must
    // survive intact and still name the finding.
    withPinnedRepo "fmtchk-current" (fun tmpDir ->
        let bad = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(bad, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        runFormatCheckBatches tmpDir (Some cache) [ [ bad ] ] |> ignore

        let fresh = runFormatCheckBatches tmpDir None [ [ bad ] ]
        let freshSummary = summaryOf fresh
        test <@ formatCheckLedgerCount fresh = 1 @>

        let replayed = runFormatCheckBatches tmpDir (Some cache) [ [ bad ] ]
        let replayedSummary = summaryOf replayed

        // Replayed from cache, and still red: the entry's error replay put the
        // finding back in the ledger.
        test <@ formatCheckLedgerCount replayed = 1 @>
        test <@ replayedSummary = freshSummary + " (cached)" @>

        // And the summary names it, rather than passing the comparison by being a
        // constant that never mentions a finding at all.
        test <@ replayedSummary.Contains "need formatting" @>)
