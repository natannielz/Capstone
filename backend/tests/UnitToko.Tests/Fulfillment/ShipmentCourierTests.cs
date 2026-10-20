using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Fulfillment;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentCourierTests(MongoFixture fixture)
{
    private static async Task<ShipmentCourierChange> MoveAsync(ShipmentCourierTestData data, long revision, long courier, string key) {
        var preview = await data.Service.PreviewAsync(data.Admin, data.ShipmentId, revision, courier);
        return await data.Service.HandoverAsync(data.Admin, data.ShipmentId, revision, courier, preview.Review, GenuineFiniteDispatchTestData.Meta(key));
    }
    [Fact]
    public async Task Actual_assignment_A_B_A_preserves_departure_history_and_old_QR_never_regains_authority() {
        await using var data = await ShipmentCourierTestData.CreateAsync(fixture);
        var visit = await data.Attempts.PreviewAsync(data.CourierA, data.ShipmentId);
        var attempted = await data.Attempts.RecordAsync(data.CourierA, data.ShipmentId, 0, "recipient_unavailable", visit.ServerTime,
            GenuineFiniteDispatchTestData.Meta("courier-original-visit"));
        var old = await data.Receipts.IssueChallengeAsync(data.CourierA, data.ShipmentId, data.DocumentId, data.Recipient.UserId,
            GenuineFiniteDispatchTestData.Meta("courier-before-handover"));
        var original = await data.Documents.DownloadAsync(data.CourierA, data.ShipmentId, data.DocumentId);
        var before = await data.BusinessAsync();
        var preview = await data.Service.PreviewAsync(data.Admin, data.ShipmentId, 1, data.CourierB.UserId);
        var first = await data.Service.HandoverAsync(data.Admin, data.ShipmentId, 1, data.CourierB.UserId, preview.Review,
            GenuineFiniteDispatchTestData.Meta("courier-to-b"));
        Assert.Equal(2, first.Revision);
        Assert.Equal(before, await data.BusinessAsync());
        Assert.Empty((await data.Queries.ListAsync(data.CourierA)).Items);
        Assert.Equal(data.ShipmentId, Assert.Single((await data.Queries.ListAsync(data.CourierB)).Items).Id.Value);
        var current = await data.Queries.ReadAsync(data.CourierB, data.ShipmentId);
        Assert.Equal(data.CourierA.UserId, current.CourierId.Value);
        Assert.Equal(data.CourierB.UserId, current.CurrentAssignment.CourierId.Value);
        Assert.Equal(original, await data.Documents.DownloadAsync(data.CourierB, data.ShipmentId, data.DocumentId));
        Assert.Equal(attempted, Assert.Single((await data.Attempts.ReadAsync(data.CourierB, data.ShipmentId)).Items));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Attempts.PreviewAsync(data.CourierA, data.ShipmentId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Receipts.IssueChallengeAsync(data.CourierA, data.ShipmentId, data.DocumentId,
            data.Recipient.UserId, GenuineFiniteDispatchTestData.Meta("courier-former-qr")));
        Assert.Equal("receipt_challenge_reassigned", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Receipts.ReviewAsync(data.Recipient, old.Id.Value, old.Token!))).Code);
        var second = await MoveAsync(data, 2, data.CourierA.UserId, "courier-back-a");
        Assert.Equal(3, second.Revision);
        Assert.Equal("receipt_challenge_reassigned", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Receipts.ReviewAsync(data.Recipient, old.Id.Value, old.Token!))).Code);
        Assert.Equal(first, await data.Service.RecoverAsync(data.Admin, "courier-to-b"));
        Assert.Equal(first, await data.Service.HandoverAsync(data.Admin, data.ShipmentId, 1, data.CourierB.UserId, preview.Review,
            GenuineFiniteDispatchTestData.Meta("courier-to-b")));
        Assert.Equal(attempted, await data.Attempts.RecoverAsync(data.CourierA, "courier-original-visit"));
        Assert.Equal(before, await data.BusinessAsync());
        var qr = await data.Receipts.IssueChallengeAsync(data.CourierA, data.ShipmentId, data.DocumentId, data.Recipient.UserId,
            GenuineFiniteDispatchTestData.Meta("courier-current-qr"));
        var review = await data.Receipts.ReviewAsync(data.Recipient, qr.Id.Value, qr.Token!);
        var lines = review.Lines.Select(line => new ReceiptLineDeclaration(line.SkuId, new(line.Packages, 0), new(0, 0), new(0, 0), null,
            long.Parse(line.LineRevision, CultureInfo.InvariantCulture))).ToImmutableArray();
        var receipt = await data.Receipts.ConfirmAsync(data.Recipient, qr.Id.Value, qr.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
            lines, GenuineFiniteDispatchTestData.Meta("courier-current-receipt"));
        Assert.False(receipt.HasException);
        var closed = await data.Service.ReadAsync(data.Admin, data.ShipmentId);
        Assert.True(closed.Received); Assert.False(closed.CanHandover); Assert.Equal(2, closed.History.Length);
        Assert.Equal("shipment_already_received", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.PreviewAsync(data.Admin, data.ShipmentId, 3, data.CourierB.UserId))).Code);
        Assert.Equal(first, await data.Service.RecoverAsync(data.Admin, "courier-to-b"));
        Assert.Equal(original, await data.Documents.DownloadAsync(data.CourierA, data.ShipmentId, data.DocumentId));
        Assert.Equal(2, await data.Rows(ShipmentCourierRecords.Events).CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Warmed_assignment_and_binding_proofs_refuse_deleted_effects_or_orphans_and_rollback_exact_source() {
        await using var data = await ShipmentCourierTestData.CreateAsync(fixture);
        var first = await MoveAsync(data, 1, data.CourierB.UserId, "courier-guard-b");
        var change = await data.Rows(ShipmentCourierRecords.Events).Find(new BsonDocument("_id", first.Id.Value)).SingleAsync();
        var binding = await data.Rows(ShipmentCourierRecords.Bindings).Find(new BsonDocument("_id", "challenge:" + data.Challenge.Id)).SingleAsync();
        var targets = new (string Name, BsonDocument Filter)[] {
            ("shipments", new("_id", data.ShipmentId)), (ShipmentCourierRecords.Anchors, new("_id", data.ShipmentId)),
            (ShipmentCourierRecords.Events, new("_id", first.Id.Value)), ("auditEvents", new("_id", change["auditId"])),
            ("idempotencyRecords", new() { { "operation", ShipmentCourierRecords.Operation }, { "key", "courier-guard-b" } }),
            ("outboxEvents", new("sourceKey", ShipmentCourierRecords.Operation + ":" + first.Id.Value)),
            (ShipmentCourierRecords.Bindings, new("_id", binding["_id"])), ("auditEvents", new("_id", binding["auditId"]))
        };
        foreach (var (name, filter) in targets) {
            var original = await data.Rows(name).Find(filter).SingleAsync();
            var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Data.Base.Store.ReadAsync(data.Admin, async (authority, tx, ct) => {
                var source = await data.Queries.AssignmentSourceAsync(data.Admin, authority, tx, data.ShipmentId, ct);
                var challenge = await data.Rows(ShipmentReceiptRecords.Challenges).Find(tx, new BsonDocument("_id", data.Challenge.Id.Value)).SingleAsync(ct);
                await new ShipmentCourierRecords(data.Data.Database, data.Data.Clock).RequireBindingAsync(tx, source, "challenge", challenge, ct);
                await data.Rows(name).DeleteOneAsync(tx, filter, cancellationToken: ct);
                await data.Queries.CompleteAssignmentAsync(tx, source, ct); return true;
            }, default));
            Assert.Equal("shipment_assignment_unavailable", denied.Code);
            Assert.Equal(original, await data.Rows(name).Find(filter).SingleAsync());
        }
        var count = await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument());
        var orphan = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Data.Base.Store.ReadAsync(data.Admin, async (authority, tx, ct) => {
            var source = await data.Queries.AssignmentSourceAsync(data.Admin, authority, tx, data.ShipmentId, ct);
            await data.Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", 981099L }, { "operation", ShipmentCourierRecords.Operation },
                { "result", new BsonDocument("shipmentId", data.ShipmentId) } }, new InsertOneOptions { BypassDocumentValidation = true }, ct);
            await data.Queries.CompleteAssignmentAsync(tx, source, ct); return true;
        }, default));
        Assert.Equal("shipment_assignment_unavailable", orphan.Code);
        Assert.Equal(count, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument()));
        await data.Data.Base.Store.ReadAsync(data.Admin, async (authority, tx, ct) => {
            var source = await data.Queries.AssignmentSourceAsync(data.Admin, authority, tx, data.ShipmentId, ct);
            await data.Queries.CompleteAssignmentAsync(tx, source, ct); await data.Queries.CompleteAssignmentAsync(tx, source, ct);
            using var foreign = await data.Data.Database.Client.StartSessionAsync(cancellationToken: ct); foreign.StartTransaction();
            Assert.Equal("shipment_assignment_unavailable", Assert.Throws<AuthorizationDeniedException>(() => source.Assignment!.Require(foreign)).Code);
            await foreign.AbortTransactionAsync(ct);
            var at = data.Data.Clock.UtcNow;
            try { data.Data.Clock.UtcNow = at.AddMilliseconds(-1); Assert.Equal(at.AddMilliseconds(-1), data.Data.Clock.UtcNow);
                Assert.Equal("shipment_assignment_unavailable", Assert.Throws<AuthorizationDeniedException>(() => source.Assignment!.Require(tx)).Code); }
            finally { data.Data.Clock.UtcNow = at; }
            return true;
        }, default);
    }

    [Fact]
    public async Task Assignment_requires_current_admin_scope_MFA_and_reviewed_target_eligibility_without_grants_or_money_effects() {
        await using var data = await ShipmentCourierTestData.CreateAsync(fixture);
        var before = await data.BusinessAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.WrongScope, data.ShipmentId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Admin with { Assurance = FactorAssurance.SingleFactor }, data.ShipmentId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Queries.ReadAsync(data.Admin, data.ShipmentId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Admin, data.ShipmentId, 1, data.Recipient.UserId));
        var preview = await data.Service.PreviewAsync(data.Admin, data.ShipmentId, 1, data.CourierB.UserId);
        var profile = await data.Rows("accountProfiles").Find(new BsonDocument("_id", data.CourierB.UserId)).SingleAsync();
        await data.Rows("accountProfiles").UpdateOneAsync(new BsonDocument("_id", data.CourierB.UserId), Builders<BsonDocument>.Update.Inc("version", 1L));
        Assert.Equal("shipment_assignment_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.HandoverAsync(data.Admin,
            data.ShipmentId, 1, data.CourierB.UserId, preview.Review, GenuineFiniteDispatchTestData.Meta("courier-stale-target")))).Code);
        await data.Rows("accountProfiles").ReplaceOneAsync(new BsonDocument("_id", data.CourierB.UserId), profile);
        var grants = await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Admin.UserId)).ToListAsync();
        Assert.NotEmpty(grants);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Admin.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.HandoverAsync(data.Admin, data.ShipmentId, 1, data.CourierB.UserId,
            preview.Review, GenuineFiniteDispatchTestData.Meta("courier-revoked-admin")));
        foreach (var grant in grants) await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
        Assert.Equal(0, await data.Rows(ShipmentCourierRecords.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await data.BusinessAsync());
        var receiptReview = await data.Receipts.ReviewAsync(data.Recipient, data.Challenge.Id.Value, data.Challenge.Token!);
        var declarations = receiptReview.Lines.Select(line => new ReceiptLineDeclaration(line.SkuId, new(line.Packages, 0), new(0, 0), new(0, 0), null,
            long.Parse(line.LineRevision, CultureInfo.InvariantCulture))).ToImmutableArray();
        static async Task<Exception?> Outcome(Func<Task> operation) { try { await operation(); return null; } catch (Exception error) { return error; } }
        var move = Outcome(() => data.Service.HandoverAsync(data.Admin, data.ShipmentId, 1, data.CourierB.UserId, preview.Review,
            GenuineFiniteDispatchTestData.Meta("courier-race-handover")));
        var accept = Outcome(() => data.Receipts.ConfirmAsync(data.Recipient, data.Challenge.Id.Value, data.Challenge.Token!, DeliveryReceiptPolicy.ConsentVersion,
            true, declarations, GenuineFiniteDispatchTestData.Meta("courier-race-receipt")));
        await Task.WhenAll(move, accept);
        var moved = await move; var accepted = await accept;
        Assert.True((moved is null) != (accepted is null), "Exactly one task handover or receipt may commit for the original assignment");
        if (moved is null) {
            Assert.Equal("receipt_challenge_reassigned", Assert.IsType<AuthorizationDeniedException>(accepted).Code);
            Assert.Equal(1, await data.Rows(ShipmentCourierRecords.Events).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, await data.Rows(ShipmentReceiptRecords.Receipts).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(before, await data.BusinessAsync());
        } else {
            Assert.Equal("shipment_already_received", Assert.IsType<PersistenceConflictException>(moved).Code);
            Assert.Equal(0, await data.Rows(ShipmentCourierRecords.Events).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(1, await data.Rows(ShipmentReceiptRecords.Receipts).CountDocumentsAsync(new BsonDocument()));
        }
    }
}
