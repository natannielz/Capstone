using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentEvidenceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Exact_private_original_is_quarantined_and_recovery_never_creates_cash_or_duplicate_uploads() {
        await using var store = await EvidenceAdmissionTestData.CreateAsync(fixture);
        await using var data = await PaymentDraftData.CreateAsync(fixture);
        var service = await Service(data, store); var actor = data.Invoice.Order.Buyer;
        var saved = await data.Save(null, 0, data.Input, "payment-image-draft"); var id = saved["declarationId"].AsInt64;
        var reservation = await service.ReserveAsync(actor, id, 1, EvidenceAdmissionTestData.Meta("payment-image"));
        Assert.Equal(reservation, await service.RecoverReservationAsync(actor, id, "payment-image"));
        Assert.Equal(reservation, await service.ReserveAsync(actor, id, 1, EvidenceAdmissionTestData.Meta("payment-image")));
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+axtoAAAAASUVORK5CYII=");
        await using var source = new MemoryStream(png);
        var result = await service.UploadAsync(actor, id, reservation.ReservationId, reservation.Revision, source, EvidenceAdmissionTestData.Meta("payment-upload"));
        Assert.Equal("quarantined", result.State); Assert.NotNull(result.Evidence);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant(), result.Evidence.Reference.Sha256);
        Assert.Equal(result, await service.RecoverUploadAsync(actor, id, reservation.ReservationId, EvidenceAdmissionTestData.Meta("payment-upload")));
        var status = Assert.Single(await service.ListAsync(actor, id)); Assert.Equal("quarantined", status.State); Assert.Equal("unavailable", status.ScanStatus); Assert.Equal("unavailable", status.DecoderStatus);
        Assert.False(EvidencePolicy.CanSubmitPayment(status.State, status.ScanStatus, "image/png", true));
        var draft = await data.Service.ReadAsync(actor, id); Assert.Equal("draft", draft.State); Assert.Equal(1, draft.Revision);
        Assert.Equal(1, await data.Invoice.Count("documentVersions")); Assert.Equal(0, await data.Invoice.Count("payments")); Assert.Equal(0, await data.Invoice.Count("bankReceipts")); Assert.Equal(1, await data.Invoice.Count("journalEntries"));
        Assert.Equal(1, await data.Invoice.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "evidence.validation.requested")));
        var row = await data.Invoice.Rows("evidenceUploadReservations").Find(new BsonDocument("_id", reservation.ReservationId)).SingleAsync();
        foreach (var change in new Action<BsonDocument>[] { value => value["purpose"] = "CatalogImage", value => value["operation"] = "evidence.catalog-image.reserve", value => { value["admittedDefinitionId"] = 1L; value["admittedDefinitionVersion"] = 1L; } }) {
            var wrong = row.DeepClone().AsBsonDocument; change(wrong);
            await Assert.ThrowsAsync<MongoWriteException>(() => data.Invoice.Rows("evidenceUploadReservations").ReplaceOneAsync(new BsonDocument("_id", reservation.ReservationId), wrong));
        }
    }
    [WindowsStorageFact]
    public async Task Wrong_declaration_another_buyer_or_revoked_membership_cannot_consume_source_bytes() {
        await using var store = await EvidenceAdmissionTestData.CreateAsync(fixture); await using var data = await PaymentDraftData.CreateAsync(fixture);
        var service = await Service(data, store); var actor = data.Invoice.Order.Buyer;
        var one = await data.Save(null, 0, data.Input, "payment-owner-one"); var two = await data.Save(null, 0, data.Input, "payment-owner-two");
        var id = one["declarationId"].AsInt64; var reservation = await service.ReserveAsync(actor, id, 1, EvidenceAdmissionTestData.Meta("payment-owner"));
        using var source = new MustNotRead();
        await Assert.ThrowsAnyAsync<Exception>(() => service.UploadAsync(actor, two["declarationId"].AsInt64, reservation.ReservationId, 1, source, EvidenceAdmissionTestData.Meta()));
        var other = await data.Invoice.Order.Prices.Catalog.ActorAsync(9960, CatalogAuthoringTestData.Audience, UnitToko.Modules.Identity.Permissions.PaymentEvidence, UnitToko.Modules.Identity.Permissions.InvoiceRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(other, id));
        await data.Invoice.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.UploadAsync(actor, id, reservation.ReservationId, 1, source, EvidenceAdmissionTestData.Meta()));
        Assert.Equal(0, source.Reads); Assert.Equal("fresh", (await data.Invoice.Rows("evidenceUploadAttempts").Find(new BsonDocument("_id", reservation.AttemptId)).SingleAsync())["state"]);
    }
    [Fact]
    public async Task Target_final_membership_fence_aborts_joined_writes() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var actor = data.Invoice.Order.Buyer;
        var saved = await data.Save(null, 0, data.Input, "payment-target-fence"); var once = false;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new MongoUnitOfWork(fixture.Client).ExecuteAsync((tx, ct) => ((IPaymentEvidenceUploadTarget)data.Service).WithTargetAsync(tx, actor,
            new(EvidenceTargetKind.PaymentDeclaration, saved["declarationId"].AsInt64), 1, async (_, inner) => {
                await data.Invoice.Rows("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument("_id", "payment-target-fence"), cancellationToken: inner);
                if (!once) { once = true; await data.Invoice.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"), cancellationToken: inner); }
                return true;
            }, ct)));
        Assert.True(once); Assert.False(await data.Invoice.Rows("fixtureBusinessRecords").Find(new BsonDocument("_id", "payment-target-fence")).AnyAsync());
    }
    private static async Task<PaymentEvidenceService> Service(PaymentDraftData data, EvidenceAdmissionTestData store) {
        var policy = EvidenceAdmissionSchemaTests.Policy(); policy["_id"] = (await new IdAllocator(data.Invoice.Order.Database).NextAsync("evidenceStorePolicies")).Value;
        await data.Invoice.Rows("evidenceStorePolicies").InsertOneAsync(policy);
        return new(data.Invoice.Order.Database, new(data.Invoice.Order.Prices.Catalog.Client), new(data.Invoice.Order.Database), data.Invoice.Order.Prices.Catalog.Clock, data.Service, store.Registration);
    }
    private sealed class MustNotRead : MemoryStream {
        internal int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Reads++; throw new InvalidOperationException("Denied input was read"); }
    }
}
