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
                // the framework cannot infer that for you. `RunSummary.nothingVerified`
                // is the marker every surface reads to downgrade the glyph to ⚠ (and
                // the agent / verdict.json token to `warn`). The STATUS stays
                // `Completed` — nothing failed.
                | FileChecked result when not ((AbsFilePath.value result.File).EndsWith ".fs") ->
                    ctx.ReportStatus(
                        Completed(
                            DateTime.UtcNow,
                            RunVerdict.create (RunSummary.nothingVerified "not an F# source file") TimeSpan.Zero
                        )
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
