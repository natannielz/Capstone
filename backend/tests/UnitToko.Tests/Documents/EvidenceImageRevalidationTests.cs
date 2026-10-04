using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceImageRevalidationTests(MongoFixture fixture)
{
    private const string Consumer = "synthetic-catalog-image-rescanner";
    private static readonly string Pipeline = new('d', 64);

    [WindowsStorageFact]
    public async Task New_scan_preserves_first_result_and_recovers_only_own_current_request()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        await data.Catalog.AssortmentsAsync();
        await data.Catalog.Service.SetPublicationsAsync(data.Actor,
            new(new(data.Target.Id), sku["revision"].AsInt64, [new(501)], [new(new(501), 1)]),
            CatalogAuthoringTestData.Metadata("revalidation-publication"));
        sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var reservation = await EvidenceValidationTests.Upload(data);
        var first = EvidenceValidationTests.Processor(data,
            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        await EvidenceValidationTests.Policy(data, first.PipelineId);
        Assert.Equal("completed", await first.RunOnceAsync());
        await ReadPolicyAsync(data);
        var workflow = new CatalogPhotoWorkflow(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            data.Catalog.Service, data.Registration);
        var reference = Assert.IsType<EvidenceRef>((await data.StatusAsync(reservation.ReservationId)).Reference);
        _ = await workflow.AssociateAsync(data.Actor,
            new CatalogAssociatePrimaryPhoto(new(data.Target.Id), sku["revision"].AsInt64, 0,
                reservation.ReservationId, reference), CatalogAuthoringTestData.Metadata("revalidation-photo"));
        // The original scan is now too old for a new private read. Revalidation
        // must use its immutable source, not change its checkedAt or the photo row.
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(45);
        await RevalidationPolicyAsync(data);
        var original = await data.RowAsync("evidenceValidationResults", reservation.VersionRowId);
        var service = Service(data);
        var input = new RequestCatalogImageRevalidation(new(data.Target.Id), sku["revision"].AsInt64, 0,
            reservation.ReservationId, reference);
        var metadata = EvidenceAdmissionTestData.Meta("catalog-image-rescan-1");
        var requested = await service.RequestAsync(data.Actor, input, metadata);
        Assert.Equal("pending", requested.State);
        Assert.Equal(requested, await service.RequestAsync(data.Actor, input, metadata));
        Assert.Equal(requested, await service.LookupAsync(data.Actor, metadata.IdempotencyKey));
        Assert.Equal("pending", (await service.CurrentAsync(data.Actor, data.Target.Id,
            reservation.ReservationId, reference)).State);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => workflow.OpenBuyerAsync(
            data.Catalog.Buyer, sku["publicId"].AsString,
            new AuthorizationReadMetadata("catalog-photo-current", "pending-rescan-refused")));
        var scan = new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        var processor = Processor(data, scan);
        Assert.Equal("completed", await processor.RunOnceAsync());
        Assert.Equal(1, scan.Calls);
        Assert.Equal("passed", (await service.CurrentAsync(data.Actor, data.Target.Id,
            reservation.ReservationId, reference)).State);
        Assert.Equal("passed", (await service.LookupAsync(data.Actor, metadata.IdempotencyKey))?.State);
        await using (var buyer = Assert.IsType<ProtectedEvidenceRead>(await workflow.OpenBuyerAsync(
            data.Catalog.Buyer, sku["publicId"].AsString,
            new AuthorizationReadMetadata("catalog-photo-current", "revalidated-buyer"))))
        {
            using var received = new MemoryStream();
            await buyer.Content.CopyToAsync(received);
            Assert.Equal(ImageDecoderTests.Png(13, 7), received.ToArray());
        }
        Assert.Equal(original, await data.RowAsync("evidenceValidationResults", reservation.VersionRowId));
        Assert.Equal(1, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(EvidenceImageRevalidationSchemas.Audits).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        await data.Rows(EvidenceImageRevalidationSchemas.Heads).DeleteOneAsync(new BsonDocument("_id", reservation.VersionRowId));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.CurrentAsync(data.Actor, data.Target.Id,
            reservation.ReservationId, reference));
    }

    [WindowsStorageFact]
    public async Task Changed_catalog_source_prevents_a_result_without_restoring_an_older_clean_head()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var reservation = await EvidenceValidationTests.Upload(data);
        var first = EvidenceValidationTests.Processor(data,
            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        await EvidenceValidationTests.Policy(data, first.PipelineId);
        Assert.Equal("completed", await first.RunOnceAsync());
        await ReadPolicyAsync(data);
        await RevalidationPolicyAsync(data);
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var reference = Assert.IsType<EvidenceRef>((await data.StatusAsync(reservation.ReservationId)).Reference);
        var service = Service(data);
        var input = new RequestCatalogImageRevalidation(new(data.Target.Id), sku["revision"].AsInt64, 0,
            reservation.ReservationId, reference);
        _ = await service.RequestAsync(data.Actor, input, EvidenceAdmissionTestData.Meta("image-rescan-source-race"));
        await data.Rows("skus").UpdateOneAsync(new BsonDocument("_id", data.Target.Id),
            Builders<BsonDocument>.Update.Inc("revision", 1L));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Processor(data,
            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1))).RunOnceAsync());
        Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        var head = await data.RowAsync(EvidenceImageRevalidationSchemas.Heads, reservation.VersionRowId);
        Assert.Equal("pending", head["status"]);
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Infected_latest_scan_revokes_the_older_clean_result()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var reservation = await EvidenceValidationTests.Upload(data);
        var first = EvidenceValidationTests.Processor(data,
            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        await EvidenceValidationTests.Policy(data, first.PipelineId);
        Assert.Equal("completed", await first.RunOnceAsync());
        await ReadPolicyAsync(data);
        await RevalidationPolicyAsync(data);
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var reference = Assert.IsType<EvidenceRef>((await data.StatusAsync(reservation.ReservationId)).Reference);
        var service = Service(data);
        _ = await service.RequestAsync(data.Actor, new RequestCatalogImageRevalidation(new(data.Target.Id),
            sku["revision"].AsInt64, 0, reservation.ReservationId, reference),
            EvidenceAdmissionTestData.Meta("infected-image-rescan"));
        var infected = Processor(data, new EvidenceValidationTests.FixtureScanner("infected",
            signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        Assert.Equal("completed", await infected.RunOnceAsync());
        Assert.Equal("rejected", (await service.CurrentAsync(data.Actor, data.Target.Id,
            reservation.ReservationId, reference)).State);
        var head = await data.RowAsync(EvidenceImageRevalidationSchemas.Heads, reservation.VersionRowId);
        Assert.Equal("rejected", head["status"]);
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Own_current_policy_is_required_and_a_foreign_approved_consumer_cannot_claim_the_job()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var pending = await PendingAsync(data, "image-rescan-policy-ownership");
        var jobBefore = await data.Rows(EvidenceImageRevalidationSchemas.Jobs).Find(new BsonDocument()).SingleAsync();
        var policy = await data.Rows(EvidenceImageRevalidationSchemas.Policies).Find(new BsonDocument()).SingleAsync();
        var foreignConsumer = Consumer + "-foreign";
        var foreign = policy.DeepClone().AsBsonDocument;
        foreign["_id"] = (await new IdAllocator(data.DB).NextAsync(EvidenceImageRevalidationSchemas.Policies)).Value;
        foreign["consumerId"] = foreignConsumer;
        foreign["fence"] = 0L;
        await data.Rows(EvidenceImageRevalidationSchemas.Policies).InsertOneAsync(foreign);
        var foreignScan = new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        var foreignProcessor = new EvidenceImageRevalidationProcessor(data.DB, data.Unit, new(data.DB),
            data.Catalog.Clock, data.Registration, foreignConsumer, Pipeline, new(1024, 100, 100, 10000),
            foreignScan, new WindowsImageDecoder(ImageDecoderTests.Options()));
        Assert.Equal("idle", await foreignProcessor.RunOnceAsync());
        Assert.Equal(0, foreignScan.Calls);
        Assert.Equal(jobBefore, await data.Rows(EvidenceImageRevalidationSchemas.Jobs).Find(new BsonDocument("_id", jobBefore["_id"])).SingleAsync());

        await data.Rows(EvidenceImageRevalidationSchemas.Policies).UpdateOneAsync(new BsonDocument("_id", policy["_id"]),
            Builders<BsonDocument>.Update.Set("active", false));
        var ownScan = new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        Assert.Equal("unavailable", await Processor(data, ownScan).RunOnceAsync());
        Assert.Equal(0, ownScan.Calls);
        Assert.Equal(jobBefore, await data.Rows(EvidenceImageRevalidationSchemas.Jobs).Find(new BsonDocument("_id", jobBefore["_id"])).SingleAsync());
        Assert.Equal("pending", (await pending.Service.CurrentAsync(data.Actor, data.Target.Id,
            pending.Reservation.ReservationId, pending.Reference)).State);
        Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Interrupted_private_scan_reclaims_only_after_lease_expiry_and_commits_once()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var pending = await PendingAsync(data, "image-rescan-interrupted");
        var original = await data.RowAsync("evidenceValidationResults", pending.Reservation.VersionRowId);
        var interrupt = new InterruptedScanner();
        await Assert.ThrowsAsync<ScannerInterruptedException>(() => Processor(data, interrupt).RunOnceAsync());
        Assert.Equal(1, interrupt.Calls);
        var leased = await data.Rows(EvidenceImageRevalidationSchemas.Jobs).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("leased", leased["status"]);
        Assert.Equal(1, leased["attempts"].AsInt32);
        Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(4);
        var successor = new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        Assert.Equal("idle", await Processor(data, successor).RunOnceAsync());
        Assert.Equal(0, successor.Calls);
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(2);
        Assert.Equal("completed", await Processor(data, successor).RunOnceAsync());
        Assert.Equal(1, successor.Calls);
        Assert.Equal("idle", await Processor(data, successor).RunOnceAsync());
        Assert.Equal(1, successor.Calls);
        var completed = await data.Rows(EvidenceImageRevalidationSchemas.Jobs).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("completed", completed["status"]);
        Assert.Equal(2, completed["attempts"].AsInt32);
        Assert.Equal(1, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(EvidenceImageRevalidationSchemas.Audits).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(original, await data.RowAsync("evidenceValidationResults", pending.Reservation.VersionRowId));
    }

    [WindowsStorageFact]
    public async Task Unavailable_scanner_exhausts_five_attempts_without_restoring_old_clean_content()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var pending = await PendingAsync(data, "image-rescan-unavailable-scanner");
        var scan = new EvidenceValidationTests.FixtureScanner("scanner_unavailable",
            signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        for (var attempt = 1; attempt <= 5; attempt++) {
            Assert.Equal(attempt == 5 ? "dead" : "retry", await Processor(data, scan).RunOnceAsync());
            var job = await data.Rows(EvidenceImageRevalidationSchemas.Jobs).Find(new BsonDocument()).SingleAsync();
            Assert.Equal(attempt, job["attempts"].AsInt32);
            Assert.Equal(attempt == 5 ? "dead" : "retry", job["status"].AsString);
            data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(1).AddSeconds(1);
        }
        Assert.Equal(5, scan.Calls);
        Assert.Equal("idle", await Processor(data, scan).RunOnceAsync());
        Assert.Equal(5, scan.Calls);
        Assert.Equal("pending", (await pending.Service.CurrentAsync(data.Actor, data.Target.Id,
            pending.Reservation.ReservationId, pending.Reference)).State);
        Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Audits).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Scan_that_crosses_captured_deadline_cannot_commit_and_original_request_recovers()
    {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var pending = await PendingAsync(data, "image-rescan-deadline");
        var original = await data.RowAsync("evidenceValidationResults", pending.Reservation.VersionRowId);
        var late = new EvidenceValidationTests.FixtureScanner(after: () => {
            data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(6);
            return Task.CompletedTask;
        }, signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        try { Assert.Equal("claim_lost", await Processor(data, late).RunOnceAsync()); }
        catch (PersistenceConflictException error) { Assert.Equal("image_revalidation_claim_lost", error.Code); }
        Assert.Equal(1, late.Calls);
        Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows(EvidenceImageRevalidationSchemas.Audits).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("pending", (await pending.Service.LookupAsync(data.Actor, pending.Metadata.IdempotencyKey))?.State);
        Assert.Equal(original, await data.RowAsync("evidenceValidationResults", pending.Reservation.VersionRowId));
        var timely = new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1));
        Assert.Equal("completed", await Processor(data, timely).RunOnceAsync());
        Assert.Equal(1, timely.Calls);
        Assert.Equal("passed", (await pending.Service.LookupAsync(data.Actor, pending.Metadata.IdempotencyKey))?.State);
        Assert.Equal(1, await data.Rows(EvidenceImageRevalidationSchemas.Results).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(original, await data.RowAsync("evidenceValidationResults", pending.Reservation.VersionRowId));
    }

    private static async Task<(ReservationResult Reservation, EvidenceRef Reference,
        EvidenceImageRevalidationService Service, AuthorizationCommandMetadata Metadata)> PendingAsync(
        EvidenceAdmissionTestData data, string key)
    {
        var reservation = await EvidenceValidationTests.Upload(data);
        var first = EvidenceValidationTests.Processor(data,
            new EvidenceValidationTests.FixtureScanner(signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)));
        await EvidenceValidationTests.Policy(data, first.PipelineId);
        Assert.Equal("completed", await first.RunOnceAsync());
        await ReadPolicyAsync(data);
        await RevalidationPolicyAsync(data);
        var sku = await data.Catalog.RowAsync("skus", data.Target.Id);
        var reference = Assert.IsType<EvidenceRef>((await data.StatusAsync(reservation.ReservationId)).Reference);
        var service = Service(data);
        var metadata = EvidenceAdmissionTestData.Meta(key);
        var input = new RequestCatalogImageRevalidation(new(data.Target.Id), sku["revision"].AsInt64, 0,
            reservation.ReservationId, reference);
        Assert.Equal("pending", (await service.RequestAsync(data.Actor, input, metadata)).State);
        return (reservation, reference, service, metadata);
    }

    private sealed class InterruptedScanner : IContentScanner
    {
        internal int Calls { get; private set; }
        public async Task<ContentScanResult> ScanAsync(Stream original, CancellationToken ct)
        {
            Calls++;
            await original.CopyToAsync(Stream.Null, ct);
            throw new ScannerInterruptedException();
        }
    }
    private sealed class ScannerInterruptedException : Exception { }

    private static EvidenceImageRevalidationService Service(EvidenceAdmissionTestData data) =>
        new(data.DB, data.Unit, new(data.DB), data.Catalog.Clock, data.Registration,
            data.Catalog.Service, Consumer, Pipeline);
    private static EvidenceImageRevalidationProcessor Processor(EvidenceAdmissionTestData data,
        IContentScanner scanner) => new(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            data.Registration, Consumer, Pipeline, new(1024, 100, 100, 10000), scanner,
            new WindowsImageDecoder(ImageDecoderTests.Options()));

    private static async Task RevalidationPolicyAsync(EvidenceAdmissionTestData data)
    {
        var installed = await data.Rows("schemaMigrations").Find(new BsonDocument {
            { "_id", 81 }, { "state", "applied" } }).AnyAsync();
        Assert.True(installed, "The additive M81 schema must be installed before a test writes its synthetic policy");
        var now = data.Catalog.Clock.UtcNow;
        await data.Rows(EvidenceImageRevalidationSchemas.Policies).InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(data.DB).NextAsync(EvidenceImageRevalidationSchemas.Policies)).Value },
            { "schemaVersion", 1 }, { "consumerId", Consumer }, { "storeId", data.StoreId },
            { "pipelineId", Pipeline }, { "active", true }, { "revision", 1L }, { "fence", 0L },
            { "approvedAt", now.AddMinutes(-1).UtcDateTime }, { "expiresAt", now.AddDays(7).UtcDateTime },
            { "hostAcceptanceRef", "synthetic-test-only-not-production-acceptance" } });
    }
    private static async Task ReadPolicyAsync(EvidenceAdmissionTestData data)
    {
        var now = data.Catalog.Clock.UtcNow;
        await data.Rows("evidenceReadPolicies").InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(data.DB).NextAsync("evidenceReadPolicies")).Value },
            { "schemaVersion", 1 }, { "storeId", data.StoreId }, { "active", true }, { "revision", 1L },
            { "fence", 0L }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime },
            { "maximumBytes", 1024L }, { "verificationDurationMs", 10000L }, { "leaseDurationMs", 5000L },
            { "requestDurationMs", 60000L }, { "maximumValidationAgeMs", 30000L },
            { "maximumSignatureAgeMs", 604800000L } });
    }
}
