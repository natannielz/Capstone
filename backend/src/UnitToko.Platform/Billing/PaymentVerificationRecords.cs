using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentVerificationRecords(IMongoDatabase database)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue left, BsonValue right) { if (!left.Equals(right)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await Rows(name).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), PaymentVerificationSchemas.Validator(name) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static void PolicyAt(BsonDocument row, DateTimeOffset now) {
        foreach (var name in new[] { "payerVerificationProcedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(row[name].AsString, 256)) throw B.Broken();
        if (!row["active"].AsBoolean || row["approvedAt"].ToUniversalTime() > now.UtcDateTime || row["expiresAt"].ToUniversalTime() <= now.UtcDateTime) throw B.Changed("payment_verification_policy_unavailable");
    }
    internal static void SameParty(BsonDocument source, BankReceiptPaymentSource bank) {
        foreach (var field in new[] { "fulfillmentOrganizationId", "shopId", "channelId" }) Equal(source["party"][field], bank.Receipt["scope"][field]);
        if (!bank.Receipt["scope"]["warehouseId"].IsBsonNull) throw B.Broken();
    }
    internal static void Facts(BsonDocument source, BankReceiptPaymentSource bank, BsonDocument review) {
        SameParty(source, bank);
        var input = PaymentDraftBson.Input(source["input"].AsBsonDocument); var fields = PaymentCorrectionBson.Fields(review["fields"].AsBsonArray);
        PaymentVerificationPolicy.RequireExactFunds(input, InvoiceBson.Money(bank.Receipt["amount"]), fields, bank.Account["details"]["accountNumber"].AsString);
    }
    internal static System.Collections.Immutable.ImmutableArray<PaymentDiscrepancy> Discrepancies(BsonDocument source, BankReceiptPaymentSource bank, BsonDocument review) =>
        PaymentVerificationPolicy.Discrepancies(PaymentDraftBson.Input(source["input"].AsBsonDocument), PaymentCorrectionBson.Fields(review["fields"].AsBsonArray),
            bank.Receipt["bookedDate"].AsString, bank.Account["details"]["holderName"].AsString, bank.Account["details"]["bankCode"].AsString,
            bank.BankRow["reference"].IsBsonNull ? null : bank.BankRow["reference"].AsString);
    internal async Task RequireUnverifiedAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        if (await Rows(B.Decisions).Find(tx, new BsonDocument("declarationId", id)).AnyAsync(ct)) throw B.Changed("payment_already_verified");
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "operation", B.Operation }, { "result.declarationId", id } }),
            ("auditEvents", new() { { "kind", B.Operation }, { "result.declarationId", id } }),
            ("outboxEvents", new() { { "kind", B.Operation }, { "payload.declarationId", id } })
        }) if (await Rows(name).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return await RawAsync(tx, row["_id"].AsInt64, ct);
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "actorId", actor }, { "operation", B.Operation }, { "key", key } }),
            ("auditEvents", new() { { "actorId", actor }, { "kind", B.Operation }, { "requestKey", key } }),
            ("outboxEvents", new() { { "actorId", actor }, { "kind", B.Operation }, { "requestKey", key } })
        }) if (await Rows(name).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    internal async Task<BsonDocument> RawAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Equal(row["snapshotHash"], B.Hash(row));
        var input = PaymentVerificationPolicy.Copy(B.Input(row));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.Body(row["declarationId"].AsInt64, row["receiptId"].AsInt64, input), row["reason"].AsString));
        Equal(row["declarationId"], row["source"]["declarationId"]);
        Equal(row["source"]["recordedBy"], row["source"]["uploaderId"]);
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Select(p => p.UserId).Distinct().Count() != 2) throw B.Broken();
        var reviewer = people.SingleOrDefault(p => p.UserId == row["actorId"].AsInt64) ?? throw B.Broken();
        var maker = people.SingleOrDefault(p => p.UserId == row["source"]["recordedBy"].AsInt64) ?? throw B.Broken();
        if (!PersonIndependencePolicy.AreIndependent(reviewer, maker)) throw B.Broken();
        PolicyAt(row["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", B.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var revision = row["source"]["revision"].AsInt64;
        Equal(receipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.PaymentVerify, new(revision, revision, RecordedBy: maker.UserId)));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"], B.Operation); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]);
        Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]); return row;
    }
    internal async Task<BsonDocument?> PartyAsync(IClientSessionHandle tx, BankReceiptPaymentSource bank, BsonDocument source, CancellationToken ct) {
        var id = bank.Receipt["_id"].AsInt64;
        var raw = await Rows(B.Parties).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        if (raw is null) {
            if (await Rows(B.Decisions).Find(tx, new BsonDocument("receiptId", id)).AnyAsync(ct)) throw B.Broken(); return null;
        }
        var row = await RowAsync(tx, B.Parties, id, ct); Equal(row["snapshotHash"], B.Hash(row)); Equal(row["receiptHash"], bank.Receipt["snapshotHash"]);
        var first = await RawAsync(tx, row["firstVerificationId"].AsInt64, ct);
        Equal(first["receiptId"], row["_id"]); Equal(first["source"]["party"], row["party"]); Equal(first["receiptHash"], row["receiptHash"]); Equal(first["recordedAt"], row["recordedAt"]);
        if (!row["party"].Equals(source["party"])) throw B.Changed("payment_receipt_payer_conflict");
        return row;
    }
    internal async Task TouchAsync(IClientSessionHandle tx, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(tx, row, new BsonDocument("$inc", new BsonDocument("fence", 1L)), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
    }
}
