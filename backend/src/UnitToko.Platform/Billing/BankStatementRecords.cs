using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.BankStatementBson;

namespace UnitToko.Platform.Billing;

internal sealed class BankStatementRecords(IMongoDatabase database)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string collection, long id, CancellationToken ct) =>
        await Rows(collection).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), BankStatementSchemas.Validator(collection) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static void Equal(BsonValue a, BsonValue b) { if (!a.Equals(b)) throw B.Broken(); }
    internal static void ProfileAt(BsonDocument profile, DateTimeOffset now) {
        Equal(profile["parser"],"bank-csv-v1");
        BankStatementParser.Validate(B.Format(profile["format"]));
        _ = ReceivingAccountPolicy.Copy(new(profile["bankCode"].AsString,"0000","Profile bank code validation","IDR"));
        foreach (var name in new[] { "name", "trustedSourceReference", "operatingOwnerReference", "approvalReference" })
            if (!BankStatementParser.Text(profile[name].AsString, name == "name" ? 160 : 256)) throw B.Broken();
        if (!profile["active"].AsBoolean || profile["approvedAt"].ToUniversalTime() > now.UtcDateTime || profile["expiresAt"].ToUniversalTime() <= now.UtcDateTime) throw B.Changed();
    }
    internal async Task<BsonDocument> ImportAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx,B.Imports,id,ct); Equal(row["snapshotHash"],B.Hash(row));
        var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime()); var profile = row["profile"].AsBsonDocument; var account = row["account"].AsBsonDocument;
        ProfileAt(profile,at); Equal(row["scope"],profile["scope"]); Equal(row["scope"],account["scope"]); Equal(profile["bankCode"],account["details"]["bankCode"]);
        if (account["effectiveAt"].ToUniversalTime() > at.UtcDateTime || row["personAtImport"]["userId"] != row["actorId"] ||
            !BankStatementParser.Text(row["filename"].AsString,160) || row["filename"].AsString.IndexOfAny(['/', '\\']) >= 0 || !BankStatementParser.Text(row["sourceReference"].AsString,256) ||
            !BankStatementParser.Text(row["reason"].AsString,1000)) throw B.Broken();
        var person = ReceivingAccountBson.Person(row["personAtImport"]);
        if (person.Verification is not (PersonVerification.Unknown or PersonVerification.Verified)) throw B.Broken();
        var parsed = BankStatementParser.Parse(row["original"].AsByteArray,B.Format(profile["format"]),ReceivingAccountBson.Details(account["details"]),account["physicalIdentity"].AsString,AccountingCalendar.BusinessDate(at));
        if (!parsed.CanStage) throw B.Broken(); Equal(row["preview"],B.Preview(parsed)); Equal(row["binding"],B.Binding(profile,account,parsed.SourceSha256));
        Equal(row["bodyHash"],AuthorizationStore.CommandHash(B.Body(row),row["reason"].AsString));
        var audit = await Rows("auditEvents").Find(tx,new BsonDocument("_id",row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(audit,B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx,new BsonDocument { { "actorId",row["actorId"] }, { "operation",B.Operation }, { "key",row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 8 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken();
        Equal(receipt["bodyHash"],row["bodyHash"]); Equal(receipt["result"],B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx,new BsonDocument("sourceKey",B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"],B.Operation); Equal(effect["actorId"],row["actorId"]); Equal(effect["requestKey"],row["requestKey"]);
        Equal(effect["payload"],B.Result(row)); Equal(effect["createdAt"],row["recordedAt"]); return row;
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx,long actorId,string key,CancellationToken ct) {
        var row = await Rows(B.Imports).Find(tx,new BsonDocument { { "actorId",actorId }, { "requestKey",key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return await ImportAsync(tx,row["_id"].AsInt64,ct);
        if (await Rows("idempotencyRecords").Find(tx,new BsonDocument { { "actorId",actorId }, { "operation",B.Operation }, { "key",key } }).AnyAsync(ct) ||
            await Rows("auditEvents").Find(tx,new BsonDocument { { "actorId",actorId }, { "kind",B.Operation }, { "requestKey",key } }).AnyAsync(ct) ||
            await Rows("outboxEvents").Find(tx,new BsonDocument { { "actorId",actorId }, { "kind",B.Operation }, { "requestKey",key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    internal async Task TouchAsync(IClientSessionHandle tx,string collection,BsonDocument row,CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue-1 || (await Rows(collection).UpdateOneAsync(tx,row,Builders<BsonDocument>.Update.Inc("fence",1L),cancellationToken:ct)).ModifiedCount != 1) throw B.Changed();
    }
}
