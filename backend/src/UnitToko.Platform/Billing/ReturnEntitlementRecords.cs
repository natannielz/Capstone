using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using E = UnitToko.Platform.Billing.ReturnEntitlementBson;

namespace UnitToko.Platform.Billing;
internal sealed class ReturnEntitlementRecords(IMongoDatabase database, IClock clock)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue left, BsonValue right) { if (!InvoiceRecords.Equal(left, right)) throw B.Broken(); }
    internal static BsonDocument Empty(BsonArray issued) => new() { { "version", 0L }, { "lastRequestId", BsonNull.Value }, { "lastRequestHash", BsonNull.Value },
        { "lines", new BsonArray(issued.Select(line => new BsonDocument { { "receiptId", line["receiptId"] }, { "skuId", line["skuId"] }, { "attributedBaseUnits", 0L },
            { "net", new BsonDecimal128(0m) }, { "tax", new BsonDecimal128(0m) }, { "charges", new BsonDecimal128(0m) } })) } };
    internal static BsonDocument State(BsonDocument head) => new() { { "version", head["version"] }, { "lastRequestId", head["lastRequestId"] }, { "lastRequestHash", head["lastRequestHash"] }, { "lines", head["lines"].DeepClone() } };
    internal static BsonDocument After(BsonDocument row) {
        var next = row["before"].DeepClone().AsBsonDocument; next["version"] = row["version"]; next["lastRequestId"] = row["_id"]; next["lastRequestHash"] = row["snapshotHash"];
        foreach (var line in row["lines"].AsBsonArray) {
            var target = next["lines"].AsBsonArray.Single(value => E.SameLine(value, line)).AsBsonDocument;
            target["attributedBaseUnits"] = checked(target["attributedBaseUnits"].AsInt64 + line["attributedBaseUnits"].AsInt64);
            foreach (var component in new[] { "net", "tax", "charges" }) target[component] = InvoiceBson.Money(UnitToko.Modules.Accounting.Money.Add(InvoiceBson.Money(target[component]), InvoiceBson.Money(line[component])));
        }
        return next;
    }
    internal async Task<BsonDocument> RawAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var row = await Rows(E.Requests).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & ReturnEntitlementSchemas.Validator(E.Requests)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row)); var source = row["source"].AsBsonDocument; var before = row["before"].AsBsonDocument; var policy = row["policy"].AsBsonDocument;
        Equal(row["_id"], source["returnId"]); Equal(row["caseId"], source["caseId"]); Equal(row["invoiceId"], source["invoiceId"]); Equal(row["action"], source["action"]);
        Equal(row["invoiceId"], source["invoice"]["invoiceId"]); Equal(row["version"], before["version"].AsInt64 + 1);
        if (row["requestedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken(); E.PolicyAt(policy, new(row["requestedAt"].ToUniversalTime()));
        var party = PaymentDraftBson.Party(new BsonArray { source["invoice"] });
        foreach (var field in new[] { "fulfillmentOrganizationId", "shopId", "channelId" }) Equal(policy["scope"][field], party[field]);
        if (!policy["scope"]["warehouseId"].IsBsonNull || !ReceivingAccountPolicy.Text(row["reason"].AsString, 1000) || !ReceivingAccountPolicy.Text(row["correlationId"].AsString, 128) ||
            !ReceivingAccountPolicy.Text(row["requestKey"].AsString, 128) || row["requestKey"].AsString.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))) throw B.Broken();
        Equal(row["binding"], E.Binding(source, before, policy)); var input = E.Input(row["input"].AsBsonDocument);
        Equal(row["input"], E.Input(input)); Equal(row["bodyHash"], AuthorizationStore.CommandHash(E.Body(row["caseId"].AsInt64, id, row["action"].AsString, before["version"].AsInt64, row["binding"].AsString, input), row["reason"].AsString));
        var result = E.Review(source, before, policy, input); Equal(row["lines"], E.Lines(result)); Equal(row["net"], InvoiceBson.Money(result.Net)); Equal(row["tax"], InvoiceBson.Money(result.Tax));
        Equal(row["charges"], InvoiceBson.Money(result.Charges)); Equal(row["credit"], InvoiceBson.Money(result.Credit));
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        var users = source["participants"].AsBsonArray.Select(value => value.AsInt64).Append(row["actorId"].AsInt64).Distinct().Order().ToArray();
        if (people.Any(person => person.Verification != PersonVerification.Verified) || !people.Select(person => person.UserId).Order().SequenceEqual(users) || !users.Contains(source["requestedBy"].AsInt64) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == row["actorId"].AsInt64), people.Single(person => person.UserId == source["requestedBy"].AsInt64))) throw B.Broken();
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", E.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].CompareTo(row["requestedAt"]) > 0) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], E.Result(row));
        Equal(receipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.CaseManage, new(1, 1, RequestedBy: source["requestedBy"].AsInt64)));
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), E.Audit(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", E.Operation + ":" + id)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var (field, expected) in new (string, BsonValue)[] { ("kind", E.Operation), ("actorId", row["actorId"]), ("requestKey", row["requestKey"]), ("createdAt", row["requestedAt"]), ("payload", E.Result(row)) }) Equal(effect[field], expected);
        return row;
    }
    internal async Task<(BsonDocument State, BsonDocument? Head)> PoolAsync(IClientSessionHandle tx, BsonDocument invoice, BsonArray issued,
        Func<BsonDocument, Task> validateSource, CancellationToken ct) {
        var id = invoice["invoiceId"].AsInt64; var expected = Empty(issued); var seen = new HashSet<long>();
        using var cursor = await Rows(E.Requests).Find(tx, new BsonDocument("invoiceId", id)).Sort(new BsonDocument("version", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            var row = await RawAsync(tx, candidate["_id"].AsInt64, ct); if (!seen.Add(row["_id"].AsInt64)) throw B.Broken();
            await validateSource(row); Equal(row["source"]["invoice"], invoice); Equal(row["source"]["issuedLines"], issued); Equal(row["before"], expected); expected = After(row);
        }
        foreach (var (table, kind, prefix) in new[] { ("auditEvents", "kind", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") }) {
            var effects = await Rows(table).Find(tx, new BsonDocument { { kind, E.Operation }, { prefix + ".invoiceId", id } }).ToListAsync(ct);
            if (effects.Count != seen.Count || effects.Any(effect => !seen.Contains(effect[prefix]["returnId"].AsInt64))) throw B.Broken();
        }
        var raw = await Rows(E.Pools).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        if (raw is null) { if (seen.Count > 0) throw B.Broken(); return (expected, null); }
        if (!await Rows(E.Pools).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & ReturnEntitlementSchemas.Validator(E.Pools)).AnyAsync(ct)) throw B.Broken();
        Equal(raw["snapshotHash"], B.Hash(raw)); Equal(raw["sourceHash"], invoice["snapshotHash"]); Equal(State(raw), expected); return (expected, raw);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await Rows(E.Requests).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return await RawAsync(tx, row["_id"].AsInt64, ct);
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { field, E.Operation }, { name == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    internal async Task RecordAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument? previous, CancellationToken ct) {
        await Rows(E.Requests).InsertOneAsync(tx, row, cancellationToken: ct);
        var head = new BsonDocument { { "_id", row["invoiceId"] }, { "schemaVersion", 1 }, { "sourceHash", row["source"]["invoice"]["snapshotHash"] }, { "fence", 0L } };
        head.AddRange(After(row)); head.Add("snapshotHash", B.Hash(head));
        if (previous is null) await Rows(E.Pools).InsertOneAsync(tx, head, cancellationToken: ct);
        else if ((await Rows(E.Pools).ReplaceOneAsync(tx, previous, head, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed("return_entitlement_changed");
        await Rows("auditEvents").InsertOneAsync(tx, E.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", E.Operation }, { "sourceKey", E.Operation + ":" + row["_id"] },
            { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", E.Result(row) }, { "createdAt", row["requestedAt"] }, { "dueAt", row["requestedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}
