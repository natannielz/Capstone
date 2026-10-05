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
using V = UnitToko.Platform.Fulfillment.ReturnedLotVerificationRecords;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
public sealed record ReturnedLotChoice(LongId OriginalLotId, LongId PoolId, LongId DefinitionId, string PackageLabel, string BaseUnitCode, int BaseUnitsPerPackage, long DispatchedBaseUnits);
public sealed record ReturnedLotLineReview(LongId ReceiptId, LongId SkuId, long ReceivedBaseUnits, long VerifiedBaseUnits, long ManualBaseUnits,
    string State, string Explanation, ImmutableArray<ReturnedLotChoice> Choices);
public sealed record ReturnedLotVerificationView(LongId CaseId, LongId ReturnId, long Revision, string Binding, string State, ImmutableArray<ReturnedLotLineReview> Lines);
public sealed partial class AfterSalesCaseService
{
    private void RequireLotReviewer(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        BsonDocument root, BsonDocument receipt, PersonAuthorityProof? proof)
    {
        Require(actor, authority, root, Permissions.CaseManage);
        Q.RequireReceiver(actor, authority, receipt["source"].AsBsonDocument, clock.UtcNow, Permissions.StockCount);
        proof?.RequireCurrent(tx, clock.UtcNow);
    }
    private static ReturnedLotVerificationView LotView(BsonDocument receipt, BsonDocument inspection, BsonDocument? verified)
    {
        var lines = ImmutableArray.CreateBuilder<ReturnedLotLineReview>();
        foreach (var received in receipt["input"]["lines"].AsBsonArray) {
            var inspected = inspection["input"]["lines"].AsBsonArray.Single(line => line["receiptId"] == received["receiptId"] && line["skuId"] == received["skuId"]);
            var stock = receipt["source"]["stock"].AsBsonArray.Single(line => line["receiptId"] == received["receiptId"] && line["skuId"] == received["skuId"]);
            var amount = verified is null ? 0L : (V.Input(verified["input"].AsBsonDocument).Lines.SingleOrDefault(line => line.ReceiptId == received["receiptId"].AsInt64 && line.SkuId == received["skuId"].AsInt64)?.VerifiedBaseUnits ?? 0L);
            var eligible = inspected["identity"] == "matched" && inspected["condition"] == "sealed" && !inspected["observedLotLabel"].IsBsonNull &&
                received["receivedBaseUnits"].AsInt64 >= stock["baseUnitsPerPackage"].AsInt32;
            lines.Add(new(new(received["receiptId"].AsInt64), new(received["skuId"].AsInt64), received["receivedBaseUnits"].AsInt64, amount,
                received["receivedBaseUnits"].AsInt64 - amount, verified is not null ? "tetap_dikarantina" : eligible ? "menunggu_verifikasi_fisik" : "penanganan_manual",
                verified is not null ? "Hasil verifikasi tersimpan. Seluruh barang tetap dikarantina; jumlah yang belum diverifikasi memerlukan penanganan manual." :
                eligible ? "Pilih lot pengiriman asli yang sudah dicocokkan secara fisik. Verifikasi tidak melepas stok atau menyetujui pembayaran." :
                    "Identitas atau kemasan utuh belum cocok. Barang tetap dikarantina dan memerlukan penanganan manual.",
                !eligible || verified is not null ? [] : [.. stock["originalLots"].AsBsonArray.Select(original => new ReturnedLotChoice(new(original["lotId"].AsInt64),
                    new(original["poolId"].AsInt64), new(stock["definitionId"].AsInt64), stock["packageLabel"].AsString, stock["baseUnitCode"].AsString,
                    stock["baseUnitsPerPackage"].AsInt32, checked(original["dispatchedPackages"].AsInt64 * stock["baseUnitsPerPackage"].AsInt32)))]));
        }
        return new(new(receipt["caseId"].AsInt64), new(receipt["_id"].AsInt64), verified is null ? 0 : 1, V.Binding(receipt, inspection),
            verified is null ? "belum_diverifikasi" : "lot_asli_diverifikasi_tetap_dikarantina", lines.ToImmutable());
    }
    public async Task<ReturnedLotVerificationView> ReadLotVerificationAsync(ActorContext actor, long caseId, long returnId, CancellationToken ct = default)
    {
        if (caseId <= 0 || returnId <= 0) throw ReturnedLotVerificationPolicy.Invalid(); await StorageAsync(ct); await AfterSalesStockSourceStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token);
            var receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, Permissions.CaseManage, token);
            var inspection = await new ReturnInspectionRecords(database, clock).ReadAsync(tx, receipt, token) ?? throw R.Broken();
            var proof = await DecisionPeopleAsync(actor, tx, root, token);
            await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, receipt["source"].AsBsonDocument, clock.UtcNow, token, Permissions.StockCount);
            var saved = await new V(database, clock).ReadAsync(tx, receipt, inspection, token);
            await AuditReadAsync(actor, tx, "return-lot.read", returnId, token); await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, token);
            RequireLotReviewer(actor, authority, tx, root, receipt, proof); return LotView(receipt, inspection, saved);
        }, ct);
    }
    public async Task<BsonDocument> VerifyReturnedLotAsync(ActorContext actor, long caseId, long returnId, long revision, string binding,
        ReturnedLotVerificationInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        if (caseId <= 0 || returnId <= 0 || revision != 0 || binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch))) throw ReturnedLotVerificationPolicy.Invalid();
        input = ReturnedLotVerificationPolicy.Copy(input); AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.IdempotencyKey, 128)) throw ReturnedLotVerificationPolicy.Invalid();
        await StorageAsync(ct); await AfterSalesStockSourceStorage.RequireAsync(database, ct);
        var store = new V(database, clock); var body = V.Body(returnId, revision, binding, input); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        BsonDocument? root = null, receipt = null, inspection = null, fresh = null; AuthorizationSnapshot? current = null; IClientSessionHandle? session = null; PersonAuthorityProof? proof = null; Action? sourceCurrent = null;
        void Current() => RequireLotReviewer(actor, current ?? throw R.Broken(), session ?? throw R.Broken(), root ?? throw R.Broken(), receipt ?? throw R.Broken(), proof);
        return await authorization.ExecuteCommandAsync(actor, V.Operation, metadata, body, async (authority, tx, token) => {
            current = authority; session = tx; fresh = null; proof = null; sourceCurrent = null;
            root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token);
            receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, Permissions.CaseManage, token);
            inspection = await new ReturnInspectionRecords(database, clock).ReadAsync(tx, receipt, token) ?? throw R.Broken();
            Current(); var resources = PaymentDraftBson.Scopes(root["scopes"].AsBsonArray); var approval = new ApprovalRequest(1, 1, RequestedBy: root["requestedBy"].AsInt64);
            return new(ScopePolicy.Authorize(actor, authority, Permissions.CaseManage, resources[0], clock.UtcNow, approval),
                [.. resources.SelectMany(scope => ScopePolicy.RequiredNodes(scope, authority.Directory)).Distinct()], Permissions.CaseManage, new(resources[0], approval));
        }, async (tx, token) => {
            var returned = receipt ?? throw R.Broken(); var inspected = inspection ?? throw R.Broken(); var original = root ?? throw R.Broken();
            if (binding != V.Binding(returned, inspected) || await store.ReadAsync(tx, returned, inspected, token) is not null) throw AfterSalesDecisionRecords.Changed("return_lot_verification_changed");
            await invoices.CaseReturnFenceAsync(tx, original["source"].AsBsonDocument, token);
            await store.RequireCapacityAsync(tx, returned, inspected, input, token); proof = await DecisionPeopleAsync(actor, tx, original, token); var at = Now;
            if (inspected["inspectedAt"].ToUniversalTime() > at.UtcDateTime) throw R.Broken();
            var row = new BsonDocument { { "_id", returnId }, { "schemaVersion", 1 }, { "revision", 1L }, { "source", V.Source(returned, inspected) }, { "binding", binding },
                { "input", V.Input(input) }, { "state", "verified_original_lot_quarantined" }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "verifiedAt", at.UtcDateTime },
                { "people", new BsonArray(proof.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) }, { "requestKey", metadata.IdempotencyKey },
                { "bodyHash", hash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } }; row.Add("snapshotHash", R.Hash(row));
            await store.RecordAsync(tx, row, returned, inspected, token); fresh = row; return V.Result(row);
        }, ct, async (authority, tx, token) => {
            var original = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token);
            var returned = await InspectionReceiptAsync(actor, authority, tx, original, returnId, Permissions.CaseManage, token);
            var inspected = await new ReturnInspectionRecords(database, clock).ReadAsync(tx, returned, token) ?? throw R.Broken();
            var saved = await store.ReadAsync(tx, returned, inspected, token) ?? throw R.Broken();
            if (saved["actorId"] != actor.UserId || saved["requestKey"] != metadata.IdempotencyKey || saved["bodyHash"] != hash) throw R.Broken();
            await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, returned["source"].AsBsonDocument, clock.UtcNow, token, Permissions.StockCount);
            await FenceAsync(actor, authority, tx, original, Permissions.CaseManage, token);
            if (fresh is not null) sourceCurrent = await new AfterSalesStockSourceProducer(database, ids, clock).RecordFreshAsync(tx, actor, authority, authorization, "verifyLot", fresh, Current, token);
        }, validateCurrent: () => { Current(); sourceCurrent?.Invoke(); });
    }
    public async Task<BsonDocument?> RecoverLotVerificationAsync(ActorContext actor, long caseId, long returnId, string key, CancellationToken ct = default)
    {
        if (caseId <= 0 || returnId <= 0) throw ReturnedLotVerificationPolicy.Invalid(); AuthorizationStore.ValidateMetadata(new(key, "Recover original return lot verification", "return-lot-recovery"));
        await StorageAsync(ct); await AfterSalesStockSourceStorage.RequireAsync(database, ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseManage, token);
            var receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, Permissions.CaseManage, token);
            var inspection = await new ReturnInspectionRecords(database, clock).ReadAsync(tx, receipt, token) ?? throw R.Broken();
            var proof = await DecisionPeopleAsync(actor, tx, root, token); var store = new V(database, clock); var saved = await store.ReadAsync(tx, receipt, inspection, token);
            var own = await store.Rows(V.Collection).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "requestKey", key } }).SingleOrDefaultAsync(token);
            if (own is not null && (saved is null || own != saved)) throw R.Broken();
            if (own is null) foreach (var (table, field) in new[] { ("idempotencyRecords", "key"), ("auditEvents", "requestKey"), ("outboxEvents", "requestKey") })
                if (await store.Rows(table).Find(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", V.Operation }, { "actorId", actor.UserId }, { field, key } }).AnyAsync(token)) throw R.Broken();
            await AuditReadAsync(actor, tx, "return-lot.recover", returnId, token);
            await Q.FenceReceiverAsync(database, authorization, actor, authority, tx, receipt["source"].AsBsonDocument, clock.UtcNow, token, Permissions.StockCount);
            await FenceAsync(actor, authority, tx, root, Permissions.CaseManage, token); RequireLotReviewer(actor, authority, tx, root, receipt, proof);
            return own is null ? null : V.Result(own);
        }, ct);
    }
}
