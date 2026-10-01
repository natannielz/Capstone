using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;
internal sealed class AfterSalesCaseRecords(IMongoDatabase database)
{
    internal const string Collection = "afterSalesCases", Operation = "fulfillment.case.request";
    internal static AuthorizationDeniedException Broken() => new("after_sales_case_unavailable");
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static string Hash(BsonDocument value) => CatalogRepository.Digest(new BsonDocument(value.Where(field => field.Name is not ("snapshotHash" or "fence"))));
    internal static string Binding(BsonDocument source) => CatalogRepository.Digest(source);
    internal static BsonDocument Input(AfterSalesCaseInput value) => new() { { "invoiceId", value.InvoiceId }, { "binding", value.Binding }, { "issue", value.Issue },
        { "lines", new BsonArray(value.Lines.Select(line => new BsonDocument { { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "reportedBaseUnits", line.ReportedBaseUnits } })) } };
    internal static AfterSalesCaseInput Input(BsonDocument value) => AfterSalesCasePolicy.Copy(new(value["invoiceId"].AsInt64, value["binding"].AsString, value["issue"].AsString,
        [.. value["lines"].AsBsonArray.Select(line => new AfterSalesCaseLine(line["receiptId"].AsInt64, line["skuId"].AsInt64, line["reportedBaseUnits"].AsInt64))]));
    internal static BsonDocument Result(BsonDocument row) => new() { { "caseId", row["_id"] }, { "invoiceId", row["source"]["invoiceId"] }, { "revision", row["revision"] },
        { "requestedAt", row["requestedAt"] }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["requestedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "reason", row["reason"] }, { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["requestedAt"] }, { "result", Result(row) } };
    internal static void RequireSource(AfterSalesCaseInput input, BsonDocument source) {
        if (input.InvoiceId != source["invoiceId"].AsInt64 || input.Binding != Binding(source)) throw new PersistenceConflictException("after_sales_source_changed", "Reload the finalized sale before requesting a case");
        foreach (var reported in input.Lines) {
            var accepted = source["lines"].AsBsonArray.Where(line => line["receiptId"] == reported.ReceiptId && line["skuId"] == reported.SkuId).ToArray();
            if (accepted.Length != 1 || reported.ReportedBaseUnits > accepted[0]["acceptedBaseUnits"].AsInt64) throw AfterSalesCasePolicy.Invalid();
        }
    }
    internal async Task<BsonDocument> ReadAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(Collection).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & AfterSalesCaseSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw Broken();
        var input = Input(row["input"].AsBsonDocument); RequireSource(input, row["source"].AsBsonDocument);
        if (!InvoiceRecords.Equal(row["input"], Input(input)) || row["snapshotHash"] != Hash(row) || row["bodyHash"] != AuthorizationStore.CommandHash(input, row["reason"].AsString) ||
            row["requestedAt"].CompareTo(row["source"]["issuedAt"]) < 0 || !InvoiceRecords.Equal(row["scopes"], PaymentDraftBson.Scopes(new BsonArray { row["source"] }, row["requestedBy"].AsInt64))) throw Broken();
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["requestedBy"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + id)).SingleOrDefaultAsync(ct);
        if (audit is null || !InvoiceRecords.Equal(audit, Audit(row)) || receipt is null || receipt.ElementCount != 8 || receipt["schemaVersion"] != 1 || !receipt["_id"].IsObjectId ||
            !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].CompareTo(row["requestedAt"]) > 0 || receipt["bodyHash"] != row["bodyHash"] || !InvoiceRecords.Equal(receipt["result"], Result(row)) ||
            effect is null || effect["kind"] != Operation || effect["actorId"] != row["requestedBy"] || effect["requestKey"] != row["requestKey"] ||
            effect["createdAt"] != row["requestedAt"] || !InvoiceRecords.Equal(effect["payload"], Result(row))) throw Broken();
        return row;
    }
    internal async Task TouchAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Broken();
        var touched = await Rows(Collection).UpdateOneAsync(tx, new BsonDocument { { "_id", row["_id"] }, { "snapshotHash", row["snapshotHash"] }, { "fence", new BsonDocument("$lt", long.MaxValue - 1) } },
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct); if (touched.ModifiedCount != 1) throw Broken();
    }
}
