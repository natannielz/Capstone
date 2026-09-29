using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentDraftBson;

namespace UnitToko.Platform.Billing;
internal sealed class PaymentDraftRecords(IMongoDatabase database)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<BsonDocument> VersionAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Versions).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & PaymentDraftSchemas.Validator(B.Versions)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var input = PaymentDraftPolicy.Copy(B.Input(row["input"].AsBsonDocument), AccountingCalendar.BusinessDate(ShipmentReceiptRecords.Time(row["recordedAt"])));
        var revision = row["revision"].AsInt64;
        if (!InvoiceRecords.Equal(row["input"], B.Input(input)) || row["snapshotHash"] != B.Hash(row) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(B.Body(revision == 1 ? null : row["declarationId"].AsInt64, revision - 1, input), row["reason"].AsString) ||
            !InvoiceRecords.Equal(new BsonArray(input.InvoiceIds.Select(value => new BsonInt64(value.Value))), new BsonArray(row["invoices"].AsBsonArray.Select(value => value["invoiceId"]))) ||
            !InvoiceRecords.Equal(row["party"], B.Party(row["invoices"].AsBsonArray)) || !InvoiceRecords.Equal(row["scopes"], B.Scopes(row["invoices"].AsBsonArray, row["recordedBy"].AsInt64))) throw B.Invalid();
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["recordedBy"] }, { "operation", B.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (audit is null || !InvoiceRecords.Equal(audit, B.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !InvoiceRecords.Equal(receipt.GetValue("result", BsonNull.Value), B.Result(row))) throw B.Invalid();
        return row;
    }
    internal async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long actorId, long id, CancellationToken ct) {
        var head = await Rows(B.Heads).Find(tx, Builders<BsonDocument>.Filter.And(new BsonDocument { { "_id", id }, { "recordedBy", actorId } }, PaymentDraftSchemas.Validator(B.Heads))).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var latest = await Rows(B.Versions).Find(tx, new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", -1)).FirstOrDefaultAsync(ct);
        var row = await VersionAsync(tx, head["versionId"].AsInt64, ct);
        if (latest is null || latest["_id"] != row["_id"] || row["revision"] != head["revision"] || row["declarationId"] != head["_id"] ||
            row["recordedBy"] != head["recordedBy"] || row["recordedAt"] != head["updatedAt"] || !InvoiceRecords.Equal(row["party"], head["party"]) ||
            !InvoiceRecords.Equal(row["scopes"], head["scopes"])) throw B.Invalid();
        // Every edit retains its original maker and previous immutable version. Gaps are
        // corruption, not permission to replace declaration history with a fresh head.
        long expected = 1; BsonValue previous = BsonNull.Value; BsonValue previousHash = BsonNull.Value; BsonValue? firstDate = null;
        using var versions = await Rows(B.Versions).Find(tx, new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", 1)).ToCursorAsync(ct);
        while (await versions.MoveNextAsync(ct)) foreach (var candidate in versions.Current) {
            var version = await VersionAsync(tx, candidate["_id"].AsInt64, ct);
            if (version["revision"] != expected++ || version["recordedBy"] != actorId || version["previousVersionId"] != previous || version["previousHash"] != previousHash ||
                !InvoiceRecords.Equal(version["party"], head["party"]) || firstDate is not null && ShipmentReceiptRecords.Time(version["recordedAt"]) < ShipmentReceiptRecords.Time(firstDate)) throw B.Invalid();
            if (firstDate is null && version["recordedAt"] != head["createdAt"]) throw B.Invalid();
            firstDate = version["recordedAt"]; previous = version["_id"]; previousHash = version["snapshotHash"];
        }
        if (expected - 1 != head["revision"] || previous != head["versionId"]) throw B.Invalid();
        return head;
    }
    internal async Task TouchAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        if ((await Rows(B.Heads).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Changed();
    }
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Changed() => new("payment_draft_changed", "Reload the declaration before saving");
}
