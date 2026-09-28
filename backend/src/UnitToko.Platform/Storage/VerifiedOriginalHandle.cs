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
