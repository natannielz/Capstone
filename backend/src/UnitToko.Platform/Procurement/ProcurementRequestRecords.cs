using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementRequestRecords(IMongoDatabase database, CatalogAuthoringService catalog, PurchaseOrderSourceReadCapture? sourceCapture = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private async Task<BsonDocument?> OneAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        sourceCapture is null ? await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct) : await sourceCapture.OneAsync(database, tx, name, filter, ct);
    private Task<bool> AnyAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        sourceCapture is null ? Rows(name).Find(tx, filter).AnyAsync(ct) : sourceCapture.AnyAsync(database, tx, name, filter, ct);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    private async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await OneAsync(tx, name, new BsonDocument { { "_id", id }, { "$and", new BsonArray { ProcurementRequestSchemas.Validator(name) } } }, ct) ?? throw B.Broken();
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!people.Select(p => p.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(p => p is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    internal async Task SourcesAsync(IClientSessionHandle tx, ProcurementLocation key, BsonDocument sources, CancellationToken ct) {
        foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, key.OrganizationId), ("shop", ScopeNodeKind.Shop, key.ShopId), ("warehouse", ScopeNodeKind.Warehouse, key.WarehouseId), ("channel", ScopeNodeKind.Channel, key.ChannelId) }) {
            var source = sources[name].AsBsonDocument;
            await new OrganizationProfileRecords(database, sourceCapture).HistoricalAsync(tx, source, ct);
            Equal(source["target"]["id"], id); Equal(source["target"]["kind"], AuthorizationBson.Name(kind));
            if (kind != ScopeNodeKind.FulfillmentOrganization) Equal(source["target"]["parentId"], kind == ScopeNodeKind.Warehouse ? key.ShopId : key.OrganizationId);
        }
        var nodes = B.SourceNames.SelectMany(name => sources[name]["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node)).ToArray();
        if (nodes.GroupBy(node => node.Id).Any(group => group.Distinct().Count() != 1)) throw B.Broken();
    }
    internal async Task<BsonDocument> RequestAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Requests, id, ct); Equal(row["snapshotHash"], B.Hash(row));
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["location"], B.Location(input.Location));
        await SourcesAsync(tx, input.Location, row["sources"].AsBsonDocument, ct); People(row, row["actorId"].AsInt64);
        Equal(row["reason"], input.Reason);
        if (row["catalog"].AsBsonArray.Count != input.Lines.Length) throw B.Broken();
        for (var index = 0; index < input.Lines.Length; index++) {
            var line = input.Lines[index]; var source = row["catalog"][index].AsBsonDocument;
            await catalog.VerifyProcurementSourceAsync(tx, input.Location, source, ct, sourceCapture);
            Equal(source["sku"]["_id"], line.SkuId); Equal(source["sku"]["revision"], line.ExpectedSkuRevision);
            Equal(source["definition"]["_id"], line.DefinitionId); Equal(source["definition"]["version"], line.DefinitionVersion);
            if (source["definition"]["effectiveAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        }
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, row["sources"].AsBsonDocument, row["catalog"].AsBsonArray));
        await EffectsAsync(tx, row, B.SubmitBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Equal(row["snapshotHash"], B.Hash(row));
        var decision = B.Decision(row["decision"]); Equal(row["requestId"], proposal["_id"]); Equal(row["requestHash"], proposal["snapshotHash"]);
        foreach (var field in new[] { "location", "sources" }) Equal(row[field], proposal[field]);
        await SourcesAsync(tx, B.Location(row["location"]), row["reviewSources"].AsBsonDocument, ct);
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal, decision, row["reviewSources"].AsBsonDocument, row["reviewCatalog"].AsBsonArray));
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64);
        var people = row["people"].AsBsonArray.Select(B.Person).ToDictionary(p => p.UserId);
        var maker = proposal["people"].AsBsonArray.Select(B.Person).Single();
        if (people[maker.UserId].PersonId != maker.PersonId || !PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[maker.UserId], maker)) throw B.Broken();
        if (decision == ProcurementRequestDecision.Approve) { Equal(row["reviewSources"], row["sources"]); Equal(row["reviewCatalog"], proposal["catalog"]); }
        else if (row["reviewCatalog"].AsBsonArray.Count != 0) throw B.Broken();
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), ct,
            new ApprovalRequest(1, 1, RequestedBy: proposal["actorId"].AsInt64)); return row;
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await OneAsync(tx, B.Decisions, new BsonDocument("requestId", proposal["_id"]), ct);
        if (row is null) {
            foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
                if (await AnyAsync(tx, table, new BsonDocument { { field, new BsonDocument("$in", new BsonArray { B.Prefix + "approve", B.Prefix + "reject" }) },
                    { result + ".requestId", proposal["_id"] } }, ct)) throw B.Broken();
        }
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task HistoryAsync(IClientSessionHandle tx, ProcurementLocation key, CancellationToken ct) {
        var filter = new BsonDocument("location", B.Location(key)); long proposals = 0, decisions = 0;
        using var cursor = await Rows(B.Requests).Find(tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var stored in cursor.Current) {
            var proposal = await RequestAsync(tx, stored["_id"].AsInt64, ct); proposals++;
            if (await DecisionForAsync(tx, proposal, ct) is not null) decisions++;
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, filter, cancellationToken: ct) != decisions) throw B.Broken();
        foreach (var table in new[] { "auditEvents", "outboxEvents" })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { "location", B.Location(key) }, { "kind", new BsonRegularExpression(@"^procurement\.request\.") } }, cancellationToken: ct) != proposals + decisions) throw B.Broken();
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, CancellationToken ct, ApprovalRequest? approval = null) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        var audit = await OneAsync(tx, "auditEvents", new BsonDocument("_id", row["auditId"]), ct) ?? throw B.Broken(); Equal(audit, B.Audit(row));
        var receipt = await OneAsync(tx, "idempotencyRecords", new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }, ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (approval is not null) { fields = [.. fields, PersonApprovalReceipt.Field]; if (PersonApprovalReceipt.Read(receipt, Permissions.ProcurementApprove) != approval) throw B.Broken(); }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 ||
            receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await OneAsync(tx, "outboxEvents", new BsonDocument("sourceKey", B.SourceKey(row)), ct) ?? throw B.Broken();
        Equal(effect["kind"], row["operation"]); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]);
        Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]); Equal(effect["location"], row["location"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "submit" ? B.Requests : B.Decisions;
        var row = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (table, filter) in new (string, BsonDocument)[] {
                ("idempotencyRecords", new() { { "actorId", actor }, { "operation", operation }, { "key", key } }),
                ("auditEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }),
                ("outboxEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }) })
                if (await Rows(table).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var proposal = await RequestAsync(tx, row["requestId"].AsInt64, ct); return name == B.Requests ? proposal : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
}
