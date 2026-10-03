using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// Disposable test child only. The private original's name is never passed to this process.
[SupportedOSPlatform("windows")]
internal static class NativeInputProbe
{
    internal static async Task<int> RunAsync(string mode, string directory) {
        if (mode is not ("verify" or "hold" or "flood")) return 2;
        if (!Path.IsPathFullyQualified(directory)) return 2;
        var root = Path.GetFullPath(directory);
        var marker = File.ReadAllText(Path.Combine(root, ".fixture-id"));
        var parent = File.ReadAllText(Path.Combine(root, ".fixture-parent"));
        if (marker.Length != 32 || !marker.All(Uri.IsHexDigit) || Path.GetFileName(root) != ".storage-test-" + marker ||
            !Path.IsPathFullyQualified(parent) || !Directory.Exists(parent) ||
            !string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return 2;
        for (DirectoryInfo? ancestor = new(root); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return 2;
        using var handle = new SafeFileHandle(GetStdHandle(-10), ownsHandle: false);
        if (handle.IsInvalid || GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var identity)) return 3;
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        var initialPosition = stream.Position; var length = stream.Length;
        stream.Seek(-1, SeekOrigin.End); var last = stream.ReadByte(); stream.Seek(0, SeekOrigin.Begin);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var writeSucceeded = WriteFile(handle, [255], 1, out var written, 0); var writeError = Marshal.GetLastWin32Error();
        if (!IsProcessInJob(GetCurrentProcess(), 0, out var inJob)) return 4;
        var report = JsonSerializer.Serialize(new { pid = Environment.ProcessId, initialPosition, length, last, hash,
            identity = Identity(identity), writeSucceeded, written, writeError, inJob });
        var ready = Path.Combine(root, "native-ready.json"); var temp = Path.Combine(root, "native-ready.tmp");
        await File.WriteAllTextAsync(temp, report); File.Move(temp, ready);
        if (mode != "verify") {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!File.Exists(Path.Combine(root, "native-release"))) await Task.Delay(20, timeout.Token);
        }
        if (mode == "flood") Console.Write(new string('x', 8192));
        else Console.Write(report);
        return 0;
    }
    private static string Identity(FileInformation info) => $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}:{info.FileIndexLow:x8}";
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint length, out uint written, nint overlapped);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
