using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using MongoDB.Driver;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record EvidenceImageRevalidationOptions(string Root, string StoreId, string[] TrustedOperatorSids,
    string ConsumerId, string HostAcceptanceReference, EvidenceLimits Limits, ImageDecoderOptions Decoder,
    LocalClamdOptions Scanner);

/// <summary>One private-store registration shared by the governed request and its actual scanner.</summary>
public sealed class EvidenceImageRevalidationRuntime
{
    private readonly EvidenceImageRevalidationService requests;
    private readonly EvidenceImageRevalidationProcessor processor;
    private readonly CollectionMigrations migrations;
    public string PipelineId { get; }

    internal EvidenceImageRevalidationRuntime(EvidenceImageRevalidationService requests,
        EvidenceImageRevalidationProcessor processor, CollectionMigrations migrations, string pipelineId)
    { this.requests = requests; this.processor = processor; this.migrations = migrations; PipelineId = pipelineId; }

    public static EvidenceImageRevalidationRuntime Create(IMongoDatabase database, MongoUnitOfWork unit,
        IdAllocator ids, IClock clock, EvidenceImageRevalidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() || options.TrustedOperatorSids is null || options.Decoder is null ||
            options.Scanner is null || options.Limits is null || options.Scanner.MaximumBytes != options.Limits.MaximumBytes ||
            string.IsNullOrWhiteSpace(options.HostAcceptanceReference) || options.HostAcceptanceReference.Length > 256 ||
            options.HostAcceptanceReference.Any(char.IsControl) || string.IsNullOrWhiteSpace(options.ConsumerId) ||
            options.ConsumerId.Length > 128 || options.ConsumerId != options.ConsumerId.Trim() || options.ConsumerId.Any(char.IsControl))
            throw new ArgumentException("Image revalidation runtime configuration is incomplete");
        var registration = EvidenceStoreRegistration.OpenLocal(options.Root, options.StoreId, options.TrustedOperatorSids);
        if (registration.Store is null) throw new InvalidOperationException("Private image storage is unavailable");
        var pipeline = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            Format = "evidence-image-revalidation-v1", options.HostAcceptanceReference,
            options.Limits, options.Decoder, options.Scanner })))).ToLowerInvariant();
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new EvidenceImageRevalidationService(database, unit, ids, clock, registration,
            catalog, options.ConsumerId, pipeline);
        var processor = new EvidenceImageRevalidationProcessor(database, unit, ids, clock, registration,
            options.ConsumerId, pipeline, options.Limits, new LocalClamdScanner(options.Scanner, clock),
            new WindowsImageDecoder(options.Decoder));
        return new(requests, processor, new CollectionMigrations(database, clock), pipeline);
    }

    public Task<CatalogImageRevalidationRequestResult> RequestCatalogImageAsync(ActorContext actor,
        RequestCatalogImageRevalidation input, UnitToko.Platform.Authorization.AuthorizationCommandMetadata metadata,
        CancellationToken ct = default) => requests.RequestAsync(actor, input, metadata, ct);
    public Task<CatalogImageRevalidationRequestResult?> LookupCatalogImageAsync(ActorContext actor, string key,
        CancellationToken ct = default) => requests.LookupAsync(actor, key, ct);
    public Task<CatalogImageRevalidationCurrent> CurrentCatalogImageAsync(ActorContext actor, long skuId,
        long reservationId, EvidenceRef reference, CancellationToken ct = default) =>
        requests.CurrentAsync(actor, skuId, reservationId, reference, ct);
    public async Task<string> RunOnceAsync(CancellationToken ct = default) =>
        await migrations.IsCurrentAsync(ct) ? await processor.RunOnceAsync(ct) : "schema_unavailable";
}
