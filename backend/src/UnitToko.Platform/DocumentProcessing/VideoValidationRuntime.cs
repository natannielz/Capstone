using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record VideoValidationRuntimeOptions(string Root, string StoreId, string[] TrustedOperatorSids,
    string ConsumerId, string HostAcceptanceReference, VideoEvidenceLimits Limits, VideoDecoderOptions Decoder,
    LocalClamdOptions Scanner, long AcceptedClamdStreamCeiling, string ClamdConfigurationAcceptanceReference,
    OutboxClaimPolicy ClaimPolicy);

/// <summary>Explicit private video composition. It installs neither operational approval nor a clean-verdict adapter.</summary>
public sealed class VideoValidationRuntime
{
    private readonly VideoValidationProcessor processor;
    private readonly CollectionMigrations migrations;
    public string PipelineId => processor.PipelineId;
    private VideoValidationRuntime(VideoValidationProcessor processor, CollectionMigrations migrations) { this.processor = processor; this.migrations = migrations; }
    public static VideoValidationRuntime Create(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, VideoValidationRuntimeOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() || options.Decoder is null || options.Scanner is null || options.Limits is null ||
            options.TrustedOperatorSids is null || options.ClaimPolicy is null || !Reference(options.HostAcceptanceReference) ||
            !Reference(options.ClamdConfigurationAcceptanceReference) || options.Scanner.MaximumBytes != options.Limits.MaximumBytes ||
            !options.ClaimPolicy.Kinds.SequenceEqual([VideoValidationRecords.Kind]) || options.Decoder.Timeout > options.ClaimPolicy.Lease ||
            options.Scanner.Timeout > options.ClaimPolicy.Lease) throw new ArgumentException("Video processing configuration is incomplete or unsupported");
        var scanner = LocalClamdScanner.ForVideo(options.Scanner, clock, options.AcceptedClamdStreamCeiling, options.ClamdConfigurationAcceptanceReference);
        var registration = EvidenceStoreRegistration.OpenLocal(options.Root, options.StoreId, options.TrustedOperatorSids);
        if (registration.Store is null) throw new InvalidOperationException("Private evidence storage is unavailable");
        // Approval pins are supplied, never inferred from executables currently present on the host.
        var pipeline = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            Format = "evidence-video-validation-v1", options.StoreId, options.ConsumerId, options.HostAcceptanceReference,
            options.Limits, options.Decoder, options.Scanner, options.AcceptedClamdStreamCeiling,
            options.ClamdConfigurationAcceptanceReference, options.ClaimPolicy })))).ToLowerInvariant();
        return new(new(database, unit, ids, clock, registration, options.ConsumerId, pipeline, options.ClaimPolicy,
            options.Limits, scanner, new WindowsVideoDecoder(options.Decoder), options.HostAcceptanceReference,
            options.ClamdConfigurationAcceptanceReference), new(database, clock));
    }
    public async Task<string> RunOnceAsync(CancellationToken ct = default) =>
        await migrations.IsCurrentAsync(ct) ? await processor.RunOnceAsync(ct) : "schema_unavailable";
    private static bool Reference(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim() && !value.Any(char.IsControl);
}
