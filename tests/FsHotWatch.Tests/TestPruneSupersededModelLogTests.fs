/// What test-prune says when it refuses a result captured against a replaced project
/// model. In the LogGlobal collection because it captures the plugin's log lines.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.TestPruneSupersededModelLogTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

// Test-prune refuses a FileChecked or BatchChecked the current model did not stamp, and
// the analysis it skips is retried against the current model. The refusal is logged at
// Info, so a run that seems to ignore an edit is explained in the daemon log without
// turning on Debug.
[<Fact(Timeout = 30000)>]
let ``test-prune logs its refusal of a superseded model's results at info`` () =
    withTempDir "superseded-model-log" (fun repoRoot ->
        let stale = Path.Combine(repoRoot, "src", "Stale.fs")
        let logged = System.Collections.Concurrent.ConcurrentQueue<string>()

        // Installed BEFORE RegisterHandler: the plugin's mailbox captures this context when
        // it starts. At Info, so a refusal logged only at Debug is not visible to it.
        use _sink =
            FsHotWatch.Logging.installSink
                { Write = logged.Enqueue
                  Level = FsHotWatch.Logging.LogLevel.Info }

        // Publishes the fixture model (generation 1), the one both results are stamped with.
        let host = createModelHost (Unchecked.defaultof<_>) repoRoot
        host.RegisterHandler(create ":memory:" repoRoot None None None None None [])

        // A rediscovery replaces it before the results are folded.
        host.WorkStore.PublishProjectModel(fixtureModelOf 2L)
        host.EmitFileChecked(fakeFileCheckResult stale)
        host.EmitBatchChecked(fakeBatchChecked [ stale ])

        let refused (fragment: string) =
            logged |> Seq.exists (fun l -> l.Contains "[test-prune]" && l.Contains fragment)

        test
            <@
                waitUntilTrue
                    (fun () ->
                        refused $"ignoring FileChecked for %s{stale} from model Some 1L"
                        && refused "ignoring BatchChecked from model Some 1L")
                    15000
            @>)
