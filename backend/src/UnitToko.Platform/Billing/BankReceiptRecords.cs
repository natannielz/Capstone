using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

internal sealed class BankReceiptRecords(IMongoDatabase database, BankStatementService statements)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await Rows(name).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id",id), BankReceiptSchemas.Validator(name) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static void Equal(BsonValue left, BsonValue right) { if (!left.Equals(right)) throw B.Broken(); }
    internal static void PolicyAt(BsonDocument row, DateTimeOffset at) {
        foreach (var name in new[] { "trustedSourceReference", "operatingOwnerReference", "approvalReference", "identityNamespace" })
            if (!BankStatementParser.Text(row[name].AsString, name == "identityNamespace" ? 80 : 256)) throw B.Broken();
        AccountingValidation.Code(row["cashMapping"].AsString); AccountingValidation.Code(row["suspenseMapping"].AsString);
        if (row["cashMapping"] == row["suspenseMapping"] || !row["active"].AsBoolean || row["approvedAt"].ToUniversalTime() > at.UtcDateTime || row["expiresAt"].ToUniversalTime() <= at.UtcDateTime) throw B.Changed();
    }
    internal static void Source(BankStatementReceiptSource source, BsonDocument policy, DateTimeOffset at) {
        PolicyAt(policy,at);
        var import = source.Import; var line = source.Row; var account = import["account"];
        Equal(policy["scope"],import["scope"]); Equal(policy["physicalIdentity"],account["physicalIdentity"]); Equal(policy["bankCode"],account["details"]["bankCode"]);
        Equal(policy["identityNamespace"],import["profile"]["format"]["identityNamespace"]);
        if (line["direction"] != "credit" || line["identityCandidate"].IsBsonNull || line["transactionId"].IsBsonNull ||
            import["preview"]["rows"].AsBsonArray.Count(row => row["identityCandidate"] == line["identityCandidate"]) != 1) throw BankReceiptPolicy.Invalid("bank_receipt_identity_unresolved");
        if (import["recordedAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken();
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actorId, string key, CancellationToken ct) {
        var filter = new BsonDocument { { "actorId", actorId }, { "requestKey", key } };
        var row = await Rows(B.Confirmations).Find(tx,filter).SingleOrDefaultAsync(ct);
        if (row is not null) return await ConfirmationRawAsync(tx,row["_id"].AsInt64,ct);
        if (await Rows("idempotencyRecords").Find(tx,new BsonDocument { { "actorId",actorId }, { "operation",B.Operation }, { "key",key } }).AnyAsync(ct) ||
            await Rows("auditEvents").Find(tx,new BsonDocument { { "actorId",actorId }, { "kind",B.Operation }, { "requestKey",key } }).AnyAsync(ct) ||
            await Rows("outboxEvents").Find(tx,new BsonDocument { { "actorId",actorId }, { "kind",B.Operation }, { "requestKey",key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    private Task<BsonDocument> ConfirmationRawAsync(IClientSessionHandle tx, long id, CancellationToken ct) => ReadConfirmationAsync(database, tx, id, ct);
    internal static async Task<BsonDocument> ReadConfirmationAsync(IMongoDatabase database, IClientSessionHandle tx, long id, CancellationToken ct) {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        async Task<BsonDocument> RowAsync(IClientSessionHandle session, string name, long value, CancellationToken token) => await Rows(name).Find(session, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", value), BankReceiptSchemas.Validator(name) })).SingleOrDefaultAsync(token) ?? throw B.Broken();
        var row = await RowAsync(tx,B.Confirmations,id,ct); Equal(row["snapshotHash"],B.Hash(row));
        _ = BankReceiptPolicy.Copy(B.Observation(row["observed"]));
        if (!BankStatementParser.Text(row["sourceReference"].AsString,256) || !BankStatementParser.Text(row["reason"].AsString,1000)) throw B.Broken();
        Equal(row["bodyHash"],AuthorizationStore.CommandHash(B.Body(row),row["reason"].AsString));
        Equal(await Rows("auditEvents").Find(tx,new BsonDocument("_id",row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(),B.Audit(row));
        var saved = await Rows("idempotencyRecords").Find(tx,new BsonDocument { { "actorId",row["actorId"] }, { "operation",B.Operation }, { "key",row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (saved.ElementCount != 9 || !saved["_id"].IsObjectId || saved["schemaVersion"] != 1 || !saved["createdAt"].IsBsonDateTime || saved["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        var participants=row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (participants.Select(p=>p.UserId).Distinct().Count()!=2 || !participants.Any(p=>p.UserId==row["actorId"].AsInt64)) throw B.Broken();
        var maker=participants.Single(p=>p.UserId!=row["actorId"].AsInt64).UserId;
        Equal(saved[PersonApprovalReceipt.Field],PersonApprovalReceipt.Create(Permissions.BankReceiptConfirm,new(1,1,RecordedBy:maker)));
        Equal(saved["bodyHash"],row["bodyHash"]); Equal(saved["result"],B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx,new BsonDocument("sourceKey",B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"],B.Operation); Equal(effect["actorId"],row["actorId"]); Equal(effect["requestKey"],row["requestKey"]); Equal(effect["payload"],B.Result(row)); Equal(effect["createdAt"],row["recordedAt"]);
        return row;
    }
    internal static void ConfirmationSource(BsonDocument row, BankStatementReceiptSource source) {
        Source(source,row["policy"].AsBsonDocument,new(row["recordedAt"].ToUniversalTime()));
        Equal(row["scope"],source.Import["scope"]); Equal(row["importHash"],source.Import["snapshotHash"]); Equal(row["rowHash"],B.Hash(source.Row));
        Equal(row["canonicalIdentity"],source.Row["identityCandidate"]); Equal(row["binding"],B.Binding(source,row["policy"].AsBsonDocument));
        BankReceiptPolicy.RequireMatch(B.Observation(row["observed"]),source.Import["account"]["details"]["accountNumber"].AsString,B.Line(source.Row));
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Select(p => p.UserId).Distinct().Count() != 2) throw B.Broken();
        var actor = people.SingleOrDefault(p => p.UserId == row["actorId"].AsInt64) ?? throw B.Broken();
        var maker = people.SingleOrDefault(p => p.UserId == source.Import["actorId"].AsInt64) ?? throw B.Broken();
        BankReceiptPolicy.Independent(actor,maker);
    }
    internal static void SameTransfer(BsonDocument receipt, BankStatementReceiptSource source) {
        var line = source.Row;
        if (receipt["canonicalIdentity"] != line["identityCandidate"] || receipt["physicalIdentity"] != source.Import["account"]["physicalIdentity"] ||
            receipt["identityNamespace"] != source.Import["profile"]["format"]["identityNamespace"] || receipt["scope"] != source.Import["scope"] ||
            receipt["transactionId"] != line["transactionId"] || receipt["bookedDate"] != line["bookedDate"] || receipt["amount"] != line["amount"]) throw B.Changed("bank_receipt_identity_conflict");
    }
    internal async Task<BsonDocument> ReceiptAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx,B.Receipts,id,ct); Equal(row["snapshotHash"],B.Hash(row));
        if (row["journalId"].IsBsonNull || row["journalFingerprint"].IsBsonNull || row["postingFence"] != 1L) throw B.Broken();
        var source = await statements.ReceiptSourceAsync(actor,authority,tx,row["importId"].AsInt64,row["rowNumber"].AsInt32,false,ct);
        var confirmation = await ConfirmationRawAsync(tx,row["confirmationId"].AsInt64,ct); ConfirmationSource(confirmation,source); SameTransfer(row,source);
        Equal(confirmation["receiptId"],row["_id"]); Equal(confirmation["createdReceipt"],true); Equal(confirmation["policy"],row["policy"]);
        Equal(confirmation["actorId"],row["actorId"]); Equal(confirmation["recordedAt"],row["recordedAt"]); Equal(row["importHash"],source.Import["snapshotHash"]); Equal(row["rowHash"],B.Hash(source.Row));
        var journal = await Rows("journalEntries").Find(tx,new BsonDocument("_id",row["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var expected = BankReceiptPostingSource.Snapshot(row);
        if (draft.Source != expected.Source || draft.SourceRevision != 1 || draft.Date != expected.Date || draft.Book != expected.Book || draft.Dimensions != expected.Dimensions ||
            draft.ReversalOf is not null || draft.Fingerprint != row["journalFingerprint"].AsString || journal["actorId"] != row["actorId"] ||
            !draft.Lines.Select(line => new PostingLine(line.MappingKey,line.Debit,line.Credit)).SequenceEqual(expected.Lines)) throw B.Broken();
        var anchor = await AnchorAsync(tx,row["physicalIdentity"].AsString,ct) ?? throw B.Broken(); Equal(anchor["scope"],row["scope"]); Equal(anchor["identityNamespace"],row["identityNamespace"]);
        return row;
    }
    internal async Task<(BsonDocument Row, BankStatementReceiptSource Source)> ConfirmationAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await ConfirmationRawAsync(tx,id,ct);
        var source = await statements.ReceiptSourceAsync(actor,authority,tx,row["importId"].AsInt64,row["rowNumber"].AsInt32,false,ct);
        ConfirmationSource(row,source);
        var receipt = await ReceiptAsync(actor,authority,tx,row["receiptId"].AsInt64,ct); SameTransfer(receipt,source);
        if (row["createdReceipt"].AsBoolean != (receipt["confirmationId"] == row["_id"]) || receipt["recordedAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        return (row,source);
    }
    internal async Task<BsonDocument?> AnchorAsync(IClientSessionHandle tx, string physicalIdentity, CancellationToken ct) {
        var anchor = await Rows(B.Accounts).Find(tx,new BsonDocument("physicalIdentity",physicalIdentity)).SingleOrDefaultAsync(ct);
        if (anchor is null) {
            if (await Rows(B.Receipts).Find(tx,new BsonDocument("physicalIdentity",physicalIdentity)).AnyAsync(ct)) throw B.Broken(); return null;
        }
        anchor = await RowAsync(tx,B.Accounts,anchor["_id"].AsInt64,ct); Equal(anchor["snapshotHash"],B.Hash(anchor));
        var first = await RowAsync(tx,B.Receipts,anchor["firstReceiptId"].AsInt64,ct); Equal(first["snapshotHash"],B.Hash(first));
        Equal(first["physicalIdentity"],anchor["physicalIdentity"]); Equal(first["scope"],anchor["scope"]); Equal(first["identityNamespace"],anchor["identityNamespace"]); Equal(first["recordedAt"],anchor["createdAt"]);
        if (first["postingFence"] != 1L || first["journalId"].IsBsonNull) throw B.Broken(); return anchor;
    }
    internal async Task TouchAsync(IClientSessionHandle tx,string name,BsonDocument row,CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue-1 || (await Rows(name).UpdateOneAsync(tx,row,Builders<BsonDocument>.Update.Inc("fence",1L),cancellationToken:ct)).ModifiedCount != 1) throw B.Changed();
    }
}
