/// A finished exclusive run's result waits in the plugin's mailbox behind the events
/// admitted ahead of it. While it waits the plugin is still `Running` and nothing is
/// executing any more; the framework shows that wait as a subtask, so a wait line and
/// the wedge monitor can name the mailbox rather than the run.
module FsHotWatch.Tests.PluginFrameworkQueuedResultTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.PluginFrameworkFixtures

type private Msg = Finished

let private waitFor (deadline: TimeSpan) (condition: unit -> bool) =
    let until = DateTime.UtcNow + deadline

    while not (condition ()) && DateTime.UtcNow < until do
        Thread.Sleep 10

    condition ()

[<Fact(Timeout = 15000)>]
let ``a finished run's result shows as a queued-result subtask until the plugin picks it up`` () =
    use runMayFinish = new ManualResetEventSlim(false)
    use blockerStarted = new ManualResetEventSlim(false)
    use blockerMayReturn = new ManualResetEventSlim(false)
    use resultFolded = new ManualResetEventSlim(false)
    let subtasks = Collections.Concurrent.ConcurrentQueue<string * string>()
    let mutable changes = 0

    let handler =
        { Name = PluginName.create "queued-result"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        // The first change launches the run; the second occupies the
                        // mailbox for as long as the test says, so the run's result has
                        // to queue behind it.
                        if Interlocked.Increment &changes = 1 then
                            ctx.RunExclusive
                                "work"
                                (async {
                                    runMayFinish.Wait()
                                    return Finished
                                })
                            |> ignore
                        else
                            blockerStarted.Set()
                            blockerMayReturn.Wait()
                    | Custom Finished -> resultFolded.Set()
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let services =
        { defaultServices with
            StartSubtask = fun _ key _ -> subtasks.Enqueue("start", key)
            EndSubtask = fun _ key -> subtasks.Enqueue("end", key) }

    let registration = registerHandler services handler
    let key = QueuedResult.subtaskKey "work"

    let recorded () =
        subtasks.ToArray() |> Array.filter (fun (_, k) -> k = key) |> List.ofArray

    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
    registration.Dispatch(DispatchFileChanged SolutionChanged)
    test <@ blockerStarted.Wait(TimeSpan.FromSeconds 5.0) @>

    // The run finishes while the mailbox is occupied: its result is now queued, and
    // the framework says so from that moment.
    runMayFinish.Set()
    test <@ waitFor (TimeSpan.FromSeconds 5.0) (fun () -> recorded () = [ "start", key ]) @>

    // Nothing ends the wait but the plugin taking the result off the mailbox.
    Thread.Sleep 100
    test <@ recorded () = [ "start", key ] @>
    test <@ not resultFolded.IsSet @>

    blockerMayReturn.Set()
    test <@ resultFolded.Wait(TimeSpan.FromSeconds 5.0) @>
    test <@ waitFor (TimeSpan.FromSeconds 5.0) (fun () -> recorded () = [ "start", key; "end", key ]) @>

[<Fact(Timeout = 15000)>]
let ``a queued-result subtask key names the run and is recognised as such`` () =
    test <@ QueuedResult.subtaskKey "tests" = "tests result queued" @>
    test <@ QueuedResult.isSubtaskKey (QueuedResult.subtaskKey "tests") @>
    test <@ not (QueuedResult.isSubtaskKey "primary") @>
    test <@ not (QueuedResult.isSubtaskKey "Intelligence.Tests.Integration") @>
    test <@ (QueuedResult.label "tests").Contains "'tests'" @>

[<Fact(Timeout = 15000)>]
let ``a slow fold's line names the event, its duration, and what waited behind it`` () =
    test
        <@
            slowFoldLine "BatchChecked" (TimeSpan.FromSeconds 1085.0) 3 [ "tests result queued" ] = "BatchChecked fold took 18m 5s; 3 event(s) queued behind it, among them tests result queued"
        @>

    test
        <@
            slowFoldLine "BuildCompleted" (TimeSpan.FromSeconds 45.0) 2 [] = "BuildCompleted fold took 45s; 2 event(s) queued behind it"
        @>

    test <@ slowFoldLine "Custom" (TimeSpan.FromSeconds 31.0) 0 [] = "Custom fold took 31s; nothing queued behind it" @>
    test <@ eventKind (FileChanged SolutionChanged) = "FileChanged" @>
    test <@ eventKind (Custom Finished) = "Custom" @>
