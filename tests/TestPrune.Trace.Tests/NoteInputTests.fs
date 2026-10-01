/// `Scopes.NoteInput`, the escape hatch for a read the weaver cannot see: the note lands
/// on the scope of the code that made it, and on no other.
[<Xunit.Collection("recorder-counters")>]
module TestPrune.Trace.Tests.NoteInputTests

open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder

let private inputsOf (state: RecorderState) (key: string) =
    state.Scopes
    |> Seq.filter (fun s -> s.Key = key)
    |> Seq.collect (fun s -> s.Inputs.Keys)
    |> Seq.map (fun (struct (k, p)) -> k, p)
    |> Set.ofSeq

[<Fact(Timeout = 30000)>]
let ``a noted input lands in the noting test's scope and not a concurrent sibling's`` () =
    let state = RecorderState(8, None, null)
    state.RepoRoot <- Fixtures.repoRoot
    let a = Path.Combine(Fixtures.repoRoot, "global.json")
    let b = Path.Combine(Fixtures.repoRoot, "TestPrune.slnx")
    let saved = Runtime.state
    Runtime.state <- state

    try
        // Both tests are inside their scopes at once, on separate async flows.
        use bothEntered = new Barrier(2)

        let run (key: string) (kind: string) (path: string) =
            Task.Run(fun () ->
                Scopes.Enter key

                try
                    bothEntered.SignalAndWait() |> ignore
                    Scopes.NoteInput(kind, path)
                    bothEntered.SignalAndWait() |> ignore
                finally
                    Scopes.Exit())

        Task.WaitAll(run "T:a" "read" a, run "T:b" "list-deep" Fixtures.repoRoot)
    finally
        Runtime.state <- saved

    test <@ inputsOf state "T:a" = set [ "read", a ] @>
    test <@ inputsOf state "T:b" = set [ "list-deep", Fixtures.repoRoot ] @>
    test <@ inputsOf state "A:ambient" |> Set.isEmpty @>

[<Fact(Timeout = 30000)>]
let ``every kind is noted under its recorded name`` () =
    let state = RecorderState(8, None, null)
    state.RepoRoot <- Fixtures.repoRoot
    let file = Path.Combine(Fixtures.repoRoot, "global.json")
    let saved = Runtime.state
    Runtime.state <- state

    try
        Scopes.Enter "T:kinds"
        Scopes.NoteInput("read", file)
        Scopes.NoteInput("exists", file)
        Scopes.NoteInput("list", Fixtures.repoRoot)
        Scopes.NoteInput("list-deep", Fixtures.repoRoot)
        // Outside the repository, unnamed, or of no known kind: nothing.
        Scopes.NoteInput("read", "/etc/hosts")
        Scopes.NoteInput("read", null)
        Scopes.NoteInput("bogus", file)
        Scopes.NoteInput(null, file)
        Scopes.Exit()
    finally
        Runtime.state <- saved

    test
        <@
            inputsOf state "T:kinds" = set
                [ "read", file
                  "exists", file
                  "list", Fixtures.repoRoot
                  "list-deep", Fixtures.repoRoot ]
        @>

[<Fact(Timeout = 30000)>]
let ``with no recorder a note does nothing`` () =
    let saved = Runtime.state
    Runtime.state <- null

    try
        Scopes.NoteInput("read", Path.Combine(Fixtures.repoRoot, "global.json"))
    finally
        Runtime.state <- saved
