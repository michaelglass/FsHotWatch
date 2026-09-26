namespace TestPrune.Trace.Recorder

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO

/// Call-site replacements for `Process.Start`. The child inherits the starting scope
/// through `TESTPRUNE_TRACE_PARENT_SCOPE`, so a woven child records into the test that
/// started it, and the start is noted on that scope with the child's pid. A child with
/// no dump of its own makes the test's trace incomplete; ingestion decides that, this
/// only notes.
[<AbstractClass; Sealed>]
type ProcessShims =
    /// Passes the note scope's key to the child's environment. False when there is no
    /// recorder, when the child is shell-executed and so gets no environment from us, or
    /// when the child dumps somewhere other than `ownOut` (this process's dump directory):
    /// it is then a trace of its own, and any parent scope it inherited is removed.
    static member internal PrepareWith(state: RecorderState, psi: ProcessStartInfo, ownOut: string) : bool =
        if isNull state || psi.UseShellExecute then
            false
        else
            let orEmpty (v: string) = if isNull v then "" else v

            let childOut =
                match psi.Environment.TryGetValue Contract.OutEnv with
                | true, v -> v
                | _ -> null

            if orEmpty childOut <> orEmpty ownOut then
                psi.Environment.Remove Contract.ParentScopeEnv |> ignore
                false
            else
                psi.Environment.[Contract.ParentScopeEnv] <- state.NoteScope().Key
                true

    /// `PrepareWith` against this process's own dump directory.
    static member internal PrepareWith(state: RecorderState, psi: ProcessStartInfo) : bool =
        ProcessShims.PrepareWith(state, psi, Environment.GetEnvironmentVariable Contract.OutEnv)

    /// Notes a started child on the note scope.
    static member internal RecordWith(state: RecorderState, p: Process, injected: bool) : unit =
        if not (isNull state) && not (isNull p) then
            state.NoteScope().Children.Enqueue(struct (p.Id, Path.GetFileName p.StartInfo.FileName, injected))

    /// Prepares `p`, starts it with `start`, and notes it when it started.
    static member internal StartWith(state: RecorderState, p: Process, start: Process -> bool) : bool =
        let injected = ProcessShims.PrepareWith(state, p.StartInfo)
        let started = start p

        if started then
            ProcessShims.RecordWith(state, p, injected)

        started

    /// Shim for `Process.Start(ProcessStartInfo)`.
    static member Process_Start(psi: ProcessStartInfo) : Process =
        let s = Runtime.state
        let injected = ProcessShims.PrepareWith(s, psi)
        let p = Process.Start psi
        ProcessShims.RecordWith(s, p, injected)
        p

    /// Shim for `Process.Start(string)`.
    static member Process_Start(fileName: string) : Process =
        ProcessShims.Process_Start(ProcessStartInfo fileName)

    /// Shim for `Process.Start(string, string)`.
    static member Process_Start(fileName: string, arguments: string) : Process =
        ProcessShims.Process_Start(ProcessStartInfo(fileName, arguments))

    /// Shim for `Process.Start(string, IEnumerable<string>)`.
    static member Process_Start(fileName: string, arguments: IEnumerable<string>) : Process =
        ProcessShims.Process_Start(ProcessStartInfo(fileName, arguments))

    /// Shim for the instance `Process.Start()`.
    static member Process_Start(p: Process) : bool =
        ProcessShims.StartWith(Runtime.state, p, (fun proc -> proc.Start()))
