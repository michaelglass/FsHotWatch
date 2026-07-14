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
                | FileChecked result ->
                    let started = DateTime.UtcNow

                    // result.ParseResults and result.CheckResults come from the
                    // warm FSharpChecker — no re-parsing needed.
                    printfn "Checked: %s" (AbsFilePath.value result.File)

                    // A terminal status carries its verdict: what was done, and
                    // how long it took — MEASURED, never guessed. "Done with
                    // nothing to report" does not typecheck (`RunVerdict.create`
                    // rejects an empty summary) — by design.
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
