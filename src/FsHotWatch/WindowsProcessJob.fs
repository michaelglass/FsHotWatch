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

/// Windows native job capability. This code remains compiled on every platform;
/// macOS/Linux coverage does not qualify its Windows runtime behavior.
type internal WindowsProcessJob private (handle: SafeFileHandle) =
    static member Open(ready: JsonElement, expectedJobName: string) =
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

        new WindowsProcessJob(handle)

    member _.IsEmpty() =
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

    member this.Terminate() =
        if not (this.IsEmpty()) then
            if not (NativeContainment.TerminateJobObject(handle, 137u)) then
                raise (Win32Exception(Marshal.GetLastPInvokeError()))

    interface IDisposable with
        member _.Dispose() = handle.Dispose()
