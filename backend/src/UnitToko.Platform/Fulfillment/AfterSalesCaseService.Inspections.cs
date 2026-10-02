using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
public sealed record AfterSalesInspectionLine(LongId ReceiptId, LongId SkuId, long InspectedBaseUnits, string Identity, string Condition);
public sealed record AfterSalesInspectionAssessment(DateTimeOffset InspectedAt, string PublicNote, ImmutableArray<AfterSalesInspectionLine> Lines);
public sealed record AfterSalesReturnInspectionView(LongId CaseId, LongId InvoiceId, long Revision, string Binding, AfterSalesReturnReceipt Receipt, AfterSalesInspectionAssessment? Inspection);
public sealed partial class AfterSalesCaseService
{
    private async Task<BsonDocument> InspectionReceiptAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument root, long returnId, string permission, CancellationToken ct,
        FinancialInvoiceSource? issuedInvoice = null) {
        var source = await ReturnSourceAsync(actor, authority, tx, root, permission, ct, issuedInvoice); _ = await new Q(database, clock).ReadAsync(tx, source, ct);
        var receipt = await records.Rows(Q.Collection).Find(tx, new BsonDocument { { "_id", returnId }, { "caseId", root["_id"] } }).SingleOrDefaultAsync(ct) ?? throw R.Broken();
        if (!InvoiceRecords.Equal(receipt["source"], source)) throw R.Broken(); return receipt;
    }
    private static AfterSalesReturnInspectionView InspectionView(BsonDocument receipt, BsonDocument? inspection) => new(new(receipt["caseId"].AsInt64), new(receipt["source"]["invoiceId"].AsInt64), inspection is null ? 0 : 1,
        I.Binding(receipt), new(new(receipt["_id"].AsInt64), receipt["revision"].AsInt64, new(receipt["receivedAt"].ToUniversalTime()), receipt["input"]["publicNote"].AsString, Q.Input(receipt["input"].AsBsonDocument).Lines),
        inspection is null ? null : new(new(inspection["inspectedAt"].ToUniversalTime()), inspection["input"]["publicNote"].AsString,
            [.. I.Input(inspection["input"].AsBsonDocument).Lines.Select(line => new AfterSalesInspectionLine(new(line.ReceiptId), new(line.SkuId), line.InspectedBaseUnits, line.Identity, line.Condition))]));
    public async Task<AfterSalesReturnInspectionView> ReadInspectionAsync(ActorContext actor, long caseId, long returnId, bool preview = false, CancellationToken ct = default) {
        if (caseId <= 0 || returnId <= 0) throw ReturnedStockInspectionPolicy.Invalid(); await StorageAsync(ct); var permission = preview ? Permissions.CaseManage : Permissions.CaseRead;
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, caseId, permission, token); var receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, permission, token);
            var proof = preview ? await DecisionPeopleAsync(actor, tx, root, token) : null;
            if (preview) await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, receipt["source"].AsBsonDocument, clock.UtcNow, token, Permissions.StockCount);
            var saved = await new I(database, clock).ReadAsync(tx, receipt, token); await AuditReadAsync(actor, tx, "inspection.read", returnId, token);
            await FenceAsync(actor, authority, tx, root, permission, token); proof?.RequireCurrent(tx, clock.UtcNow);
            if (preview) Q.RequireReceiver(actor, authority, receipt["source"].AsBsonDocument, clock.UtcNow, Permissions.StockCount); return InspectionView(receipt, saved);
        }, ct);
    }
    public async Task<BsonDocument> InspectReturnAsync(ActorContext actor, long caseId, long returnId, long revision, string binding, ReturnedStockInspectionInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (caseId <= 0 || returnId <= 0 || revision != 0 || binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch))) throw ReturnedStockInspectionPolicy.Invalid();
        input = ReturnedStockInspectionPolicy.Copy(input); AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128)) throw ReturnedStockInspectionPolicy.Invalid();
        await StorageAsync(ct); var body = I.Body(returnId, revision, binding, input); var hash = AuthorizationStore.CommandHash(body, metadata.Reason); var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var store = new I(database, clock);
        BsonDocument? root = null, receipt = null; AuthorizationSnapshot? current = null; IClientSessionHandle? session = null; PersonAuthorityProof? proof = null;
        return await authorization.ExecuteCommandAsync(actor, I.Operation, metadata, body, async (authority, tx, token) => {
            current = authority; session = tx; proof = null; root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token);
            receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, Permissions.CaseManage, token); Q.RequireReceiver(actor, authority, receipt["source"].AsBsonDocument, clock.UtcNow, Permissions.StockCount);
            var resources = PaymentDraftBson.Scopes(root["scopes"].AsBsonArray); var approval = new ApprovalRequest(1, 1, RequestedBy: root["requestedBy"].AsInt64);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.CaseManage, resources[0], clock.UtcNow, approval),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.CaseManage, new(resources[0], approval));
        }, async (tx, token) => {
            var original = root ?? throw R.Broken(); var received = receipt ?? throw R.Broken();
            if (binding != I.Binding(received) || await store.ReadAsync(tx, received, token) is not null) throw AfterSalesDecisionRecords.Changed("return_inspection_changed");
            ReturnedStockInspectionPolicy.RequireReceipt(input, Q.Input(received["input"].AsBsonDocument)); await invoices.CaseReturnFenceAsync(tx, original["source"].AsBsonDocument, token);
            proof = await DecisionPeopleAsync(actor, tx, original, token); var at = Now; if (received["receivedAt"].ToUniversalTime() > at.UtcDateTime) throw R.Broken();
            var row = new BsonDocument { { "_id", returnId }, { "schemaVersion", 1 }, { "revision", 1L }, { "source", I.Source(received) }, { "binding", binding }, { "input", I.Input(input) },
                { "state", "inspected_quarantine" }, { "lotIdentity", "unverified" }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "inspectedAt", at.UtcDateTime },
                { "people", new BsonArray(proof.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash },
                { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } }; row.Add("snapshotHash", R.Hash(row));
            await store.RecordAsync(tx, row, received, token); return I.Result(row);
        }, ct, async (authority, tx, token) => {
            var original = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token); var received = await InspectionReceiptAsync(actor, authority, tx, original, returnId, Permissions.CaseManage, token);
            var saved = await store.ReadAsync(tx, received, token) ?? throw R.Broken(); if (saved["actorId"] != actor.UserId || saved["requestKey"] != metadata.IdempotencyKey || saved["bodyHash"] != hash) throw R.Broken();
            await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, received["source"].AsBsonDocument, clock.UtcNow, token, Permissions.StockCount);
            await FenceAsync(actor, authority, tx, original, Permissions.CaseManage, token);
        }, validateCurrent: () => { Require(actor, current ?? throw R.Broken(), root ?? throw R.Broken(), Permissions.CaseManage); Q.RequireReceiver(actor, current!, receipt?["source"].AsBsonDocument ?? throw R.Broken(), clock.UtcNow, Permissions.StockCount); proof?.RequireCurrent(session!, clock.UtcNow); });
    }
    public async Task<BsonDocument?> RecoverInspectionAsync(ActorContext actor, long caseId, long returnId, string key, CancellationToken ct = default) {
        if (caseId <= 0 || returnId <= 0) throw ReturnedStockInspectionPolicy.Invalid(); AuthorizationStore.ValidateMetadata(new(key, "Recover original return inspection", "return-inspection-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token); var received = await InspectionReceiptAsync(actor, authority, tx, root, returnId, Permissions.CaseManage, token);
            var proof = await DecisionPeopleAsync(actor, tx, root, token); var store = new I(database, clock); var saved = await store.ReadAsync(tx, received, token);
            var own = await store.Rows(I.Collection).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
            if (own is not null && (saved is null || !InvoiceRecords.Equal(own, saved))) throw R.Broken();
            if (own is null) foreach (var (table, field) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
                if (await store.Rows(table).Find(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", I.Operation }, { "actorId", actor.UserId }, { field, key } }).AnyAsync(token)) throw R.Broken();
            await AuditReadAsync(actor, tx, "inspection.recover", returnId, token); await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, received["source"].AsBsonDocument, clock.UtcNow, token, Permissions.StockCount);
            await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, token); proof.RequireCurrent(tx, clock.UtcNow); Q.RequireReceiver(actor, authority, received["source"].AsBsonDocument, clock.UtcNow, Permissions.StockCount); return own is null ? null : I.Result(own);
        }, ct);
    }
}
