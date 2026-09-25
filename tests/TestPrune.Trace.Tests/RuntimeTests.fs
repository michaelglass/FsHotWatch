/// These tests swap the process-wide recorder, so they run in the non-parallel counters collection.
[<Xunit.Collection("recorder-counters")>]
module TestPrune.Trace.Tests.RuntimeTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder
open TestPrune.Trace

/// Run `f` with `state` installed as the process's recorder, then restore the untraced default.
let private withRecorder (state: RecorderState) (f: unit -> unit) =
    let saved = Runtime.state
    Runtime.state <- state

    try
        f ()
    finally
        Runtime.state <- saved

let private idsOf (state: RecorderState) key =
    state.Scopes
    |> Seq.find (fun s -> s.Key = key)
    |> (fun s -> s.Ids() |> Set.ofArray)

[<Fact>]
let ``probes record through the installed recorder`` () =
    let state = RecorderState(64, None, null)

    withRecorder state (fun () ->
        Scopes.Enter "T:x"
        Probes.Hit 1
        Probes.HitIfNotNull(box "x", 2)
        Probes.HitIfNotNull(null, 3)
        Probes.HitIfTrue(true, 4)
        Probes.HitIfTrue(false, 5)
        Probes.HitTag(2, 10)
        Probes.EnterStatic()
        Probes.Hit 6
        Probes.ExitStatic()
        test <@ Scopes.CurrentKey() = "T:x" @>
        Scopes.LinkCurrentTo "P:pool"
        Scopes.Exit()
        test <@ isNull (Scopes.CurrentKey()) @>
        Scopes.LinkCurrentTo "P:ignored")

    test <@ idsOf state "T:x" = set [ 1; 2; 4; 12 ] @>
    test <@ idsOf state "S:static-init" = set [ 6 ] @>
    test <@ (state.Scopes |> Seq.find (fun s -> s.Key = "T:x")).Links.Keys |> List.ofSeq = [ "P:pool" ] @>

[<Fact>]
let ``probes and scopes are inert with no recorder`` () =
    withRecorder null (fun () ->
        Probes.Hit 1
        Probes.EnterStatic()
        Probes.ExitStatic()
        Scopes.Enter "T:x"
        Scopes.LinkCurrentTo "P:pool"
        test <@ isNull (Scopes.CurrentKey()) @>
        Scopes.Exit())

let private env (pairs: (string * string) list) =
    let m = Map.ofList pairs
    fun (name: string) -> m |> Map.tryFind name |> Option.toObj

[<Fact>]
let ``with no output directory nothing is installed`` () =
    let mutable registered = 0
    let register (_: EventHandler) = registered <- registered + 1
    test <@ isNull (Runtime.install (env []) register) @>
    test <@ isNull (Runtime.install (env [ Contract.OutEnv, "" ]) register) @>
    test <@ registered = 0 @>

[<Fact>]
let ``an output directory installs a recorder that dumps at exit`` () =
    let out = Path.Combine(Directory.CreateTempSubdirectory().FullName, "out")
    let mutable handler: EventHandler = null

    let state =
        Runtime.install
            (env
                [ Contract.OutEnv, out
                  Contract.IdsEnv, "12"
                  Contract.ParentScopeEnv, "T:parent" ])
            (fun h -> handler <- h)

    test <@ (state.IdCount, state.ParentScope) = (12, "T:parent") @>
    state.Hit 3
    handler.Invoke(null, EventArgs.Empty)

    let dump =
        DumpReader.readFile (DumpWriter.pathIn out) |> Result.defaultWith failwith

    test <@ dump.IdCount = 12 && dump.ParentScope = Some "T:parent" @>
    // The installed recorder binds the real xUnit context, so the hit belongs to this very test.
    let mine =
        dump.Scopes
        |> List.find (fun s -> s.Test |> Option.exists (fun t -> t.Method.StartsWith "an output directory"))

    test <@ mine.Ids = [| 3 |] @>
    test <@ mine.Parents = [ "C:TestPrune.Trace.Tests.RuntimeTests"; "L:recorder-counters"; "A:assembly" ] @>

[<Fact>]
let ``a missing or bad id count falls back to a generous default`` () =
    let out = Directory.CreateTempSubdirectory().FullName
    let ignoreExit (_: EventHandler) = ()

    for ids in [ None; Some "x"; Some "0" ] do
        let vars =
            (Contract.OutEnv, out)
            :: (ids |> Option.map (fun v -> Contract.IdsEnv, v) |> Option.toList)

        let state = Runtime.install (env vars) ignoreExit
        test <@ state.IdCount = (1 <<< 20) && isNull state.ParentScope @>
