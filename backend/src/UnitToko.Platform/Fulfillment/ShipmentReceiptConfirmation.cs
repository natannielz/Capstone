using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentReceiptService
{
    private void RequireOpen(BsonDocument challenge) {
        if (!challenge["consumedReceiptId"].IsBsonNull || challenge["failedAttempts"].AsInt32 >= DeliveryReceiptPolicy.MaximumFailedAttempts ||
            clock.UtcNow < ShipmentReceiptRecords.Time(challenge["recordedAt"]) || clock.UtcNow >= ShipmentReceiptRecords.Time(challenge["expiresAt"])) throw ShipmentReceiptRecords.Invalid();
    }
    // Failed-token increments must commit. Throwing inside the transaction would roll them back.
    // Only the authenticated intended recipient can reach this increment; another user cannot exhaust a QR.
    private async Task<ShipmentReceiptReview> ProbeAsync(ActorContext actor, long challengeId, string secret, bool allowAccepted, CancellationToken ct) {
        if (challengeId <= 0 || secret is null || secret.Length > 128) throw ShipmentReceiptRecords.Invalid();
        await StorageAsync(ct);
        var outcome = await authorization.ReadAsync<ShipmentReceiptReview?>(actor, async (snapshot, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.ReceiptAccept, clock.UtcNow));
            var challenge = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("_id", challengeId) & F.Eq("recipientId", actor.UserId)).SingleOrDefaultAsync(token) ?? throw ShipmentReceiptRecords.Invalid();
            var source = await shipments.DocumentSourceAsync(actor, snapshot, tx, challenge["shipmentId"].AsInt64, Permissions.ReceiptAccept, token);
            await VerifyChallengeAsync(tx, challenge, source, token);
            await DeliveryAttempts.RequireUsableChallengeAsync(tx, source, challengeId, token);
            var target = await recipients.ReadAsync(tx, snapshot, source.Resource, actor.UserId, token);
            var proof = await recipients.IndependenceAsync(tx, challenge["courierId"].AsInt64, actor.UserId, token);
            var receipt = await records.FindReceiptAsync(tx, challenge["shipmentId"].AsInt64, token);
            if (receipt is not null) {
                await VerifyReceiptAsync(tx, receipt, source, token);
                if (!allowAccepted || receipt["challengeId"] != challengeId || receipt["recipientId"] != actor.UserId || !TokenMatches(challenge, secret)) throw ShipmentReceiptRecords.Invalid();
            } else RequireOpen(challenge);
            if (!TokenMatches(challenge, secret)) {
                if ((await Rows(ShipmentReceiptRecords.Challenges).UpdateOneAsync(tx, F.Eq("_id", challengeId) & F.Eq("failedAttempts", challenge["failedAttempts"]) & F.Eq("consumedReceiptId", BsonNull.Value),
                    Builders<BsonDocument>.Update.Inc("failedAttempts", 1), cancellationToken: token)).ModifiedCount != 1) throw ShipmentReceiptRecords.Invalid();
                await FenceAsync(actor, snapshot, tx, source, Permissions.ReceiptAccept, target, proof, token); return null;
            }
            var result = new ShipmentReceiptReview(new(challengeId), new(challenge["shipmentId"].AsInt64), new(challenge["documentId"].AsInt64), challenge["documentVersion"].AsInt64,
                challenge["documentHash"].AsString, target.Name, ShipmentReceiptRecords.Time(challenge["expiresAt"]), Now, DeliveryReceiptPolicy.ConsentVersion, DeliveryReceiptPolicy.ConsentText,
                source.Snapshot["lines"].AsBsonArray.Select(line => new ShipmentReceiptReviewLine(new(line["skuId"].AsInt64), line["skuCode"].AsString, line["productName"].AsString,
                    new(line["definition"]["_id"].AsInt64), line["definition"]["packageLabel"].AsString, line["definition"]["baseUnitCode"].AsString, line["definition"]["baseUnitsPerPackage"].AsInt32, line["packages"].AsInt32,
                    UnitToko.Platform.Ordering.OrderLineBson.Revision(line).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray());
            await FenceAsync(actor, snapshot, tx, source, Permissions.ReceiptAccept, target, proof, token);
            if (receipt is null) RequireOpen(challenge); return result;
        }, ct);
        return outcome ?? throw ShipmentReceiptRecords.Invalid();
    }
    public Task<ShipmentReceiptReview> ReviewAsync(ActorContext actor, long challengeId, string token, CancellationToken ct = default) => ProbeAsync(actor, challengeId, token, false, ct);

    public async Task<ShipmentReceiptView> ConfirmAsync(ActorContext actor, long challengeId, string secret, string consentVersion, bool consent,
        ImmutableArray<ReceiptLineDeclaration> declarations, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!consent || consentVersion != DeliveryReceiptPolicy.ConsentVersion || declarations.IsDefaultOrEmpty || declarations.Any(line => line is null))
            throw new UnitToko.Modules.Contracts.DomainValidationException("receipt_consent_required", "Review the quantities and explicitly confirm receipt");
        await ProbeAsync(actor, challengeId, secret, true, ct);
        var id = (await ids.NextAsync(ShipmentReceiptRecords.Receipts, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = ConfirmBody(challengeId, Hash(secret), declarations);
        ShipmentDocumentSource source = null!; BsonDocument challenge = null!, document = null!, final = null!;
        ShipmentRecipientTarget target = null!; PersonAuthorityProof proof = null!; ImmutableArray<ReceiptConfirmedLine> lines = []; var wrote = false;
        await authorization.ExecuteCommandAsync(actor, ShipmentReceiptRecords.Confirm, metadata, body, async (snapshot, tx, token) => {
            // The unit of work can retry this delegate. Never carry a prior attempt's mutable state.
            wrote = false;
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.ReceiptAccept, clock.UtcNow));
            challenge = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("_id", challengeId) & F.Eq("recipientId", actor.UserId)).SingleOrDefaultAsync(token) ?? throw ShipmentReceiptRecords.Invalid();
            source = await shipments.DocumentSourceAsync(actor, snapshot, tx, challenge["shipmentId"].AsInt64, Permissions.ReceiptAccept, token);
            await VerifyChallengeAsync(tx, challenge, source, token);
            await DeliveryAttempts.RequireUsableChallengeAsync(tx, source, challengeId, token);
            if (!TokenMatches(challenge, secret)) throw ShipmentReceiptRecords.Invalid();
            target = await recipients.ReadAsync(tx, snapshot, source.Resource, actor.UserId, token);
            proof = await recipients.IndependenceAsync(tx, challenge["courierId"].AsInt64, actor.UserId, token);
            document = await documents.RequireReceiptDocumentAsync(tx, source, challenge["documentId"].AsInt64, token);
            lines = DeliveryReceiptPolicy.Require(ShipmentReceiptRecords.Dispatched(source.Snapshot), declarations);
            var existing = await records.FindReceiptAsync(tx, challenge["shipmentId"].AsInt64, token);
            if (existing is not null) await VerifyReceiptAsync(tx, existing, source, token);
            return Allow(actor, snapshot, source, Permissions.ReceiptAccept, clock.UtcNow);
        }, async (tx, token) => {
            if (await records.FindReceiptAsync(tx, challenge["shipmentId"].AsInt64, token) is not null) throw new PersistenceConflictException("shipment_already_received", "Use the original receipt request or read the existing receipt");
            RequireOpen(challenge); var now = Now;
            var row = Header(id, actor, source, document, target, metadata, body, audit, now);
            row.AddRange(new BsonDocument { { "challengeId", challengeId }, { "tokenHash", challenge["tokenHash"] },
                { "courierPersonId", proof.Evidence.Single(person => person.UserId == challenge["courierId"].AsInt64).PersonId!.Value },
                { "recipientPersonId", proof.Evidence.Single(person => person.UserId == actor.UserId).PersonId!.Value },
                { "consentVersion", DeliveryReceiptPolicy.ConsentVersion }, { "consentText", DeliveryReceiptPolicy.ConsentText },
                { "hasException", lines.Any(line => line.HasException) }, { "lines", ShipmentReceiptRecords.Lines(lines) } });
            if ((await Rows(ShipmentReceiptRecords.Challenges).UpdateOneAsync(tx, F.Eq("_id", challengeId) & F.Eq("consumedReceiptId", BsonNull.Value) &
                F.Eq("failedAttempts", challenge["failedAttempts"]) & F.Gt("expiresAt", now.UtcDateTime), Builders<BsonDocument>.Update.Set("consumedReceiptId", id).Set("consumedAt", now.UtcDateTime), cancellationToken: token)).ModifiedCount != 1)
                throw ShipmentReceiptRecords.Invalid();
            await Rows(ShipmentReceiptRecords.Receipts).InsertOneAsync(tx, row, cancellationToken: token);
            await records.EffectsAsync(tx, row, ShipmentReceiptRecords.Confirm, token);
            wrote = true;
            return ShipmentReceiptRecords.Result(row, ShipmentReceiptRecords.Confirm);
        }, ct, async (snapshot, tx, token) => {
            final = await records.FindReceiptAsync(tx, challenge["shipmentId"].AsInt64, token) ?? throw ShipmentReceiptRecords.Invalid();
            lines = await VerifyReceiptAsync(tx, final, source, token);
            if (final["actorId"] != actor.UserId || final["requestKey"] != metadata.IdempotencyKey || final["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw ShipmentReceiptRecords.Invalid();
            if (wrote) await new FulfillmentStockSourceProducer(database, ids, clock).RecordAsync(tx, "receipt", id, token);
            await FenceAsync(actor, snapshot, tx, source, Permissions.ReceiptAccept, target, proof, token);
            if (wrote && (clock.UtcNow >= ShipmentReceiptRecords.Time(challenge["expiresAt"]) || clock.UtcNow < ShipmentReceiptRecords.Time(final["recordedAt"]))) throw ShipmentReceiptRecords.Invalid();
        });
        return Receipt(final, lines);
    }
    public async Task<ShipmentReceiptView?> ReadAsync(ActorContext actor, long shipmentId, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var source = await shipments.DocumentSourceAsync(actor, snapshot, tx, shipmentId, Permissions.ShipmentRead, token);
            var row = await records.FindReceiptAsync(tx, shipmentId, token);
            var result = row is null ? null : Receipt(row, await VerifyReceiptAsync(tx, row, source, token));
            await shipments.FenceAsync(actor, snapshot, tx, [source.Resource], token); return result;
        }, ct);
    }
    public async Task<ShipmentReceiptView?> RecoverConfirmationAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original delivery receipt", "receipt-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.ReceiptAccept, clock.UtcNow));
            var row = await Rows(ShipmentReceiptRecords.Receipts).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).SingleOrDefaultAsync(token);
            if (row is null) {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", ShipmentReceiptRecords.Confirm) & F.Eq("key", key)).AnyAsync(token)) throw ShipmentReceiptRecords.Invalid();
                await shipments.FenceAsync(actor, snapshot, tx, [], token, Permissions.ReceiptAccept); return null;
            }
            var source = await shipments.DocumentSourceAsync(actor, snapshot, tx, row["shipmentId"].AsInt64, Permissions.ReceiptAccept, token);
            var result = Receipt(row, await VerifyReceiptAsync(tx, row, source, token));
            var target = await recipients.ReadAsync(tx, snapshot, source.Resource, actor.UserId, token);
            var proof = await recipients.IndependenceAsync(tx, row["courierId"].AsInt64, actor.UserId, token);
            await FenceAsync(actor, snapshot, tx, source, Permissions.ReceiptAccept, target, proof, token); return result;
        }, ct);
    }
    public async Task<ShipmentReceiptChallengeView?> RecoverChallengeAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original delivery QR", "receipt-qr-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.DeliveryEvidence, clock.UtcNow));
            var row = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", key)).SingleOrDefaultAsync(token);
            if (row is null) {
                if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", ShipmentReceiptRecords.Issue) & F.Eq("key", key)).AnyAsync(token)) throw ShipmentReceiptRecords.Invalid();
                await shipments.FenceAsync(actor, snapshot, tx, [], token, Permissions.DeliveryEvidence); return null;
            }
            var source = await shipments.DocumentSourceAsync(actor, snapshot, tx, row["shipmentId"].AsInt64, Permissions.DeliveryEvidence, token); Courier(actor, source);
            await VerifyChallengeAsync(tx, row, source, token);
            await DeliveryAttempts.HistoryAsync(tx, source, token);
            var target = await recipients.ReadAsync(tx, snapshot, source.Resource, row["recipientId"].AsInt64, token);
            var proof = await recipients.IndependenceAsync(tx, actor.UserId, target.UserId, token);
            await FenceAsync(actor, snapshot, tx, source, Permissions.DeliveryEvidence, target, proof, token); return Challenge(row);
        }, ct);
    }
}
