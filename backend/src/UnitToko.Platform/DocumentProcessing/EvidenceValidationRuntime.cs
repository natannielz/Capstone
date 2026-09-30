using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record EvidenceValidationRuntimeOptions(string Root, string StoreId, string[] TrustedOperatorSids,
    string ConsumerId, string HostAcceptanceReference, EvidenceLimits Limits, ImageDecoderOptions Decoder, LocalClamdOptions Scanner);

/// <summary>Composition for the actual private adapters only. No injectable clean-verdict production adapter.</summary>
public sealed class EvidenceValidationRuntime
{
    private readonly EvidenceValidationProcessor processor;
    private readonly CollectionMigrations migrations;
    public string PipelineId => processor.PipelineId;
    private EvidenceValidationRuntime(EvidenceValidationProcessor processor, CollectionMigrations migrations) { this.processor = processor; this.migrations = migrations; }
    public static EvidenceValidationRuntime Create(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, EvidenceValidationRuntimeOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() || options.Decoder is null || options.Scanner is null || options.Limits is null || options.TrustedOperatorSids is null ||
            string.IsNullOrWhiteSpace(options.HostAcceptanceReference) || options.HostAcceptanceReference.Length > 256 || options.HostAcceptanceReference.Any(char.IsControl) ||
            options.Scanner.MaximumBytes != options.Limits.MaximumBytes) throw new ArgumentException("Evidence processing configuration is incomplete or unsupported");
        var registration = EvidenceStoreRegistration.OpenLocal(options.Root, options.StoreId, options.TrustedOperatorSids);
        if (registration.Store is null) throw new InvalidOperationException("Private evidence storage is unavailable");
        // Supplied pins are reviewed release inputs. Never compute approval pins from whatever executable is installed.
        var pipeline = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            Format = "evidence-validation-v1", options.HostAcceptanceReference, options.Limits, options.Decoder, options.Scanner })))).ToLowerInvariant();
        var claim = new OutboxClaimPolicy(["evidence.validation.requested"], 5, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return new(new(database, unit, ids, clock, registration, options.ConsumerId, pipeline, claim, options.Limits,
            new LocalClamdScanner(options.Scanner, clock), new WindowsImageDecoder(options.Decoder)), new(database, clock));
    }
    public async Task<string> RunOnceAsync(CancellationToken ct = default) =>
        await migrations.IsCurrentAsync(ct) ? await processor.RunOnceAsync(ct) : "schema_unavailable";
}
