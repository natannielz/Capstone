using System.Diagnostics;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

public sealed class ProtectedEvidenceRead : IAsyncDisposable
{
    public Stream Content { get; }
    public string Mime { get; }
    public long Length { get; }
    public EvidenceRef Reference { get; }
    internal ProtectedEvidenceRead(Stream content, string mime, long length, EvidenceRef reference) {
        Content = content; Mime = mime; Length = length; Reference = reference;
    }
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

internal sealed class EvidenceReadStream : Stream
{
    private readonly VerifiedOriginalHandle handle;
    private readonly IClock clock;
    private readonly Func<CancellationToken, Task<EvidenceReadWindow>> renew;
    private readonly Func<Task> release;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource request;
    private EvidenceReadWindow window;
    private long leaseStarted = Stopwatch.GetTimestamp();
    private TimeSpan leaseRemaining;
    private long remaining;
    private bool closed;
    internal EvidenceReadStream(VerifiedOriginalHandle handle, IClock clock, EvidenceReadWindow window, TimeSpan requestRemaining,
        Func<CancellationToken, Task<EvidenceReadWindow>> renew, Func<Task> release, CancellationToken ct) {
        this.handle = handle; this.clock = clock; this.window = window; this.renew = renew; this.release = release;
        remaining = handle.Content.Length; leaseRemaining = window.Until - clock.UtcNow;
        if (requestRemaining <= TimeSpan.Zero || leaseRemaining <= TimeSpan.Zero) throw new IOException("Evidence read deadline expired");
        request = CancellationTokenSource.CreateLinkedTokenSource(ct); request.CancelAfter(requestRemaining);
    }
    public override bool CanRead => !closed;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
        await gate.WaitAsync(ct);
        try {
            Check(); if (remaining == 0 || buffer.Length == 0) return 0;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token);
            if (window.Until - clock.UtcNow <= window.LeaseDuration / 2 || Stopwatch.GetElapsedTime(leaseStarted) >= leaseRemaining / 2) {
                window = await renew(operation.Token); leaseStarted = Stopwatch.GetTimestamp(); leaseRemaining = window.Until - clock.UtcNow; Check();
            }
            var duration = new[] { window.Until - clock.UtcNow, leaseRemaining - Stopwatch.GetElapsedTime(leaseStarted), window.Deadline - clock.UtcNow }.Min();
            if (duration <= TimeSpan.Zero) throw new IOException("Evidence read deadline expired"); operation.CancelAfter(duration);
            var read = await handle.Stream.ReadAsync(buffer[..(int)Math.Min(Math.Min(buffer.Length, 65536), remaining)], operation.Token);
            operation.Token.ThrowIfCancellationRequested(); Check();
            if (read == 0 && remaining > 0) throw new IOException("Evidence original changed"); remaining -= read; return read;
        } catch { try { await CloseCoreAsync(); } catch { } throw; }
        finally { gate.Release(); }
    }
    private void Check() {
        ObjectDisposedException.ThrowIf(closed, this); request.Token.ThrowIfCancellationRequested();
        if (clock.UtcNow >= window.Until || clock.UtcNow >= window.Deadline || Stopwatch.GetElapsedTime(leaseStarted) >= leaseRemaining) throw new IOException("Evidence read deadline expired");
    }
    private async Task CloseCoreAsync() {
        if (closed) return; closed = true;
        try { await handle.DisposeAsync(); }
        finally { try { await release(); } finally { request.Dispose(); } }
    }
    public override async ValueTask DisposeAsync() {
        try { request.Cancel(); } catch (ObjectDisposedException) { }
        await gate.WaitAsync();
        try { await CloseCoreAsync(); } finally { gate.Release(); } GC.SuppressFinalize(this);
    }
    protected override void Dispose(bool disposing) { if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
