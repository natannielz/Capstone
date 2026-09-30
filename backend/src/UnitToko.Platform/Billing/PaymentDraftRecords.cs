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
        var row = await Rows(B.Versions).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & PaymentReplacementSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var input = PaymentDraftPolicy.Copy(B.Input(row["input"].AsBsonDocument), AccountingCalendar.BusinessDate(ShipmentReceiptRecords.Time(row["recordedAt"])));
        var revision = row["revision"].AsInt64;
        if (!InvoiceRecords.Equal(row["input"], B.Input(input)) || row["snapshotHash"] != B.Hash(row) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(B.CommandBody(row), row["reason"].AsString) ||
            !InvoiceRecords.Equal(new BsonArray(input.InvoiceIds.Select(value => new BsonInt64(value.Value))), new BsonArray(row["invoices"].AsBsonArray.Select(value => value["invoiceId"]))) ||
            !InvoiceRecords.Equal(row["party"], B.Party(row["invoices"].AsBsonArray)) || !InvoiceRecords.Equal(row["scopes"], B.Scopes(row["invoices"].AsBsonArray, row["recordedBy"].AsInt64))) throw B.Invalid();
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["recordedBy"] }, { "operation", B.CommandOperation(row) }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (audit is null || !InvoiceRecords.Equal(audit, B.Audit(row)) || receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
            !InvoiceRecords.Equal(receipt.GetValue("result", BsonNull.Value), B.Result(row))) throw B.Invalid();
        if (row["schemaVersion"] == 2) {
            var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.ReplacementOperation + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
            if (effect["kind"] != B.ReplacementOperation || effect["actorId"] != row["recordedBy"] || effect["requestKey"] != row["requestKey"] ||
                effect["createdAt"] != row["recordedAt"] || !InvoiceRecords.Equal(effect["payload"], B.Result(row))) throw B.Invalid();
            await RequireReplacementAsync(tx, row, ct);
        }
        return row;
    }
    private async Task RequireReplacementAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var link = row["replacement"].AsBsonDocument; var source = link["source"].AsBsonDocument; var maker = row["recordedBy"].AsInt64;
        // ponytail: validate the retained parent chain; use verified checkpoints if measured replacement history becomes expensive.
        // IDs strictly decrease along parent links, so damaged data cannot create a recursive cycle.
        if (source["declarationId"].AsInt64 >= row["declarationId"].AsInt64 || source["recordedBy"] != maker || source["uploaderId"] != maker ||
            !InvoiceRecords.Equal(row["input"], source["input"]) || !InvoiceRecords.Equal(row["invoices"], source["invoices"]) ||
            !InvoiceRecords.Equal(row["party"], source["party"]) || !InvoiceRecords.Equal(row["scopes"], source["scopes"])) throw B.Invalid();
        var parent = await HeadAsync(tx, maker, source["declarationId"].AsInt64, ct);
        if (parent["state"] != "submitted") throw B.Invalid();
        var version = await VersionAsync(tx, parent["versionId"].AsInt64, ct);
        var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, parent["submissionId"].AsInt64, ct);
        if (!InvoiceRecords.Equal(source, B.SubmittedSource(parent, version, submitted))) throw B.Invalid();
        var action = await new PaymentReviewActionRecords(database).LatestAsync(tx, parent["_id"].AsInt64, source, ct) ?? throw B.Invalid();
        if (action["state"] != "replacement_requested" || action["revision"] != link["reviewRevision"] || action["_id"] != link["actionId"] || action["snapshotHash"] != link["actionHash"] ||
            link["binding"] != PaymentReviewActionRecords.Binding(source, action["revision"].AsInt64) || row["recordedAt"].CompareTo(action["recordedAt"]) < 0) throw B.Invalid();
        await new PaymentVerificationRecords(database).RequireUnverifiedAsync(tx, parent["_id"].AsInt64, ct);
    }
    internal async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long actorId, long id, CancellationToken ct) {
        var head = await Rows(B.Heads).Find(tx, Builders<BsonDocument>.Filter.And(new BsonDocument { { "_id", id }, { "recordedBy", actorId } }, PaymentSubmissionSchemas.DeclarationValidator())).SingleOrDefaultAsync(ct) ?? throw B.Invalid();
        var latest = await Rows(B.Versions).Find(tx, new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", -1)).FirstOrDefaultAsync(ct);
        var row = await VersionAsync(tx, head["versionId"].AsInt64, ct);
        if (latest is null || latest["_id"] != row["_id"] || row["revision"] != head["revision"] || row["declarationId"] != head["_id"] ||
            row["recordedBy"] != head["recordedBy"] || head["state"] == "draft" && row["recordedAt"] != head["updatedAt"] || !InvoiceRecords.Equal(row["party"], head["party"]) ||
            !InvoiceRecords.Equal(row["scopes"], head["scopes"])) throw B.Invalid();
        // Every edit retains its original maker and previous immutable version. Gaps are
        // corruption, not permission to replace declaration history with a fresh head.
        long expected = 1; BsonValue previous = BsonNull.Value; BsonValue previousHash = BsonNull.Value; BsonValue? firstDate = null; BsonValue? replacementInvoices = null;
        using var versions = await Rows(B.Versions).Find(tx, new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", 1)).ToCursorAsync(ct);
        while (await versions.MoveNextAsync(ct)) foreach (var candidate in versions.Current) {
            var version = await VersionAsync(tx, candidate["_id"].AsInt64, ct);
            if (version["schemaVersion"] == 2) replacementInvoices = version["replacement"]["source"]["input"]["invoiceIds"];
            if (replacementInvoices is not null && !InvoiceRecords.Equal(version["input"]["invoiceIds"], replacementInvoices)) throw B.Invalid();
            if (version["revision"] != expected++ || version["recordedBy"] != actorId || version["previousVersionId"] != previous || version["previousHash"] != previousHash ||
                !InvoiceRecords.Equal(version["party"], head["party"]) || firstDate is not null && ShipmentReceiptRecords.Time(version["recordedAt"]) < ShipmentReceiptRecords.Time(firstDate)) throw B.Invalid();
            if (firstDate is null && version["recordedAt"] != head["createdAt"]) throw B.Invalid();
            firstDate = version["recordedAt"]; previous = version["_id"]; previousHash = version["snapshotHash"];
        }
        if (expected - 1 != head["revision"] || previous != head["versionId"]) throw B.Invalid();
        await new PaymentSubmissionRecords(database).RequireHeadAsync(tx, head, ct);
        return head;
    }
    internal async Task TouchAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        if ((await Rows(B.Heads).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Changed();
    }
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Changed() => new("payment_draft_changed", "Reload the declaration before saving");
}
