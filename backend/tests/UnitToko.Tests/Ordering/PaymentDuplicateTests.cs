using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentDuplicateTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Exact_image_and_reference_signals_preserve_unknowns_paging_and_do_not_decide_money() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        await PaymentOcrProcessorTests.Submit(data); var reviewer = await PaymentReviewTests.Reviewer(data);
        var both = await AdditionalAsync(data, "both", data.Payment.Input);
        var image = await AdditionalAsync(data, "image", data.Payment.Input with { BankReference = "different-reference" });
        var reference = await AdditionalAsync(data, "reference", data.Payment.Input, ImageDecoderTests.Png(14, 7));
        var unknown = await AdditionalAsync(data, "unknown", data.Payment.Input with { BankReference = null }, ImageDecoderTests.Png(15, 7));
        var service = data.Payment.Service;
        var page = await service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 1, data.Reference, null, PaymentReviewTests.Metadata);
        Assert.Equal(new[] { reference.Id, image.Id, both.Id }, page.Items.Select(item => item.DeclarationId)); Assert.False(page.HasMore);
        Assert.Equal(new[] { "same_image", "same_reference" }, page.Items.Single(item => item.DeclarationId == both.Id).Signals);
        Assert.Equal("same_image", Assert.Single(page.Items.Single(item => item.DeclarationId == image.Id).Signals));
        Assert.Equal("same_reference", Assert.Single(page.Items.Single(item => item.DeclarationId == reference.Id).Signals));
        Assert.Equal(both.Id, Assert.Single((await service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 1, data.Reference, image.Id, PaymentReviewTests.Metadata)).Items).DeclarationId);
        Assert.Empty((await service.ReadDuplicatesAsync(reviewer, unknown.Id, 1, unknown.Reference, null, PaymentReviewTests.Metadata)).Items);
        Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows(PaymentVerificationBson.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("submitted", (await service.ReadAsync(data.Actor, data.DeclarationId)).State);
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("operation", "billing.payment.review.duplicates")).FirstAsync();
        Assert.Equal(reviewer.UserId, audit["actorId"]); Assert.DoesNotContain(unknown.Id, audit["declarationIds"].AsBsonArray.Select(value => value.AsInt64));
    }
    [WindowsStorageFact]
    public async Task Scoped_hints_exclude_hidden_heads_before_paging_and_recheck_source_revision_and_audit() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        await PaymentOcrProcessorTests.Submit(data); var reviewer = await PaymentReviewTests.Reviewer(data);
        var other = await data.Catalog.ActorAsync(9981, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
        foreach (var actor in new[] { data.Actor, data.Payment.Invoice.Finance, other })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payment.Service.ReadDuplicatesAsync(actor, data.DeclarationId, 1, data.Reference, null, PaymentReviewTests.Metadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Payment.Service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 2, data.Reference, null, PaymentReviewTests.Metadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Payment.Service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 1, new EvidenceRef(data.Reference.Id, data.Reference.Version, new string('f', 64)), null, PaymentReviewTests.Metadata));
        var head = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync();
        // Deliberately invalid foreign history must never be loaded, counted, or expose its identifiers.
        for (var i = 0; i < 30; i++) {
            var hidden = head.DeepClone().AsBsonDocument; hidden["_id"] = 99000L + i;
            foreach (var scope in hidden["scopes"].AsBsonArray) scope["fulfillmentOrganizationId"] = 999999L;
            await data.Rows(PaymentDraftBson.Heads).InsertOneAsync(hidden);
        }
        var valid = await AdditionalAsync(data, "visible", data.Payment.Input);
        var page = await data.Payment.Service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 1, data.Reference, null, PaymentReviewTests.Metadata);
        Assert.Equal(valid.Id, Assert.Single(page.Items).DeclarationId); Assert.False(page.HasMore);
        var before = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("operation", new BsonDocument("$ne", "billing.payment.review.duplicates")) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Payment.Service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 1, data.Reference, null, PaymentReviewTests.Metadata));
        Assert.Equal(before, await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync());
        await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", reviewer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payment.Service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 1, data.Reference, null, PaymentReviewTests.Metadata));
    }
    [WindowsStorageFact]
    public async Task Session_expiring_after_the_final_read_rolls_back_duplicate_audit_and_fences() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await PaymentOcrProcessorTests.Submit(data);
        var reviewer = await PaymentReviewTests.Reviewer(data); var clock = data.Catalog.Clock; var start = clock.UtcNow;
        await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", reviewer.Session!.Id), Builders<BsonDocument>.Update.Set("idleExpiresAt", start.AddSeconds(1).UtcDateTime));
        var original = await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync();
        var audited = false; var advanced = false; var selected = 0; var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.TryGetValue("insert", out var name) && name == "auditEvents" && command.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("operation", "") == "billing.payment.review.duplicates")) audited = true;
                if (audited && command.Command.TryGetValue("find", out var read) && read == "identitySessions") selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => { if (!advanced && selected == command.RequestId) { advanced = true; clock.UtcNow = start.AddSeconds(2); } });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(db, new(client), new(db), clock);
        var preparation = new UnitToko.Platform.Fulfillment.ShipmentPreparationService(db, new(client), new(db), clock, orders);
        var shipments = new UnitToko.Platform.Fulfillment.ShipmentQueryService(db, new(client), clock, orders, preparation);
        var documents = new UnitToko.Platform.Fulfillment.ShipmentDocumentService(db, new(client), new(db), clock, shipments);
        var received = new UnitToko.Platform.Fulfillment.ShipmentReceiptService(db, new(client), new(db), clock, shipments, documents);
        var invoices = new InvoiceService(db, new(client), new(db), clock, received);
        var service = new PaymentDraftService(db, new(client), new(db), clock, invoices);
        var failure = await Record.ExceptionAsync(() => service.ReadDuplicatesAsync(reviewer, data.DeclarationId, 1, data.Reference, null, PaymentReviewTests.Metadata));
        Assert.True(advanced); Assert.Equal("stale_or_inactive_session", Assert.IsType<AuthorizationDeniedException>(failure).Code);
        Assert.False(await data.Rows("auditEvents").Find(new BsonDocument("operation", "billing.payment.review.duplicates")).AnyAsync());
        Assert.Equal(original, await data.Rows(PaymentDraftBson.Heads).Find(new BsonDocument("_id", data.DeclarationId)).SingleAsync());
    }
    internal static async Task<(long Id, EvidenceRef Reference)> AdditionalAsync(PaymentEvidenceReadTests.ReadData data, string key, PaymentDraftInput input, byte[]? bytes = null) {
        var id = (await data.Payment.Save(null, 0, input, key + "-draft"))["declarationId"].AsInt64;
        var reservation = await data.Service.ReserveAsync(data.Actor, id, 1, EvidenceAdmissionTestData.Meta(key + "-reserve"));
        using var stream = new MemoryStream(bytes ?? data.Bytes);
        var reference = (await data.Service.UploadAsync(data.Actor, id, reservation.ReservationId, 1, stream, EvidenceAdmissionTestData.Meta(key + "-upload"))).Evidence!.Reference;
        var validator = new EvidenceValidationProcessor(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, data.Files.Registration,
            EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
            new EvidenceValidationTests.FixtureScanner("clean", signaturesAt: data.Catalog.Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
        Assert.Equal("completed", await validator.RunOnceAsync());
        await data.Service.SubmitAsync(data.Actor, id, 1, reservation.ReservationId, reference, EvidenceAdmissionTestData.Meta(key + "-submit"));
        return (id, reference);
    }
}
