using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

/// <summary>Explicit server video limits. They neither select a live policy nor change image admission.</summary>
public sealed record VideoEvidenceLimits
{
    public long MaximumBytes { get; }
    public int MaximumWidth { get; }
    public int MaximumHeight { get; }
    public long MaximumPixels { get; }
    public TimeSpan MaximumDuration { get; }
    public long MaximumVideoFrames { get; }
    public int MaximumAudioChannels { get; }
    public int MaximumAudioSampleRate { get; }
    /// <summary>Total decoded scalar audio samples across all channels, not encoded packets.</summary>
    public long MaximumAudioSamples { get; }
    public long MaximumAudioFrames { get; }

    public VideoEvidenceLimits(long maximumBytes, int maximumWidth, int maximumHeight, long maximumPixels,
        TimeSpan maximumDuration, long maximumVideoFrames, int maximumAudioChannels, int maximumAudioSampleRate,
        long maximumAudioSamples, long maximumAudioFrames)
    {
        if (maximumBytes <= 0 || maximumWidth <= 0 || maximumHeight <= 0 || maximumPixels <= 0 ||
            maximumDuration <= TimeSpan.Zero || maximumVideoFrames <= 0 || maximumAudioChannels <= 0 ||
            maximumAudioSampleRate <= 0 || maximumAudioSamples <= 0 || maximumAudioFrames <= 0)
            throw new DomainValidationException("video_evidence_invalid_limits", "Video limits must be explicitly configured");
        MaximumBytes = maximumBytes; MaximumWidth = maximumWidth; MaximumHeight = maximumHeight; MaximumPixels = maximumPixels;
        MaximumDuration = maximumDuration; MaximumVideoFrames = maximumVideoFrames; MaximumAudioChannels = maximumAudioChannels;
        MaximumAudioSampleRate = maximumAudioSampleRate; MaximumAudioSamples = maximumAudioSamples; MaximumAudioFrames = maximumAudioFrames;
    }

    public bool AcceptsDimensions(int width, int height) => width > 0 && width <= MaximumWidth &&
        height > 0 && height <= MaximumHeight && (long)width * height <= MaximumPixels;
}
