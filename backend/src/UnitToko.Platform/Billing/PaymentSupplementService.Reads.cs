using UnitToko.Platform.Documents;
using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.PaymentSupplementBson;

namespace UnitToko.Platform.Billing;

internal sealed record PaymentSupplementRecovered(string BodyHash, BsonDocument Result);
public sealed partial class PaymentSupplementService
{
    internal async Task<PaymentSupplementRecovered?> LookupAsync(ActorContext actor, long declarationId, long parentRevision,
        string operation, string key, long? supplementId = null, CancellationToken ct = default) {
        if (operation is not (B.CreateOperation or B.AttachOperation)) throw PaymentSupplementRules.Invalid();
        if (supplementId is not null) PaymentSupplementRules.Id(supplementId.Value);
        if (operation == B.AttachOperation && supplementId is null || operation == B.CreateOperation && supplementId is not null) throw PaymentSupplementRules.Invalid();
        PaymentSupplementRules.Text(key, 128); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var parent = await declarations.SupplementParentAsync(actor, authority, tx, declarationId, parentRevision, null, false, token);
            var recovered = await LookupVersionAsync(tx, actor.UserId, operation, key, token);
            if (recovered is not null && B.Primary(recovered.Value.Version["primary"].AsBsonDocument) != parent.Binding) throw PaymentSupplementRecords.Broken();
            if (recovered is not null && supplementId is not null && recovered.Value.Version["supplementId"] != supplementId.Value) throw PaymentSupplementRecords.Broken();
            await AuditReadAsync(tx, actor, parent, "operation-recovery", key, [], false, token);
            await declarations.SupplementParentFenceAsync(actor, authority, tx, parent, false, token);
            return recovered is null ? null : new PaymentSupplementRecovered(recovered.Value.Version["bodyHash"].AsString,
                B.Result(recovered.Value.Version, recovered.Value.Version["revision"] == 1L ? null : recovered.Value.History.Attachment));
        }, ct);
    }
    private async Task<(BsonDocument Version, PaymentSupplementHistory History)?> LookupVersionAsync(IClientSessionHandle tx, long actorId,
        string operation, string key, CancellationToken ct) {
        var receipt = await records.Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actorId }, { "operation", operation }, { "key", key } }).SingleOrDefaultAsync(ct);
        var versions = await records.Rows(B.Versions).Find(tx, new BsonDocument { { "actorId", actorId }, { "operation", operation }, { "requestKey", key } }).Limit(2).ToListAsync(ct);
        if (receipt is null && versions.Count == 0) return null;
        if (receipt is null || versions.Count != 1) throw PaymentSupplementRecords.Broken();
        var version = versions[0]; var history = await records.HistoryAsync(tx, version["supplementId"].AsInt64, ct);
        if (!history.Versions.Any(row => row.Equals(version)) || receipt["bodyHash"] != version["bodyHash"]) throw PaymentSupplementRecords.Broken();
        return (version, history);
    }
    public async Task<PaymentSupplementPage> ListAsync(ActorContext actor, long declarationId, long parentRevision, long? beforeId,
        AuthorizationReadMetadata metadata, bool review, CancellationToken ct = default) {
        if (beforeId is not null) PaymentSupplementRules.Id(beforeId.Value);
        ReadMetadata(metadata, review ? "payment-review-supplements-list" : "payment-supplements-list"); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var parent = await declarations.SupplementParentAsync(actor, authority, tx, declarationId, parentRevision, null, review, token);
            var filter = new BsonDocument("primary", B.Primary(parent.Binding)); if (beforeId.HasValue) filter.Add("_id", new BsonDocument("$lt", beforeId.Value));
            var selected = await records.Rows(B.Heads).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = ImmutableArray.CreateBuilder<PaymentSupplementView>();
            foreach (var head in selected.Take(25)) {
                var history = await records.HistoryAsync(tx, head["_id"].AsInt64, token); RequireOwner(history, parent);
                items.Add(B.View(history.Versions[^1], history.Attachment)); await FenceHeadAsync(tx, history.Head, token);
            }
            var policy = await policies.FindCurrentAsync(tx, parent.PolicyScope, token);
            await AuditReadAsync(tx, actor, parent, metadata.Purpose, metadata.CorrelationId, selected.Take(25).Select(row => row["_id"].AsInt64), review, token);
            if (policy is not null) await policies.FenceAsync(tx, policy, token);
            await declarations.SupplementParentFenceAsync(actor, authority, tx, parent, review, token);
            return new PaymentSupplementPage(items.ToImmutable(), selected.Count > 25 ? items[^1].SupplementId : null, Now, policy is null ? "policy_unavailable" : "allowed");
        }, ct);
    }
    public async Task<PaymentSupplementView> DetailAsync(ActorContext actor, long declarationId, long parentRevision, long supplementId,
        AuthorizationReadMetadata metadata, bool review, CancellationToken ct = default) {
        ReadMetadata(metadata, review ? "payment-review-supplement-detail" : "payment-supplement-detail"); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var parent = await declarations.SupplementParentAsync(actor, authority, tx, declarationId, parentRevision, null, review, token);
            var history = await records.HistoryAsync(tx, supplementId, token); RequireOwner(history, parent);
            await AuditReadAsync(tx, actor, parent, metadata.Purpose, metadata.CorrelationId, [supplementId], review, token);
            await FenceHeadAsync(tx, history.Head, token); await declarations.SupplementParentFenceAsync(actor, authority, tx, parent, review, token);
            return B.View(history.Versions[^1], history.Attachment);
        }, ct);
    }
    // Used by the evidence admission/read adapter in the SAME owning transaction. It is
    // intentionally not a public grant or a standalone unaudited original-byte endpoint.
    internal async Task<T> WithAdmissionAsync<T>(IClientSessionHandle tx, ActorContext actor, long supplementId, long expectedRevision,
        bool read, bool review, EvidenceRef? reference, Func<PaymentSupplementAdmissionAuthority, CancellationToken, Task<T>> action, CancellationToken ct) {
        var authority = await authorization.LoadAsync(actor, tx, ct); var history = await records.HistoryAsync(tx, supplementId, ct);
        if (history.Head["revision"] != expectedRevision || review && history.Attachment is null || !read && (review || history.Attachment is not null)) throw PaymentDraftRecords.Changed();
        var primary = B.Primary(history.Head["primary"].AsBsonDocument);
        var parent = await declarations.SupplementParentAsync(actor, authority, tx, primary.DeclarationId, primary.DeclarationRevision, primary, review, ct);
        RequireOwner(history, parent);
        var policy = await policies.RequireAsync(tx, parent.PolicyScope, ct);
        if (reference is not null && history.Attachment is not null && !history.Attachment["evidence"].Equals(PaymentSubmissionBson.Reference(reference))) throw PaymentDraftRecords.Changed();
        var result = await action(new(supplementId, expectedRevision, history.Head["uploaderId"].AsInt64, parent.Binding, parent.PolicyScope,
            history.Attachment?["reservationId"].AsInt64, history.Attachment is null ? null : PaymentSubmissionBson.Reference(history.Attachment["evidence"].AsBsonDocument),
            new DateTimeOffset(policy["expiresAt"].ToUniversalTime())), ct);
        await FenceHeadAsync(tx, history.Head, ct); await policies.FenceAsync(tx, policy, ct);
        await declarations.SupplementParentFenceAsync(actor, authority, tx, parent, review, ct); return result;
    }
    private static void RequireOwner(PaymentSupplementHistory history, PaymentSupplementParent parent) {
        if (B.Primary(history.Head["primary"].AsBsonDocument) != parent.Binding || history.Head["primarySubmissionHash"] != parent.Submission["snapshotHash"] ||
            !history.Head["scope"].Equals(parent.PolicyScope) || history.Head["uploaderId"] != parent.Submission["uploaderId"]) throw PaymentSupplementRecords.Broken();
    }
    private async Task FenceHeadAsync(IClientSessionHandle tx, BsonDocument head, CancellationToken ct) {
        if (head["fence"].AsInt64 >= long.MaxValue - 2 || (await records.Rows(B.Heads).UpdateOneAsync(tx, head,
            new BsonDocument("$inc", new BsonDocument("fence", 1L)), cancellationToken: ct)).ModifiedCount != 1) throw PaymentDraftRecords.Changed();
    }
    private static void ReadMetadata(AuthorizationReadMetadata metadata, string purpose) {
        if (metadata.Purpose != purpose) throw PaymentSupplementRules.Invalid(); PaymentSupplementRules.Text(metadata.CorrelationId, 128);
    }
    private async Task AuditReadAsync(IClientSessionHandle tx, ActorContext actor, PaymentSupplementParent parent, string purpose,
        string correlation, IEnumerable<long> selected, bool review, CancellationToken ct) {
        PaymentSupplementRules.Text(correlation, 128);
        var people = parent.IndependentPeople?.Evidence.Select(person => new BsonDocument { { "userId", person.UserId },
            { "personId", person.PersonId is { } id ? new BsonInt64(id) : BsonNull.Value }, { "verification", person.Verification.ToString() }, { "version", person.Version } });
        await records.Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 },
            { "kind", "billing.payment.supplement.read" }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
            { "primary", B.Primary(parent.Binding) }, { "supplementIds", new BsonArray(selected.Select(value => new BsonInt64(value))) },
            { "purpose", purpose }, { "review", review }, { "people", new BsonArray(people ?? []) }, { "correlationId", correlation }, { "createdAt", Now.UtcDateTime } }, cancellationToken: ct);
    }
}
