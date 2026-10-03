using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace UnitToko.Platform.Storage;

/// <summary>Integrity only: caller must finalize current authorization and enforce its own Mongo read lease.</summary>
internal sealed class VerifiedOriginalHandle : IAsyncDisposable
{
    private readonly ReadOnlyOriginalStream stream;
    public Stream Stream => stream;
    public StorageContent Content { get; }
    public StorageAttempt Attempt { get; }
    internal VerifiedOriginalHandle(FileStream file, ExpectedPublication expected)
    {
        Content = expected.Content; Attempt = expected.Attempt;
        stream = new ReadOnlyOriginalStream(file, expected.Content.Length);
    }
    public ValueTask DisposeAsync() => stream.DisposeAsync();

    private sealed class ReadOnlyOriginalStream(FileStream file, long length) : Stream
    {
        private long remaining = length;
        private bool disposed;
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (remaining == 0) return 0;
            var read = file.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            remaining -= read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining == 0) return 0;
            var read = await file.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
            remaining -= read; return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) { disposed = true; file.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (!disposed) { disposed = true; await file.DisposeAsync(); }
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>Created only by verified native acquisition. No stream, path or caller-supplied OS handle is exposed.</summary>
internal sealed class VerifiedNativeOriginalHandle : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly FileStream file;
    private readonly SafeFileHandle parent;
    private readonly FileStamp stamp;
    private bool disposed;
    public ExpectedPublication Publication { get; }
    public StorageContent Content => Publication.Content;
    public StorageAttempt Attempt => Publication.Attempt;

    [SupportedOSPlatform("windows")]
    internal VerifiedNativeOriginalHandle(FileStream file, ExpectedPublication expected)
    {
        this.file = file; parent = file.SafeFileHandle; Publication = expected;
        stamp = FileStamp.Read(parent);
        if (stamp.Length != expected.Content.Length) throw new StorageUnavailableException("storage_native_identity_changed");
    }

    [SupportedOSPlatform("windows")]
    internal NativeInputLease AcquireInput(ExpectedPublication expected, long maximumBytes)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (expected != Publication || maximumBytes <= 0 || Content.Length > maximumBytes)
                throw new InvalidDataException("Native input does not match the verified publication and budget.");
            var retained = false;
            SafeFileHandle? reopened = null, inherited = null;
            try
            {
                parent.DangerousAddRef(ref retained);
                if (FileStamp.Read(parent) != stamp) throw new StorageUnavailableException("storage_native_identity_changed");
                // A fresh file object provides synchronous reads and an independent position without reopening any pathname.
                reopened = ReOpenFile(parent, 0x80000000, 1, 0);
                if (reopened.IsInvalid) throw new StorageUnavailableException("storage_native_reopen_unavailable");
                if (FileStamp.Read(reopened) != stamp) throw new StorageUnavailableException("storage_native_identity_changed");
                if (!SetFilePointerEx(reopened, 0, out var position, 0) || position != 0)
                    throw new StorageUnavailableException("storage_native_seek_unavailable");
                // Only this read-only duplicate is inheritable. The original capability is never made inheritable.
                if (!DuplicateHandle(GetCurrentProcess(), reopened, GetCurrentProcess(), out inherited, 0, true, 2))
                    throw new StorageUnavailableException("storage_native_duplicate_unavailable");
                var lease = new NativeInputLease(parent, reopened, inherited, stamp, Content);
                retained = false; reopened = null; inherited = null;
                return lease;
            }
            finally
            {
                inherited?.Dispose(); reopened?.Dispose();
                if (retained) parent.DangerousRelease();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            // An active input lease retains the original native handle until its child has actually exited.
            return file.DisposeAsync();
        }
    }

    internal sealed class NativeInputLease : IDisposable
    {
        private readonly SafeFileHandle parent;
        private readonly SafeFileHandle reopened;
        private readonly FileStamp stamp;
        private int disposed;
        internal SafeFileHandle ChildInput { get; }
        internal StorageContent Content { get; }
        internal NativeInputLease(SafeFileHandle parent, SafeFileHandle reopened, SafeFileHandle inherited, FileStamp stamp, StorageContent content)
        {
            this.parent = parent; this.reopened = reopened; ChildInput = inherited; this.stamp = stamp; Content = content;
        }
        internal void RequireUnchanged()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (FileStamp.Read(reopened) != stamp || stamp.Length != Content.Length)
                throw new StorageUnavailableException("storage_native_identity_changed");
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            ChildInput.Dispose(); reopened.Dispose(); parent.DangerousRelease();
        }
    }

    internal readonly record struct FileStamp(uint Volume, uint IndexHigh, uint IndexLow, long Length)
    {
        internal static FileStamp Read(SafeFileHandle handle)
        {
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) ||
                (info.Attributes & (0x10 | 0x400)) != 0)
                throw new StorageUnavailableException("storage_native_identity_unavailable");
            var size = ((ulong)info.SizeHigh << 32) | info.SizeLow;
            if (size == 0 || size > long.MaxValue) throw new StorageUnavailableException("storage_native_identity_changed");
            return new(info.Volume, info.IndexHigh, info.IndexLow, (long)size);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint origin);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(nint sourceProcess, SafeFileHandle source,
        nint targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
