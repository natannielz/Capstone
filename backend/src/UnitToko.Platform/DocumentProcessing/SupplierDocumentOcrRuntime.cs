using MongoDB.Driver;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record SupplierDocumentOcrRuntimeOptions(string Root, string StoreId, string[] TrustedOperatorSids,
    string ConsumerId, string HostAcceptanceReference, string ModelSelectionReference, LocalOcrOptions Engine);

/// <summary>Offline candidate composition. Configuration is not model/host acceptance; matching current database approvals are also required.</summary>
public sealed class SupplierDocumentOcrRuntime
{
    private readonly SupplierDocumentOcrProcessor processor;
    private readonly CollectionMigrations migrations;
    public string PipelineId => processor.PipelineId;
    private SupplierDocumentOcrRuntime(SupplierDocumentOcrProcessor processor, CollectionMigrations migrations) { this.processor = processor; this.migrations = migrations; }
    public static SupplierDocumentOcrRuntime Create(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, SupplierDocumentOcrRuntimeOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() || options.Engine is null || options.Engine.Files is null || options.TrustedOperatorSids is null ||
            !Reference(options.HostAcceptanceReference) || !Reference(options.ModelSelectionReference)) throw new ArgumentException("OCR processing configuration is incomplete or unsupported");
        var registration = EvidenceStoreRegistration.OpenLocal(options.Root, options.StoreId, options.TrustedOperatorSids);
        if (registration.Store is null) throw new InvalidOperationException("Private evidence storage is unavailable");
        return new(new(database, unit, ids, clock, registration, options.ConsumerId, new WindowsLocalOcrEngine(options.Engine, UnitToko.Modules.Documents.ExtractionPurpose.SupplierInvoice),
            options.HostAcceptanceReference, options.ModelSelectionReference), new(database, clock));
    }
    private static bool Reference(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim() && !value.Any(char.IsControl);
    public async Task<string> RunOnceAsync(CancellationToken ct = default) => await migrations.IsCurrentAsync(ct) ? await processor.RunOnceAsync(ct) : "schema_unavailable";
}
