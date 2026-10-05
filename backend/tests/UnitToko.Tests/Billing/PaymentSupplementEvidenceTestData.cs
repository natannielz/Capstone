using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Billing;

// Real image validation/submission, private store and Mongo. The D06 row below is
// explicitly synthetic test input, never seeded by product/migration code.
[SupportedOSPlatform("windows")]
internal sealed class PaymentSupplementEvidenceTestData : IAsyncDisposable
{
    internal PaymentEvidenceReadTests.ReadData Data { get; private set; } = null!;
    internal PaymentSupplementEvidenceService Facade { get; private set; } = null!;
    internal PaymentSupplementPrimary Primary { get; private set; } = null!;
    internal PaymentSupplementView Supplement { get; private set; } = null!;
    internal static async Task<PaymentSupplementEvidenceTestData> CreateAsync(MongoFixture fixture, bool approve = true,
        long maximumContentBytes = 1024, byte[]? primaryImageBytes = null, EvidenceLimits? primaryImageLimits = null,
        long maximumReadBytes = 1024, long verificationDurationMs = 10000, long leaseDurationMs = 5000,
        long maximumValidationAgeMs = 86400000)
    {
        var result = new PaymentSupplementEvidenceTestData();
        try {
            result.Data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture, maximumContentBytes: maximumContentBytes,
                primaryImageBytes: primaryImageBytes, primaryImageLimits: primaryImageLimits, maximumReadBytes: maximumReadBytes,
                verificationDurationMs: verificationDurationMs, leaseDurationMs: leaseDurationMs, maximumValidationAgeMs: maximumValidationAgeMs);
            if (primaryImageBytes is null) await PaymentOcrProcessorTests.Submit(result.Data);
            else {
                await result.Data.ValidateAsync();
                var proof = await result.Data.Rows("evidenceValidationResults")
                    .Find(new BsonDocument("_id", result.Data.Reservation.VersionRowId)).SingleAsync();
                // Queue completion may also mean a durable rejection. Require the genuine
                // complete, successful security result before submitting the supplied image.
                Assert.True(proof["status"] == "passed" && proof["decoderStatus"] == "decoded" && proof["safeFailure"].IsBsonNull,
                    "Actual primary security result: status=" + proof["status"].AsString + "; decoder=" + proof["decoderStatus"].AsString +
                    "; failure=" + (proof["safeFailure"].IsBsonNull ? "none" : proof["safeFailure"].AsString));
                Assert.Equal(primaryImageBytes.LongLength, proof["length"].AsInt64);
                Assert.Equal(1000, proof["width"].AsInt32); Assert.Equal(650, proof["height"].AsInt32);
                Assert.Equal(EvidenceValidationRecords.Hash(proof), proof["resultHash"]);
                await result.Data.Service.SubmitAsync(result.Data.Actor, result.Data.DeclarationId, 1,
                    result.Data.Reservation.ReservationId, result.Data.Reference, EvidenceAdmissionTestData.Meta("ocr-submission"));
            }
            await PaymentSupplementMigrationTests.ApplyAsync(result.Data.Database, result.Data.Catalog.Clock);
            await PaymentSupplementCorrectionTestData.ApplyAsync(result.Data.Database, result.Data.Catalog.Clock);
            await new CollectionMigrations(result.Data.Database, result.Data.Catalog.Clock).ApplyAsync();
            var submission = await result.Data.Rows("paymentSubmissions").Find(new BsonDocument("declarationId", result.Data.DeclarationId)).SingleAsync();
            result.Primary = new(result.Data.DeclarationId, submission["declarationRevision"].AsInt64, submission["_id"].AsInt64, result.Data.Reference);
            result.Facade = result.Restart();
            if (approve) {
                await result.ApproveAsync();
                var created = await result.Facade.Domain.CreateAsync(result.Data.Actor, result.Primary, Meta("create-fixture"));
                result.Supplement = await result.Facade.Domain.DetailAsync(result.Data.Actor, result.Primary.DeclarationId, result.Primary.DeclarationRevision,
                    created["supplementId"].AsInt64, new("payment-supplement-detail", "synthetic-fixture"), false);
            }
            return result;
        } catch { await result.DisposeAsync(); throw; }
    }
    internal PaymentSupplementEvidenceService Restart() => new(Data.Database, new(Data.Catalog.Client), new(Data.Database), Data.Catalog.Clock,
        Data.Payment.Service, Data.Files.Registration);
    internal async Task ApproveAsync()
    {
        var head = await Data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", Data.DeclarationId)).SingleAsync(); var party = head["party"];
        var snapshot = PaymentSupplementContractTests.Policy(new() { { "organizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] },
            { "channelId", party["channelId"] } }, Data.Catalog.Clock.UtcNow.UtcDateTime);
        var id = (await new IdAllocator(Data.Database).NextAsync(PaymentSupplementBson.Policies)).Value;
        snapshot.Remove("id"); snapshot.InsertAt(0, new BsonElement("_id", id)); snapshot.Add("schemaVersion", 1).Add("active", true).Add("fence", 0L)
            .Add("createdAt", Data.Catalog.Clock.UtcNow.UtcDateTime).Add("updatedAt", Data.Catalog.Clock.UtcNow.UtcDateTime);
        await Data.Rows(PaymentSupplementBson.Policies).InsertOneAsync(snapshot);
    }
    internal static AuthorizationCommandMetadata Meta(string key) => new("ps-evidence." + key, "Lampiran PDF tambahan untuk peninjauan", "synthetic-supplement-evidence");
    internal async Task<BsonDocument> FinancialAsync()
    {
        var snapshot = new BsonDocument();
        foreach (var table in new[] { "paymentSubmissions", "payments", "paymentVerifications", "paymentAllocations", "bankReceipts", "journalEntries",
            "stockMovements", "inventoryReservations", "refunds", "documentExtractionResults" })
            snapshot[table] = new BsonArray(await Data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        snapshot["primaryJobs"] = new BsonArray(await Data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).ToListAsync());
        return snapshot;
    }
    public async ValueTask DisposeAsync() { if (Data is not null) await Data.DisposeAsync(); }
}
