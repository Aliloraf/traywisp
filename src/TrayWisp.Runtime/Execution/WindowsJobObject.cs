using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TrayWisp.Runtime.Execution;

// A Windows job object for one plugin run, see docs/execution-engine.md ("Windows job objects").
//
// The process is assigned after Process.Start returns, so anything it starts before the assignment is not in the
// job. JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE stays set for the job's whole life, so Windows terminates the job's
// processes when it is closed, at the end of the run or if TrayWisp exits without closing it (for example, a crash). The job does not allow breakaway. It sets
// no other limits, and it restricts no access: it only tracks and terminates processes.
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJobObject : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;

    private readonly SafeJobHandle _handle;

    private WindowsJobObject(SafeJobHandle handle) => _handle = handle;

    public bool IsAssigned { get; private set; }

    // Creates a job that kills its processes when its last handle is closed, or returns null and the Win32 error.
    public static WindowsJobObject? TryCreate(out int error)
    {
        SafeJobHandle handle = CreateJobObject(0, null);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }

        var job = new WindowsJobObject(handle);
        if (!job.SetKillOnClose(out error))
        {
            job.Dispose();
            return null;
        }

        return job;
    }

    public bool TryAssign(SafeProcessHandle process, out int error)
    {
        if (AssignProcessToJobObject(_handle, process))
        {
            IsAssigned = true;
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    // Terminates every process in the job. Returns false and the Win32 error if Windows refused.
    public bool TryTerminate(uint exitCode, out int error)
    {
        if (TerminateJobObject(_handle, exitCode))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    // The number of processes currently in the job, or null if it cannot be queried.
    public int? ActiveProcesses()
    {
        if (QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out BasicAccountingInformation info, (uint)Marshal.SizeOf<BasicAccountingInformation>(), 0))
        {
            return (int)info.ActiveProcesses;
        }

        return null;
    }

    public void Dispose() => _handle.Dispose();

    // Kill-on-close is set once, when the job is created, and never cleared: a run never releases its tracked processes.
    private bool SetKillOnClose(out int error)
    {
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, in limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeJobHandle CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int informationClass, in ExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(SafeJobHandle job, int informationClass, out BasicAccountingInformation information, uint length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    // A job object handle, closed with CloseHandle.
    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
