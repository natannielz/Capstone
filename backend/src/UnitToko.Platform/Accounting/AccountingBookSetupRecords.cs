using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Accounting.AccountingBookSetupBson;

namespace UnitToko.Platform.Accounting;

internal sealed class AccountingBookSetupRecords(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    private async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await Rows(name).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { AccountingBookSetupSchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!people.Select(p => p.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(p => p is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    internal async Task SourcesAsync(IClientSessionHandle tx, OperationalBookKey key, BsonDocument sources, CancellationToken ct) {
        foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, key.OrganizationId), ("shop", ScopeNodeKind.Shop, key.ShopId), ("channel", ScopeNodeKind.Channel, key.ChannelId) }) {
            var source = sources[name].AsBsonDocument;
            await new OrganizationProfileRecords(database).HistoricalAsync(tx, source, ct);
            Equal(source["target"]["id"], id); Equal(source["target"]["kind"], AuthorizationBson.Name(kind));
            if (kind != ScopeNodeKind.FulfillmentOrganization) Equal(source["target"]["parentId"], key.OrganizationId);
        }
        var nodes = B.SourceNames.SelectMany(name => sources[name]["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node)).ToArray();
        if (nodes.GroupBy(node => node.Id).Any(group => group.Distinct().Count() != 1)) throw B.Broken();
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); Equal(row["snapshotHash"], B.Hash(row));
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["key"], B.Key(input.Key));
        await SourcesAsync(tx, input.Key, row["sources"].AsBsonDocument, ct); People(row, row["actorId"].AsInt64);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, row["sources"].AsBsonDocument));
        await EffectsAsync(tx, row, B.ProposeBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Equal(row["snapshotHash"], B.Hash(row));
        var decision = B.Decision(row["decision"]); Equal(row["proposalId"], proposal["_id"]); Equal(row["proposalHash"], proposal["snapshotHash"]);
        foreach (var field in new[] { "key", "sources" }) Equal(row[field], proposal[field]);
        await SourcesAsync(tx, B.Key(row["key"]), row["reviewSources"].AsBsonDocument, ct);
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal, decision, row["reviewSources"].AsBsonDocument));
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64);
        var people = row["people"].AsBsonArray.Select(B.Person).ToDictionary(p => p.UserId);
        var maker = proposal["people"].AsBsonArray.Select(B.Person).Single();
        if (people[maker.UserId].PersonId != maker.PersonId || !PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[maker.UserId], maker)) throw B.Broken();
        if (decision == AccountingBookSetupDecision.Approve) {
            Equal(row["reviewSources"], row["sources"]); var input = B.Input(proposal["input"]); var accounts = row["accounts"].AsBsonArray;
            Equal(new BsonArray(accounts.Select(value => new BsonDocument { { "mappingKey", value["mappingKey"] }, { "accountCode", value["accountCode"] } })), B.Mappings(input));
            _ = AccountingBson.ReadChart(B.Chart(row));
            await CoreAsync(tx, proposal, row, ct);
        } else if (row["accounts"].AsBsonArray.Count != 0 || new[] { "bookId", "chartId", "periodId" }.Any(field => !row[field].IsBsonNull)) throw B.Broken();
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), ct,
            new ApprovalRequest(1, 1, ProposedBy: proposal["actorId"].AsInt64)); return row;
    }
    private async Task CoreAsync(IClientSessionHandle tx, BsonDocument proposal, BsonDocument decision, CancellationToken ct) {
        async Task<BsonDocument> Core(string name, BsonValue id) => await Rows(name).Find(tx, new BsonDocument { { "_id", id },
            { "$and", new BsonArray { AccountingSchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var book = await Core("accountingBooks", decision["bookId"]); var chart = await Core("accountingChartVersions", decision["chartId"]);
        var period = await Core("periods", decision["periodId"]);
        Equal(book["key"], decision["key"]); Equal(book["canonicalKey"], B.Key(decision["key"]).CanonicalKey); Equal(book["chartVersionId"], decision["chartId"]);
        Equal(chart, B.Chart(decision)); Equal(period["bookId"], decision["bookId"]); Equal(period["month"], proposal["input"]["initialMonth"]);
        // Normal journal posting advances fences and period history; setup never rewrites them.
        if (period["version"] == 1L) Equal(period, B.Period(decision, proposal));
        else {
            if (period["lastHistoryId"].IsBsonNull) throw B.Broken();
            var last = await Rows("accountingPeriodHistory").Find(tx, new BsonDocument { { "_id", period["lastHistoryId"] },
                { "$and", new BsonArray { AccountingSchemas.Validator("accountingPeriodHistory") } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(last["bookId"], book["_id"]); Equal(last["periodId"], period["_id"]); Equal(last["month"], period["month"]);
            Equal(last["version"], period["version"]); Equal(last["dataRevision"], period["dataRevision"]); Equal(last["snapshotHash"], period["snapshotHash"]);
            Equal(last["stage"], period["state"] == "open" ? "invalidated" : period["state"]);
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument { { "key", decision["key"] }, { "decision", "approve" } }, cancellationToken: ct) != 1) throw B.Broken();
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task<BsonDocument?> ApprovedAsync(IClientSessionHandle tx, OperationalBookKey key, CancellationToken ct) {
        var rows = await Rows(B.Decisions).Find(tx, new BsonDocument { { "key", B.Key(key) }, { "decision", "approve" } }).Limit(2).ToListAsync(ct);
        if (rows.Count > 1) throw B.Broken(); if (rows.Count == 0) return null;
        var proposal = await ProposalAsync(tx, rows[0]["proposalId"].AsInt64, ct); _ = await DecisionAsync(tx, rows[0]["_id"].AsInt64, proposal, ct); return proposal;
    }
    internal async Task HistoryAsync(IClientSessionHandle tx, OperationalBookKey key, CancellationToken ct) {
        var filter = new BsonDocument("key", B.Key(key)); long proposals = 0, decisions = 0;
        using var cursor = await Rows(B.Proposals).Find(tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var stored in cursor.Current) {
            var proposal = await ProposalAsync(tx, stored["_id"].AsInt64, ct); proposals++;
            if (await DecisionForAsync(tx, proposal, ct) is not null) decisions++;
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, filter, cancellationToken: ct) != decisions) throw B.Broken();
        foreach (var table in new[] { "auditEvents", "outboxEvents" })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { "key", B.Key(key) }, { "kind", new BsonRegularExpression(@"^accounting\.book\.") } }, cancellationToken: ct) != proposals + decisions) throw B.Broken();
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, CancellationToken ct, ApprovalRequest? approval = null) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(audit, B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (approval is not null) { fields = [.. fields, PersonApprovalReceipt.Field]; if (PersonApprovalReceipt.Read(receipt, Permissions.AccountingBookApprove) != approval) throw B.Broken(); }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 ||
            receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"], row["operation"]); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]);
        Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]); Equal(effect["key"], row["key"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "propose" ? B.Proposals : B.Decisions;
        var row = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (table, filter) in new (string, BsonDocument)[] {
                ("idempotencyRecords", new() { { "actorId", actor }, { "operation", operation }, { "key", key } }),
                ("auditEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }),
                ("outboxEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }) })
                if (await Rows(table).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct); return name == B.Proposals ? proposal : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
}
