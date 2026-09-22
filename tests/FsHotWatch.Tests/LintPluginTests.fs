module FsHotWatch.Tests.LintPluginTests

open Xunit
open Swensen.Unquote
open FSharpLint.Application
open FSharpLint.Framework.Suggestion
open FSharp.Compiler.Text
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.ErrorLedger
open FsHotWatch.Lint.LintPlugin
open FsHotWatch.Tests.TestHelpers

/// The spelling the plugin framework keys a per-file cache entry by: REPO-RELATIVE,
/// so an entry survives being read in another checkout. A test that
/// stores under the absolute path stores under a key the framework will never look up.
let private compositeFileKey (repoRoot: string) (file: string) =
    FsHotWatch.CachePathIdentity.keyOf (Some repoRoot) file


[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = create None None None None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "lint" @>

// the lint plugin (a FSharpLint path, like the analyzers plugin)
// must skip compile items outside the repo root — NuGet-injected `_content`
// source etc. — since they're not ours to lint. The `Some repoRoot` enables the
// skip; the `includeOutsideRepo` config maps to `None` to disable it.
[<Fact(Timeout = 20000)>]
let ``lint skips compile items outside the repo`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    // The runner is reached only for files that pass the skip — count them.
    let mutable lintedCount = 0

    let runner (_result: FileCheckResult) =
        System.Threading.Interlocked.Increment(&lintedCount) |> ignore
        Lint.LintResult.Success []

    let handler = create (Some "/my/repo") None (Some runner) None
    host.RegisterHandler(handler)

    // Out-of-repo NuGet _content compile item, emitted FIRST — must be skipped.
    host.EmitFileChecked(
        fakeFileCheckResult "/home/dev/.nuget/packages/xunit.v3.core.mtp-v1/3.2.2/_content/DefaultRunnerReporters.fs"
    )
    // Repo-owned file, emitted SECOND — IS linted, and its terminal status is the
    // deterministic sync point (per-plugin events are serialized, so once this
    // completes the skipped one was already dequeued and returned without linting).
    host.EmitFileChecked(fakeFileCheckResult "/my/repo/src/File.fs")
    waitForTerminalStatus host "lint" 15000

    // Exactly one file linted — the repo-owned one; the out-of-repo one was skipped.
    test <@ lintedCount = 1 @>

[<Fact(Timeout = 15000)>]
let ``warnings command returns zeroes when no files checked`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>
    test <@ result.Value.Contains("\"warnings\": 0") @>

[<Fact(Timeout = 15000)>]
let ``LintPlugin with configPath sets up lint params`` () =
    let handler = create None (Some "/tmp/nonexistent-config.json") None None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "lint" @>

[<Fact(Timeout = 20000)>]
let ``lint error path sets Failed status on null check results`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    // Explicitly null ParseResults to exercise the lint plugin's null-guard path.
    let fakeResult =
        { fakeFileCheckResult "/tmp/nonexistent/Fake.fs" with
            Source = ""
            ParseResults = Unchecked.defaultof<_> }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

    waitUntil
        (fun () ->
            match host.GetStatus("lint") with
            | Some(Failed _)
            | Some(Running _) -> true
            | _ -> false)
        15000

    let status = host.GetStatus("lint")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> ()
    | Running _ -> ()
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")

[<Fact(Timeout = 15000)>]
let ``warnings command with args passes through`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    // The warnings command ignores args, but verify it handles non-empty args
    let result =
        host.RunCommand("warnings", [| "--verbose" |]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>

[<Fact(Timeout = 15000)>]
let ``lint skips file with null ParseResults without crashing`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None None None None
    host.RegisterHandler(handler)

    let fakeResult =
        { fakeFileCheckResult "/tmp/test/Empty.fs" with
            Source = "module Empty"
            ParseResults = Unchecked.defaultof<_> }

    // Should not throw
    host.EmitFileChecked(fakeResult)

    waitUntil
        (fun () ->
            match host.GetStatus("lint") with
            | Some _ -> true
            | None -> false)
        15000

    let status = host.GetStatus("lint")
    test <@ status.IsSome @>

    // Should be Running (set at start of handler), not Failed
    match status.Value with
    | Failed(msg, _, _) -> Assert.Fail($"Should not fail -- got: %s{msg}")
    | _ -> ()

[<Fact(Timeout = 20000)>]
let ``lint handler times out when runner exceeds TimeoutSec`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let slowRunner (_result: FileCheckResult) =
        System.Threading.Thread.Sleep 3000
        Lint.LintResult.Success []

    let handler = create None None (Some slowRunner) (Some 1)
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/slow/File.fs")
    waitForTerminalStatus host "lint" 5000

    let snap = host.GetActivitySnapshot("lint")

    match snap.LastRun with
    | Some r ->
        match r.Outcome with
        | TimedOut _ -> ()
        | other -> Assert.Fail($"Expected TimedOut, got {other}")
    | None -> Assert.Fail "Expected LastRun record"

[<Fact(Timeout = 15000)>]
let ``lint runner returning Failure reports errors and sets Failed status`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let runner (_result: FileCheckResult) =
        Lint.LintResult.Failure(Lint.LintFailure.RunTimeConfigError "bad config")

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/Bad.fs")

    waitForTerminalStatus host "lint" 15000

    let status = host.GetStatus("lint")

    match status with
    | Some(Failed(msg, _, _)) -> test <@ msg.Contains("bad config") @>
    | other -> Assert.Fail($"Expected Failed, got: %A{other}")

    let errors = host.GetErrorsByPlugin("lint")
    test <@ errors |> Map.isEmpty |> not @>

[<Fact(Timeout = 20000)>]
let ``lint runner returning Success with warnings reports them to error ledger`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let range = Range.mkRange "Warn.fs" (Position.mkPos 10 4) (Position.mkPos 10 20)

    let warning: LintWarning =
        { Details =
            { Range = range
              Message = "Consider using List.isEmpty"
              SuggestedFix = None
              TypeChecks = [] }
          ErrorText = "FL0065"
          FilePath = "/tmp/test/Warn.fs"
          RuleName = "Hints"
          RuleIdentifier = "FL0065" }

    let runner (_result: FileCheckResult) = Lint.LintResult.Success [ warning ]

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/Warn.fs")

    waitForTerminalStatus host "lint" 15000

    let status = host.GetStatus("lint")

    match status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"Expected Completed, got: %A{other}")

    let errors = host.GetErrorsByPlugin("lint")
    let fileErrors = errors |> Map.tryFind "/tmp/test/Warn.fs"
    test <@ fileErrors.IsSome @>

    let entries = fileErrors.Value
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Message = "Consider using List.isEmpty" @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Warning @>
    test <@ entries.[0].Line = 10 @>
    test <@ entries.[0].Column = 4 @>

[<Fact(Timeout = 15000)>]
let ``lint runner returning Success with no warnings clears errors`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let runner (_result: FileCheckResult) = Lint.LintResult.Success []

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/Clean.fs")

    waitForTerminalStatus host "lint" 15000

    let status = host.GetStatus("lint")

    match status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"Expected Completed, got: %A{other}")

    let errors = host.GetErrorsByPlugin("lint")
    let fileErrors = errors |> Map.tryFind "/tmp/test/Clean.fs"
    // Either no entry or empty list
    test <@ fileErrors.IsNone || fileErrors.Value.IsEmpty @>

    // Verify warnings command reflects zero warnings
    let cmdResult = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ cmdResult.IsSome @>
    test <@ cmdResult.Value.Contains("\"warnings\": 0") @>

[<Fact(Timeout = 20000)>]
let ``warnings command reflects warning count after lint with warnings`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let range = Range.mkRange "A.fs" (Position.mkPos 1 0) (Position.mkPos 1 5)

    let mkWarning msg : LintWarning =
        { Details =
            { Range = range
              Message = msg
              SuggestedFix = None
              TypeChecks = [] }
          ErrorText = "FL0001"
          FilePath = "/tmp/test/A.fs"
          RuleName = "TestRule"
          RuleIdentifier = "FL0001" }

    let runner (_result: FileCheckResult) =
        Lint.LintResult.Success [ mkWarning "warn1"; mkWarning "warn2" ]

    let handler = create None None (Some runner) None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeFileCheckResult "/tmp/test/A.fs")

    waitForTerminalStatus host "lint" 15000

    waitForQuiescent host 30000

    let cmdResult = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ cmdResult.IsSome @>
    test <@ cmdResult.Value.Contains("\"files\": 1") @>
    test <@ cmdResult.Value.Contains("\"warnings\": 2") @>

[<Fact(Timeout = 15000)>]
let ``lint per-file cache replay derives its summary from the live ledger`` () =
    // Lint is a `FileChecked` plugin (per-file, `File = Some`), so it shares the
    // analyzers' defect class: its status summary is built from whole-session
    // accumulation. The framework fix reaches it through the SAME per-file
    // derive path — a replayed per-file entry must report EXACTLY the findings
    // the live ledger holds, never a stale stored count.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)

    let handler = create None None None None
    host.RegisterHandler(handler)

    let file = "/tmp/test/LintReplay.fs"
    let checkResult = fakeFileCheckResult file

    let cacheKey =
        ((handler.CacheKey.Value handler.Init) (FileChecked checkResult)).Value

    // Three live warnings for this file replay into the ledger.
    let findings =
        [ ErrorEntry.warningWithDetail "w1" "d"
          ErrorEntry.warningWithDetail "w2" "d"
          ErrorEntry.warningWithDetail "w3" "d" ]

    let entry: FsHotWatch.TaskCache.TaskCacheResult =
        { CacheKey = cacheKey
          Errors = [ file, findings ]
          Status = FsHotWatch.TaskCache.CachedFileCompleted(System.TimeSpan.FromMilliseconds 7.0)
          EmittedEvents = [] }

    cacheIface.Set
        { Plugin = "lint"
          File = Some(compositeFileKey "/tmp" file) }
        cacheKey
        entry

    host.EmitFileChecked(checkResult)
    waitForTerminalStatus host "lint" 15000

    let liveFindings =
        host.GetErrorsByPlugin("lint")
        |> Map.toList
        |> List.sumBy (fun (_, entries) -> List.length entries)

    test <@ liveFindings = 3 @>

    let summary =
        match host.GetStatus("lint") with
        | Some(Completed(_, v)) -> v.Summary
        | Some(Failed(_, _, v)) -> v.Summary
        | other -> failwith $"expected a terminal lint status, got %A{other}"

    let claimedFindings =
        let m = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+) findings")
        if m.Success then int m.Groups.[1].Value else 0

    Assert.True(
        (claimedFindings = liveFindings),
        $"lint replay summary must match the live ledger: summary=\"%s{summary}\" claims %d{claimedFindings}, live diagnostics has %d{liveFindings}"
    )

// A rediscovery that drops a file clears that file's findings in EVERY plugin ledger
// (`rediscoverAndClearRemoved` -> `ClearFileEverywhere`), and it clears them while the
// discovery coordinator is still `Rediscovering` — before the replacement model is
// published. A `FileChecked` captured under the OLD model and still sitting in the lint
// mailbox is folded after that clear, and nothing checks a dropped path again: the
// finding it re-reports is about a file outside the build and stands until the daemon
// restarts. So the fold must refuse what the current model did not stamp.
[<Fact(Timeout = 20000)>]
let ``lint refuses a FileChecked captured against a superseded model`` () =
    let repoRoot = "/my/repo"
    let removed = "/my/repo/src/Removed.fs"
    let present = "/my/repo/src/Present.fs"

    // Publishes the fixture model (generation 1) — the model both results below were
    // captured under.
    let host = createModelHost (Unchecked.defaultof<_>) repoRoot

    let linted = System.Collections.Concurrent.ConcurrentQueue<string>()

    let runner (result: FileCheckResult) =
        let file = AbsFilePath.value result.File
        linted.Enqueue file
        let range = Range.mkRange file (Position.mkPos 1 0) (Position.mkPos 1 5)

        let warning: LintWarning =
            { Details =
                { Range = range
                  Message = "Consider using List.isEmpty"
                  SuggestedFix = None
                  TypeChecks = [] }
              ErrorText = "FL0065"
              FilePath = file
              RuleName = "Hints"
              RuleIdentifier = "FL0065" }

        Lint.LintResult.Success [ warning ]

    let handler = create (Some repoRoot) None (Some runner) None
    host.RegisterHandler(handler)

    // The rediscovery lands: generation 2 no longer has Removed.fs, and the host has
    // already cleared its findings.
    host.WorkStore.PublishProjectModel(fixtureModelOf 2L)
    host.ClearFileEverywhere(removed)

    // Queued under generation 1 — the model that still had the file.
    host.EmitFileChecked(fakeFileCheckResult removed)

    // Positive control, stamped with the generation now in force. Emitted second, so
    // its terminal status is a sleep-free sync point: per-plugin events are serialized
    // by the MailboxProcessor, so the stale one was dequeued before this one completes.
    host.EmitFileChecked(
        { fakeFileCheckResult present with
            ModelGeneration = Some 2L }
    )

    waitForTerminalStatus host "lint" 15000

    // The superseded result never reached the linter...
    test <@ linted |> List.ofSeq = [ present ] @>

    let errors = host.GetErrorsByPlugin("lint")

    // ...so no finding was re-reported for the file the new model dropped...
    test <@ errors |> Map.containsKey removed |> not @>
    // ...and the current-generation result still reports its findings.
    test <@ (errors |> Map.tryFind present |> Option.map List.length) = Some 1 @>

// The refusal has two disjuncts and the test above exercises only one of them: a
// result stamped with a generation that is no longer in force. The other is a result
// carrying NO generation at all, which the daemon does not produce today but a plugin
// under test, a replayed cache entry, or a future emitter can. It is refused for a
// different reason than being out of date — nothing stamped it, so there is no model
// it can be said to describe, and admitting it would mean trusting a claim no one made.
[<Fact(Timeout = 20000)>]
let ``lint refuses a FileChecked that carries no model generation at all`` () =
    let repoRoot = "/my/repo"
    let unstamped = "/my/repo/src/Unstamped.fs"
    let present = "/my/repo/src/Present.fs"

    let host = createModelHost (Unchecked.defaultof<_>) repoRoot
    let linted = System.Collections.Concurrent.ConcurrentQueue<string>()

    let runner (result: FileCheckResult) =
        linted.Enqueue(AbsFilePath.value result.File)
        Lint.LintResult.Success []

    let handler = create (Some repoRoot) None (Some runner) None
    host.RegisterHandler(handler)

    // The model in force. Nothing is superseded here — the only thing wrong with the
    // first result is that it claims nothing.
    host.WorkStore.PublishProjectModel(fixtureModelOf 1L)

    host.EmitFileChecked(
        { fakeFileCheckResult unstamped with
            ModelGeneration = None }
    )

    // Positive control, stamped with the generation in force, emitted second: the
    // mailbox serializes per-plugin events, so its terminal status proves the
    // unstamped one was already dequeued rather than merely slow.
    host.EmitFileChecked(
        { fakeFileCheckResult present with
            ModelGeneration = Some 1L }
    )

    waitForTerminalStatus host "lint" 15000

    test <@ linted |> List.ofSeq = [ present ] @>
