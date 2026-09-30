using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record BoundedProcessResult(string Status, int ExitCode = -1, string Output = "", string Error = "", long InputLength = 0, string? InputSha256 = null, long? PeakJobMemoryBytes = null);

/// <summary>Resource confinement, not a substitute for a restricted service identity or denied host egress.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsBoundedProcess
{
    internal static async Task<BoundedProcessResult> RunAsync(string executable, IReadOnlyList<string> args, long memoryBytes, TimeSpan timeout,
        Stream input, long maximumInputBytes, CancellationToken ct, int maximumOutputBytes = 4096) {
        if (maximumOutputBytes is < 1 or > 131072) throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        using var job = CreateJobObject(0, null);
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new() { LimitFlags = 0x2000 | 0x100 | 0x200 | 0x8 | 0x2 | 0x400,
            ActiveProcessLimit = 1, PerProcessUserTimeLimit = timeout.Ticks }, ProcessMemoryLimit = (nuint)memoryBytes, JobMemoryLimit = (nuint)memoryBytes };
        Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));
        var handles = new List<SafeFileHandle>();
        nint attributes = 0, handleValues = 0, jobValues = 0, environment = 0; Process? process = null; var attributeListInitialized = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        try {
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            Check(CreatePipe(out var childInput, out var parentInput, ref security, 0)); handles.Add(childInput); handles.Add(parentInput);
            Check(CreatePipe(out var parentOutput, out var childOutput, ref security, 0)); handles.Add(parentOutput); handles.Add(childOutput);
            Check(CreatePipe(out var parentError, out var childError, ref security, 0)); handles.Add(parentError); handles.Add(childError);
            foreach (var handle in new[] { parentInput, parentOutput, parentError }) Check(SetHandleInformation(handle, 1, 0));
            nuint size = 0; _ = InitializeProcThreadAttributeList(0, 2, 0, ref size);
            if (size == 0) throw new Win32Exception();
            attributes = Marshal.AllocHGlobal(checked((int)size)); Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size)); attributeListInitialized = true;
            handleValues = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handleValues, 0, childInput.DangerousGetHandle()); Marshal.WriteIntPtr(handleValues, nint.Size, childOutput.DangerousGetHandle()); Marshal.WriteIntPtr(handleValues, 2 * nint.Size, childError.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handleValues, (nuint)(3 * nint.Size), 0, 0));
            jobValues = Marshal.AllocHGlobal(nint.Size); Marshal.WriteIntPtr(jobValues, job.DangerousGetHandle());
            // Windows assigns the job atomically during creation: no running or suspended unconfined child gap.
            Check(UpdateProcThreadAttribute(attributes, 0, 0x2000d, jobValues, (nuint)nint.Size, 0, 0));
            var startup = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                Input = childInput.DangerousGetHandle(), Output = childOutput.DangerousGetHandle(), Error = childError.DangerousGetHandle() }, Attributes = attributes };
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["SystemRoot"] = systemRoot, ["WINDIR"] = systemRoot, ["DOTNET_ROOT"] = Path.GetDirectoryName(executable)!,
                ["DOTNET_EnableDiagnostics"] = "0", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1",
                ["PATH"] = Path.Combine(systemRoot, "System32") };
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(v => v.Key + "=" + v.Value)) + "\0\0");
            var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(args).Select(Quote)));
            Check(CreateProcess(executable, command, 0, 0, true, 0x80000 | 0x400 | 0x08000000, environment, Path.GetDirectoryName(executable)!, ref startup, out var info));
            using var processHandle = new SafeFileHandle(info.Process, true); using var threadHandle = new SafeFileHandle(info.Thread, true);
            process = Process.GetProcessById((int)info.ProcessId);
            childInput.Dispose(); childOutput.Dispose(); childError.Dispose();
            using var send = new FileStream(parentInput, FileAccess.Write, 65536, false);
            using var receive = new FileStream(parentOutput, FileAccess.Read, 4096, false);
            using var error = new FileStream(parentError, FileAccess.Read, 4096, false);
            var writer = SendAsync(send, input, maximumInputBytes, deadline.Token);
            var stdout = ReadBoundedAsync(receive, maximumOutputBytes, deadline.Token); var stderr = ReadBoundedAsync(error, 4096, deadline.Token);
            var exit = process.WaitForExitAsync(deadline.Token);
            try {
                await Task.WhenAll(writer, stdout, stderr, exit).WaitAsync(deadline.Token);
                var peak = QueryInformationJobObject(job, 9, out var measured, (uint)Marshal.SizeOf<ExtendedLimits>(), out _) ? (long?)measured.PeakJobMemoryUsed : null;
                return new("exited", process.ExitCode, await stdout, await stderr, writer.Result.Length, writer.Result.Hash, peak);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("decode_timeout"); }
            catch (InputLimitException) { return new("invalid_media"); }
            catch (OperationCanceledException) { throw; }
            catch { return new("decoder_error"); }
            finally {
                // Terminate first so pipe readers/writers cannot keep an escaped child alive after a fault.
                _ = TerminateJobObject(job, 1); deadline.Cancel();
                try { await Task.WhenAll(writer, stdout, stderr, exit).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
        } finally {
            _ = TerminateJobObject(job, 1); process?.Dispose();
            if (attributes != 0) { if (attributeListInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handleValues != 0) Marshal.FreeHGlobal(handleValues);
            if (jobValues != 0) Marshal.FreeHGlobal(jobValues);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            foreach (var handle in handles) handle.Dispose();
        }
    }
    private static async Task<(long Length, string Hash)> SendAsync(Stream pipe, Stream source, long maximum, CancellationToken ct) {
        try {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[65536]; long total = 0;
            while (true) {
                var read = await source.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, maximum - total + 1)), ct);
                if (read == 0) break; total += read;
                if (total > maximum) throw new InputLimitException();
                digest.AppendData(bytes, 0, read); await pipe.WriteAsync(bytes.AsMemory(0, read), ct);
            }
            await pipe.FlushAsync(ct); return (total, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
        } finally { pipe.Dispose(); }
    }
    private static async Task<string> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct) {
        var bytes = new byte[maximum + 1]; var total = 0;
        while (total < bytes.Length) { var read = await stream.ReadAsync(bytes.AsMemory(total), ct); if (read == 0) return new UTF8Encoding(false, true).GetString(bytes, 0, total); total += read; }
        throw new InvalidDataException("Decoder output exceeded its envelope");
    }
    private static string Quote(string value) {
        if (value.Any(c => c == '"' || char.IsControl(c)) || value.EndsWith('\\')) throw new ArgumentException("Invalid decoder process argument");
        return '"' + value + '"';
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private sealed class InputLimitException : IOException;
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo {
        public int Size; public nint Reserved; public nint Desktop; public nint Title; public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags; public ushort Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out ExtendedLimits info, uint length, out uint returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation result);
}
