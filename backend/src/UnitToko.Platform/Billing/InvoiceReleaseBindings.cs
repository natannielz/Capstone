using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

internal sealed partial class InvoiceReleaseRecords
{
    internal static async Task<bool> BindingRequiredAsync(IMongoDatabase db, IClientSessionHandle tx, CancellationToken ct) {
        var history = await db.GetCollection<BsonDocument>("schemaMigrations").Find(tx, new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, CollectionMigrations.CurrentVersion) || history.Count < 29) throw B.Broken();
        if (history.Count >= InvoiceReleaseMigration.Number) return true;
        // Earlier real migrations still verify their legacy invoice anchors. This
        // compatibility path requires the complete predecessor prefix and no trace
        // of release storage. Missing a new sidecar never activates a runtime fallback.
        foreach (var name in InvoiceReleaseSchemas.Collections)
            if (await AccountContactStorage.DefinitionAsync(db, name, ct) is not null) throw B.Broken();
        return false;
    }
    internal static async Task<BsonDocument> BindingAsync(IMongoDatabase db, IClientSessionHandle tx, BsonDocument invoice,
        InvoiceReleaseHistory? history, CancellationToken ct)
    {
        var receipt = await db.GetCollection<BsonDocument>("idempotencyRecords").Find(tx, new BsonDocument {
            { "actorId", invoice["issuedBy"] }, { "operation", InvoiceBson.Operation }, { "key", invoice["requestKey"] }
        }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var audit = await db.GetCollection<BsonDocument>("auditEvents").Find(tx, new BsonDocument("_id", invoice["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId ||
            receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].CompareTo(invoice["issuedAt"]) > 0) throw B.Broken();
        Equal(receipt["bodyHash"], invoice["bodyHash"]); Equal(receipt["result"], InvoiceBson.Result(invoice)); Equal(audit, InvoiceBson.Audit(invoice));
        BsonDocument? decision = null, proposal = null;
        if (history is not null) {
            var reference = invoice["terms"]["policy"];
            Equal(reference["id"], history.Head["_id"]);
            decision = history.Events.SingleOrDefault(row => row["revision"] == reference["revision"]) ?? throw B.Broken();
            if (decision["action"] != "approve") throw B.Broken();
            proposal = history.Events.SingleOrDefault(row => row["_id"] == decision["proposalId"]) ?? throw B.Broken();
            var input = B.Input(proposal["input"]); var issuedAt = B.Time(invoice["issuedAt"]);
            if (B.Time(decision["recordedAt"]) > issuedAt || issuedAt < input.ValidFrom || issuedAt >= input.ValidUntil) throw B.Broken();
            Equal(invoice["terms"], InvoiceBson.Terms(InvoiceReleaseSources.Terms(history.Head["_id"].AsInt64, decision["revision"].AsInt64, input, proposal["source"].AsBsonDocument)));
            Equal(invoice["sources"], proposal["source"]["receipts"]); Equal(B.Selected(InvoiceBson.Selected(invoice)), proposal["input"]["receipts"]);
            var prepared = proposal["source"]["lines"].AsBsonArray;
            if (prepared.Count != invoice["lines"].AsBsonArray.Count) throw B.Broken();
            foreach (var line in invoice["lines"].AsBsonArray) {
                var expected = prepared.SingleOrDefault(row => row["receiptId"] == line["receiptId"] && Ordering.OrderLineBson.Same(row, line)) ?? throw B.Broken();
                foreach (var field in new[] { "orderId", "shipmentId", "receiptId", "skuId", "skuCode", "productName", "definition", "price", "acceptedBaseUnits", "previousBaseUnits", "previousNet", "net", "tax", "charges" }) Equal(line[field], expected[field]);
            }
        }
        var row = new BsonDocument { { "_id", invoice["_id"] }, { "schemaVersion", 1 }, { "kind", history is null ? "pre_release" : "approved" },
            { "migrationVersion", InvoiceReleaseMigration.Number }, { "invoiceId", invoice["_id"] }, { "invoiceHash", invoice["snapshotHash"] }, { "termsHash", B.Hash(invoice["terms"].AsBsonDocument) },
            { "receiptId", receipt["_id"] }, { "receiptHash", B.Hash(receipt) }, { "auditId", audit["_id"] }, { "auditHash", B.Hash(audit) },
            { "releaseId", history?.Head["_id"] ?? BsonNull.Value }, { "releaseRevision", decision?["revision"] ?? BsonNull.Value },
            { "proposalId", proposal?["_id"] ?? BsonNull.Value }, { "decisionId", decision?["_id"] ?? BsonNull.Value },
            { "proposalHash", proposal?["recordHash"] ?? BsonNull.Value }, { "decisionHash", decision?["recordHash"] ?? BsonNull.Value } };
        row.Add("recordHash", B.Hash(row)); return row;
    }
    internal async Task BindInvoiceAsync(IClientSessionHandle tx, BsonDocument invoice, InvoiceReleaseHistory history, CancellationToken ct)
    {
        if (history.Latest["action"] != "approve" || invoice["terms"]["policy"]["revision"] != history.Latest["revision"]) throw B.Changed();
        var current = await ReadAsync(tx, history.Head["_id"].AsInt64, ct); Equal(B.Hash(current.Head), B.Hash(history.Head));
        var binding = await BindingAsync(database, tx, invoice, current, ct);
        if (await Rows(B.Bindings).Find(tx, new BsonDocument("_id", invoice["_id"])).AnyAsync(ct)) throw B.Broken();
        await Rows(B.Bindings).InsertOneAsync(tx, binding, cancellationToken: ct);
    }
    internal async Task<InvoiceReleaseHistory?> VerifyInvoiceBindingAsync(IClientSessionHandle tx, BsonDocument invoice, CancellationToken ct)
    {
        var row = await ExactAsync(tx, B.Bindings, invoice["_id"], ct); Equal(row["recordHash"], B.Hash(row));
        InvoiceReleaseHistory? history = null;
        if (row["kind"] == "approved") history = await ReadAsync(tx, row["releaseId"].AsInt64, ct);
        else if (row["kind"] != "pre_release") throw B.Broken();
        Equal(row, await BindingAsync(database, tx, invoice, history, ct));
        if (row["kind"] == "pre_release") {
            // A closed migration manifest records the exact predecessor invoice set.
            // It is never populated by runtime issuance or repaired from new input.
            var migration = await Rows(B.Anchors).Find(tx, new BsonDocument { { "_id", invoice["_id"] },
                { "invoiceHash", invoice["snapshotHash"] }, { "bindingHash", row["recordHash"] }, { "migrationVersion", InvoiceReleaseMigration.Number } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (migration.ElementCount != 5 || migration.GetValue("schemaVersion", 0) != 1) throw B.Broken();
            await FenceFrozenAsync(database, tx, B.Anchors, migration, ct);
        }
        await FenceFrozenAsync(database, tx, B.Bindings, row, ct);
        return history;
    }
}
