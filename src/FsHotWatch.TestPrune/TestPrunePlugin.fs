module FsHotWatch.TestPrune.TestPrunePlugin

open FsHotWatch.Events
open FsHotWatch.Plugin
open TestPrune.Database
open TestPrune.ImpactAnalysis

type TestPrunePlugin(dbPath: string) =
    let mutable _db = Database.create dbPath

    interface IFsHotWatchPlugin with
        member _.Name = "test-prune"

        member _.Initialize(ctx) =
            ctx.OnFileChanged.Add(fun change ->
                ctx.ReportStatus(Running(since = System.DateTime.UtcNow))

                match change with
                | SourceChanged files ->
                    // TODO: re-index changed files, query affected tests
                    ctx.ReportStatus(Completed(box files, System.DateTime.UtcNow))
                | ProjectChanged _
                | SolutionChanged ->
                    ctx.ReportStatus(
                        Completed(box "project/solution changed — full re-index needed", System.DateTime.UtcNow)
                    ))

            ctx.RegisterCommand(
                "affected-tests",
                fun _args ->
                    async {
                        // TODO: return current affected tests
                        return "[]"
                    }
            )

        member _.Dispose() = ()
