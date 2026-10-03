using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
public sealed class VideoDecoderTests
{
    private const string FfprobeHash = "b364c7974d2aa8195f7e97064e658fd7f738fe4e78a6db092a3bf6cd5cc9488c";
    private const string FfmpegHash = "c61984150fbe7699cb7387f23b9e71c759d0b097dd7a61bf670b555e054d6afe";

    [WindowsStorageFact]
    public async Task Actual_private_mp4_and_webm_decode_all_frames_and_preserve_exact_original_tuple()
    {
        foreach (var (fixture, mime, video, audio) in new[] {
            ("UNITTOKO_VIDEO_FIXTURE_MP4", "video/mp4", "h264", "aac"),
            ("UNITTOKO_VIDEO_FIXTURE_WEBM", "video/webm", "vp9", "opus") }) {
            var bytes = Fixture(fixture); using var root = new StorageRoot(); var expected = Expected(bytes);
            await using var original = await PublishAsync(root, expected, bytes);
            var result = await new WindowsVideoDecoder(Options()).DecodeAsync(original, expected, Limits(), default);
            Assert.True(result.Status == "decoded", $"Actual {mime} decoder returned {result.Status}");
            Assert.Equal(mime, result.Mime); Assert.Equal(video, result.VideoCodec); Assert.Equal(audio, result.AudioCodec);
            Assert.Equal(64, result.Width); Assert.Equal(48, result.Height); Assert.Equal(5L, result.VideoFrames);
            Assert.Equal(1, result.AudioChannels); Assert.Equal(48000, result.AudioSampleRate);
            Assert.Equal(mime == "video/mp4" ? 24L : 26L, result.AudioFrames);
            Assert.Equal(24000L, result.AudioSamples); Assert.InRange(result.DurationSeconds!.Value, 0.5m, 0.51m);
            Assert.Equal(expected.Content.Length, result.Length); Assert.Equal(expected.Content.Sha256, result.Sha256);
            Assert.Equal(expected, original.Publication);
            Assert.Throws<IOException>(() => File.Open(root.Original, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            await original.DisposeAsync(); RequireReleasedOriginal(root, bytes); RequireReleasedBinaries();
        }
    }

    [WindowsStorageFact]
    public async Task Wrong_publication_or_pinned_binary_and_malformed_or_truncated_private_original_never_decode()
    {
        var bytes = Fixture("UNITTOKO_VIDEO_FIXTURE_MP4"); using (var root = new StorageRoot()) {
            var expected = Expected(bytes); await using var original = await PublishAsync(root, expected, bytes);
            var wrongRevision = new ExpectedPublication(expected.Attempt, 2, expected.Content);
            var mismatch = await new WindowsVideoDecoder(Options()).DecodeAsync(original, wrongRevision, Limits(), default);
            AssertRejected(mismatch, "invalid_media");
            foreach (var options in new[] { Options() with { FfprobeSha256 = new string('0', 64) }, Options() with { FfmpegSha256 = new string('0', 64) } })
                AssertRejected(await new WindowsVideoDecoder(options).DecodeAsync(original, expected, Limits(), default), "decoder_unavailable");
            await original.DisposeAsync(); RequireReleasedOriginal(root, bytes); RequireReleasedBinaries();
        }
        foreach (var malformed in new[] { "not a video container"u8.ToArray(), bytes[..(bytes.Length / 2)], Fixture("UNITTOKO_VIDEO_FIXTURE_WEBM")[..100] }) {
            using var root = new StorageRoot(); var expected = Expected(malformed);
            await using var original = await PublishAsync(root, expected, malformed);
            AssertRejected(await new WindowsVideoDecoder(Options()).DecodeAsync(original, expected, Limits(), default), "invalid_media");
            await original.DisposeAsync(); RequireReleasedOriginal(root, malformed); RequireReleasedBinaries();
        }
    }

    [WindowsStorageFact]
    public void Complete_actual_probe_rejects_late_frame_changes_and_aggregate_audio_budgets()
    {
        var bytes = Fixture("UNITTOKO_VIDEO_FIXTURE_MP4"); var expected = Expected(bytes).Content;
        var output = File.ReadAllText(RequiredPath("UNITTOKO_VIDEO_PROBE_JSON"));
        var parsed = WindowsVideoDecoder.ParseProbe(output, expected, Limits());
        Assert.Equal("probed", parsed.Status); Assert.Equal(5L, parsed.VideoFrames); Assert.Equal(24L, parsed.AudioFrames); Assert.Equal(24000L, parsed.AudioSamples);
        Assert.Equal(expected.Length, parsed.Length); Assert.Equal(expected.Sha256, parsed.Sha256);
        var original = JsonNode.Parse(output)!.AsObject();
        foreach (var mutation in new Action<JsonObject>[] {
            root => root["streams"]![0]!["tags"] = new JsonObject { ["unexpected"] = "metadata" },
            root => Last(root, "video")["width"] = 65,
            root => Last(root, "video")["best_effort_timestamp_time"] = "0.950000",
            root => Last(root, "audio")["channels"] = 2,
            root => root["format"]!["size"] = (bytes.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }) {
            var changed = original.DeepClone().AsObject(); mutation(changed);
            Assert.Throws<InvalidDataException>(() => WindowsVideoDecoder.ParseProbe(changed.ToJsonString(), expected, Limits()));
        }
        foreach (var limits in new[] { Limits(audioSamples: 23999), Limits(audioFrames: 23), Limits(videoFrames: 4) })
            Assert.Throws<InvalidDataException>(() => WindowsVideoDecoder.ParseProbe(output, expected, limits));
        // A stereo observation doubles scalar samples even when each channel's sample-frame count is unchanged.
        var stereo = original.DeepClone().AsObject();
        foreach (var stream in stereo["streams"]!.AsArray().OfType<JsonObject>().Where(row => row["codec_type"]!.GetValue<string>() == "audio")) stream["channels"] = 2;
        foreach (var frame in stereo["frames"]!.AsArray().OfType<JsonObject>().Where(row => row["media_type"]!.GetValue<string>() == "audio")) frame["channels"] = 2;
        Assert.Equal(48000L, WindowsVideoDecoder.ParseProbe(stereo.ToJsonString(), expected, Limits(audioSamples: 48000)).AudioSamples);
        Assert.Throws<InvalidDataException>(() => WindowsVideoDecoder.ParseProbe(stereo.ToJsonString(), expected, Limits(audioSamples: 47999)));
    }

    [WindowsStorageFact]
    public async Task Cancellation_and_decoder_deadline_release_binary_pins_and_private_handles_without_success()
    {
        var bytes = Fixture("UNITTOKO_VIDEO_FIXTURE_MP4"); using var root = new StorageRoot(); var expected = Expected(bytes);
        await using var original = await PublishAsync(root, expected, bytes);
        using (var alreadyCanceled = new CancellationTokenSource()) {
            alreadyCanceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsVideoDecoder(Options()).DecodeAsync(original, expected, Limits(), alreadyCanceled.Token));
        }
        using (var cancellation = new CancellationTokenSource()) {
            // The real asynchronous executable hash pinning has begun before cancellation.
            var decoding = new WindowsVideoDecoder(Options()).DecodeAsync(original, expected, Limits(), cancellation.Token);
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);
            RequireReleasedBinaries();
        }
        var started = Stopwatch.StartNew();
        AssertRejected(await new WindowsVideoDecoder(Options() with { Timeout = TimeSpan.FromTicks(1) }).DecodeAsync(original, expected, Limits(), default), "decode_timeout");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8)); RequireReleasedBinaries();
        Assert.Throws<IOException>(() => File.Delete(root.Original)); // The caller still owns its verified original.
        await original.DisposeAsync(); RequireReleasedOriginal(root, bytes);
        File.Delete(root.Original); Assert.False(File.Exists(root.Original));
    }

    private static VideoEvidenceLimits Limits(long audioSamples = 48000, long audioFrames = 100, long videoFrames = 20) =>
        new(1_000_000, 128, 128, 16384, TimeSpan.FromSeconds(1), videoFrames, 2, 48000, audioSamples, audioFrames);
    private static VideoDecoderOptions Options() => new(RequiredPath("UNITTOKO_VIDEO_FFPROBE"), RequiredPath("UNITTOKO_VIDEO_FFMPEG"),
        FfprobeHash, FfmpegHash, 256 * 1024 * 1024, TimeSpan.FromSeconds(20), 256 * 1024);
    private static string RequiredPath(string variable) {
        var value = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"Set {variable} for the actual configured local video tests");
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value)) throw new InvalidOperationException($"{variable} must identify the existing absolute local fixture/runtime file");
        return Path.GetFullPath(value);
    }
    private static byte[] Fixture(string variable) {
        var path = RequiredPath(variable); Assert.InRange(new FileInfo(path).Length, 1L, 1_000_000L); return File.ReadAllBytes(path);
    }
    private static ExpectedPublication Expected(byte[] bytes) => new(new(new string('1', 64), 1, 2, 1, new string('2', 64), new string('3', 64)), 1,
        new(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
    private static async Task<VerifiedNativeOriginalHandle> PublishAsync(StorageRoot root, ExpectedPublication expected, byte[] bytes) {
        var store = root.Open(); var budget = new StorageIoBudget(1_000_000, TimeSpan.FromSeconds(10), 65536); using var source = new MemoryStream(bytes);
        var staged = Assert.IsType<StageOutcome.Sealed>(await store.StageAsync(expected.Attempt, source, budget, default)); Assert.Equal(expected.Content, staged.Stage.Content);
        Assert.Equal(PublishState.Published, (await store.PublishAsync(expected, budget, default)).State);
        return Assert.IsType<NativeOpenOutcome.Opened>(await store.OpenVerifiedForNativeValidationAsync(expected, budget, default)).Handle;
    }
    private static JsonObject Last(JsonObject root, string kind) => root["frames"]!.AsArray().OfType<JsonObject>().Last(row => row["media_type"]!.GetValue<string>() == kind);
    private static void AssertRejected(VideoDecodeResult result, string status) {
        Assert.True(result.Status == status, $"Expected {status}; actual decoder returned {result.Status}");
        Assert.Null(result.Mime); Assert.Null(result.Sha256); Assert.Null(result.Length); Assert.Null(result.VideoFrames); Assert.Null(result.AudioSamples);
    }
    private static void RequireReleasedOriginal(StorageRoot root, byte[] bytes) {
        using var file = File.Open(root.Original, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(bytes.LongLength, file.Length); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(file)));
    }
    private static void RequireReleasedBinaries() {
        foreach (var variable in new[] { "UNITTOKO_VIDEO_FFPROBE", "UNITTOKO_VIDEO_FFMPEG" }) {
            using var pin = File.Open(RequiredPath(variable), FileMode.Open, FileAccess.Read, FileShare.None); Assert.True(pin.Length > 0);
        }
    }
}
