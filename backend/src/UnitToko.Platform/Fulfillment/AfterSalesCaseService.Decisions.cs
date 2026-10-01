using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using D = UnitToko.Platform.Fulfillment.AfterSalesDecisionRecords;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
public sealed record AfterSalesDecisionView(LongId CaseId, LongId InvoiceId, long Revision, string Binding, string State, DateTimeOffset? DecidedAt, string? PublicExplanation, ImmutableArray<AfterSalesCaseLine> Lines);
public sealed partial class AfterSalesCaseService
{
    private static AfterSalesDecisionView DecisionView(BsonDocument root, BsonDocument? row) => new(new(root["_id"].AsInt64), new(root["source"]["invoiceId"].AsInt64), row is null ? 1 : 2, D.Binding(root), row?["state"].AsString ?? "requested",
        row is null ? null : new(row["decidedAt"].ToUniversalTime()), row?["input"]["publicExplanation"].AsString, row is null ? [] : D.Input(row["input"].AsBsonDocument).Lines);
    private async Task<PersonAuthorityProof> DecisionPeopleAsync(ActorContext actor, IClientSessionHandle tx, BsonDocument root, CancellationToken ct) {
        var approval = new ApprovalRequest(1, 1, RequestedBy: root["requestedBy"].AsInt64); var account = ApprovalPolicy.Evaluate(actor.UserId, Permissions.CaseManage, approval);
        if (!account.Allowed) throw new AuthorizationDeniedException(account.Code);
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, PersonApprovalPolicy.RequiredAccounts(actor.UserId, Permissions.CaseManage, approval), ct);
        var decision = PersonApprovalPolicy.Evaluate(actor.UserId, Permissions.CaseManage, approval, proof.Evidence);
        if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, ct); return proof;
    }
    public async Task<AfterSalesDecisionView> ReadDecisionAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw AfterSalesDecisionPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseManage, token); var proof = await DecisionPeopleAsync(actor, tx, root, token);
            var store = new D(database); var saved = await store.ReadAsync(tx, root, token); await store.RequireCapacityAsync(tx, root, [], token); await AuditReadAsync(actor, tx, "decision.read", id, token);
            await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, token); proof.RequireCurrent(tx, clock.UtcNow); Require(actor, authority, root, Permissions.CaseManage);
            return DecisionView(root, saved);
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long id, long revision, string binding, AfterSalesDecisionInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (id <= 0 || revision != 1 || binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch))) throw AfterSalesDecisionPolicy.Invalid();
        input = AfterSalesDecisionPolicy.Copy(input); AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128)) throw AfterSalesDecisionPolicy.Invalid();
        await StorageAsync(ct); var body = D.Body(id, revision, binding, input); var hash = AuthorizationStore.CommandHash(body, metadata.Reason); var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var store = new D(database); BsonDocument? root = null; AuthorizationSnapshot? current = null; IClientSessionHandle? session = null; PersonAuthorityProof? proof = null;
        return await authorization.ExecuteCommandAsync(actor, D.Operation, metadata, body, async (authority, tx, token) => {
            current = authority; session = tx; proof = null; root = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseManage, token);
            var resources = PaymentDraftBson.Scopes(root["scopes"].AsBsonArray); var approval = new ApprovalRequest(1, 1, RequestedBy: root["requestedBy"].AsInt64);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.CaseManage, resources[0], clock.UtcNow, approval),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.CaseManage, new(resources[0], approval));
        }, async (tx, token) => {
            var original = root ?? throw R.Broken(); if (binding != D.Binding(original) || await store.ReadAsync(tx, original, token) is not null) throw D.Changed();
            AfterSalesDecisionPolicy.RequireRequested(input, R.Input(original["input"].AsBsonDocument).Lines);
            await invoices.CaseReturnFenceAsync(tx, original["source"].AsBsonDocument, token); await store.RequireCapacityAsync(tx, original, input.Lines, token);
            proof = await DecisionPeopleAsync(actor, tx, original, token); var at = Now; if (original["requestedAt"].ToUniversalTime() > at.UtcDateTime) throw R.Broken();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "revision", 2L }, { "source", D.Source(original) }, { "binding", binding }, { "input", D.Input(input) },
                { "state", AfterSalesDecisionPolicy.State(input) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "people", new BsonArray(proof.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) },
                { "decidedAt", at.UtcDateTime }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
            row.Add("snapshotHash", R.Hash(row)); await store.Rows(D.Collection).InsertOneAsync(tx, row, cancellationToken: token);
            await store.Rows("auditEvents").InsertOneAsync(tx, D.Audit(row), cancellationToken: token);
            await store.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", D.Operation }, { "sourceKey", D.Operation + ":" + id },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", D.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return D.Result(row);
        }, ct, async (authority, tx, token) => {
            var original = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseManage, token); var saved = await store.ReadAsync(tx, original, token) ?? throw R.Broken();
            if (saved["actorId"] != actor.UserId || saved["requestKey"] != metadata.IdempotencyKey || saved["bodyHash"] != hash) throw R.Broken();
            await store.RequireCapacityAsync(tx, original, [], token);
            await FenceAsync(actor, authority, tx, original, Permissions.CaseManage, token);
        }, validateCurrent: () => { Require(actor, current ?? throw R.Broken(), root ?? throw R.Broken(), Permissions.CaseManage); proof?.RequireCurrent(session!, clock.UtcNow); });
    }
    public async Task<BsonDocument?> RecoverDecisionAsync(ActorContext actor, long id, string key, CancellationToken ct = default) {
        if (id <= 0) throw AfterSalesDecisionPolicy.Invalid(); AuthorizationStore.ValidateMetadata(new(key, "Recover original case decision", "case-decision-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseManage, token); var proof = await DecisionPeopleAsync(actor, tx, root, token); var store = new D(database);
            var saved = await store.ReadAsync(tx, root, token); var own = await store.Rows(D.Collection).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
            if (own is not null && (own["_id"] != id || saved is null || !InvoiceRecords.Equal(own, saved))) throw D.Changed();
            if (own is null) foreach (var (table, field) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
                if (await store.Rows(table).Find(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", D.Operation }, { "actorId", actor.UserId }, { field, key } }).AnyAsync(token)) throw R.Broken();
            await store.RequireCapacityAsync(tx, root, [], token); await AuditReadAsync(actor, tx, "decision.recover", id, token); await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, token);
            proof.RequireCurrent(tx, clock.UtcNow); Require(actor, authority, root, Permissions.CaseManage); return own is null ? null : D.Result(own);
        }, ct);
    }
}
