namespace FsHotWatch.ProcessOwnership

open System
open System.ComponentModel
open System.IO
open System.Runtime.InteropServices
open System.Text.Json

module private NativeProcessGroup =
    [<DllImport("libc", SetLastError = true)>]
    extern int kill(int pid, int signal)

/// Spawn-time identity: Unix group IDs are queried only, never signalled.
type internal Containment private (isEmpty: unit -> bool, terminate: unit -> unit, dispose: unit -> unit) =
    static member Open(ready: JsonElement, hostPid: int, expectedJobName: string) =
        if OperatingSystem.IsWindows() then
            let job = WindowsProcessJob.Open(ready, expectedJobName)
            new Containment(job.IsEmpty, job.Terminate, (fun () -> (job :> IDisposable).Dispose()))
        else
            let group = ready.GetProperty("processGroup").GetInt32()

            if
                group <= 1
                || group <> hostPid
                || ready.GetProperty("jobName").ValueKind <> JsonValueKind.Null
            then
                raise (IOException("Process host supplied an unexpected Unix containment identity."))

            let isEmpty () =
                if NativeProcessGroup.kill (-group, 0) = 0 then
                    false
                else
                    let error = Marshal.GetLastPInvokeError()

                    if error = 3 then
                        true
                    else
                        raise (Win32Exception(error, "Cannot establish whether the owned process group is empty."))

            new Containment(isEmpty, ignore, ignore)

    member _.IsEmpty() = isEmpty ()
    member _.TerminateWindowsJob() = terminate ()

    interface IDisposable with
        member _.Dispose() = dispose ()
