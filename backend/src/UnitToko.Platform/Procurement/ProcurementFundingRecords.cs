using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementFundingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementFundingRecords(IMongoDatabase database, CatalogAuthoringService catalog, PurchaseOrderService orders)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) => await Rows(name)
        .Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { ProcurementFundingSchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static void PolicyAt(BsonDocument policy, BsonDocument source, DateTimeOffset at) {
        Equal(policy["location"], source["order"]["location"]);
        if (!policy["active"].AsBoolean || !policy["reserveUndisbursed"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime ||
            !policy["eligibleRoutes"].AsBsonArray.Contains(source["request"]["input"]["route"])) throw B.Changed();
        if (policy["requiresAdditionalBudgetSource"].AsBoolean || policy["requiresAdditionalLimitSource"].AsBoolean)
            throw new PersistenceConflictException("procurement_funding_policy_unsupported", "The Finance policy requires an additional verified budget or limit source");
    }
    internal static void CapacityInput(ProcurementFundingInput input, BsonDocument policy, BsonDocument capacity) {
        Equal(capacity["orderId"], input.OrderId); Equal(capacity["orderVersion"], input.OrderVersion);
        var limit = Money.Parse(capacity["requiredAdvance"].AsString); var authorized = Money.Parse(capacity["authorized"].AsString); var remaining = Money.Parse(capacity["remaining"].AsString);
        if (limit.Value <= 0 || Money.Add(authorized, remaining) != limit || input.Amount.Value > remaining.Value ||
            !policy["allowInstallments"].AsBoolean && (authorized.Value != 0 || input.Amount != limit)) throw B.Changed();
    }
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!people.Select(person => person.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    internal async Task<BsonDocument> RequestAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Requests, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(row["input"]); Equal(row["input"], B.Input(input));
        Equal(row["location"], R.Location(input.Location)); Equal(row["orderId"], input.OrderId);
        var source = await orders.FundingSourceAsync(database, tx, input.OrderId, input.OrderVersion, input.OrderApprovalId, ct); Equal(row["orderSource"], source);
        Equal(source["order"]["location"], row["location"]); Equal(row["policy"]["_id"], input.PolicyId); Equal(row["policy"]["revision"], input.PolicyRevision);
        if (source["order"]["recordedAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        await new ProcurementRequestRecords(database, catalog).SourcesAsync(tx, input.Location, row["sources"].AsBsonDocument, ct);
        PolicyAt(row["policy"].AsBsonDocument, source, new(row["recordedAt"].ToUniversalTime())); CapacityInput(input, row["policy"].AsBsonDocument, row["capacity"].AsBsonDocument);
        Equal(row["capacity"]["requiredAdvance"], source["proposal"]["input"]["paymentTerms"]["requiredAdvance"]); People(row, row["actorId"].AsInt64);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, source, row["sources"].AsBsonDocument, row["policy"].AsBsonDocument, row["capacity"].AsBsonDocument));
        await EffectsAsync(tx, row, B.RequestBody(input, row["binding"].AsString), null, ct); return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument request, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(request["input"]); var decision = B.Decision(row["decision"]);
        foreach (var field in new[] { "requestId", "orderId", "location" }) Equal(row[field], request[field]); Equal(row["requestHash"], request["snapshotHash"]);
        if (row["recordedAt"].ToUniversalTime() < request["recordedAt"].ToUniversalTime()) throw B.Broken();
        await new ProcurementRequestRecords(database, catalog).SourcesAsync(tx, input.Location, row["reviewSources"].AsBsonDocument, ct);
        if (decision == ProcurementFundingDecision.Approve) {
            Equal(row["reviewSources"], request["sources"]); Equal(row["reviewPolicy"], request["policy"]); Equal(row["authorizedAmount"], input.Amount.ToString());
            PolicyAt(row["reviewPolicy"].AsBsonDocument, request["orderSource"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
            CapacityInput(input, row["reviewPolicy"].AsBsonDocument, row["reviewCapacity"].AsBsonDocument);
            Equal(row["reviewCapacity"]["requiredAdvance"], request["capacity"]["requiredAdvance"]);
            var allocation = await RowAsync(tx, B.Allocations, row["authorizationId"].AsInt64, ct); RequireAllocation(row, request, allocation);
        } else if (!row["authorizationId"].IsBsonNull || !row["authorizedAmount"].IsBsonNull || !row["reviewPolicy"].IsBsonNull || !row["reviewCapacity"].IsBsonNull) throw B.Broken();
        People(row, row["actorId"].AsInt64, request["actorId"].AsInt64); var people = row["people"].AsBsonArray.Select(B.Person).ToDictionary(person => person.UserId); var maker = request["people"].AsBsonArray.Select(B.Person).Single();
        if (people[maker.UserId].PersonId != maker.PersonId || !PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[maker.UserId], maker)) throw B.Broken();
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, request, decision, row["reviewSources"].AsBsonDocument,
            row["reviewPolicy"].IsBsonNull ? null : row["reviewPolicy"].AsBsonDocument, row["reviewCapacity"].IsBsonNull ? null : row["reviewCapacity"].AsBsonDocument));
        await EffectsAsync(tx, row, B.DecideBody(request["_id"].AsInt64, 1, decision, row["binding"].AsString), request, ct); return row;
    }
    internal static void RequireAllocation(BsonDocument decision, BsonDocument request, BsonDocument row) {
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["_id"], decision["authorizationId"]); Equal(row["requestId"], request["_id"]); Equal(row["requestHash"], request["snapshotHash"]);
        Equal(row["decisionId"], decision["_id"]); Equal(row["decisionHash"], decision["snapshotHash"]); Equal(row["orderId"], request["orderId"]); Equal(row["orderVersion"], request["input"]["orderVersion"]);
        Equal(row["orderHash"], request["orderSource"]["order"]["snapshotHash"]); Equal(row["orderApprovalId"], request["input"]["orderApprovalId"]); Equal(row["amount"], decision["authorizedAmount"]); Equal(row["recordedAt"], decision["recordedAt"]);
        Equal(row["sequence"], checked(decision["reviewCapacity"]["version"].AsInt64 + 1));
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument request, CancellationToken ct) {
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument("requestId", request["_id"])).SingleOrDefaultAsync(ct);
        if (row is null) {
            if (await Rows(B.Allocations).Find(tx, new BsonDocument("requestId", request["_id"])).AnyAsync(ct)) throw B.Broken();
            foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
                if (await Rows(table).Find(tx, new BsonDocument { { field, new BsonDocument("$in", new BsonArray { B.Prefix + "approve", B.Prefix + "reject" }) }, { result + ".requestId", request["_id"] } }).AnyAsync(ct)) throw B.Broken();
        }
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, request, ct);
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, BsonDocument? request, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString)); Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (request is not null) { fields = [.. fields, PersonApprovalReceipt.Field]; if (PersonApprovalReceipt.Read(receipt, Permissions.FundingApprove) != new ApprovalRequest(1, 1, RequestedBy: request["actorId"].AsInt64)) throw B.Broken(); }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location", "orderId" }) Equal(effect[field], row[field]); Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var table = operation == B.Prefix + "request" ? B.Requests : B.Decisions; var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { field, operation }, { name == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken(); return null;
        }
        var request = await RequestAsync(tx, row["requestId"].AsInt64, ct); return table == B.Requests ? request : await DecisionAsync(tx, row["_id"].AsInt64, request, ct);
    }
    internal async Task<BsonArray> ClaimsAsync(IClientSessionHandle tx, long orderId, CancellationToken ct) {
        var claims = new BsonArray(); long requests = 0, decisions = 0;
        using var cursor = await Rows(B.Requests).Find(tx, new BsonDocument("orderId", orderId)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            var request = await RequestAsync(tx, raw["_id"].AsInt64, ct); requests++; var decision = await DecisionForAsync(tx, request, ct); if (decision is null) continue; decisions++;
            if (decision["decision"] == "approve") claims.Add(new BsonDocument { { "authorizationId", decision["authorizationId"] }, { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] }, { "amount", decision["authorizedAmount"] }, { "reviewCapacity", decision["reviewCapacity"] }, { "recordedAt", decision["recordedAt"] } });
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument("orderId", orderId), cancellationToken: ct) != decisions) throw B.Broken();
        foreach (var table in new[] { "auditEvents", "outboxEvents" }) if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { "orderId", orderId }, { "kind", new BsonRegularExpression(@"^procurement\.funding\.") } }, cancellationToken: ct) != requests + decisions) throw B.Broken();
        return claims;
    }
}
