using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo.Migrations;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Inventory;
internal sealed record ReturnQuarantineHistory(long Revision, BsonValue HeadHash, ImmutableDictionary<(long Receipt, long Sku), long> Quantities, ImmutableArray<BsonDocument> Recent);
/// <summary>Inventory owns immutable physical custody of returned base units. This ledger is excluded
/// from usable package balances, reservation sources, cost valuation and financial entitlements.</summary>
internal sealed class ReturnQuarantineLedger(IMongoDatabase database, IClock clock)
{
    internal const string Collection = "returnQuarantineReceipts", Operation = "fulfillment.case.return_receive";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Source(BsonDocument root, BsonDocument decision, BsonArray stock) => new() {
        { "caseId", root["_id"] }, { "caseHash", root["snapshotHash"] }, { "decisionHash", decision["snapshotHash"] }, { "requestedBy", root["requestedBy"] },
        { "invoiceId", root["source"]["invoiceId"] }, { "decidedAt", decision["decidedAt"] }, { "authorizedLines", decision["input"]["lines"].DeepClone() }, { "stock", stock } };
    internal static string Binding(BsonDocument source, long revision, BsonValue head) => R.Binding(new BsonDocument { { "source", source }, { "revision", revision }, { "headHash", head } });
    internal static object Body(long id, long revision, string binding, ReturnedStockInput input) => new { caseId = id, expectedRevision = revision, binding, input };
    internal static BsonDocument Input(ReturnedStockInput input) => new() { { "publicNote", input.PublicNote }, { "lines", new BsonArray(input.Lines.Select(line => new BsonDocument {
        { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "receivedBaseUnits", line.ReceivedBaseUnits }, { "observedCondition", line.ObservedCondition } })) } };
    internal static ReturnedStockInput Input(BsonDocument row) => ReturnedStockPolicy.Copy(new(row["publicNote"].AsString, [.. row["lines"].AsBsonArray.Select(line =>
        new ReturnedStockLine(line["receiptId"].AsInt64, line["skuId"].AsInt64, line["receivedBaseUnits"].AsInt64, line["observedCondition"].AsString))]));
    internal static BsonDocument Result(BsonDocument row) => new() { { "returnId", row["_id"] }, { "caseId", row["caseId"] }, { "invoiceId", row["source"]["invoiceId"] },
        { "revision", row["revision"] }, { "receivedAt", row["receivedAt"] }, { "state", "quarantined" }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] },
        { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["receivedAt"] }, { "result", Result(row) } };
    internal async Task<ReturnQuarantineHistory> ReadAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw R.Broken();
        var revision = 0L; BsonValue head = BsonNull.Value; var quantities = new Dictionary<(long, long), long>(); var recent = new Queue<BsonDocument>();
        // ponytail: verify the case's immutable custody chain; checkpoint only if measured return volume requires it.
        using var cursor = await Rows(Collection).Find(tx, new BsonDocument("caseId", source["caseId"])).Sort(new BsonDocument("revision", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) {
            if (!await Rows(Collection).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", row["_id"]) & ReturnQuarantineSchemas.Validator()).AnyAsync(ct)) throw R.Broken();
            var input = Input(row["input"].AsBsonDocument);
            if (row["receivedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime || row["revision"] != revision + 1 || !InvoiceRecords.Equal(row["source"], source) || row["previousHash"] != head || row["binding"] != Binding(source, revision, head) ||
                !InvoiceRecords.Equal(row["input"], Input(input)) || row["snapshotHash"] != R.Hash(row) || row["bodyHash"] != AuthorizationStore.CommandHash(Body(source["caseId"].AsInt64, revision, row["binding"].AsString, input), row["reason"].AsString)) throw R.Broken();
            if (recent.Count > 0 && row["receivedAt"].CompareTo(recent.Last()["receivedAt"]) < 0) throw R.Broken();
            var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
            if (people.Select(person => person.UserId).Distinct().Count() != 2 || !PersonIndependencePolicy.AreIndependent(people.SingleOrDefault(person => person.UserId == row["actorId"].AsInt64),
                    people.SingleOrDefault(person => person.UserId == source["requestedBy"].AsInt64))) throw R.Broken();
            foreach (var field in new[] { "requestKey", "reason", "correlationId" }) if (!ReceivingAccountPolicy.Text(row[field].AsString, field == "reason" ? 1000 : 128)) throw R.Broken();
            var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
            var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + row["_id"])).SingleOrDefaultAsync(ct);
            if (receipt is null || receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].CompareTo(row["receivedAt"]) > 0 ||
                receipt["bodyHash"] != row["bodyHash"] || !InvoiceRecords.Equal(receipt["result"], Result(row)) || !InvoiceRecords.Equal(receipt.GetValue(PersonApprovalReceipt.Field, BsonNull.Value),
                    PersonApprovalReceipt.Create(Permissions.CaseManage, new(1, 1, RequestedBy: source["requestedBy"].AsInt64))) || audit is null || !InvoiceRecords.Equal(audit, Audit(row)) ||
                effect is null || effect["kind"] != Operation || effect["actorId"] != row["actorId"] || effect["requestKey"] != row["requestKey"] || effect["createdAt"] != row["receivedAt"] || !InvoiceRecords.Equal(effect["payload"], Result(row))) throw R.Broken();
            RequireCapacity(source, quantities, input.Lines); foreach (var line in input.Lines) quantities[(line.ReceiptId, line.SkuId)] = checked(quantities.GetValueOrDefault((line.ReceiptId, line.SkuId)) + line.ReceivedBaseUnits);
            revision++; head = row["snapshotHash"]; recent.Enqueue(row); if (recent.Count > 25) recent.Dequeue();
        }
        foreach (var (table, path) in new[] { ("idempotencyRecords", "result.caseId"), ("auditEvents", "result.caseId"), ("outboxEvents", "payload.caseId") })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", Operation }, { path, source["caseId"] } }, cancellationToken: ct) != revision) throw R.Broken();
        return new(revision, head, quantities.ToImmutableDictionary(), [.. recent.Reverse()]);
    }
    internal static void RequireCapacity(BsonDocument source, IReadOnlyDictionary<(long Receipt, long Sku), long> quantities, ImmutableArray<ReturnedStockLine> lines) {
        foreach (var line in lines) {
            var authorized = source["authorizedLines"].AsBsonArray.SingleOrDefault(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId);
            if (authorized is null || source["stock"].AsBsonArray.Count(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId) != 1 ||
                quantities.GetValueOrDefault((line.ReceiptId, line.SkuId)) > authorized["reportedBaseUnits"].AsInt64 - line.ReceivedBaseUnits) throw AfterSalesDecisionRecords.Changed("returned_stock_quantity_exceeded");
        }
    }
    internal static AuthorizationQuery RequireReceiver(ActorContext actor, AuthorizationSnapshot authority, BsonDocument source, DateTimeOffset now) {
        var query = ScopePolicy.Compile(actor, authority, Permissions.StockReceive, now); AuthorizationStore.Require(query);
        foreach (var line in source["stock"].AsBsonArray.Where(line => source["authorizedLines"].AsBsonArray.Any(item => item["receiptId"] == line["receiptId"] && item["skuId"] == line["skuId"]))) {
            var resource = CatalogRepository.Resource(InventoryGraph.Location(line["location"].AsBsonDocument).ToScope());
            if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource)) throw R.Broken();
        }
        return query;
    }
    internal static async Task FenceReceiverAsync(IMongoDatabase database, AuthorizationStore authorization, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        BsonDocument source, DateTimeOffset now, CancellationToken ct) {
        var query = RequireReceiver(actor, authority, source, now);
        var locations = source["stock"].AsBsonArray.Select(line => InventoryGraph.Location(line["location"].AsBsonDocument).ToScope()).Distinct().ToArray();
        var clauses = query.Clauses.Where(clause => locations.Any(location => clause.Matches(CatalogRepository.Resource(location)))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Concat(locations.SelectMany(location => ScopePolicy.RequiredNodes(location, authority.Directory))).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
    }
    internal async Task ReceiveAsync(IClientSessionHandle tx, BsonDocument row, ReturnQuarantineHistory history, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || row["revision"] != history.Revision + 1 || row["previousHash"] != history.HeadHash) throw R.Broken();
        RequireCapacity(row["source"].AsBsonDocument, history.Quantities, Input(row["input"].AsBsonDocument).Lines);
        await Rows(Collection).InsertOneAsync(tx, row, cancellationToken: ct);
        await Rows("auditEvents").InsertOneAsync(tx, Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", Operation }, { "sourceKey", Operation + ":" + row["_id"] },
            { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", Result(row) }, { "createdAt", row["receivedAt"] }, { "dueAt", row["receivedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}
