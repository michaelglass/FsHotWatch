using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FsHotWatch.ProcessOwnership;

internal sealed class Containment : IDisposable
{
    private readonly SafeFileHandle? job;
    private readonly int processGroup;

    private Containment(SafeFileHandle job) => this.job = job;
    private Containment(int processGroup) => this.processGroup = processGroup;

    public static Containment Open(JsonElement ready, int hostPid, string expectedJobName)
    {
        if (OperatingSystem.IsWindows())
        {
            if (ready.GetProperty("processGroup").ValueKind != JsonValueKind.Null
                || ready.GetProperty("jobName").GetString() != expectedJobName)
                throw new IOException("Process host supplied an unexpected Windows containment identity.");
            var job = OpenJobObjectW(0x0004 | 0x0008, false, expectedJobName); // QUERY | TERMINATE
            if (job.IsInvalid)
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                job.Dispose();
                throw error;
            }
            return new Containment(job);
        }

        var processGroup = ready.GetProperty("processGroup").GetInt32();
        if (processGroup <= 1 || processGroup != hostPid
            || ready.GetProperty("jobName").ValueKind != JsonValueKind.Null)
            throw new IOException("Process host supplied an unexpected Unix containment identity.");
        return new Containment(processGroup);
    }

    public bool IsEmpty()
    {
        if (job is not null)
        {
            if (!QueryInformationJobObject(job, 1, out var accounting,
                (uint)Marshal.SizeOf<BasicAccounting>(), out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return accounting.ActiveProcesses == 0;
        }
        // This is exclusively a read. A recycled group can cause conservative refusal,
        // but can never cause this parent to signal somebody else's processes.
        if (kill(-processGroup, 0) == 0)
            return false;
        var error = Marshal.GetLastPInvokeError();
        if (error == 3) // ESRCH on supported Unix platforms
            return true;
        throw new Win32Exception(error, "Cannot establish whether the owned process group is empty.");
    }

    public void TerminateWindowsJob()
    {
        if (job is not null && !IsEmpty() && !TerminateJobObject(job, 137))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose() => job?.Dispose();

    // https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenJobObjectW(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
        out BasicAccounting information, uint informationLength, out uint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
