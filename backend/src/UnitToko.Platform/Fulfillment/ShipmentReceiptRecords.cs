using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

internal sealed class ShipmentReceiptRecords(IMongoDatabase database)
{
    internal const string Challenges = "shipmentReceiptChallenges", Receipts = "shipmentReceipts";
    internal const string Issue = "fulfillment.receipt.challenge.issue", Confirm = "fulfillment.receipt.confirm";
    internal IMongoCollection<BsonDocument> Rows(string collection) => database.GetCollection<BsonDocument>(collection);
    internal async Task StorageAsync(CancellationToken ct) {
        foreach (var name in ShipmentReceiptSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, ShipmentReceiptSchemas.Validator(name), ShipmentReceiptSchemas.Key(name), ct);
    }
    internal static BsonDocument Fixed(BsonDocument row, string op) {
        var result = row.DeepClone().AsBsonDocument;
        if (op == Issue) { result.Remove("failedAttempts"); result.Remove("consumedReceiptId"); result.Remove("consumedAt"); }
        return result;
    }
    internal static BsonDocument Result(BsonDocument row, string op) => new() { { op == Issue ? "challengeId" : "receiptId", row["_id"] }, { "shipmentId", row["shipmentId"] }, { "documentId", row["documentId"] } };
    internal static BsonDocument Audit(BsonDocument row, string op) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", op }, { "operation", op },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", CatalogRepository.Digest(Fixed(row, op)) }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row, op) } };
    internal async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, string op, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, Audit(row, op), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", op },
            { "sourceKey", op + ":" + row["_id"].AsInt64 }, { "payload", Result(row, op) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    internal async Task VerifyEffectsAsync(IClientSessionHandle tx, BsonDocument row, string op, CancellationToken ct) {
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", op }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogSchemas.SemanticEquals(audit, Audit(row, op)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !CatalogSchemas.SemanticEquals(receipt.GetValue("result", BsonNull.Value), Result(row, op))) throw Invalid();
    }
    internal async Task<BsonDocument?> FindReceiptAsync(IClientSessionHandle tx, long shipmentId, CancellationToken ct) {
        var row = await Rows(Receipts).Find(tx, new BsonDocument("shipmentId", shipmentId)).SingleOrDefaultAsync(ct);
        if (row is not null) return row;
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("auditEvents", new() { { "operation", Confirm }, { "result.shipmentId", shipmentId } }),
            ("idempotencyRecords", new() { { "operation", Confirm }, { "result.shipmentId", shipmentId } }),
            ("outboxEvents", new() { { "kind", Confirm }, { "payload.shipmentId", shipmentId } }),
            (Challenges, new() { { "shipmentId", shipmentId }, { "consumedReceiptId", new BsonDocument("$ne", BsonNull.Value) } })
        }) if (await Rows(name).Find(tx, filter).Limit(1).AnyAsync(ct)) throw Invalid();
        return null;
    }
    internal static ImmutableArray<ReceiptDispatchedLine> Dispatched(BsonDocument source) => source["lines"].AsBsonArray.Select(value => {
        var row = value.AsBsonDocument; var definition = row["definition"].AsBsonDocument;
        return new ReceiptDispatchedLine(new(row["skuId"].AsInt64), new(definition["_id"].AsInt64), row["packages"].AsInt32, definition["baseUnitsPerPackage"].AsInt32);
    }).ToImmutableArray();
    internal static BsonDocument Amount(ReceiptPackageAmount amount) => new() { { "packages", amount.Packages }, { "looseUnits", amount.LooseUnits } };
    internal static ReceiptPackageAmount Amount(BsonValue amount) => new(amount["packages"].AsInt32, amount["looseUnits"].AsInt32);
    internal static BsonArray Lines(ImmutableArray<ReceiptConfirmedLine> lines) => new(lines.Select(line => new BsonDocument {
        { "skuId", line.Declaration.SkuId.Value }, { "definitionId", line.DefinitionId.Value }, { "dispatchedBaseUnits", line.DispatchedBaseUnits },
        { "accepted", Amount(line.Declaration.Accepted) }, { "rejected", Amount(line.Declaration.Rejected) }, { "missing", Amount(line.Declaration.Missing) },
        { "acceptedBaseUnits", line.AcceptedBaseUnits }, { "rejectedBaseUnits", line.RejectedBaseUnits }, { "missingBaseUnits", line.MissingBaseUnits },
        { "exceptionReason", (BsonValue?)line.Declaration.ExceptionReason ?? BsonNull.Value }
    }));
    internal static ImmutableArray<ReceiptLineDeclaration> Declarations(BsonArray lines) => lines.Select(line => new ReceiptLineDeclaration(new(line["skuId"].AsInt64),
        Amount(line["accepted"]), Amount(line["rejected"]), Amount(line["missing"]), line["exceptionReason"].IsBsonNull ? null : line["exceptionReason"].AsString)).ToImmutableArray();
    internal static DateTimeOffset Time(BsonValue value) => DateTimeOffset.FromUnixTimeMilliseconds(value.AsBsonDateTime.MillisecondsSinceEpoch);
    internal static AuthorizationDeniedException Invalid() => new("shipment_receipt_unavailable");
}
