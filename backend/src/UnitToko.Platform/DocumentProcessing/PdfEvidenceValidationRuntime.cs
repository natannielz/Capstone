using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.DocumentProcessing;

public sealed record PdfEvidenceRuntimePin(string Path, string Sha256);
public sealed record PdfEvidenceRasterOptions(string PythonExecutable, string HelperPath, string PackageRoot, string PillowRoot,
    string WheelPath, string PillowVersion, IReadOnlyList<PdfEvidenceRuntimePin> Files,
    int MaximumPages, int MaximumPagePixels, int MaximumTotalPixels, int MaximumOutputBytes, long MaximumMemoryBytes, TimeSpan Timeout);
public sealed record PdfEvidenceValidationRuntimeOptions(string Root, string StoreId, string[] TrustedOperatorSids,
    string ConsumerId, string HostAcceptanceReference, string ClamdConfigurationAcceptanceReference,
    PdfEvidenceRasterOptions Raster, LocalClamdOptions Scanner);

/// <summary>Only the actual private store, local scanner and existing pinned PDFium adapter are composable in production.</summary>
public sealed class PdfEvidenceValidationRuntime
{
    private readonly PdfEvidenceValidationProcessor processor;
    private readonly CollectionMigrations migrations;
    public string PipelineId => processor.PipelineId;
    private PdfEvidenceValidationRuntime(PdfEvidenceValidationProcessor processor, CollectionMigrations migrations) { this.processor = processor; this.migrations = migrations; }
    public static PdfEvidenceValidationRuntime Create(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        PdfEvidenceValidationRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() || options.Raster is null || options.Scanner is null || options.TrustedOperatorSids is null ||
            options.Raster.Files is null || options.Raster.Files.Count is < 20 or > 8192 ||
            options.Raster.Files.Any(pin => pin is null || string.IsNullOrEmpty(pin.Path) || pin.Path.Length > 1024 || !PdfRasterProtocol.Hash(pin.Sha256)) ||
            options.Raster.MaximumMemoryBytes is < 32 * 1024 * 1024 or > 512L * 1024 * 1024 ||
            options.Raster.Timeout <= TimeSpan.Zero || options.Raster.Timeout > TimeSpan.FromSeconds(60) ||
            !PdfEvidenceValidationProcessor.Reference(options.HostAcceptanceReference, 256) ||
            !PdfEvidenceValidationProcessor.Reference(options.ClamdConfigurationAcceptanceReference, 256) ||
            options.Scanner.MaximumBytes is < 1 or > PdfEvidenceValidationContract.MaximumOriginalBytes)
            throw new ArgumentException("PDF validation configuration is incomplete or unsupported");
        var frozen = options.Raster with { Files = Array.AsReadOnly(options.Raster.Files.OrderBy(pin => pin.Path, StringComparer.OrdinalIgnoreCase).ToArray()) };
        var limits = new PdfRasterLimits(frozen.MaximumPages, frozen.MaximumPagePixels, frozen.MaximumTotalPixels, frozen.MaximumOutputBytes); limits.RequireValid();
        var raster = new PdfRasterOptions(frozen.PythonExecutable, frozen.HelperPath, frozen.PackageRoot, frozen.PillowRoot,
            frozen.WheelPath, frozen.PillowVersion, frozen.Files.Select(pin => new PdfRasterFilePin(pin.Path, pin.Sha256)).ToArray(), limits,
            frozen.MaximumMemoryBytes, frozen.Timeout);
        var registration = EvidenceStoreRegistration.OpenLocal(options.Root, options.StoreId, options.TrustedOperatorSids);
        if (registration.Store is null) throw new InvalidOperationException("Private PDF evidence storage is unavailable");
        // Supplied pins/acceptance references remain operator inputs; installed files never approve themselves.
        var pipeline = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            Format = "pdf-evidence-validation-v1", options.HostAcceptanceReference, options.ClamdConfigurationAcceptanceReference,
            Raster = frozen, options.Scanner })))).ToLowerInvariant();
        var claim = new OutboxClaimPolicy([PdfEvidenceValidationRecords.Kind], 5, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return new(new(database, unit, ids, clock, registration, options.ConsumerId, pipeline, claim,
            new LocalClamdScanner(options.Scanner, clock), new WindowsPdfRaster(raster), options.HostAcceptanceReference,
            options.ClamdConfigurationAcceptanceReference), new(database, clock));
    }
    public async Task<string> RunOnceAsync(CancellationToken ct = default) =>
        await migrations.IsCurrentAsync(ct) ? await processor.RunOnceAsync(ct) : "schema_unavailable";
}
