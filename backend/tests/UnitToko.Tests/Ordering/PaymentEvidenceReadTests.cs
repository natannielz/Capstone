using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentEvidenceReadTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Checked_original_opens_under_a_bound_read_lease_and_preserves_upload_receipt_and_money() {
        await using var data = await ReadData.CreateAsync(fixture);
        await data.ValidateAsync();
        await using (var file = await data.OpenAsync()) {
            Assert.Equal("image/png", file.Mime); Assert.Equal(data.Bytes.Length, file.Length); Assert.Equal(data.Reference, file.Reference);
            using var bytes = new MemoryStream(); await file.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        }
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync(); Assert.Equal("released", lease["state"]);
        Assert.Equal(3, await data.Rows("evidenceReadAudits").CountDocumentsAsync(new BsonDocument()));
        var lifecycle = await data.Rows("evidenceLifecycles").Find(new BsonDocument()).SingleAsync(); Assert.Equal("validated", lifecycle["state"]);
        var original = await data.Rows("documentVersions").Find(new BsonDocument()).SingleAsync(); Assert.Equal("quarantined", original["state"]);
        Assert.Equal(original["lifecycleRevision"].AsInt64 + 1, lifecycle["revision"].AsInt64);
        Assert.Equal("quarantined", (await data.Service.RecoverUploadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, EvidenceAdmissionTestData.Meta())).State);
        Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Pending_or_rejected_security_check_cannot_issue_a_read_lease() {
        await using var data = await ReadData.CreateAsync(fixture);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        await data.ValidateAsync("infected"); await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Different_owner_wrong_reference_and_changed_draft_cannot_open_the_original() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => data.OpenAsync(data.Payment.Invoice.Finance));
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.OpenReadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, 1,
            new(data.Reference.Id, data.Reference.Version, new string('f', 64)), ReadData.Metadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.OpenReadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, 2, data.Reference, ReadData.Metadata));
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Membership_revoked_after_actual_file_verification_denies_finalization_and_releases_the_handle() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        data.Service.ReadObserver = new(async () => { entered.TrySetResult(); await resume.Task.WaitAsync(TimeSpan.FromSeconds(10)); });
        var pending = data.OpenAsync();
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), new BsonDocument("$set", new BsonDocument("status", "revoked")));
        } finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal("released", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
        Assert.False(await data.Rows("evidenceReadAudits").Find(new BsonDocument("kind", "activated")).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Read_lease_renewal_rechecks_current_authority_before_another_chunk() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync(); await using var file = await data.OpenAsync();
        var one = new byte[1]; Assert.Equal(1, await file.Content.ReadAsync(one));
        await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), new BsonDocument("$set", new BsonDocument("status", "revoked")));
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(3);
        await Assert.ThrowsAnyAsync<Exception>(() => file.Content.ReadAsync(one).AsTask());
    }
    [WindowsStorageFact]
    public async Task Policy_or_draft_change_after_verification_cannot_activate_access() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        foreach (var collection in new[] { "evidenceReadPolicies", "evidenceValidationPolicies" }) {
            data.Service.ReadObserver = new(async () => { await data.Rows(collection).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false))); });
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
            await data.Rows(collection).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", true)));
        }
        data.Service.ReadObserver = new(async () => { await data.Payment.Save(data.DeclarationId, 1, data.Payment.Input, "changed-during-file-verification"); });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.False(await data.Rows("evidenceReadAudits").Find(new BsonDocument("kind", "activated")).AnyAsync());
        Assert.Equal(3, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument("state", "released")));
    }
    [WindowsStorageFact]
    public async Task Hard_deadline_stops_remaining_bytes_and_closes_the_file() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync(); await using var file = await data.OpenAsync();
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(61);
        await Assert.ThrowsAsync<IOException>(() => file.Content.ReadAsync(new byte[1]).AsTask());
        Assert.False(file.Content.CanRead); Assert.Equal("released", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
    }
    [WindowsStorageFact]
    public async Task Missing_lifecycle_or_read_audit_denies_bytes_without_repairing_history() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var lifecycleAudit = await data.Rows("evidenceLifecycleAudits").Find(new BsonDocument()).SingleAsync();
        await data.Rows("evidenceLifecycleAudits").DeleteOneAsync(new BsonDocument("_id", lifecycleAudit["_id"]));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        await data.Rows("evidenceLifecycleAudits").InsertOneAsync(lifecycleAudit);
        BsonDocument? readAudit = null;
        data.Service.ReadObserver = new(async () => {
            readAudit = await data.Rows("evidenceReadAudits").Find(new BsonDocument()).SingleAsync();
            await data.Rows("evidenceReadAudits").DeleteOneAsync(new BsonDocument("_id", readAudit["_id"]));
        });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.Equal("provisional", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
        Assert.False(await data.Rows("evidenceReadAudits").Find(new BsonDocument()).AnyAsync());
        // Only the test restores its deliberately removed audit; production does not recreate it.
        await data.Rows("evidenceReadAudits").InsertOneAsync(readAudit!); data.Service.ReadObserver = null;
        await using var file = await data.OpenAsync(); Assert.Equal(1, await file.Content.ReadAsync(new byte[1]));
        var active = await data.Rows("evidenceReadLeases").Find(new BsonDocument("state", "active")).SingleAsync();
        await data.Rows("evidenceReadAudits").DeleteOneAsync(new BsonDocument { { "leaseId", active["_id"] }, { "kind", "acquired" } });
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(3);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => file.Content.ReadAsync(new byte[1]).AsTask()); Assert.False(file.Content.CanRead);
    }
    [WindowsStorageFact]
    public async Task Required_activation_audit_failure_rolls_back_activation_before_any_bytes() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var stronger = new BsonDocument("$and", new BsonArray { EvidenceAccessSchemas.Validator("evidenceReadAudits"), new BsonDocument("kind", new BsonDocument("$ne", "activated")) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "evidenceReadAudits" }, { "validator", stronger } });
        await Assert.ThrowsAnyAsync<MongoException>(() => data.OpenAsync());
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync(); Assert.Equal("released", lease["state"]); Assert.Equal(2L, lease["revision"]);
        Assert.Equal(new[] { "acquired", "released" }, (await data.Rows("evidenceReadAudits").Find(new BsonDocument()).Sort(new BsonDocument("revision", 1)).ToListAsync()).Select(row => row["kind"].AsString));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
    }

    internal sealed class ReadData : IAsyncDisposable {
        internal EvidenceAdmissionTestData Files = null!;
        internal PaymentDraftData Payment = null!;
        internal PaymentEvidenceService Service = null!;
        internal ReservationResult Reservation = null!;
        internal EvidenceRef Reference = null!;
        internal readonly byte[] Bytes = ImageDecoderTests.Png(13, 7);
        internal long DeclarationId;
        internal IMongoDatabase Database => Payment.Invoice.Order.Database;
        internal UnitToko.Tests.Catalog.CatalogAuthoringTestData Catalog => Payment.Invoice.Order.Prices.Catalog;
        internal ActorContext Actor => Payment.Invoice.Order.Buyer;
        internal static AuthorizationReadMetadata Metadata => new("payment-evidence-view", "synthetic-private-read");
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal Task<ProtectedEvidenceRead> OpenAsync(ActorContext? actor = null) => Service.OpenReadAsync(actor ?? Actor, DeclarationId, Reservation.ReservationId, 1, Reference, Metadata);
        internal static async Task<ReadData> CreateAsync(MongoFixture fixture) {
            var data = new ReadData();
            try {
                data.Files = await EvidenceAdmissionTestData.CreateAsync(fixture); data.Payment = await PaymentDraftData.CreateAsync(fixture);
                var storage = EvidenceAdmissionSchemaTests.Policy(); storage["_id"] = (await new IdAllocator(data.Database).NextAsync("evidenceStorePolicies")).Value;
                await data.Rows("evidenceStorePolicies").InsertOneAsync(storage);
                await PolicyAsync(data.Database, data.Files.StoreId, data.Catalog.Clock.UtcNow);
                data.Service = new(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, data.Payment.Service, data.Files.Registration);
                var saved = await data.Payment.Save(null, 0, data.Payment.Input, "private-read-draft"); data.DeclarationId = saved["declarationId"].AsInt64;
                data.Reservation = await data.Service.ReserveAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta());
                using var stream = new MemoryStream(data.Bytes); var uploaded = await data.Service.UploadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, 1, stream, EvidenceAdmissionTestData.Meta());
                data.Reference = uploaded.Evidence!.Reference; return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        internal async Task ValidateAsync(string status = "clean") {
            var processor = new EvidenceValidationProcessor(Database, new(Catalog.Client), new(Database), Catalog.Clock, Files.Registration,
                EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
                new EvidenceValidationTests.FixtureScanner(status, signaturesAt: Catalog.Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
            await EvidenceValidationTests.Policy(Database, Catalog.Clock, Files.StoreId, processor.PipelineId);
            Assert.Equal("completed", await processor.RunOnceAsync());
        }
        internal static async Task PolicyAsync(IMongoDatabase database, string storeId, DateTimeOffset now) =>
            await database.GetCollection<BsonDocument>("evidenceReadPolicies").InsertOneAsync(new BsonDocument {
                { "_id", (await new IdAllocator(database).NextAsync("evidenceReadPolicies")).Value }, { "schemaVersion", 1 }, { "storeId", storeId },
                { "active", true }, { "revision", 1L }, { "fence", 0L }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime },
                { "maximumBytes", 1024L }, { "verificationDurationMs", 10000L }, { "leaseDurationMs", 5000L }, { "requestDurationMs", 60000L },
                { "maximumValidationAgeMs", 86400000L }, { "maximumSignatureAgeMs", 604800000L } });
        public async ValueTask DisposeAsync() { if (Payment is not null) await Payment.DisposeAsync(); if (Files is not null) await Files.DisposeAsync(); }
    }
}
