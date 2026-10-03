using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementStockReceiptService
{
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, ProcurementStockReceiptInput input, CancellationToken ct = default) {
        input = ProcurementStockReceiptRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await PrepareAsync(actor, authority, tx, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "source", B.SourceView(context.Order!.Source, context.Inspection!, input.InspectionLineNumber) },
                { "pool", StockSourceSetupService.HeldReceiptPoolView(context.Pool!.Source) }, { "capacity", context.Capacity!.Capacity },
                { "binding", B.Binding(actor.UserId, actor.GrantVersion, input, context.Order.Source, context.Inspection!, context.Pool.Source, context.Capacity.Capacity) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ReceiveAsync(ActorContext actor, ProcurementStockReceiptInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementStockReceiptRules.Copy(input); Binding(binding); AuthorizationStore.ValidateMetadata(metadata); Key(metadata.IdempotencyKey);
        ProcurementStockReceiptRules.Text(metadata.CorrelationId, 128); if (metadata.Reason != input.Reason) throw ProcurementStockReceiptRules.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Receipts, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var lotId = (await ids.NextAsync("lots", ct)).Value; var balanceId = (await ids.NextAsync("stockBalances", ct)).Value; var movementId = (await ids.NextAsync("movements", ct)).Value;
        var body = B.Body(input, binding); Context? context = null;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, token) => {
            _ = Query(actor, authority, Permissions.StockReceive); var existing = await records.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token);
            if (existing is not null) context = await ReceiptAsync(actor, authority, tx, existing["_id"].AsInt64, Permissions.StockReceive, token);
            else {
                context = await PrepareAsync(actor, authority, tx, input, token);
                ProcurementStockReceiptRecords.Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Order!.Source, context.Inspection!, context.Pool!.Source, context.Capacity!.Capacity));
            }
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var capacity = active.Capacity ?? throw B.Broken(); var before = capacity.Capacity;
            var headId = capacity.Head?["_id"].AsInt64 ?? (await ids.NextAsync(Q.PhysicalHeads, token)).Value;
            var allocationId = (await ids.NextAsync(Q.PhysicalAllocations, token)).Value;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "receiptId", id }, { "revision", 1L }, { "location", R.Location(input.Location) },
                { "orderId", input.OrderId }, { "supplierId", active.Order!.Source["proposal"]["input"]["supplierId"] }, { "inspectionId", input.InspectionId }, { "inspectionLineNumber", input.InspectionLineNumber },
                { "poolId", input.PoolId }, { "receiptReference", input.ReceiptReference }, { "input", B.Input(input) }, { "source", active.Order.Source.DeepClone() },
                { "inspection", active.Inspection!.DeepClone() }, { "poolSource", active.Pool!.Source.DeepClone() }, { "before", before.DeepClone() }, { "after", Q.After(before, headId, input.Packages) },
                { "disposition", new BsonDocument { { "allocationId", allocationId }, { "headId", headId }, { "previousAllocationId", capacity.Head?["lastAllocationId"] ?? BsonNull.Value }, { "previousHash", capacity.Head?["lastHash"] ?? BsonNull.Value } } },
                { "lotId", lotId }, { "balanceId", balanceId }, { "movementId", movementId }, { "binding", binding },
                { "people", new BsonArray(active.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) }, { "valuationState", "pending" }, { "valuation", BsonNull.Value }, { "state", B.State } };
            R.Metadata(row, B.Operation, audit, actor, metadata, body, Now);
            await Rows(B.Receipts).InsertOneAsync(tx, row, cancellationToken: token);
            var allocation = B.Allocation(row); var head = ProcurementQuarantineDisposition.Head(capacity, allocation);
            await Rows(Q.PhysicalAllocations).InsertOneAsync(tx, allocation, cancellationToken: token);
            if (capacity.Head is null) await Rows(Q.PhysicalHeads).InsertOneAsync(tx, head, cancellationToken: token);
            else if ((await Rows(Q.PhysicalHeads).ReplaceOneAsync(tx, capacity.Head, head, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            active.Fences.RemoveAll(value => value.Table == Q.PhysicalHeads && value.Row["_id"] == head["_id"]); AddFence(active, Q.PhysicalHeads, head);
            var proof = new ProcurementStockReceiptWriteProof(row, active.Pool, actor, active.Authority, tx, () => Current(active));
            await new InventoryHeldReceiptWriter(database).WriteAsync(tx, actor, proof, token);
            await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", B.Operation }, { "sourceKey", B.EffectKey(row) },
                { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "orderId", row["orderId"] }, { "payload", B.Result(row) },
                { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            active.Receipt = row; return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var actual = await records.ReadAsync(tx, active.Receipt!["_id"].AsInt64, token);
            ProcurementStockReceiptRecords.Equal(actual["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            // The generic command receipt is complete here; reconstruct both physical owners before final authority fencing.
            await InspectionAsync(active, input.InspectionId, input.InspectionLineNumber, token); await records.OrphansAsync(tx, token); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
}
