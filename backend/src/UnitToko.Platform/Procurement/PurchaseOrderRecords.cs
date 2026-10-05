using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed class PurchaseOrderRecords(IMongoDatabase database, CatalogAuthoringService catalog, PurchaseOrderSourceReadCapture? sourceCapture = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private async Task<BsonDocument?> OneAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        sourceCapture is null ? await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct) : await sourceCapture.OneAsync(database, tx, name, filter, ct);
    private Task<bool> AnyAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        sourceCapture is null ? Rows(name).Find(tx, filter).AnyAsync(ct) : sourceCapture.AnyAsync(database, tx, name, filter, ct);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await OneAsync(tx, name, new BsonDocument { { "_id", id }, { "$and", new BsonArray { PurchaseOrderSchemas.Validator(name) } } }, ct) ?? throw B.Broken();
    internal async Task<(BsonDocument Request, BsonDocument Decision)> RequestAsync(IClientSessionHandle tx, PurchaseOrderInput input, CancellationToken ct) {
        var records = new ProcurementRequestRecords(database, catalog, sourceCapture); var request = await records.RequestAsync(tx, input.RequestId, ct);
        var decision = await records.DecisionAsync(tx, input.RequestDecisionId, request, ct); Equal(decision["decision"], "approve"); Equal(request["version"], input.RequestVersion);
        Equal(request["location"], R.Location(input.Location)); var original = R.Input(request["input"]);
        if (original.Route == ProcurementRoute.OmiRegular && input.ExternalOrderReference is null) throw B.Broken();
        foreach (var line in input.Lines) {
            var source = original.Lines.SingleOrDefault(value => value.SkuId == line.RequestSkuId) ?? throw B.Broken();
            if (source.DefinitionId != line.DefinitionId || source.DefinitionVersion != line.DefinitionVersion || line.Packages > source.Packages) throw B.Broken();
        }
        return (request, decision);
    }
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!people.Select(person => person.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    private static void Capacity(PurchaseOrderInput input, BsonArray capacity) {
        if (capacity.Count != input.Lines.Length) throw B.Broken();
        for (var index = 0; index < input.Lines.Length; index++) {
            var row = capacity[index]; Equal(row["requestSkuId"], input.Lines[index].RequestSkuId);
            if (row["committedPackages"].AsInt32 < 0 || row["availablePackages"].AsInt32 < input.Lines[index].Packages ||
                row["requestedPackages"].AsInt32 != checked(row["committedPackages"].AsInt32 + row["availablePackages"].AsInt32)) throw B.Broken();
        }
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(row["input"]); Equal(row["input"], B.Input(input));
        Equal(row["requestId"], input.RequestId); Equal(row["location"], R.Location(input.Location)); var source = await RequestAsync(tx, input, ct);
        Equal(row["requestHash"], source.Request["snapshotHash"]); Equal(row["requestDecisionHash"], source.Decision["snapshotHash"]); Equal(row["sources"], source.Request["sources"]);
        var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime());
        if (source.Decision["recordedAt"].ToUniversalTime() > at.UtcDateTime || input.CommercialReference.ValidUntil <= at ||
            input.CommercialReference.IssuedDate > UnitToko.Modules.Accounting.AccountingCalendar.BusinessDate(at)) throw B.Broken();
        await new SupplierRecords(database, catalog, sourceCapture).PurchaseOrderSourceAsync(tx, input, row["supplier"].AsBsonDocument, at, ct);
        Capacity(input, row["capacity"].AsBsonArray); People(row, row["actorId"].AsInt64);
        foreach (var capacity in row["capacity"].AsBsonArray) Equal(capacity["requestedPackages"], R.Input(source.Request["input"]).Lines.Single(line => line.SkuId == capacity["requestSkuId"].AsInt64).Packages);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, source.Request, source.Decision,
            row["sources"].AsBsonDocument, row["supplier"].AsBsonDocument, row["capacity"].AsBsonArray));
        await EffectsAsync(tx, row, B.ProposeBody(input, row["binding"].AsString), null, ct); return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(proposal["input"]); var decision = B.Decision(row["decision"]);
        foreach (var field in new[] { "proposalId", "requestId", "location" }) Equal(row[field], proposal[field]); Equal(row["proposalHash"], proposal["snapshotHash"]);
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        await new ProcurementRequestRecords(database, catalog, sourceCapture).SourcesAsync(tx, input.Location, row["reviewSources"].AsBsonDocument, ct);
        if (decision == PurchaseOrderDecision.Approve) {
            Equal(row["reviewSources"], proposal["sources"]); Equal(row["reviewSupplier"], proposal["supplier"]); Capacity(input, row["reviewCapacity"].AsBsonArray);
            var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime()); if (input.CommercialReference.ValidUntil <= at) throw B.Broken();
            await new SupplierRecords(database, catalog, sourceCapture).PurchaseOrderSourceAsync(tx, input, row["reviewSupplier"].AsBsonDocument, at, ct);
            var order = await RowAsync(tx, B.Orders, row["orderId"].AsInt64, ct); Equal(order["snapshotHash"], B.Hash(order));
            Equal(order["version"], row["orderVersion"]); Equal(order["proposalId"], proposal["_id"]); Equal(order["proposalHash"], proposal["snapshotHash"]);
            Equal(order["approvalId"], row["_id"]); Equal(order["approvalHash"], row["snapshotHash"]); Equal(order["requestId"], input.RequestId);
            Equal(order["requestDecisionId"], input.RequestDecisionId); Equal(order["location"], proposal["location"]); Equal(order["recordedAt"], row["recordedAt"]);
        } else if (!row["orderId"].IsBsonNull || !row["orderVersion"].IsBsonNull || !row["reviewSupplier"].IsBsonNull || row["reviewCapacity"].AsBsonArray.Count != 0) throw B.Broken();
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64);
        var people = row["people"].AsBsonArray.Select(B.Person).ToDictionary(person => person.UserId); var maker = proposal["people"].AsBsonArray.Select(B.Person).Single();
        if (people[maker.UserId].PersonId != maker.PersonId || !PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[maker.UserId], maker)) throw B.Broken();
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal, decision,
            row["reviewSources"].AsBsonDocument, row["reviewSupplier"].IsBsonNull ? null : row["reviewSupplier"].AsBsonDocument, row["reviewCapacity"].AsBsonArray));
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), proposal, ct); return row;
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await OneAsync(tx, B.Decisions, new BsonDocument("proposalId", proposal["_id"]), ct);
        if (row is null) {
            if (await AnyAsync(tx, B.Orders, new BsonDocument("proposalId", proposal["_id"]), ct)) throw B.Broken();
            foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
                if (await AnyAsync(tx, table, new BsonDocument { { field, new BsonDocument("$in", new BsonArray { B.Prefix + "approve", B.Prefix + "reject" }) }, { result + ".proposalId", proposal["_id"] } }, ct)) throw B.Broken();
        }
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, BsonDocument? proposal, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        Equal(await OneAsync(tx, "auditEvents", new BsonDocument("_id", row["auditId"]), ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await OneAsync(tx, "idempotencyRecords", new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }, ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (proposal is not null) { fields = [.. fields, PersonApprovalReceipt.Field]; if (PersonApprovalReceipt.Read(receipt, Permissions.ProcurementApprove) != new ApprovalRequest(1, 1, RequestedBy: proposal["actorId"].AsInt64)) throw B.Broken(); }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await OneAsync(tx, "outboxEvents", new BsonDocument("sourceKey", B.SourceKey(row)), ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location", "requestId" }) Equal(effect[field], row[field]);
        Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "propose" ? B.Proposals : B.Decisions;
        var row = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { field, operation }, { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken(); return null;
        }
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct); return name == B.Proposals ? proposal : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task<BsonArray> RequestClaimsAsync(IClientSessionHandle tx, long requestId, long skuId, CancellationToken ct) {
        var claims = new BsonArray(); long decisions = 0, approvals = 0, proposals = 0;
        var rawProposals = sourceCapture is null ? null : new List<BsonDocument>();
        using var cursor = await Rows(B.Proposals).Find(tx, new BsonDocument("requestId", requestId)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            rawProposals?.Add(raw.DeepClone().AsBsonDocument);
            var proposal = await ProposalAsync(tx, raw["_id"].AsInt64, ct); proposals++; var decision = await DecisionForAsync(tx, proposal, ct); if (decision is null) continue; decisions++;
            if (decision["decision"] != "approve") continue; approvals++; var line = B.Input(proposal["input"]).Lines.SingleOrDefault(value => value.RequestSkuId == skuId);
            if (line is not null) claims.Add(new BsonDocument { { "orderId", decision["orderId"] }, { "approvalId", decision["_id"] }, { "approvalHash", decision["snapshotHash"] }, { "packages", line.Packages }, { "recordedAt", decision["recordedAt"] } });
        }
        sourceCapture?.Record(B.Proposals, new BsonDocument("requestId", requestId), rawProposals!);
        var decisionCount = await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument("requestId", requestId), cancellationToken: ct);
        sourceCapture?.RecordCount(B.Decisions, new BsonDocument("requestId", requestId), decisionCount);
        if (decisionCount != decisions) throw B.Broken();
        var orderCount = await Rows(B.Orders).CountDocumentsAsync(tx, new BsonDocument("requestId", requestId), cancellationToken: ct);
        sourceCapture?.RecordCount(B.Orders, new BsonDocument("requestId", requestId), orderCount);
        if (orderCount != approvals) throw B.Broken();
        foreach (var table in new[] { "auditEvents", "outboxEvents" }) {
            var filter = new BsonDocument { { "requestId", requestId }, { "kind", new BsonRegularExpression(@"^procurement\.purchase\.order\.") } };
            var actualCount = await Rows(table).CountDocumentsAsync(tx, filter, cancellationToken: ct);
            sourceCapture?.RecordCount(table, filter, actualCount);
            if (actualCount != proposals + decisions) throw B.Broken();
        }
        return claims;
    }
    internal async Task RequireRequestAllocationAsync(IClientSessionHandle tx, BsonDocument allocation, CancellationToken ct) {
        var raw = await RowAsync(tx, B.Decisions, allocation["approvalId"].AsInt64, ct); var proposal = await ProposalAsync(tx, raw["proposalId"].AsInt64, ct);
        var decision = await DecisionAsync(tx, raw["_id"].AsInt64, proposal, ct); Equal(decision["decision"], "approve"); var input = B.Input(proposal["input"]);
        Equal(allocation["orderId"], decision["orderId"]); Equal(allocation["approvalHash"], decision["snapshotHash"]); Equal(allocation["recordedAt"], decision["recordedAt"]);
        Equal(allocation["requestId"], input.RequestId); Equal(allocation["requestDecisionId"], input.RequestDecisionId); Equal(allocation["requestHash"], proposal["requestHash"]); Equal(allocation["requestDecisionHash"], proposal["requestDecisionHash"]);
        var line = input.Lines.SingleOrDefault(value => value.RequestSkuId == allocation["skuId"].AsInt64) ?? throw B.Broken(); Equal(allocation["packages"], line.Packages);
    }
}
