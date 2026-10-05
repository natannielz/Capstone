using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

internal sealed record FulfillmentStockSourceSnapshot(BsonDocument Head, long OpenCount);
internal sealed class FulfillmentStockSourceRecords(IMongoDatabase database)
{
    internal const string Heads = "fulfillmentStockSourceHeads", Transitions = "fulfillmentStockSourceTransitions";
    internal const int MaximumRows = 4096;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static DomainValidationException Unavailable() => new("fulfillment_source_obligation_unavailable",
        "Complete authenticated Fulfillment custody history is required before the source can be declared drained");
    internal static PersistenceConflictException Changed() => new("stock_source_revision_changed", "The reviewed stock source changed; reload it");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "transitionHash")));
    internal async Task<BsonDocument[]> BoundedAsync(string name, FilterDefinition<BsonDocument> filter, IClientSessionHandle tx, CancellationToken ct)
    {
        var rows = await Rows(name).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > MaximumRows) throw Unavailable(); return rows.ToArray();
    }
    internal async Task<(BsonDocument Head, BsonDocument[] History, Dictionary<long, bool> Open)> HistoryAsync(
        IClientSessionHandle tx, FulfillmentStockSourceEvents owner, long poolId, CancellationToken ct)
    {
        var repository = new InventoryRepository(database); repository.RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var head = await Rows(Heads).Find(tx, F.Eq("_id", poolId) & FulfillmentStockSourceSchemas.Validator(Heads)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        var history = await Rows(Transitions).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("revision", 1)).Limit(MaximumRows + 1).ToListAsync(ct);
        if (history.Count is < 1 or > MaximumRows || head["revision"].AsInt64 != history.Count) throw Unavailable();
        var open = new Dictionary<long, bool>(); var events = new HashSet<string>(); BsonDocument? previous = null; BsonValue? location = null;
        foreach (var row in history) {
            if (!await Rows(Transitions).Find(tx, F.Eq("_id", row["_id"]) & FulfillmentStockSourceSchemas.Validator(Transitions)).AnyAsync(ct) ||
                row["revision"].AsInt64 != (previous is null ? 1L : checked(previous["revision"].AsInt64 + 1)) ||
                row["previousId"] != (previous is null ? BsonNull.Value : previous["_id"]) ||
                row["previousHash"] != (previous is null ? BsonNull.Value : previous["transitionHash"]) || row["transitionHash"] != Hash(row) ||
                previous is not null && row["recordedAt"].CompareTo(previous["recordedAt"]) < 0) throw Unavailable();
            var kind = row["sourceKind"].AsString; var id = row["sourceId"].AsInt64;
            if (!events.Add(kind + ":" + id) || (previous is null) != (kind == "setup")) throw Unavailable();
            var source = await owner.ReadAsync(tx, kind, id, true, ct);
            if (!source.PoolIds.Contains(poolId) || row["sourceHash"] != source.Hash || row["sourceAuditId"] != source.Audit["_id"] ||
                row["actorId"] != source.Audit["actorId"] || row["actorGrantVersion"] != source.Audit["actorGrantVersion"] || row["recordedAt"] != source.Time)
                throw Unavailable();
            if (kind == "setup") {
                if (id != head["setupDecisionId"].AsInt64 || row["poolRevision"] != 1L || row["beforePoolFence"] != 0L || row["afterPoolFence"] != 0L ||
                    !row["shipmentId"].IsBsonNull || !row["beforeOpen"].IsBsonNull || !row["afterOpen"].IsBsonNull) throw Unavailable();
                location = source.Record["proposal"]["input"]["location"];
            } else {
                if (row["afterPoolFence"].AsInt64 != checked(row["beforePoolFence"].AsInt64 + 1) ||
                    row["poolRevision"].AsInt64 < previous!["poolRevision"].AsInt64 || row["beforePoolFence"].AsInt64 < previous["afterPoolFence"].AsInt64 ||
                    row["shipmentId"].IsBsonNull || row["shipmentId"].AsInt64 != source.ShipmentId || !source.Open.HasValue) throw Unavailable();
                var shipmentId = source.ShipmentId!.Value;
                if (kind == "dispatch") {
                    if (open.ContainsKey(shipmentId) || !row["beforeOpen"].IsBsonNull || row["afterOpen"] != true || source.Open != true) throw Unavailable();
                    open.Add(shipmentId, true);
                } else {
                    if (!open.TryGetValue(shipmentId, out var wasOpen) || !wasOpen || row["beforeOpen"] != true || row["afterOpen"] != source.Open.Value) throw Unavailable();
                    open[shipmentId] = source.Open.Value;
                }
            }
            previous = row;
        }
        if (previous is null || location is null || head["location"] != location || head["lastTransitionId"] != previous["_id"] ||
            head["lastHash"] != previous["transitionHash"] || head["updatedAt"] != previous["recordedAt"]) throw Unavailable();
        repository.RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw Unavailable();
        return (head, history.ToArray(), open);
    }
    internal async Task<FulfillmentStockSourceSnapshot> ReadAsync(IClientSessionHandle tx, FulfillmentStockSourceEvents owner,
        StockSourceObligationExpectation expected, CancellationToken ct)
    {
        var (head, history, open) = await HistoryAsync(tx, owner, expected.PoolId, ct);
        if (head["location"] != StockSourceSetupBson.Location(expected.Location)) throw Unavailable();
        var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", expected.PoolId)).SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        if (pool!["location"] != head["location"] || pool["revision"] != expected.PoolRevision || pool["fence"] != expected.PoolFence) throw Changed();
        if (pool["revision"].AsInt64 < history[^1]["poolRevision"].AsInt64 || pool["fence"].AsInt64 < history[^1]["afterPoolFence"].AsInt64) throw Unavailable();
        var (shipments, receipts) = await RequireSourcesAsync(tx, expected.PoolId, history, null, ct);
        if (open.Count != shipments.Length) throw Unavailable();
        foreach (var receipt in receipts)
            if (!open.TryGetValue(receipt["shipmentId"].AsInt64, out var value) || value != receipt["hasException"].AsBoolean) throw Unavailable();
        return new(head, open.LongCount(entry => entry.Value));
    }
    internal async Task<(BsonDocument[] Shipments, BsonDocument[] Receipts)> RequireSourcesAsync(IClientSessionHandle tx,
        long poolId, BsonDocument[] history, FulfillmentStockSourceEvent? pending, CancellationToken ct)
    {
        await RequireRetainedRootsAsync(tx, ct);
        var shipments = await BoundedAsync("shipments", F.Eq("stockEffects.allocation.poolId", poolId), tx, ct);
        var receipts = await BoundedAsync(ShipmentReceiptRecords.Receipts, F.In("shipmentId", shipments.Select(row => row["_id"])), tx, ct);
        var actual = new HashSet<string> { "setup:" + history[0]["sourceId"].AsInt64 };
        foreach (var row in shipments) actual.Add("dispatch:" + row["_id"].AsInt64);
        foreach (var row in receipts) actual.Add("receipt:" + row["_id"].AsInt64);
        var authenticated = history.Select(row => row["sourceKind"].AsString + ":" + row["sourceId"].AsInt64).ToHashSet();
        if (pending is not null && (!pending.PoolIds.Contains(poolId) || !authenticated.Add(pending.Kind + ":" + pending.Id))) throw Unavailable();
        if (!actual.SetEquals(authenticated)) throw Unavailable();
        foreach (var row in receipts)
            if (shipments.Count(shipment => shipment["_id"] == row["shipmentId"]) != 1) throw Unavailable();
        var ids = shipments.Select(row => row["_id"]).ToArray();
        var dispatchAudits = await BoundedAsync("auditEvents", F.Eq("operation", ShipmentDispatchBson.Operation) &
            (F.In("result.shipmentId", ids) | F.In("_id", shipments.Select(row => row["auditId"]))), tx, ct);
        var dispatchOutbox = await BoundedAsync("outboxEvents", F.Eq("kind", "fulfillment.shipment.dispatched") & F.In("payload.shipmentId", ids), tx, ct);
        var dispatchCommands = await BoundedAsync("idempotencyRecords", F.Eq("operation", ShipmentDispatchBson.Operation) & F.In("result.shipmentId", ids), tx, ct);
        if (dispatchAudits.Length != shipments.Length || dispatchOutbox.Length != shipments.Length ||
            dispatchCommands.Length != shipments.Length) throw Unavailable();
        var movements = await BoundedAsync("movements", F.Eq("source.namespace", "fulfillment.shipment") & F.Eq("source.eventKind", "dispatch") &
            (F.Eq("physicalPoolId", poolId) | F.Eq("source.occurrence", poolId)), tx, ct);
        if (movements.Length != shipments.Length || movements.Any(movement => movement["physicalPoolId"] != poolId ||
            movement["source"]["occurrence"] != poolId || shipments.Count(shipment => shipment["_id"] == movement["source"]["documentId"]) != 1))
            throw Unavailable();
        // Authenticate all reverse receipt effects for the exact pool membership, including missing
        // receipt roots. Historical challenge issuance/failure never substitutes for recipient consent.
        var audits = await BoundedAsync("auditEvents", F.Eq("operation", ShipmentReceiptRecords.Confirm) &
            (F.In("result.shipmentId", ids) | F.In("_id", receipts.Select(row => row["auditId"]))), tx, ct);
        var outbox = await BoundedAsync("outboxEvents", F.Eq("kind", ShipmentReceiptRecords.Confirm) & F.In("payload.shipmentId", ids), tx, ct);
        var commands = await BoundedAsync("idempotencyRecords", F.Eq("operation", ShipmentReceiptRecords.Confirm) & F.In("result.shipmentId", ids), tx, ct);
        if (audits.Length != receipts.Length || outbox.Length != receipts.Length || commands.Length != receipts.Length) throw Unavailable();
        return (shipments, receipts);
    }
    internal async Task RequireRetainedRootsAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        foreach (var (name, validator) in new[] { ("shipments", ShipmentDispatchSchemas.Validator()),
            (ShipmentReceiptRecords.Receipts, ShipmentReceiptSchemas.Validator(ShipmentReceiptRecords.Receipts)),
            (ShipmentReceiptRecords.Challenges, ShipmentReceiptSchemas.Validator(ShipmentReceiptRecords.Challenges)),
            (DeliveryAttemptRecords.Attempts, DeliveryAttemptSchemas.Validator(DeliveryAttemptRecords.Attempts)),
            (DeliveryAttemptRecords.Revocations, DeliveryAttemptSchemas.Validator(DeliveryAttemptRecords.Revocations)) })
            if (await Rows(name).Find(tx, new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Unavailable();
        foreach (var link in new[] { (Name: "shipments", Parent: "orders", Field: "orderId"),
            (Name: ShipmentReceiptRecords.Receipts, Parent: "shipments", Field: "shipmentId"),
            (Name: ShipmentReceiptRecords.Challenges, Parent: "shipments", Field: "shipmentId"),
            (Name: DeliveryAttemptRecords.Attempts, Parent: "shipments", Field: "shipmentId"),
            (Name: DeliveryAttemptRecords.Revocations, Parent: "shipments", Field: "shipmentId") }) {
            BsonDocument[] pipeline = [new("$lookup", new BsonDocument { { "from", link.Parent }, { "localField", link.Field },
                { "foreignField", "_id" }, { "as", "linkedOwner" } }), new("$match", new BsonDocument("linkedOwner", new BsonDocument("$size", 0))), new("$limit", 1)];
            if (await Rows(link.Name).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw Unavailable();
        }
        foreach (var (name, filter, field, parent) in new (string, BsonDocument, string, string)[] {
            (ShipmentReceiptRecords.Challenges, new("consumedReceiptId", new BsonDocument("$ne", BsonNull.Value)), "consumedReceiptId", ShipmentReceiptRecords.Receipts),
            ("movements", new() { { "source.namespace", "fulfillment.shipment" }, { "source.eventKind", "dispatch" } }, "source.documentId", "shipments") }) {
            BsonDocument[] pipeline = [new("$match", filter), new("$lookup", new BsonDocument { { "from", parent },
                { "localField", field }, { "foreignField", "_id" }, { "as", "linkedOwner" } }),
                new("$match", new BsonDocument("linkedOwner", new BsonDocument("$size", 0))), new("$limit", 1)];
            if (await Rows(name).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw Unavailable();
        }
        foreach (var (name, operation, field, parent) in new[] {
            ("auditEvents", ShipmentDispatchBson.Operation, "result.shipmentId", "shipments"),
            ("idempotencyRecords", ShipmentDispatchBson.Operation, "result.shipmentId", "shipments"),
            ("outboxEvents", "fulfillment.shipment.dispatched", "payload.shipmentId", "shipments"),
            ("auditEvents", ShipmentReceiptRecords.Confirm, "result.receiptId", ShipmentReceiptRecords.Receipts),
            ("idempotencyRecords", ShipmentReceiptRecords.Confirm, "result.receiptId", ShipmentReceiptRecords.Receipts),
            ("outboxEvents", ShipmentReceiptRecords.Confirm, "payload.receiptId", ShipmentReceiptRecords.Receipts) }) {
            var match = new BsonDocument(name == "outboxEvents" ? "kind" : "operation", operation);
            BsonDocument[] pipeline = [new("$match", match),
                new("$lookup", new BsonDocument { { "from", parent }, { "localField", field }, { "foreignField", "_id" }, { "as", "linkedOwner" } }),
                new("$match", new BsonDocument("linkedOwner", new BsonDocument("$size", 0))), new("$limit", 1)];
            if (await Rows(name).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw Unavailable();
        }
    }
}
