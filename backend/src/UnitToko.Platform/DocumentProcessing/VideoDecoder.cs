using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record VideoDecoderOptions(string FfprobeExecutable, string FfmpegExecutable, string FfprobeSha256,
    string FfmpegSha256, long MaximumMemoryBytes, TimeSpan Timeout, int MaximumOutputBytes);

internal sealed record VideoDecodeResult(string Status, string? Mime = null, string? VideoCodec = null,
    int? Width = null, int? Height = null, decimal? DurationSeconds = null, long? VideoFrames = null,
    string? AudioCodec = null, int? AudioChannels = null, int? AudioSampleRate = null, long? AudioFrames = null,
    long? AudioSamples = null, long? Length = null, string? Sha256 = null);

/// <summary>Two full local decode passes against one immutable original. No evidence eligibility or host acceptance is granted.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsVideoDecoder(VideoDecoderOptions options)
{
    private const string Codecs = "h264,hevc,vp8,vp9,aac,opus,vorbis";
    private const decimal PrintedTimePrecision = 0.000001m;
    private const string Entries = "format=format_name,duration,size:format_tags=major_brand:" +
        "stream=index,codec_name,codec_type,width,height,sample_rate,channels,time_base,duration_ts,duration,nb_frames:" +
        "stream_disposition=attached_pic:stream_tags=:stream_side_data=:" +
        "frame=media_type,stream_index,best_effort_timestamp_time,duration_time,width,height,nb_samples,channels:" +
        "frame_tags=:frame_side_data=:program=program_id:program_tags=:stream_group=index,id,nb_streams";

    public async Task<VideoDecodeResult> DecodeAsync(VerifiedNativeOriginalHandle original, ExpectedPublication expected,
        VideoEvidenceLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return new("decoder_unavailable");
        if (original is null || expected is null || limits is null || original.Publication != expected ||
            expected.Content.Length > limits.MaximumBytes) return new("invalid_media");
        if (!ValidOptions()) return new("decoder_unavailable");
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(options.Timeout);
        var pins = new List<FileStream>(); var pinned = false;
        try
        {
            await PinAsync(options.FfprobeExecutable, options.FfprobeSha256);
            await PinAsync(options.FfmpegExecutable, options.FfmpegSha256);
            pinned = true;
            var header = await HeaderAsync(original, expected, limits.MaximumBytes, deadline.Token);
            var probeArgs = CommonInput(header, expected, limits);
            probeArgs.InsertRange(0, ["-hide_banner", "-v", "error"]);
            probeArgs.AddRange(["-show_frames", "-show_streams", "-show_format", "-show_programs", "-show_stream_groups",
                "-show_entries", Entries, "-show_optional_fields", "auto", "-noprivate", "-of", "json=string_validation=fail",
                "-i", "fd:"]);
            var probe = await WindowsBoundedProcess.RunAsync(options.FfprobeExecutable, probeArgs, options.MaximumMemoryBytes,
                Remaining(), original, expected, limits.MaximumBytes, deadline.Token, options.MaximumOutputBytes);
            var failure = Failure(probe, expected.Content);
            if (failure is not null) return new(failure);
            var facts = ParseProbe(probe.Output, expected.Content, limits, header, deadline.Token);
            Remaining();
            var decodeArgs = CommonInput(header, expected, limits);
            decodeArgs.InsertRange(0, ["-hide_banner", "-nostdin", "-nostats", "-v", "error", "-xerror", "-max_error_rate", "0",
                "-abort_on", "empty_output+empty_output_stream", "-filter_threads", "1", "-filter_complex_threads", "1"]);
            decodeArgs.AddRange(["-hwaccel", "none", "-c:v", facts.VideoCodec!]);
            if (facts.AudioCodec is not null) decodeArgs.AddRange(["-c:a", facts.AudioCodec]);
            decodeArgs.AddRange(["-i", "fd:", "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "-1", "-map_chapters", "-1",
                "-threads", "1", "-fps_mode", "passthrough", "-f", "null", "-"]);
            var decoded = await WindowsBoundedProcess.RunAsync(options.FfmpegExecutable, decodeArgs, options.MaximumMemoryBytes,
                Remaining(), original, expected, limits.MaximumBytes, deadline.Token);
            failure = Failure(decoded, expected.Content);
            if (failure is not null) return new(failure);
            if (decoded.Output.Length != 0) return new("decoder_error");
            Remaining(); ct.ThrowIfCancellationRequested();
            return facts with { Status = "decoded" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new("decode_timeout"); }
        catch (TimeoutException) { return new("decode_timeout"); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException or
            KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return new(pinned ? "invalid_media" : "decoder_unavailable"); }
        catch { return new(pinned ? "decoder_error" : "decoder_unavailable"); }
        finally { foreach (var pin in pins) await pin.DisposeAsync(); }

        TimeSpan Remaining()
        {
            deadline.Token.ThrowIfCancellationRequested();
            var remaining = options.Timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            return remaining;
        }
        async Task PinAsync(string path, string hash)
        {
            Remaining(); LocalPath(path);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            pins.Add(file);
            if (Convert.ToHexString(await SHA256.HashDataAsync(file, deadline.Token)).ToLowerInvariant() != hash) throw Invalid();
            LocalPath(path); Remaining();
        }
    }

    private bool ValidOptions() => options is not null && options.MaximumMemoryBytes > 0 &&
        options.Timeout > TimeSpan.Zero && options.Timeout.TotalMilliseconds <= uint.MaxValue - 1 &&
        options.MaximumOutputBytes is >= 1 and <= 16 * 1024 * 1024 &&
        Path.GetFileName(options.FfprobeExecutable) == "ffprobe.exe" && Path.GetFileName(options.FfmpegExecutable) == "ffmpeg.exe" &&
        Hash(options.FfprobeSha256) && Hash(options.FfmpegSha256);

    private static List<string> CommonInput(ContainerHeader header, ExpectedPublication expected, VideoEvidenceLimits limits)
    {
        var args = new List<string> { "-protocol_whitelist", "fd", "-format_whitelist", header.Mime == "video/mp4" ? "mov" : "matroska,webm",
            "-codec_whitelist", Codecs, "-threads", "1", "-err_detect", "explode", "-max_streams", "2",
            "-probesize", expected.Content.Length.ToString(CultureInfo.InvariantCulture), "-analyzeduration",
            (limits.MaximumDuration.Ticks / 10).ToString(CultureInfo.InvariantCulture), "-fd", "0", "-f", header.Mime == "video/mp4" ? "mov" : "matroska" };
        if (header.Mime == "video/mp4") args.AddRange(["-enable_drefs", "0", "-use_absolute_path", "0"]);
        return args;
    }
    private static string? Failure(BoundedProcessResult result, StorageContent expected)
    {
        if (result.Status != "exited") return result.Status is "decode_timeout" or "invalid_media" ? result.Status : "decoder_error";
        if (result.InputLength != expected.Length || result.InputSha256 != expected.Sha256) return "invalid_media";
        if (result.ExitCode != 0 || result.Error.Length != 0) return "invalid_media";
        return null;
    }

    internal static VideoDecodeResult ParseProbe(string output, StorageContent expected, VideoEvidenceLimits limits)
        => ParseProbe(output, expected, limits, null, default);

    private static VideoDecodeResult ParseProbe(string output, StorageContent expected, VideoEvidenceLimits limits,
        ContainerHeader? expectedHeader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(output, new() { MaxDepth = 8 }); var root = document.RootElement;
        Shape(root, ["format", "frames", "streams"], ["programs", "stream_groups"]);
        EmptyArray(root, "programs"); EmptyArray(root, "stream_groups");
        var format = root.GetProperty("format"); Shape(format, ["duration", "format_name", "size"], ["tags"]);
        if (Digits(Text(format, "size")) != expected.Length || expected.Length > limits.MaximumBytes) throw Invalid();
        var maximumDuration = limits.MaximumDuration.Ticks / (decimal)TimeSpan.TicksPerSecond;
        var duration = Seconds(Text(format, "duration"), maximumDuration, positive: true);
        var name = Text(format, "format_name"); string mime;
        if (name == "mov,mp4,m4a,3gp,3g2,mj2")
        {
            var tags = format.GetProperty("tags"); Shape(tags, ["major_brand"]);
            if (!Mp4Brand(Text(tags, "major_brand"))) throw Invalid();
            if (expectedHeader?.Brand is { } brand && Text(tags, "major_brand") != brand) throw Invalid();
            mime = "video/mp4";
        }
        else if (name == "matroska,webm")
        {
            if (format.TryGetProperty("tags", out var tags)) Shape(tags, []);
            mime = "video/webm"; // DecodeAsync additionally requires the immutable original's actual EBML DocType.
        }
        else throw Invalid();
        if (expectedHeader is not null && mime != expectedHeader.Mime) throw Invalid();
        var streams = root.GetProperty("streams");
        if (streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() is < 1 or > 2) throw Invalid();
        StreamFacts? video = null, audio = null; var indexes = new HashSet<int>();
        foreach (var row in streams.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var type = Text(row, "codec_type");
            Shape(row, type switch
            {
                "video" => ["codec_name", "codec_type", "disposition", "height", "index", "time_base", "width"],
                "audio" => ["channels", "codec_name", "codec_type", "disposition", "index", "sample_rate", "time_base"],
                _ => throw Invalid()
            }, ["duration", "duration_ts", "nb_frames", "tags"]);
            // ffprobe emits an empty tags object even when stream_tags= selects no entries.
            if (row.TryGetProperty("tags", out var streamTags)) Shape(streamTags, []);
            var index = Integer(row, "index", allowZero: true);
            if (index > 1 || !indexes.Add(index)) throw Invalid();
            var disposition = row.GetProperty("disposition"); Shape(disposition, ["attached_pic"]);
            if (Integer(disposition, "attached_pic", allowZero: true) != 0) throw Invalid();
            var codec = Text(row, "codec_name"); var timeBase = Rational(Text(row, "time_base"));
            var hasDuration = row.TryGetProperty("duration", out var streamDuration);
            var hasTicks = row.TryGetProperty("duration_ts", out var durationTicks);
            if (hasDuration != hasTicks) throw Invalid();
            if (hasDuration)
            {
                var seconds = Seconds(String(streamDuration), maximumDuration, positive: true);
                var ticks = LongInteger(durationTicks);
                if (decimal.Abs(checked(ticks * timeBase) - seconds) > PrintedTimePrecision) throw Invalid();
                duration = Math.Max(duration, seconds);
            }
            long? declaredFrames = row.TryGetProperty("nb_frames", out var count) ? Digits(String(count)) : null;
            var facts = new StreamFacts(index, codec, timeBase, declaredFrames);
            if (type == "video")
            {
                if (video is not null || (mime == "video/mp4" ? codec is not ("h264" or "hevc") : codec is not ("vp8" or "vp9"))) throw Invalid();
                facts.Width = Integer(row, "width"); facts.Height = Integer(row, "height");
                if (!limits.AcceptsDimensions(facts.Width, facts.Height) || declaredFrames > limits.MaximumVideoFrames) throw Invalid();
                video = facts;
            }
            else
            {
                if (audio is not null || (mime == "video/mp4" ? codec != "aac" : codec is not ("opus" or "vorbis"))) throw Invalid();
                facts.Channels = Integer(row, "channels"); facts.SampleRate = checked((int)Digits(Text(row, "sample_rate")));
                if (facts.Channels > limits.MaximumAudioChannels || facts.SampleRate > limits.MaximumAudioSampleRate) throw Invalid();
                audio = facts;
            }
        }
        if (video is null || indexes.Count != streams.GetArrayLength() || !indexes.Contains(0)) throw Invalid();
        var frames = root.GetProperty("frames"); if (frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() == 0) throw Invalid();
        foreach (var frame in frames.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var type = Text(frame, "media_type");
            Shape(frame, type switch
            {
                "video" => ["best_effort_timestamp_time", "duration_time", "height", "media_type", "stream_index", "width"],
                "audio" => ["best_effort_timestamp_time", "channels", "duration_time", "media_type", "nb_samples", "stream_index"],
                _ => throw Invalid()
            });
            var source = type == "video" ? video : audio ?? throw Invalid();
            if (Integer(frame, "stream_index", allowZero: true) != source.Index) throw Invalid();
            var timestamp = Seconds(Text(frame, "best_effort_timestamp_time"), maximumDuration);
            var frameDuration = Seconds(Text(frame, "duration_time"), maximumDuration, positive: true);
            var end = checked(timestamp + frameDuration);
            if (end > maximumDuration || (source.LastTimestamp is not null && timestamp <= source.LastTimestamp)) throw Invalid();
            source.LastTimestamp = timestamp; source.Frames = checked(source.Frames + 1); duration = Math.Max(duration, end);
            if (type == "video")
            {
                var width = Integer(frame, "width"); var height = Integer(frame, "height");
                if (!limits.AcceptsDimensions(width, height) || width != source.Width || height != source.Height || source.Frames > limits.MaximumVideoFrames) throw Invalid();
            }
            else
            {
                var channels = Integer(frame, "channels"); var samples = Integer(frame, "nb_samples");
                // nb_samples counts sample frames per channel; this limit and result count total decoded scalar samples.
                source.Samples = checked(source.Samples + checked((long)samples * channels));
                if (channels != source.Channels || channels > limits.MaximumAudioChannels || source.Frames > limits.MaximumAudioFrames ||
                    source.Samples > limits.MaximumAudioSamples || decimal.Abs(samples / (decimal)source.SampleRate - frameDuration) > Math.Max(PrintedTimePrecision, source.TimeBase)) throw Invalid();
            }
        }
        if (video.Frames == 0 || (video.DeclaredFrames is not null && video.DeclaredFrames != video.Frames) ||
            (audio is not null && (audio.Frames == 0 || audio.Samples == 0))) throw Invalid();
        return new("probed", mime, video.Codec, video.Width, video.Height, duration, video.Frames,
            audio?.Codec, audio?.Channels, audio?.SampleRate, audio?.Frames ?? 0, audio?.Samples ?? 0, expected.Length, expected.Sha256);
    }

    private sealed class StreamFacts(int index, string codec, decimal timeBase, long? declaredFrames)
    {
        internal int Index { get; } = index;
        internal string Codec { get; } = codec;
        internal decimal TimeBase { get; } = timeBase;
        internal long? DeclaredFrames { get; } = declaredFrames;
        internal int Width, Height, Channels, SampleRate;
        internal long Frames, Samples;
        internal decimal? LastTimestamp;
    }
    private sealed record ContainerHeader(string Mime, string? Brand = null);
    private static async Task<ContainerHeader> HeaderAsync(VerifiedNativeOriginalHandle original, ExpectedPublication expected, long maximum, CancellationToken ct)
    {
        using var lease = original.AcquireInput(expected, maximum);
        using var input = new FileStream(lease.ChildInput, FileAccess.Read, 4096, false);
        var bytes = new byte[(int)Math.Min(4096, expected.Content.Length)];
        await input.ReadExactlyAsync(bytes, ct); lease.RequireUnchanged(); ct.ThrowIfCancellationRequested();
        return Header(bytes);
    }
    private static ContainerHeader Header(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 16 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (size < 16 || size > bytes.Length || (size - 16) % 4 != 0) throw Invalid();
            var brand = Encoding.ASCII.GetString(bytes.Slice(8, 4));
            if (!Mp4Brand(brand)) throw Invalid();
            return new("video/mp4", brand);
        }
        if (bytes.Length < 8 || !bytes[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) throw Invalid();
        var position = 4; var headerLength = Vint(bytes, ref position, id: false);
        if (headerLength > (ulong)(bytes.Length - position)) throw Invalid();
        var end = checked(position + (int)headerLength); var doctype = false; var elements = 0;
        while (position < end)
        {
            if (++elements > 64) throw Invalid();
            var id = Vint(bytes[..end], ref position, id: true); var size = Vint(bytes[..end], ref position, id: false);
            if (size > (ulong)(end - position)) throw Invalid();
            if (id == 0x4282)
            {
                if (doctype || size != 4 || !bytes.Slice(position, 4).SequenceEqual("webm"u8)) throw Invalid();
                doctype = true;
            }
            position = checked(position + (int)size);
        }
        if (!doctype || position != end) throw Invalid();
        return new("video/webm");
    }
    private static ulong Vint(ReadOnlySpan<byte> bytes, ref int position, bool id)
    {
        if (position >= bytes.Length || bytes[position] == 0) throw Invalid();
        var marker = 0x80; var width = 1;
        while ((bytes[position] & marker) == 0) { marker >>= 1; width++; }
        if (width > (id ? 4 : 8) || width > bytes.Length - position) throw Invalid();
        ulong value = id ? bytes[position] : (uint)(bytes[position] & (marker - 1)); position++;
        for (var i = 1; i < width; i++) value = (value << 8) | bytes[position++];
        if (!id && value == (1UL << (7 * width)) - 1) throw Invalid();
        return value;
    }
    private static void Shape(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || required.Any(name => !names.Contains(name, StringComparer.Ordinal)) ||
            names.Any(name => !required.Contains(name, StringComparer.Ordinal) && !(optional?.Contains(name, StringComparer.Ordinal) ?? false))) throw Invalid();
    }
    private static void EmptyArray(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var array) && (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 0)) throw Invalid();
    }
    private static string Text(JsonElement value, string name) => String(value.GetProperty(name));
    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text : throw Invalid();
    private static int Integer(JsonElement value, string name, bool allowZero = false)
    {
        var number = value.GetProperty(name);
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var result) || result < (allowZero ? 0 : 1)) throw Invalid();
        return result;
    }
    private static long LongInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result) || result <= 0) throw Invalid();
        return result;
    }
    private static long Digits(string value)
    {
        if (value.Length is < 1 or > 19 || value[0] == '0' || value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0) throw Invalid();
        return result;
    }
    private static decimal Seconds(string value, decimal maximum, bool positive = false)
    {
        if (value.Length is < 1 or > 32 || value.Any(c => c is not (>= '0' and <= '9') and not '.') ||
            value.Count(c => c == '.') > 1 || value[0] == '.' || value[^1] == '.' ||
            !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0 || (positive && seconds == 0) || seconds > maximum) throw Invalid();
        return seconds;
    }
    private static decimal Rational(string value)
    {
        var parts = value.Split('/'); if (parts.Length != 2) throw Invalid();
        return Digits(parts[0]) / (decimal)Digits(parts[1]);
    }
    private static bool Mp4Brand(string brand) => brand is "isom" or "iso2" or "iso3" or "iso4" or "iso5" or "iso6" or "iso7" or "iso8" or "iso9" or
        "mp41" or "mp42" or "avc1" or "hvc1" or "hev1" or "M4V ";
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 ||
            path[1] != ':' || path.AsSpan(2).Contains(':')) throw Invalid();
        for (FileSystemInfo? item = new FileInfo(path); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (!item.Exists || item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Video facts are absent, unsupported or outside the explicit limits.");
}
