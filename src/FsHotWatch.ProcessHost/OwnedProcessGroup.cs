using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FsHotWatch.ProcessHost;

internal sealed class OwnedProcessGroup
{
    private readonly SafeFileHandle? job;
    public int? ProcessGroup { get; }
    public string? JobName { get; }

    private OwnedProcessGroup(int processGroup) => ProcessGroup = processGroup;
    private OwnedProcessGroup(SafeFileHandle job, string jobName)
    {
        this.job = job;
        JobName = jobName;
    }

    public static OwnedProcessGroup Create(string jobName)
    {
        if (OperatingSystem.IsWindows())
        {
            var job = CreateJobObjectW(IntPtr.Zero, jobName);
            var createError = Marshal.GetLastPInvokeError();
            if (job.IsInvalid)
            {
                var error = new Win32Exception(createError);
                job.Dispose();
                throw error;
            }

            // The pipe-derived nonce identifies OUR new job; never adopt a pre-existing job.
            if (createError == 183) // ERROR_ALREADY_EXISTS
            {
                job.Dispose();
                throw new IOException("Process containment job already exists.");
            }

            try
            {
                var limits = new ExtendedLimits
                {
                    Basic = new BasicLimits { LimitFlags = 0x00002000 } // KILL_ON_JOB_CLOSE
                };
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                // Assignment precedes spawn. Children inherit this job with no breakaway flags.
                if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                return new OwnedProcessGroup(job, jobName);
            }
            catch { job.Dispose(); throw; }
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
            throw new PlatformNotSupportedException("Process containment requires Unix sessions or Windows jobs.");
        // setsid establishes ownership atomically. Never infer ownership from a sampled PID.
        var session = setsid();
        if (session == -1)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create process session.");
        return new OwnedProcessGroup(session);
    }

    public void Terminate()
    {
        if (job is not null)
        {
            if (!TerminateJobObject(job, 137))
            {
                // Close our handle; kill-on-close cleans up if the parent is also gone.
                // A live parent retains its handle and must detect this abnormal helper exit
                // and finish termination through that stable job handle.
                job.Dispose();
                Environment.Exit(125);
            }
        }
        else
        {
            // Zero means our CURRENT group; this cannot target a recycled external group ID.
            // Only a successfully created owner reaches this method.
            if (kill(0, 9) != 0)
                Environment.FailFast("Could not terminate the owned process group.");
        }
        // Successful native termination includes this helper. Do not return to spawn code.
        Thread.Sleep(Timeout.Infinite);
    }

    // Native layouts: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information
    // and https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedLimits information, uint informationLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
