using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_image_submission_duplicate_create_restart_recovery_and_policy_expiry_preserve_one_supplement_and_primary_bank_identity()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data);
        await new CollectionMigrations(data.Database, data.Catalog.Clock).ApplyAsync();
        var primary = await Primary(data); await Policy(data, maximum: 2); var source = await Financial(data.Database);
        var service = Service(data); var command = Meta("same-key");
        async Task<(BsonDocument? Result, PersistenceConflictException? Error)> Attempt() {
            try { return (await service.CreateAsync(data.Actor, primary, command), null); }
            catch (PersistenceConflictException error) when (error.Code == "version_conflict") { return (null, error); }
        }
        var attempts = await Task.WhenAll(Attempt(), Attempt());
        Assert.Contains(attempts, attempt => attempt.Result is not null);
        var result = attempts.First(attempt => attempt.Result is not null).Result!;
        Assert.All(attempts, attempt => {
            if (attempt.Result is null) { Assert.NotNull(attempt.Error); Assert.Equal("version_conflict", attempt.Error.Code); }
            else { Assert.Null(attempt.Error); Assert.Equal(result, attempt.Result); }
        });
        Assert.Equal("draft", result["state"]); Assert.True(result["pdfEvidence"].IsBsonNull);
        Assert.Equal(1L, await data.Rows(PaymentSupplementBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(PaymentSupplementBson.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows(PaymentSupplementBson.Attachments).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).AnyAsync());
        var restarted = Service(data); var detail = await restarted.DetailAsync(data.Actor, primary.DeclarationId, primary.DeclarationRevision,
            result["supplementId"].AsInt64, new("payment-supplement-detail", "synthetic-refresh"), false);
        Assert.Equal(primary.PrimaryEvidence, detail.PrimaryEvidence); Assert.Null(detail.PdfEvidence);
        var recovered = await restarted.LookupAsync(data.Actor, primary.DeclarationId, primary.DeclarationRevision,
            PaymentSupplementBson.CreateOperation, command.IdempotencyKey);
        Assert.NotNull(recovered); Assert.Equal(result, recovered.Result);
        Assert.Equal(result, await restarted.CreateAsync(data.Actor, primary, command));
        var receiptFilter = new BsonDocument { { "actorId", data.Actor.UserId }, { "operation", PaymentSupplementBson.CreateOperation }, { "key", command.IdempotencyKey } };
        var auditFilter = new BsonDocument { { "actorId", data.Actor.UserId }, { "kind", PaymentSupplementBson.CreateOperation }, { "requestKey", command.IdempotencyKey } };
        Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(receiptFilter));
        Assert.Equal(1L, await data.Rows("auditEvents").CountDocumentsAsync(auditFilter));
        Assert.Equal(result, (await data.Rows("idempotencyRecords").Find(receiptFilter).SingleAsync())["result"]);
        Assert.Equal(result, (await data.Rows("auditEvents").Find(auditFilter).SingleAsync())["result"]);
        await data.Rows(PaymentSupplementBson.Policies).UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false).Inc("revision", 1L));
        // Recovery and immutable metadata do not invent today's PDF approval or open bytes.
        Assert.Equal(result, await restarted.CreateAsync(data.Actor, primary, command));
        Assert.Equal("policy_unavailable", (await restarted.ListAsync(data.Actor, primary.DeclarationId, primary.DeclarationRevision, null,
            new("payment-supplements-list", "synthetic-expired-policy"), false)).Readiness);
        Assert.Equal(source, await Financial(data.Database));
    }
    [WindowsStorageFact]
    public async Task No_implicit_D06_approval_unsubmitted_parent_foreign_requester_stale_revision_or_wrong_image_can_create_an_owner()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        await new CollectionMigrations(data.Database, data.Catalog.Clock).ApplyAsync();
        var service = Service(data);
        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(data.Actor, new(data.DeclarationId, 1, 1, data.Reference), Meta("not-submitted")));
        await PaymentOcrProcessorTests.Submit(data); var primary = await Primary(data);
        Assert.Equal("payment_supplement_policy_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => service.CreateAsync(data.Actor, primary, Meta("no-D06")))).Code);
        Assert.False(await data.Rows(PaymentSupplementBson.Policies).Find(new BsonDocument()).AnyAsync());
        await Policy(data);
        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(data.Payment.Invoice.Finance, primary, Meta("not-requester")));
        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(data.Actor, primary with { DeclarationRevision = 2 }, Meta("stale-parent")));
        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(data.Actor, primary with { PrimaryEvidence = new(data.Reference.Id, 1, new string('f', 64)) }, Meta("wrong-image")));
        Assert.Equal(0L, await data.Rows(PaymentSupplementBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", PaymentSupplementBson.CreateOperation)).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Staff_read_requires_live_scoped_grants_and_independent_people_while_requester_keeps_own_status_after_alias_discovery()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data);
        await new CollectionMigrations(data.Database, data.Catalog.Clock).ApplyAsync(); await Policy(data); var primary = await Primary(data);
        var service = Service(data); var created = await service.CreateAsync(data.Actor, primary, Meta("review-source"));
        var reviewer = await data.Catalog.ActorAsync(94910, CatalogAuthoringTestData.Shop, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
        var foreign = await data.Catalog.ActorAsync(94911, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DetailAsync(reviewer, primary.DeclarationId, primary.DeclarationRevision,
            created["supplementId"].AsInt64, new("payment-review-supplement-detail", "unknown-reviewer"), true));
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Catalog.Clock, reviewer.UserId, foreign.UserId);
        var read = await service.DetailAsync(reviewer, primary.DeclarationId, primary.DeclarationRevision, created["supplementId"].AsInt64,
            new("payment-review-supplement-detail", "independent-reviewer"), true); Assert.Equal(primary.PrimaryEvidence, read.PrimaryEvidence);
        await Assert.ThrowsAnyAsync<Exception>(() => service.DetailAsync(foreign, primary.DeclarationId, primary.DeclarationRevision, read.SupplementId,
            new("payment-review-supplement-detail", "foreign-unit"), true));
        await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync(async (tx, ct) => {
            await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(reviewer.UserId)),
                Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(data.Actor.UserId)).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(data.Actor.UserId)),
                Builders<BsonDocument>.Update.Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(data.Actor.UserId), PersonTestData.PersonId(reviewer.UserId) })
                    .Set("memberUserIds", new BsonArray { data.Actor.UserId, reviewer.UserId }).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(reviewer.UserId)),
                Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct); return true;
        });
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DetailAsync(reviewer,
            primary.DeclarationId, primary.DeclarationRevision, read.SupplementId, new("payment-review-supplement-detail", "alias-discovered"), true))).Code);
        Assert.Equal(read.SupplementId, (await service.DetailAsync(data.Actor, primary.DeclarationId, primary.DeclarationRevision, read.SupplementId,
            new("payment-supplement-detail", "requester-still-owns"), false)).SupplementId);
    }
    [WindowsStorageFact]
    public async Task Required_audit_failure_rolls_back_receipt_and_owner_and_concurrent_creates_obey_operator_maximum()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data);
        await new CollectionMigrations(data.Database, data.Catalog.Clock).ApplyAsync(); await Policy(data, maximum: 1);
        var primary = await Primary(data); var source = await Financial(data.Database); var service = Service(data);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].AsBsonDocument;
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" },
            { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", PaymentSupplementBson.CreateOperation)) }) } });
        try { await Assert.ThrowsAnyAsync<MongoException>(() => service.CreateAsync(data.Actor, primary, Meta("audit-denied"))); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", validator } }); }
        Assert.False(await data.Rows(PaymentSupplementBson.Heads).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", PaymentSupplementBson.CreateOperation }, { "key", Meta("audit-denied").IdempotencyKey } }).AnyAsync());
        async Task<(string Key, BsonDocument? Result, PersistenceConflictException? Error)> Attempt(string key) {
            try { return (key, await service.CreateAsync(data.Actor, primary, Meta(key)), null); }
            catch (PersistenceConflictException error) when (error.Code is "payment_supplement_limit" or "version_conflict") { return (key, null, error); }
        }
        var attempts = await Task.WhenAll(Attempt("maximum-a"), Attempt("maximum-b"));
        var winner = Assert.Single(attempts, attempt => attempt.Result is not null);
        var loser = Assert.Single(attempts, attempt => attempt.Error is not null);
        Assert.Null(winner.Error); Assert.Null(loser.Result); Assert.NotNull(loser.Error);
        Assert.Contains(loser.Error.Code, new[] { "payment_supplement_limit", "version_conflict" });
        var loserCommand = Meta(loser.Key);
        Assert.Equal("payment_supplement_limit", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            service.CreateAsync(data.Actor, primary, loserCommand))).Code);
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument {
            { "actorId", data.Actor.UserId }, { "operation", PaymentSupplementBson.CreateOperation }, { "key", loserCommand.IdempotencyKey } }).AnyAsync());
        Assert.False(await data.Rows(PaymentSupplementBson.Versions).Find(new BsonDocument {
            { "actorId", data.Actor.UserId }, { "operation", PaymentSupplementBson.CreateOperation }, { "requestKey", loserCommand.IdempotencyKey } }).AnyAsync());
        Assert.False(await data.Rows("auditEvents").Find(new BsonDocument {
            { "actorId", data.Actor.UserId }, { "kind", PaymentSupplementBson.CreateOperation }, { "requestKey", loserCommand.IdempotencyKey } }).AnyAsync());
        Assert.Equal(1L, await data.Rows(PaymentSupplementBson.Heads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows(PaymentSupplementBson.Versions).CountDocumentsAsync(new BsonDocument()));
        var winnerCommand = Meta(winner.Key);
        var winnerReceipt = await data.Rows("idempotencyRecords").Find(new BsonDocument {
            { "actorId", data.Actor.UserId }, { "operation", PaymentSupplementBson.CreateOperation } }).SingleAsync();
        var winnerAudit = await data.Rows("auditEvents").Find(new BsonDocument {
            { "actorId", data.Actor.UserId }, { "kind", PaymentSupplementBson.CreateOperation } }).SingleAsync();
        Assert.Equal(winnerCommand.IdempotencyKey, winnerReceipt["key"].AsString);
        Assert.Equal(winnerCommand.IdempotencyKey, winnerAudit["requestKey"].AsString);
        Assert.Equal(winner.Result, winnerReceipt["result"]); Assert.Equal(winner.Result, winnerAudit["result"]);
        Assert.Equal(0L, await data.Rows(PaymentSupplementBson.Attachments).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentSupplementBson.JobKind)).AnyAsync());
        Assert.Equal(source, await Financial(data.Database));
    }
    private static PaymentSupplementService Service(PaymentEvidenceReadTests.ReadData data) =>
        new(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, data.Payment.Service, new NoAttachedOriginal());
    private static async Task<PaymentSupplementPrimary> Primary(PaymentEvidenceReadTests.ReadData data) {
        var row = await data.Rows("paymentSubmissions").Find(new BsonDocument("declarationId", data.DeclarationId)).SingleAsync();
        return new(data.DeclarationId, row["declarationRevision"].AsInt64, row["_id"].AsInt64, data.Reference);
    }
    private static AuthorizationCommandMetadata Meta(string key) => new("ps-test." + key, "Lampiran tambahan untuk peninjauan mandiri", "synthetic-supplement");
    private static async Task Policy(PaymentEvidenceReadTests.ReadData data, int maximum = 2) {
        var head = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync(); var party = head["party"];
        var snapshot = PaymentSupplementContractTests.Policy(new() { { "organizationId", party["fulfillmentOrganizationId"] }, { "shopId", party["shopId"] }, { "channelId", party["channelId"] } }, data.Catalog.Clock.UtcNow.UtcDateTime);
        var id = (await new IdAllocator(data.Database).NextAsync(PaymentSupplementBson.Policies)).Value;
        snapshot.Remove("id"); snapshot.InsertAt(0, new BsonElement("_id", id)); snapshot.Add("schemaVersion", 1).Add("active", true).Add("fence", 0L)
            .Add("createdAt", data.Catalog.Clock.UtcNow.UtcDateTime).Add("updatedAt", data.Catalog.Clock.UtcNow.UtcDateTime); snapshot["maximumSupplements"] = maximum;
        await data.Rows(PaymentSupplementBson.Policies).InsertOneAsync(snapshot);
    }
    private static async Task<BsonDocument> Financial(IMongoDatabase db) {
        var result = new BsonDocument();
        foreach (var name in new[] { "paymentSubmissions", "payments", "paymentVerifications", "paymentAllocations", "bankReceipts", "journalEntries",
            "stockMovements", "inventoryReservations", "refunds", "documentExtractionResults" })
            result[name] = new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        result["primaryJobs"] = new BsonArray(await db.GetCollection<BsonDocument>("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).ToListAsync());
        return result;
    }
    // A draft-only test guard: creating/status/recovery must never access, invent
    // or claim a PDF original. Attachment-native fixtures need the real adapter.
    private sealed class NoAttachedOriginal : IPaymentSupplementEvidenceHistory {
        public Task<ExpectedPublication> RequireOriginalAsync(IClientSessionHandle tx, long id, long reservation, EvidenceRef reference, long uploader, CancellationToken ct) => throw new InvalidOperationException("No supplementary original was admitted in this test");
        public Task RequireHistoricalPinAsync(IClientSessionHandle tx, BsonDocument attachment, CancellationToken ct) => throw new InvalidOperationException("No supplementary pin was written in this test");
        public Task RequireCompletedDocumentResultAsync(IClientSessionHandle tx, BsonDocument job, BsonDocument attachment, CancellationToken ct) => throw new InvalidOperationException("No supplementary PDF OCR was performed in this test");
    }
}
