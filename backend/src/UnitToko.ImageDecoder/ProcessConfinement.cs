using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UnitToko.ImageDecoder;

[SupportedOSPlatform("windows")]
internal static class ProcessConfinement
{
    internal static void RequireBoundedJob() {
        const uint required = 0x2000 | 0x100 | 0x200 | 0x8 | 0x2;
        if (!QueryInformationJobObject(0, 9, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), out var returned) ||
            returned != Marshal.SizeOf<ExtendedLimits>() || (limits.Basic.LimitFlags & required) != required ||
            (limits.Basic.LimitFlags & (0x800 | 0x1000)) != 0 || limits.Basic.ActiveProcessLimit != 1 ||
            limits.Basic.PerProcessUserTimeLimit <= 0 || limits.Basic.PerProcessUserTimeLimit > TimeSpan.FromMinutes(1).Ticks ||
            limits.ProcessMemoryLimit is 0 or > 1024 * 1024 * 1024 || limits.JobMemoryLimit is 0 or > 1024 * 1024 * 1024)
            throw new InvalidOperationException("Enforced decoder job is required");
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int kind, out ExtendedLimits information, uint length, out uint returnedLength);
}
