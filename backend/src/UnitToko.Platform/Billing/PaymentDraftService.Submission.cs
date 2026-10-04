using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentSubmissionBson;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentDraftService
{
    internal async Task<BsonDocument?> RecoverSubmissionAsync(ActorContext actor, long declarationId, string key, string? bodyHash, CancellationToken ct) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover payment submission", "payment-submission-recovery"));
        if (declarationId <= 0) throw PaymentDraftBson.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await HeadAsync(actor, authority, tx, declarationId, token);
            var row = await SubmissionForKeyAsync(tx, actor, key, token);
            if (row is not null && (row["declarationId"] != declarationId || bodyHash is not null && row["bodyHash"] != bodyHash))
                throw new PersistenceConflictException("idempotency_key_reused", "Submission key was used with a different request");
            await records.TouchAsync(tx, head, token); await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token);
            return row is null ? null : B.Result(row);
        }, ct);
    }
    private async Task<BsonDocument?> SubmissionForKeyAsync(IClientSessionHandle tx, ActorContext actor, string key, CancellationToken ct) {
        var receipt = await records.Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", B.Operation }, { "key", key } }).SingleOrDefaultAsync(ct);
        var rows = await records.Rows(B.Collection).Find(tx, new BsonDocument { { "submittedBy", actor.UserId }, { "requestKey", key } }).Limit(2).ToListAsync(ct);
        if (receipt is null && rows.Count == 0) return null;
        if (receipt is null || rows.Count != 1) throw PaymentDraftBson.Invalid();
        return await new PaymentSubmissionRecords(database).ReadAsync(tx, rows[0]["_id"].AsInt64, ct);
    }
    // The declaration owner writes the association and state. Documents owns the pin and
    // must validate that association in this same transaction before it can protect bytes.
    internal async Task<BsonDocument> SubmitAsync(ActorContext actor, long declarationId, long revision, long reservationId, EvidenceRef reference,
        AuthorizationCommandMetadata metadata, Func<IClientSessionHandle, long, CancellationToken, Task<Action>> pin, CancellationToken ct) {
        await StorageAsync(ct);
        var submissionId = (await ids.NextAsync(B.Collection, ct)).Value; var pinId = (await ids.NextAsync("evidencePins", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.Body(declarationId, revision, reservationId, reference); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? before = null; Action? finalPinCheck = null; PersonAuthorityProof? personProof = null;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, token) => {
            finalPinCheck = null; personProof = null;
            before = await HeadAsync(actor, authority, tx, declarationId, token);
            var resources = PaymentDraftBson.Scopes(before["scopes"].AsBsonArray);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.PaymentEvidence, resources[0], clock.UtcNow),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.PaymentEvidence, new(resources[0]));
        }, async (tx, token) => {
            if (before is null || before["state"] != "draft" || before["revision"] != revision) throw PaymentDraftRecords.Changed();
            var version = await records.VersionAsync(tx, before["versionId"].AsInt64, token); var now = Now;
            if (version["recordedAt"].AsBsonDateTime.ToUniversalTime() > now.UtcDateTime) throw PaymentDraftBson.Invalid();
            personProof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actor.UserId], token);
            var person = personProof.Evidence.Single();
            if (person.Verification == PersonVerification.Verified) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, personProof, token);
            var actualOriginal = await new EvidenceSubmissionOriginalRecords(database).ReadAsync(tx,
                new(EvidenceTargetKind.PaymentDeclaration, declarationId), revision, reservationId, reference, actor.UserId, token);
            if (actualOriginal is not null) throw B.ImageRequired();
            // Unknown is explicit historical information, never authority for later approval.
            var row = new BsonDocument { { "_id", submissionId }, { "schemaVersion", 1 }, { "declarationId", declarationId },
                { "declarationVersionId", version["_id"] }, { "declarationRevision", revision }, { "declarationHash", version["snapshotHash"] },
                { "recordedBy", version["recordedBy"] }, { "submittedBy", actor.UserId }, { "uploaderId", actor.UserId }, { "reservationId", reservationId }, { "pinId", pinId },
                { "actorGrantVersion", actor.GrantVersion }, { "reviewRevision", 1L }, { "state", "submitted" }, { "evidence", B.Reference(reference) }, { "submittedAt", now.UtcDateTime },
                { "personAtSubmission", new BsonDocument { { "userId", person.UserId }, { "personId", person.PersonId is { } personId ? new BsonInt64(personId) : BsonNull.Value }, { "verification", person.Verification.ToString() }, { "version", person.Version } } },
                { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            row.Add("snapshotHash", B.Hash(row));
            await records.Rows(B.Collection).InsertOneAsync(tx, row, cancellationToken: token);
            await records.Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
                { "kind", "payment.ocr.requested" },
                { "sourceKey", B.OcrKey(row) }, { "payload", B.OcrPayload(row) },
                { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            finalPinCheck = await pin(tx, submissionId, token);
            var next = before.DeepClone().AsBsonDocument; next["schemaVersion"] = 2; next["state"] = "submitted"; next["submissionId"] = submissionId;
            next["reviewRevision"] = 1L; next["updatedAt"] = now.UtcDateTime; next["fence"] = checked(before["fence"].AsInt64 + 1);
            if ((await records.Rows(PaymentDraftBson.Heads).ReplaceOneAsync(tx, before, next, cancellationToken: token)).ModifiedCount != 1) throw PaymentDraftRecords.Changed();
            return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var submitted = await SubmissionForKeyAsync(tx, actor, metadata.IdempotencyKey, token) ?? throw PaymentDraftBson.Invalid();
            if (submitted["declarationId"] != declarationId || submitted["bodyHash"] != bodyHash) throw PaymentDraftBson.Invalid();
            var head = await HeadAsync(actor, authority, tx, declarationId, token);
            await records.TouchAsync(tx, head, token); await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, token);
            if (personProof?.Evidence.Single().Verification == PersonVerification.Verified) personProof.RequireCurrent(tx, clock.UtcNow);
            finalPinCheck?.Invoke(); token.ThrowIfCancellationRequested();
        });
    }
    async Task<PaymentEvidenceAssociation> IPaymentEvidenceUploadTarget.RequireSubmissionAssociationAsync(IClientSessionHandle tx, ActorContext actor,
        long submissionId, EvidenceReadClaim claim, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || claim.Target.Kind != EvidenceTargetKind.PaymentDeclaration) throw PaymentDraftBson.Invalid();
        var authority = await authorization.LoadAsync(actor, tx, ct);
        var row = await records.Rows(B.Collection).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", submissionId), PaymentSubmissionSchemas.Validator(B.Collection) })).SingleOrDefaultAsync(ct) ?? throw PaymentDraftBson.Invalid();
        var head = await records.Rows(PaymentDraftBson.Heads).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", claim.Target.Id), PaymentDraftSchemas.Validator(PaymentDraftBson.Heads) })).SingleOrDefaultAsync(ct) ?? throw PaymentDraftRecords.Changed();
        var version = await records.VersionAsync(tx, head["versionId"].AsInt64, ct);
        if (row["snapshotHash"] != B.Hash(row) || row["bodyHash"] != B.BodyHash(row) || row["declarationId"] != head["_id"] || row["declarationRevision"] != head["revision"] ||
            row["declarationVersionId"] != head["versionId"] || row["declarationHash"] != version["snapshotHash"] || row["recordedBy"] != head["recordedBy"] || head["recordedBy"] != actor.UserId ||
            row["submittedBy"] != actor.UserId || row["uploaderId"] != actor.UserId || row["actorGrantVersion"] != actor.GrantVersion || row["reservationId"] != claim.ReservationId ||
            row["declarationRevision"] != claim.TargetRevision || !InvoiceRecords.Equal(row["evidence"], B.Reference(claim.Reference))) throw PaymentDraftBson.Invalid();
        var receipt = await records.Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", B.Operation }, { "key", row["requestKey"] }, { "bodyHash", row["bodyHash"] } }).SingleOrDefaultAsync(ct);
        if (receipt is null || receipt["result"].AsBsonDocument.ElementCount != 0 || !await records.Rows("auditEvents").Find(tx, B.Audit(row)).AnyAsync(ct)) throw PaymentDraftBson.Invalid();
        Require(actor, authority, head["scopes"].AsBsonArray); await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, ct);
        return new(submissionId, row["pinId"].AsInt64, head["_id"].AsInt64, head["versionId"].AsInt64, head["revision"].AsInt64, row["reviewRevision"].AsInt64,
            row["snapshotHash"].AsString, row["reason"].AsString, row["correlationId"].AsString, new(row["submittedAt"].ToUniversalTime()),
            new(GrantScopeKind.FulfillmentShop, head["party"]["fulfillmentOrganizationId"].AsInt64, ShopId: head["party"]["shopId"].AsInt64, ChannelId: head["party"]["channelId"].AsInt64));
    }
}
