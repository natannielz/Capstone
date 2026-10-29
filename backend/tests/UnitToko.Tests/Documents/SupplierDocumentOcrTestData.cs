using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.DocumentProcessing;
using D = UnitToko.Tests.Procurement.SupplierDocumentTestData;
using StorageRoot = UnitToko.Tests.Documents.PrivateEvidenceStorageTests.StorageRoot;

namespace UnitToko.Tests.Documents;

// Explicit local test approvals only. Every original, scan, decode and OCR result still uses its actual owner.
[SupportedOSPlatform("windows")]
internal static class SupplierDocumentOcrTestData
{
    internal const string Consumer = "synthetic-supplier-document-ocr", HostReference = "synthetic-host-only", ModelReference = "synthetic-model-only";
    internal static async Task<SupplierDocumentOcrRuntime> PrepareNativeAsync(D data, string fileName = "supplier-owner-invoice.png") {
        await ConfigureImageAsync(data, fileName); var runtime = CreateRuntime(data); await ApproveAsync(data, runtime.PipelineId); return runtime;
    }
    internal static async Task ConfigureImageAsync(D data, string fileName) {
        var directory = Environment.GetEnvironmentVariable("UNITTOKO_SUPPLIER_OCR_FIXTURES") ?? throw new InvalidOperationException("Explicit synthetic supplier OCR fixture directory is required");
        if (!Path.IsPathFullyQualified(directory) || Path.GetFileName(fileName) != fileName) throw new InvalidOperationException("Synthetic supplier OCR fixture path must be explicit and bounded");
        data.Bytes = await File.ReadAllBytesAsync(Path.Combine(directory, fileName));
        const long maximum = 1024 * 1024;
        Assert.InRange(data.Bytes.LongLength, 1, maximum);
        data.Runtime = EvidenceValidationRuntime.Create(data.Database, data.Unit, new(data.Database), data.Clock,
            new(data.Files.Path, data.Registration.StoreId, StorageRoot.TrustedOperators, D.Consumer, "synthetic-image-host-not-production-acceptance",
                new(maximum, 2048, 2048, 2048 * 2048), ImageDecoderTests.Options(),
                new(data.Scanner.Port, "1.5.1", 30000, TimeSpan.FromDays(2), maximum, TimeSpan.FromSeconds(3))));
        Assert.Equal(1, (await data.Rows("evidenceValidationPolicies").UpdateOneAsync(new BsonDocument("consumerId", D.Consumer),
            new BsonDocument("$set", new BsonDocument { { "pipelineId", data.Runtime.PipelineId }, { "revision", 2L } }))).ModifiedCount);
        // Explicit synthetic native-OCR approval: bounded preprocessing and both owner proofs
        // share this ten-second private-read budget, within the unchanged production schema cap.
        Assert.Equal(1, (await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument("storeId", data.Registration.StoreId),
            new BsonDocument("$set", new BsonDocument { { "maximumBytes", maximum }, { "leaseDurationMs", 10000L }, { "revision", 2L } }))).ModifiedCount);
    }
    /// <summary>
    /// The ten-second private-read lease above is the synthetic approval for the native OCR run only. Once OCR has completed the store goes back to the
    /// production five-second lease, so later private-original reads in the same fixture (disbursement evidence) are specified against the program rule.
    /// </summary>
    internal static async Task EndNativeReadBudgetAsync(D data) =>
        Assert.Equal(1, (await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument("storeId", data.Registration.StoreId),
            new BsonDocument("$set", new BsonDocument { { "leaseDurationMs", 5000L }, { "revision", 3L } }))).ModifiedCount);
    internal static SupplierDocumentOcrRuntime CreateRuntime(D data) => SupplierDocumentOcrRuntime.Create(data.Database, data.Unit, new(data.Database), data.Clock,
        new(data.Files.Path, data.Registration.StoreId, StorageRoot.TrustedOperators, Consumer, HostReference, ModelReference, LocalOcrTests.Options()));
    internal static Task ApproveAsync(D data, string pipelineId) => data.Rows("supplierDocumentProcessingPolicies").InsertOneAsync(new BsonDocument {
        { "_id", 1L }, { "schemaVersion", 1 }, { "consumerId", Consumer }, { "storeId", data.Registration.StoreId }, { "pipelineId", pipelineId },
        { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", data.Clock.UtcNow.AddMinutes(-1).UtcDateTime },
        { "expiresAt", data.Clock.UtcNow.AddHours(1).UtcDateTime }, { "hostAcceptanceRef", HostReference }, { "modelSelectionRef", ModelReference } });
}
