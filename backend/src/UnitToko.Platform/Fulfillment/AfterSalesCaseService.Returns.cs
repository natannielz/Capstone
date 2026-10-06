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
using UnitToko.Platform.Inventory;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
public sealed record AfterSalesReturnLimit(LongId ReceiptId, LongId SkuId, LongId WarehouseId, long AuthorizedBaseUnits, long ReceivedBaseUnits);
public sealed record AfterSalesReturnReceipt(LongId Id, long Revision, DateTimeOffset ReceivedAt, string PublicNote, ImmutableArray<ReturnedStockLine> Lines);
public sealed record AfterSalesReturnView(LongId CaseId, LongId InvoiceId, long Revision, string Binding, ImmutableArray<AfterSalesReturnLimit> Limits, ImmutableArray<AfterSalesReturnReceipt> Recent, bool HasEarlier);
public sealed partial class AfterSalesCaseService
{
    private async Task<BsonDocument> ReturnSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument root, string permission, CancellationToken ct,
        FinancialInvoiceSource? issuedInvoice = null) {
        var decisions = new AfterSalesDecisionRecords(database); var decision = await decisions.ReadAsync(tx, root, ct) ?? throw R.Broken();
        if (decision["state"] != "return_authorized") throw R.Broken(); await decisions.RequireCapacityAsync(tx, root, [], ct);
        await new AfterSalesStockSourceEvents(database, ids, clock).FenceTopologyAsync(tx, root["source"].AsBsonDocument, ct);
        var stock = await invoices.CaseStockSourceAsync(actor, authority, tx, root["source"].AsBsonDocument, permission, ct, issuedInvoice);
        return Q.Source(root, decision, new BsonArray(stock.Where(line => decision["input"]["lines"].AsBsonArray.Any(item => item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"]))));
    }
    private static AfterSalesReturnView ReturnsView(BsonDocument source, ReturnQuarantineHistory history) => new(new(source["caseId"].AsInt64), new(source["invoiceId"].AsInt64), history.Revision,
        Q.Binding(source, history.Revision, history.HeadHash), [.. source["authorizedLines"].AsBsonArray.Select(line => new AfterSalesReturnLimit(new(line["receiptId"].AsInt64), new(line["skuId"].AsInt64),
            new(source["stock"].AsBsonArray.Single(item => item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"])["location"]["warehouseId"].AsInt64),
            line["reportedBaseUnits"].AsInt64, history.Quantities.GetValueOrDefault((line["receiptId"].AsInt64, line["skuId"].AsInt64))))],
        [.. history.Recent.Select(row => new AfterSalesReturnReceipt(new(row["_id"].AsInt64), row["revision"].AsInt64, new(row["receivedAt"].ToUniversalTime()), row["input"]["publicNote"].AsString, Q.Input(row["input"].AsBsonDocument).Lines))], history.Revision > history.Recent.Length);
    public async Task<AfterSalesReturnView> ReadReturnsAsync(ActorContext actor, long id, bool receivingPreview = false, CancellationToken ct = default) {
        if (id <= 0) throw ReturnedStockPolicy.Invalid(); await StorageAsync(ct); var permission = receivingPreview ? Permissions.CaseManage : Permissions.CaseRead;
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, id, permission, token); var source = await ReturnSourceAsync(actor, authority, tx, root, permission, token);
            var proof = receivingPreview ? await DecisionPeopleAsync(actor, tx, root, token) : null;
            if (receivingPreview) await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, source, clock.UtcNow, token);
            var history = await new Q(database, clock).ReadAsync(tx, source, token);
            await AuditReadAsync(actor, tx, "return.read", id, token); await FenceAsync(actor, authority, tx, root, permission, token); proof?.RequireCurrent(tx, clock.UtcNow);
            if (receivingPreview) Q.RequireReceiver(actor, authority, source, clock.UtcNow); return ReturnsView(source, history);
        }, ct);
    }
    public async Task<BsonDocument> ReceiveReturnAsync(ActorContext actor, long id, long revision, string binding, ReturnedStockInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (id <= 0 || revision < 0 || revision >= long.MaxValue - 1 || binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch))) throw ReturnedStockPolicy.Invalid();
        input = ReturnedStockPolicy.Copy(input); AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128)) throw ReturnedStockPolicy.Invalid();
        await StorageAsync(ct); var returnId = (await ids.NextAsync(Q.Collection, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var store = new Q(database, clock);
        var body = Q.Body(id, revision, binding, input); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        BsonDocument? root = null, source = null, fresh = null; Action? sourceCurrent = null; AuthorizationSnapshot? current = null; IClientSessionHandle? session = null; UnitToko.Platform.Identity.PersonAuthorityProof? proof = null;
        return await authorization.ExecuteCommandAsync(actor, Q.Operation, metadata, body, async (authority, tx, token) => {
            current = authority; session = tx; proof = null; fresh = null; sourceCurrent = null; root = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseManage, token);
            source = await ReturnSourceAsync(actor, authority, tx, root, Permissions.CaseManage, token); Q.RequireReceiver(actor, authority, source, clock.UtcNow);
            var resources = PaymentDraftBson.Scopes(root["scopes"].AsBsonArray); var approval = new ApprovalRequest(1, 1, RequestedBy: root["requestedBy"].AsInt64);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.CaseManage, resources[0], clock.UtcNow, approval),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.CaseManage, new(resources[0], approval));
        }, async (tx, token) => {
            var original = root ?? throw R.Broken(); var stock = source ?? throw R.Broken(); await invoices.CaseReturnFenceAsync(tx, original["source"].AsBsonDocument, token);
            var history = await store.ReadAsync(tx, stock, token); if (revision != history.Revision || binding != Q.Binding(stock, history.Revision, history.HeadHash)) throw AfterSalesDecisionRecords.Changed("returned_stock_changed");
            Q.RequireCapacity(stock, history.Quantities, input.Lines); proof = await DecisionPeopleAsync(actor, tx, original, token); var at = Now;
            if (stock["decidedAt"].ToUniversalTime() > at.UtcDateTime || history.Recent.Any(row => row["receivedAt"].ToUniversalTime() > at.UtcDateTime)) throw R.Broken();
            var row = new BsonDocument { { "_id", returnId }, { "schemaVersion", 1 }, { "caseId", id }, { "revision", revision + 1 }, { "source", stock }, { "binding", binding }, { "previousHash", history.HeadHash },
                { "input", Q.Input(input) }, { "state", "quarantined" }, { "lotIdentity", "unverified" }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "people", new BsonArray(proof.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) }, { "receivedAt", at.UtcDateTime }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } }; row.Add("snapshotHash", R.Hash(row));
            await store.ReceiveAsync(tx, row, history, token); fresh = row; return Q.Result(row);
        }, ct, async (authority, tx, token) => {
            var original = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseManage, token); var stock = await ReturnSourceAsync(actor, authority, tx, original, Permissions.CaseManage, token);
            _ = await store.ReadAsync(tx, stock, token); var saved = await store.Rows(Q.Collection).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey } }).SingleOrDefaultAsync(token) ?? throw R.Broken();
            if (saved["caseId"] != id || saved["bodyHash"] != hash) throw R.Broken();
            await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, stock, clock.UtcNow, token); await FenceAsync(actor, authority, tx, original, Permissions.CaseManage, token);
            if (fresh is not null) sourceCurrent = await new AfterSalesStockSourceProducer(database, ids, clock).RecordFreshAsync(tx, actor, authority, authorization, "receive", fresh,
                () => { Require(actor, current ?? throw R.Broken(), root ?? throw R.Broken(), Permissions.CaseManage); Q.RequireReceiver(actor, current!, source ?? throw R.Broken(), clock.UtcNow); proof?.RequireCurrent(session!, clock.UtcNow); }, token);
        }, validateCurrent: () => { Require(actor, current ?? throw R.Broken(), root ?? throw R.Broken(), Permissions.CaseManage); Q.RequireReceiver(actor, current!, source ?? throw R.Broken(), clock.UtcNow); proof?.RequireCurrent(session!, clock.UtcNow); sourceCurrent?.Invoke(); });
    }
    public async Task<BsonDocument?> RecoverReturnAsync(ActorContext actor, long id, string key, CancellationToken ct = default) {
        if (id <= 0) throw ReturnedStockPolicy.Invalid(); AuthorizationStore.ValidateMetadata(new(key, "Recover original physical return", "return-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, id, Permissions.CaseManage, token); var source = await ReturnSourceAsync(actor, authority, tx, root, Permissions.CaseManage, token);
            var proof = await DecisionPeopleAsync(actor, tx, root, token); var store = new Q(database, clock); _ = await store.ReadAsync(tx, source, token);
            var own = await store.Rows(Q.Collection).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
            if (own is not null && own["caseId"] != id) throw R.Broken();
            if (own is null) foreach (var (table, field) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
                if (await store.Rows(table).Find(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", Q.Operation }, { "actorId", actor.UserId }, { field, key } }).AnyAsync(token)) throw R.Broken();
            await AuditReadAsync(actor, tx, "return.recover", id, token); await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, source, clock.UtcNow, token);
            await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, token); proof.RequireCurrent(tx, clock.UtcNow); Q.RequireReceiver(actor, authority, source, clock.UtcNow); return own is null ? null : Q.Result(own);
        }, ct);
    }
}
