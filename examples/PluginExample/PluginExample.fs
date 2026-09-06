module PluginExample

// The region below is the single source of truth for the plugin example shown
// in docs/writing-plugins.md. SyncDocs copies everything between the markers
// into the doc's fenced code block, and CI compiles this file — so the snippet
// can never silently drift from the live FsHotWatch API.

// sync:plugin-example:start
open System
open FsHotWatch.Events // PluginEvent cases, FileCheckResult, AbsFilePath
open FsHotWatch.PluginFramework // PluginHandler, PluginName, SubscribeFileChecked

type MyState = { FilesChecked: int }

let myPlugin: PluginHandler<MyState, unit> =
    { Name = PluginName.create "my-plugin"
      Init = { FilesChecked = 0 }
      Update =
        fun ctx state event ->
            async {
                match event with
                // Most plugins care about only some of what they are handed. Say so
                // out loud: a run that examined NOTHING must not render as a ✓, and
                // the framework cannot infer that for you. `RunVerdict.verifiedNothing`
                // is the verdict every surface reads to downgrade the glyph to ⚠ (and
                // the agent / verdict.json token to `warn`) — the run record carries
                // `RunOutcome.VerifiedNothing`. The STATUS stays `Completed` — nothing
                // failed.
                | FileChecked result when not ((AbsFilePath.value result.File).EndsWith ".fs") ->
                    ctx.ReportStatus(
                        Completed(DateTime.UtcNow, RunVerdict.verifiedNothing "not an F# source file" TimeSpan.Zero)
                    )

                    return state
                | FileChecked result ->
                    let started = DateTime.UtcNow

                    // result.ParseResults and result.CheckResults come from the
                    // warm FSharpChecker — no re-parsing needed.
                    printfn "Checked: %s" (AbsFilePath.value result.File)

                    // A terminal status carries its verdict: what was done, and
                    // how long it took — MEASURED, never guessed. "Done with
                    // nothing to report" does not typecheck (`RunVerdict.create`
                    // rejects an empty summary) — by design. An empty summary the
                    // compiler catches; a MISLEADING one ("checked 0 files") it does
                    // not, which is what the arm above exists to prevent.
                    ctx.ReportStatus(
                        Completed(
                            DateTime.UtcNow,
                            RunVerdict.create $"checked %d{state.FilesChecked + 1} files" (DateTime.UtcNow - started)
                        )
                    )

                    return
                        { state with
                            FilesChecked = state.FilesChecked + 1 }
                | _ -> return state
            }
      Commands = [ "my-status", fun _ctx state _args -> async { return $"checked %d{state.FilesChecked} files" } ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey = None
      Teardown = None }
// sync:plugin-example:end

// The region below is the single source of truth for the "reading a test run's
// results" example in docs/writing-plugins.md. It reuses the `open`s at the top of
// this file. Same contract as the block above: SyncDocs copies it into the doc, and
// CI compiles it — so the snippet cannot drift from the live API, and deleting or
// re-shaping `TestResult.verdict` breaks THIS build first.

// sync:test-verdict-example:start
/// A plugin that reports what a finished test run actually ESTABLISHED.
///
/// It exists to demonstrate the two questions a test run answers and why they need
/// different tools: `TestResult.verdict` answers the PER-PROJECT one, and
/// `TestRunCompleted.Verification` answers the RUN-level one. Reaching for the first
/// to answer the second is the mistake this API shape exists to prevent.
let testVerdictPlugin: PluginHandler<unit, unit> =
    { Name = PluginName.create "test-verdict"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | TestRunCompleted completed ->
                    // PER PROJECT. `TestResult.verdict` is the ONE place the six
                    // `TestResult` cases are told apart, and `ProjectVerdict` has exactly
                    // three answers. Match it exhaustively — there is deliberately no
                    // bool spanning all three, because `NothingVerified` is neither a
                    // pass nor a failure and any single bool has to lie about one of them.
                    let describe (project: string) (result: TestResult) =
                        match TestResult.verdict result with
                        | Verified -> $"%s{project}: verified green"
                        | Refuted -> $"%s{project}: FAILED — this project's own fault"
                        // The filter matched no test, the build was not ready, or the
                        // host aborted before writing a report. Nothing was proved — and
                        // nothing failed either, so this project is not a red.
                        | NothingVerified -> $"%s{project}: nothing verified"

                    for KeyValue(project, result) in completed.Results do
                        printfn "%s" (describe project result)

                    // The projects safe to treat as green. `verifiedGreen` is the only
                    // bool offered over `verdict`, and it is TRUE for `Verified` alone —
                    // so selecting with it can never let a project that ran nothing pass.
                    let green =
                        completed.Results
                        |> Map.toList
                        |> List.filter (fun (_, r) -> TestResult.verifiedGreen r)

                    // A failure REPORT matches `Refuted` instead of negating that bool.
                    // `not (TestResult.verifiedGreen r)` is also true for a project that
                    // verified nothing — correct for a gate, wrong for a report, which is
                    // why no "not a failure" bool is offered to blur the two.
                    let failed =
                        completed.Results
                        |> Map.toList
                        |> List.filter (fun (_, r) -> TestResult.verdict r = Refuted)

                    // PER RUN. Do NOT fold the per-project bool to get here: `Map.forall`
                    // is vacuously TRUE for an empty map, so a run that selected no
                    // project at all would report green having executed nothing.
                    // `Verification` is the total run-level answer and it settles
                    // emptiness first.
                    let verdict =
                        let elapsed = completed.TotalElapsed

                        match completed.Verification with
                        | Ran FullSuite ->
                            RunVerdict.create
                                $"full suite: %d{List.length green} green, %d{List.length failed} failed"
                                elapsed
                        | Ran Partial ->
                            RunVerdict.create
                                $"impact-filtered: %d{List.length green} green, %d{List.length failed} failed"
                                elapsed
                        | NoProjectsSelected -> RunVerdict.verifiedNothing "no project was selected" elapsed
                        | AllZeroMatch n ->
                            RunVerdict.verifiedNothing $"%d{n} project(s) ran, all matched zero tests" elapsed
                        | NothingExecuted -> RunVerdict.verifiedNothing "no project executed a test" elapsed

                    // `verifiedNothing` makes the three cases above a verdict every surface
                    // refuses a ✓ — the run record's outcome is `VerifiedNothing`, a case,
                    // not a summary prefix. The status stays `Completed` — nothing FAILED.
                    ctx.ReportStatus(Completed(DateTime.UtcNow, verdict))

                    return state
                | _ -> return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeTestRunCompleted ]
      CacheKey = None
      Teardown = None }
// sync:test-verdict-example:end
