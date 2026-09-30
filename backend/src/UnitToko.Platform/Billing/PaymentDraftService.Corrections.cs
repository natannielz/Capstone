using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentCorrectionBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentCorrectionVersion(long Id, long Revision, long ActorId, DateTimeOffset RecordedAt, string Reason,
    ImmutableArray<PaymentReviewField> BeforeFields, ImmutableArray<PaymentReviewField> Fields);
public sealed record PaymentCorrectionView(long DeclarationId, PaymentCorrectionBinding Binding, long ReviewRevision,
    ImmutableArray<PaymentReviewField> OriginalFields, ImmutableArray<PaymentReviewField> Fields, ImmutableArray<PaymentCorrectionVersion> Recent);

public sealed partial class PaymentDraftService
{
    internal async Task CorrectionStorageAsync(CancellationToken ct) {
        await StorageAsync(ct);
        foreach (var name in PaymentCorrectionSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, PaymentCorrectionSchemas.Validator(name), PaymentCorrectionSchemas.Key(name), ct);
        await PaymentVerificationStorage.RequireAsync(database, ct);
    }
    private AuthorizationQuery CorrectionQuery(ActorContext actor, AuthorizationSnapshot authority, BsonDocument head) {
        var query = ScopePolicy.Compile(actor, authority, Permissions.PaymentReviewCorrect, clock.UtcNow); AuthorizationStore.Require(query);
        if (PaymentDraftBson.Scopes(head["scopes"].AsBsonArray).Any(scope => !query.Matches(scope))) throw PaymentCorrectionRecords.Bad();
        return query;
    }
    private async Task CorrectionFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument head, CancellationToken ct) {
        var query = CorrectionQuery(actor, authority, head); var resources = PaymentDraftBson.Scopes(head["scopes"].AsBsonArray);
        var clauses = query.Clauses.Where(clause => resources.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        await ReviewFenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, ct); _ = CorrectionQuery(actor, authority, head);
    }
    private static ImmutableArray<PaymentReviewField> Original(PaymentExtractionView extraction, PaymentCorrectionBinding binding) {
        if (extraction.State != "succeeded" || extraction.Extraction is null || extraction.SubmissionId != binding.SubmissionId || extraction.ResultId != binding.ExtractionId || extraction.Evidence != binding.Evidence)
            throw PaymentDraftRecords.Changed();
        return PaymentReviewCorrectionPolicy.Copy([.. extraction.Extraction.Fields.Select(field => new PaymentReviewField(field.Field, field.Value))]);
    }
    internal async Task<PaymentCorrectionView> CorrectionViewAsync(IClientSessionHandle tx, PaymentExtractionView extraction, PaymentCorrectionBinding binding, CancellationToken ct) {
        var original = Original(extraction, binding); var store = new PaymentCorrectionRecords(database);
        var history = await store.HistoryAsync(tx, extraction.DeclarationId, binding, B.Fields(original), ct);
        await store.TouchAsync(tx, history.Head, ct);
        return new(extraction.DeclarationId, binding, history.Head?["revision"].AsInt64 ?? 0, original,
            history.Recent.Length == 0 ? original : B.Fields(history.Recent[0]["fields"].AsBsonArray),
            [.. history.Recent.Select(row => new PaymentCorrectionVersion(row["_id"].AsInt64, row["revision"].AsInt64, row["recordedBy"].AsInt64,
                new(row["recordedAt"].ToUniversalTime()), row["reason"].AsString, B.Fields(row["beforeFields"].AsBsonArray), B.Fields(row["fields"].AsBsonArray)))]);
    }
    private async Task<BsonDocument> CurrentCorrectionSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, PaymentCorrectionBinding binding, CancellationToken ct) {
        var head = await ReviewHeadAsync(actor, authority, tx, id, ct); _ = CorrectionQuery(actor, authority, head);
        var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        if (head["revision"] != binding.DeclarationRevision || head["submissionId"] != binding.SubmissionId || !InvoiceRecords.Equal(submission["evidence"], PaymentSubmissionBson.Reference(binding.Evidence))) throw PaymentDraftRecords.Changed();
        return head;
    }
    internal async Task<BsonDocument> CorrectAsync(ActorContext actor, long id, long revision, PaymentCorrectionBinding binding, ImmutableArray<PaymentReviewField> fields,
        AuthorizationCommandMetadata metadata,
        Func<IClientSessionHandle, Func<PaymentExtractionView, Action, CancellationToken, Task<BsonDocument>>, CancellationToken, Task<BsonDocument>> consume, CancellationToken ct) {
        AuthorizationStore.ValidateMetadata(metadata); PaymentReviewCorrectionPolicy.Validate(binding, revision); fields = PaymentReviewCorrectionPolicy.Copy(fields);
        if (id <= 0 || metadata.Reason.Length > 1000 || metadata.Reason != metadata.Reason.Trim() || metadata.Reason.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ||
            metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw PaymentReviewCorrectionPolicy.Invalid();
        await CorrectionStorageAsync(ct); var store = new PaymentCorrectionRecords(database);
        var versionId = (await ids.NextAsync(B.Versions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.Body(id, revision, binding, fields); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        Action? finalEligibility = null; PersonAuthorityProof? proof = null;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, token) => {
            finalEligibility = null; proof = null;
            var head = await CurrentCorrectionSourceAsync(actor, authority, tx, id, binding, token);
            _ = await store.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token);
            var resources = PaymentDraftBson.Scopes(head["scopes"].AsBsonArray);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.PaymentReviewCorrect, resources[0], clock.UtcNow),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.PaymentReviewCorrect, new(resources[0]));
        }, (tx, token) => consume(tx, async (extraction, check, inner) => {
            await new PaymentVerificationRecords(database).RequireUnverifiedAsync(tx, id, inner);
            finalEligibility = check; var original = Original(extraction, binding);
            var history = await store.HistoryAsync(tx, id, binding, B.Fields(original), inner); var prior = history.Recent.FirstOrDefault();
            if ((history.Head?["revision"].AsInt64 ?? 0) != revision) throw PaymentDraftRecords.Changed();
            var before = prior is null ? B.Fields(original) : prior["fields"];
            if (InvoiceRecords.Equal(before, B.Fields(fields))) throw PaymentReviewCorrectionPolicy.Invalid();
            var now = Now;
            if (prior is not null && prior["recordedAt"].ToUniversalTime() > now.UtcDateTime || extraction.ExtractedAt > now) throw PaymentCorrectionRecords.Bad();
            proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actor.UserId], inner); var person = proof.Evidence.Single();
            if (person.Verification == PersonVerification.Verified) await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, inner);
            var row = new BsonDocument { { "_id", versionId }, { "schemaVersion", 1 }, { "declarationId", id }, { "revision", revision + 1 }, { "binding", B.Binding(binding) },
                { "previousVersionId", prior is null ? BsonNull.Value : prior["_id"] }, { "previousHash", prior is null ? BsonNull.Value : prior["snapshotHash"] },
                { "recordedBy", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "recordedAt", now.UtcDateTime },
                { "personAtCorrection", new BsonDocument { { "userId", person.UserId }, { "personId", person.PersonId is { } personId ? new BsonInt64(personId) : BsonNull.Value }, { "verification", person.Verification.ToString() }, { "version", person.Version } } },
                { "beforeFields", before }, { "fields", B.Fields(fields) }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            row.Add("snapshotHash", B.Hash(row));
            await records.Rows(B.Versions).InsertOneAsync(tx, row, cancellationToken: inner);
            var head = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "revision", revision + 1 }, { "binding", B.Binding(binding) }, { "state", "submitted" }, { "versionId", versionId },
                { "fence", history.Head is null ? 0L : checked(history.Head["fence"].AsInt64 + 1) }, { "createdAt", history.Head is null ? new BsonDateTime(now.UtcDateTime) : history.Head["createdAt"] }, { "updatedAt", now.UtcDateTime } };
            if (history.Head is null) await records.Rows(B.Heads).InsertOneAsync(tx, head, cancellationToken: inner);
            else if ((await records.Rows(B.Heads).ReplaceOneAsync(tx, history.Head, head, cancellationToken: inner)).ModifiedCount != 1) throw PaymentDraftRecords.Changed();
            await records.Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: inner);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", B.Operation }, { "sourceKey", B.SourceKey(row) },
                { "payload", B.Result(row) }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: inner);
            return B.Result(row);
        }, token), ct, async (authority, tx, token) => {
            var saved = await store.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token) ?? throw PaymentCorrectionRecords.Bad();
            if (saved["declarationId"] != id || saved["bodyHash"] != bodyHash) throw PaymentCorrectionRecords.Bad();
            var history = await store.HistoryAsync(tx, id, binding, null, token); await store.TouchAsync(tx, history.Head, token);
            var head = await CurrentCorrectionSourceAsync(actor, authority, tx, id, binding, token);
            await records.TouchAsync(tx, head, token); await CorrectionFenceAsync(actor, authority, tx, head, token);
            if (proof?.Evidence.Single().Verification == PersonVerification.Verified) proof.RequireCurrent(tx, clock.UtcNow);
            finalEligibility?.Invoke(); token.ThrowIfCancellationRequested();
        });
    }
    internal async Task<BsonDocument?> RecoverCorrectionAsync(ActorContext actor, long id, string key, CancellationToken ct) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover payment review correction", "payment-correction-recovery")); await CorrectionStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var head = await ReviewHeadAsync(actor, authority, tx, id, token); _ = CorrectionQuery(actor, authority, head);
            var store = new PaymentCorrectionRecords(database); var row = await store.ForKeyAsync(tx, actor.UserId, key, token);
            if (row is not null) {
                if (row["declarationId"] != id) throw new PersistenceConflictException("idempotency_key_reused", "The correction belongs to another declaration");
                var binding = B.Binding(row["binding"].AsBsonDocument);
                head = await CurrentCorrectionSourceAsync(actor, authority, tx, id, binding, token);
                var history = await store.HistoryAsync(tx, id, binding, null, token); await store.TouchAsync(tx, history.Head, token);
            }
            await records.TouchAsync(tx, head, token); await CorrectionFenceAsync(actor, authority, tx, head, token); return row is null ? null : B.Result(row);
        }, ct);
    }
}
