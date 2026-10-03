using UnitToko.Api.Procurement;

namespace UnitToko.Tests.Procurement;

public sealed class ProcurementInspectionApiBoundaryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(128)]
    public async Task Advertised_byte_limit_accepts_exact_unknown_length_body_and_consumes_only_one_overrun_byte(int chunkSize)
    {
        const int advertisedLimit = 37;
        var original = Enumerable.Range(0, advertisedLimit).Select(value => (byte)value).ToArray();
        using var exactSource = new UnknownLengthBody(original, chunkSize);
        using var exact = new ProcurementInspectionEvidenceBodyStream(exactSource, advertisedLimit);
        var buffer = new byte[128]; using var captured = new MemoryStream();
        int read;
        while ((read = await exact.ReadAsync(buffer.AsMemory())) != 0) captured.Write(buffer, 0, read);
        Assert.Equal(original, captured.ToArray()); Assert.Equal(advertisedLimit, exactSource.BytesRead);
        Assert.Equal(advertisedLimit, exact.Position); Assert.False(exact.Exceeded);

        // Models chunked HTTP input: length/seek are unavailable and the sender has
        // substantially more bytes than the current policy permits.
        using var oversizedSource = new UnknownLengthBody(new byte[advertisedLimit + 100], chunkSize);
        using var oversized = new ProcurementInspectionEvidenceBodyStream(oversizedSource, advertisedLimit);
        await Assert.ThrowsAsync<IOException>(async () => {
            while (await oversized.ReadAsync(buffer, 7, buffer.Length - 7, CancellationToken.None) != 0) { }
        });
        Assert.True(oversized.Exceeded); Assert.Equal(advertisedLimit + 1, oversizedSource.BytesRead);
        Assert.Equal(advertisedLimit + 1, oversized.Position);
        var calls = oversizedSource.ReadCalls;
        await Assert.ThrowsAsync<IOException>(async () => { _ = await oversized.ReadAsync(buffer.AsMemory()); });
        Assert.Equal(calls, oversizedSource.ReadCalls); Assert.Equal(advertisedLimit + 1, oversizedSource.BytesRead);
    }

    private sealed class UnknownLengthBody(byte[] bytes, int chunkSize) : Stream
    {
        internal int BytesRead { get; private set; }
        internal int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ReadCalls++;
            var count = Math.Min(Math.Min(chunkSize, buffer.Length), bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, count).CopyTo(buffer); BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
