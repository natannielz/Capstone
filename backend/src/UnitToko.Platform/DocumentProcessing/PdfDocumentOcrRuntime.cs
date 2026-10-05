using MongoDB.Driver;
using System.Runtime.Versioning;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record PdfDocumentOcrRuntimeOptions(string Root, string StoreId, string[] TrustedOperatorSids, string ConsumerId,
    string HostAcceptanceReference, string ModelSelectionReference, string RasterAcceptanceReference,
    PdfEvidenceRasterOptions Raster, LocalOcrOptions Engine);

/// <summary>Composition does not approve a host, raster, model or document; each source namespace also needs its own finite current policy.</summary>
public sealed class PdfDocumentOcrRuntime
{
    private readonly PdfDocumentOcrProcessor processor;
    private readonly CollectionMigrations migrations;
    public string SourceKind { get; }
    public string PipelineId { get; }
    internal PdfDocumentOcrProcessor Processor => processor;
    [SupportedOSPlatform("windows")]
    private PdfDocumentOcrRuntime(PdfDocumentOcrProcessor processor, CollectionMigrations migrations) {
        this.processor = processor; this.migrations = migrations; SourceKind = processor.SourceKind; PipelineId = processor.PipelineId;
    }
    public static PdfDocumentOcrRuntime CreatePayment(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PdfDocumentOcrRuntimeOptions options) =>
        throw new NotSupportedException("Payment PDF processing requires a separate supplementary evidence owner; primary payment evidence must remain an eligible image");
    public static PdfDocumentOcrRuntime CreateSupplierInvoice(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PdfDocumentOcrRuntimeOptions options) =>
        Create(database, unit, ids, clock, options, "SupplierInvoice");
    public static PdfDocumentOcrRuntime CreatePaymentSupplement(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PdfDocumentOcrRuntimeOptions options) =>
        Create(database, unit, ids, clock, options, Billing.PaymentSupplementBson.SourceKind);
    private static PdfDocumentOcrRuntime Create(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PdfDocumentOcrRuntimeOptions options, string sourceKind) {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() || options.Raster is null || options.Engine is null || options.TrustedOperatorSids is null ||
            options.Raster.Files is null || options.Engine.Files is null || options.Engine.Limits is null ||
            options.Raster.Files.Count is < 20 or > 8192 || options.Engine.Files.Count is < 5 or > 256 ||
            options.Raster.Files.Any(pin => pin is null || string.IsNullOrEmpty(pin.Path) || !PdfRasterProtocol.Hash(pin.Sha256)) ||
            options.Engine.Files.Any(pin => pin is null || string.IsNullOrEmpty(pin.Path) || !PdfRasterProtocol.Hash(pin.Sha256)) ||
            options.Raster.Files.Select(pin => pin.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Raster.Files.Count ||
            options.Engine.Files.Select(pin => pin.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Engine.Files.Count ||
            options.Engine.Limits.MaximumBytes > PdfDocumentExtractionContract.MaximumOriginalBytes || options.Engine.Limits.MaximumWidth > 32768 ||
            options.Engine.Limits.MaximumHeight > 32768 || options.Engine.Limits.MaximumPixels > 25_000_000 ||
            !PdfDocumentOcrProcessor.Reference(options.ConsumerId, 128) || !PdfDocumentOcrProcessor.Reference(options.HostAcceptanceReference, 256) ||
            !PdfDocumentOcrProcessor.Reference(options.ModelSelectionReference, 256) || !PdfDocumentOcrProcessor.Reference(options.RasterAcceptanceReference, 256) ||
            options.Raster.MaximumMemoryBytes is < 32 * 1024 * 1024 or > 512L * 1024 * 1024 ||
            options.Engine.MaximumMemoryBytes is < 16 * 1024 * 1024 or > 1024L * 1024 * 1024 ||
            options.Raster.Timeout <= TimeSpan.Zero || options.Raster.Timeout > TimeSpan.FromMinutes(1) ||
            options.Engine.Timeout < TimeSpan.FromMilliseconds(100) || options.Engine.Timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Explicit PDF OCR configuration is incomplete or unsupported");
        var frozen = options.Raster with { Files = Array.AsReadOnly(options.Raster.Files.OrderBy(pin => pin.Path, StringComparer.OrdinalIgnoreCase).ToArray()) };
        var limits = new PdfRasterLimits(frozen.MaximumPages, frozen.MaximumPagePixels, frozen.MaximumTotalPixels, frozen.MaximumOutputBytes); limits.RequireValid();
        var raster = new PdfRasterOptions(frozen.PythonExecutable, frozen.HelperPath, frozen.PackageRoot, frozen.PillowRoot, frozen.WheelPath, frozen.PillowVersion,
            frozen.Files.Select(pin => new PdfRasterFilePin(pin.Path, pin.Sha256)).ToArray(), limits, frozen.MaximumMemoryBytes, frozen.Timeout);
        var engine = options.Engine with { Files = Array.AsReadOnly(options.Engine.Files.ToArray()) };
        var registration = EvidenceStoreRegistration.OpenLocal(options.Root, options.StoreId, options.TrustedOperatorSids);
        if (registration.Store is null) throw new InvalidOperationException("Private original PDF storage is unavailable");
        return new(new(database, unit, ids, clock, registration, sourceKind, options.ConsumerId, raster, engine, options.HostAcceptanceReference,
            options.ModelSelectionReference, options.RasterAcceptanceReference), new(database, clock));
    }
    public async Task<string> RunOnceAsync(CancellationToken ct = default) {
        if (!OperatingSystem.IsWindows()) return "unavailable";
        return await migrations.IsCurrentAsync(ct) ? await processor.RunOnceAsync(ct) : "schema_unavailable";
    }
}
