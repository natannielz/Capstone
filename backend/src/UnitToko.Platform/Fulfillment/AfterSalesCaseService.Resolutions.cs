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
using UnitToko.Platform.Inventory;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using P = UnitToko.Platform.Fulfillment.ReturnResolutionRecords;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
public sealed record AfterSalesResolutionRequestView(DateTimeOffset RequestedAt, string Action, string PublicNote, string State);
public sealed record AfterSalesResolutionView(LongId CaseId, LongId InvoiceId, LongId ReturnId, long Revision, string Binding, AfterSalesInspectionAssessment Inspection, AfterSalesResolutionRequestView? Request);
public sealed partial class AfterSalesCaseService
{
    private async Task<BsonDocument> ResolutionInspectionAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument root, long returnId, string permission, CancellationToken ct) {
        var receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, permission, ct);
        return await new I(database, clock).ReadAsync(tx, receipt, ct) ?? throw R.Broken();
    }
    private static AfterSalesResolutionView ResolutionView(BsonDocument inspection, BsonDocument? request) => new(new(inspection["source"]["caseId"].AsInt64), new(inspection["source"]["invoiceId"].AsInt64), new(inspection["_id"].AsInt64),
        request is null ? 0 : 1, P.Binding(inspection), new(new(inspection["inspectedAt"].ToUniversalTime()), inspection["input"]["publicNote"].AsString,
            [.. I.Input(inspection["input"].AsBsonDocument).Lines.Select(line => new AfterSalesInspectionLine(new(line.ReceiptId), new(line.SkuId), line.InspectedBaseUnits, line.Identity, line.Condition))]),
        request is null ? null : new(new(request["requestedAt"].ToUniversalTime()), request["input"]["action"].AsString, request["input"]["publicNote"].AsString, request["state"].AsString));
    private void RequireResolution(ActorContext actor, AuthorizationSnapshot authority, BsonDocument root, string action) {
        Require(actor, authority, root, Permissions.CaseManage); Require(actor, authority, root, ReturnResolutionPolicy.Permission(action));
    }
    private async Task FenceResolutionAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument root, string action, CancellationToken ct) {
        RequireResolution(actor, authority, root, action); var permission = ReturnResolutionPolicy.Permission(action);
        await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, ct);
        if (permission != Permissions.CaseManage) {
            var query = Query(actor, authority, permission); var resources = PaymentDraftBson.Scopes(root["scopes"].AsBsonArray);
            var clauses = query.Clauses.Where(clause => resources.Any(clause.Matches)).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory))).Distinct(), [],
                authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        }
        RequireResolution(actor, authority, root, action);
    }
    public async Task<AfterSalesResolutionView> ReadResolutionAsync(ActorContext actor, long caseId, long returnId, string? requestedAction = null, CancellationToken ct = default) {
        if (caseId <= 0 || returnId <= 0) throw ReturnResolutionPolicy.Invalid(); if (requestedAction is not null) _ = ReturnResolutionPolicy.Permission(requestedAction);
        await StorageAsync(ct); var permission = requestedAction is null ? Permissions.CaseRead : Permissions.CaseManage;
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, caseId, permission, token); var inspection = await ResolutionInspectionAsync(actor, authority, tx, root, returnId, permission, token);
            var proof = requestedAction is null ? null : await DecisionPeopleAsync(actor, tx, root, token);
            if (requestedAction is not null) await FenceResolutionAsync(actor, authority, tx, root, requestedAction, token);
            var saved = await new P(database, clock).ReadAsync(tx, inspection, token); await AuditReadAsync(actor, tx, "resolution.read", returnId, token);
            await FenceAsync(actor, authority, tx, root, permission, token); proof?.RequireCurrent(tx, clock.UtcNow);
            if (requestedAction is not null) RequireResolution(actor, authority, root, requestedAction); return ResolutionView(inspection, saved);
        }, ct);
    }
    public async Task<BsonDocument> RequestResolutionAsync(ActorContext actor, long caseId, long returnId, long revision, string binding, ReturnResolutionInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (caseId <= 0 || returnId <= 0 || revision != 0 || binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch))) throw ReturnResolutionPolicy.Invalid();
        input = ReturnResolutionPolicy.Copy(input); AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128)) throw ReturnResolutionPolicy.Invalid();
        await StorageAsync(ct); var body = P.Body(returnId, revision, binding, input); var hash = AuthorizationStore.CommandHash(body, metadata.Reason); var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var store = new P(database, clock);
        BsonDocument? root = null, inspection = null; AuthorizationSnapshot? current = null; IClientSessionHandle? session = null; PersonAuthorityProof? proof = null;
        return await authorization.ExecuteCommandAsync(actor, P.Operation, metadata, body, async (authority, tx, token) => {
            current = authority; session = tx; proof = null; root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token);
            inspection = await ResolutionInspectionAsync(actor, authority, tx, root, returnId, Permissions.CaseManage, token); RequireResolution(actor, authority, root, input.Action);
            var resources = PaymentDraftBson.Scopes(root["scopes"].AsBsonArray); var approval = new ApprovalRequest(1, 1, RequestedBy: root["requestedBy"].AsInt64);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.CaseManage, resources[0], clock.UtcNow, approval),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.CaseManage, new(resources[0], approval));
        }, async (tx, token) => {
            var original = root ?? throw R.Broken(); var assessed = inspection ?? throw R.Broken();
            if (binding != P.Binding(assessed) || await store.ReadAsync(tx, assessed, token) is not null) throw AfterSalesDecisionRecords.Changed("return_resolution_changed");
            await invoices.CaseReturnFenceAsync(tx, original["source"].AsBsonDocument, token); proof = await DecisionPeopleAsync(actor, tx, original, token); var at = Now;
            if (assessed["inspectedAt"].ToUniversalTime() > at.UtcDateTime) throw R.Broken();
            var row = new BsonDocument { { "_id", returnId }, { "schemaVersion", 1 }, { "revision", 1L }, { "source", P.Source(assessed) }, { "binding", binding }, { "input", P.Input(input) },
                { "state", "resolution_requested" }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "requestedAt", at.UtcDateTime },
                { "people", new BsonArray(proof.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash },
                { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } }; row.Add("snapshotHash", R.Hash(row));
            await store.RecordAsync(tx, row, assessed, token); return P.Result(row);
        }, ct, async (authority, tx, token) => {
            var original = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token); var assessed = await ResolutionInspectionAsync(actor, authority, tx, original, returnId, Permissions.CaseManage, token);
            var saved = await store.ReadAsync(tx, assessed, token) ?? throw R.Broken(); if (saved["actorId"] != actor.UserId || saved["requestKey"] != metadata.IdempotencyKey || saved["bodyHash"] != hash) throw R.Broken();
            await FenceResolutionAsync(actor, authority, tx, original, input.Action, token);
        }, validateCurrent: () => { RequireResolution(actor, current ?? throw R.Broken(), root ?? throw R.Broken(), input.Action); proof?.RequireCurrent(session!, clock.UtcNow); });
    }
    public async Task<BsonDocument?> RecoverResolutionAsync(ActorContext actor, long caseId, long returnId, string key, CancellationToken ct = default) {
        if (caseId <= 0 || returnId <= 0) throw ReturnResolutionPolicy.Invalid(); AuthorizationStore.ValidateMetadata(new(key, "Recover original resolution request", "return-resolution-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token); var inspection = await ResolutionInspectionAsync(actor, authority, tx, root, returnId, Permissions.CaseManage, token);
            var proof = await DecisionPeopleAsync(actor, tx, root, token); var store = new P(database, clock); var saved = await store.ReadAsync(tx, inspection, token);
            var own = await store.Rows(P.Collection).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
            if (own is not null && (saved is null || !InvoiceRecords.Equal(own, saved))) throw R.Broken();
            if (own is null) foreach (var (table, field) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
                if (await store.Rows(table).Find(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", P.Operation }, { "actorId", actor.UserId }, { field, key } }).AnyAsync(token)) throw R.Broken();
            await AuditReadAsync(actor, tx, "resolution.recover", returnId, token);
            if (own is not null) await FenceResolutionAsync(actor, authority, tx, root, own["input"]["action"].AsString, token); else await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, token);
            proof.RequireCurrent(tx, clock.UtcNow); if (own is not null) RequireResolution(actor, authority, root, own["input"]["action"].AsString); return own is null ? null : P.Result(own);
        }, ct);
    }
}
