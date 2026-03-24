module FsHotWatch.Analyzers.AnalyzersPlugin

open System
open FsHotWatch.Events
open FsHotWatch.Plugin

/// F# analyzer host plugin.
///
/// BLOCKED: FSharp.Analyzers.SDK 0.36.0 is compiled against FCS 43.10.101.
/// Our warm checker uses FCS 43.12.201. The CliContext constructor expects
/// FSharpParseFileResults/FSharpCheckFileResults from 43.10 — passing our
/// 43.12 types fails with "No constructors available" because they're
/// different assembly versions of the same types.
///
/// Resolution options:
/// 1. Wait for FSharp.Analyzers.SDK to release against FCS 43.12
/// 2. Build the SDK from source against our FCS version
/// 3. Use type forwarding / assembly binding redirects at runtime
///
/// Until resolved, this plugin tracks file changes but cannot run analyzers.
type AnalyzersPlugin(analyzerPaths: string list) =
    let mutable checkedFiles: Set<string> = Set.empty

    interface IFsHotWatchPlugin with
        member _.Name = "analyzers"

        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = DateTime.UtcNow))
                checkedFiles <- checkedFiles |> Set.add result.File
                ctx.ReportStatus(Completed(box checkedFiles, DateTime.UtcNow)))

            ctx.RegisterCommand(
                "diagnostics",
                fun _args ->
                    async {
                        return
                            $"{{\"status\": \"blocked — SDK requires FCS 43.10, we have 43.12\", \"analyzer_paths\": %d{analyzerPaths.Length}, \"checked_files\": %d{checkedFiles.Count}}}"
                    }
            )

        member _.Dispose() = ()
