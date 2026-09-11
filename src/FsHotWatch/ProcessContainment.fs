namespace FsHotWatch.ProcessOwnership

open System
open System.ComponentModel
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open Microsoft.Win32.SafeHandles

module private NativeContainment =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type BasicAccounting =
        val mutable TotalUserTime: int64
        val mutable TotalKernelTime: int64
        val mutable ThisPeriodTotalUserTime: int64
        val mutable ThisPeriodTotalKernelTime: int64
        val mutable TotalPageFaultCount: uint32
        val mutable TotalProcesses: uint32
        val mutable ActiveProcesses: uint32
        val mutable TotalTerminatedProcesses: uint32

    [<DllImport("libc", SetLastError = true)>]
    extern int kill(int pid, int signal)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern SafeFileHandle OpenJobObjectW(
        uint32 desiredAccess,
        [<MarshalAs(UnmanagedType.Bool)>] bool inheritHandle,
        string name
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool QueryInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        BasicAccounting& information,
        uint32 informationLength,
        uint32& returnLength
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool TerminateJobObject(SafeFileHandle job, uint32 exitCode)

/// Spawn-time identity: Unix group IDs are queried only, never signalled.
type internal Containment private (job: SafeFileHandle option, processGroup: int) =
    static member Open(ready: JsonElement, hostPid: int, expectedJobName: string) =
        if OperatingSystem.IsWindows() then
            if
                ready.GetProperty("processGroup").ValueKind <> JsonValueKind.Null
                || ready.GetProperty("jobName").GetString() <> expectedJobName
            then
                raise (IOException("Process host supplied an unexpected Windows containment identity."))

            let handle = NativeContainment.OpenJobObjectW(0x000Cu, false, expectedJobName)

            if handle.IsInvalid then
                let error = Win32Exception(Marshal.GetLastPInvokeError())
                handle.Dispose()
                raise error

            new Containment(Some handle, 0)
        else
            let group = ready.GetProperty("processGroup").GetInt32()

            if
                group <= 1
                || group <> hostPid
                || ready.GetProperty("jobName").ValueKind <> JsonValueKind.Null
            then
                raise (IOException("Process host supplied an unexpected Unix containment identity."))

            new Containment(None, group)

    member _.IsEmpty() =
        match job with
        | Some handle ->
            let mutable accounting = Unchecked.defaultof<NativeContainment.BasicAccounting>
            let mutable returned = 0u

            if
                not (
                    NativeContainment.QueryInformationJobObject(
                        handle,
                        1,
                        &accounting,
                        uint32 (Marshal.SizeOf<NativeContainment.BasicAccounting>()),
                        &returned
                    )
                )
            then
                raise (Win32Exception(Marshal.GetLastPInvokeError()))

            accounting.ActiveProcesses = 0u
        | None ->
            if NativeContainment.kill (-processGroup, 0) = 0 then
                false
            else
                let error = Marshal.GetLastPInvokeError()

                if error = 3 then
                    true
                else
                    raise (Win32Exception(error, "Cannot establish whether the owned process group is empty."))

    member this.TerminateWindowsJob() =
        match job with
        | Some handle when not (this.IsEmpty()) ->
            if not (NativeContainment.TerminateJobObject(handle, 137u)) then
                raise (Win32Exception(Marshal.GetLastPInvokeError()))
        | _ -> ()

    interface IDisposable with
        member _.Dispose() =
            job |> Option.iter (fun handle -> handle.Dispose())
