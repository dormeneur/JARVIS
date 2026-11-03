using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Providers.ClaudeCli;

/// <summary>
/// Ties Claude Code processes to Jarvis's lifetime: they sit in a Windows job object that kills
/// every process in it when Jarvis exits, however it exits (Quit, a crash, or Task Manager). Their
/// own children (node, tools) join the job too. A no-op off Windows.
/// </summary>
internal static partial class ChildProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    // Never closed on purpose: the OS closes it when Jarvis exits, which is what kills the children.
    private static readonly Lazy<nint> Job = new(Create);

    /// <summary>Call right after starting the process.</summary>
    public static void Track(Process process)
    {
        if (OperatingSystem.IsWindows() && Job.Value != 0)
        {
            AssignProcessToJobObject(Job.Value, process.Handle);
        }
    }

    private static unsafe nint Create()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            return 0;
        }

        var limits = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = KillOnJobClose } };
        return SetInformationJobObject(job, ExtendedLimitInformation, &limits, (uint)sizeof(ExtendedLimits)) ? job : 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
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
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
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
}
