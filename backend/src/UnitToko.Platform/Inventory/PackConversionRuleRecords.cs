using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;

namespace UnitToko.Platform.Inventory;

internal sealed class PackConversionRuleRecords(IMongoDatabase database, IClock clock)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private readonly PackConversionRuleCatalogue catalog = new(database);
    private async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        return await Rows(name).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { PackConversionRuleSchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, CancellationToken ct) {
        if (!StockCountBson.Key(row["requestKey"].AsString) || !StockCountPolicy.CanonicalText(row["reason"].AsString, 1000) ||
            !StockCountPolicy.CanonicalText(row["correlationId"].AsString, 128) || row["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) || row["snapshotHash"] != B.Hash(row) ||
            row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        if (audit is null || audit != B.Audit(row) || receipt is null || receipt.ElementCount != 8 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 ||
            receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != B.Result(row) || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", row["operation"].AsString + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct);
        if (effect is null || effect["kind"] != row["operation"] || effect["actorId"] != row["actorId"] || effect["requestKey"] != row["requestKey"] ||
            effect["createdAt"] != row["recordedAt"] || effect["payload"] != B.Result(row)) throw B.Broken();
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); var input = B.Input(row["input"].AsBsonDocument);
        if (row["operation"] != B.Propose || row["input"] != B.Input(input) || row["binding"] != B.Binding(input, row["catalog"].AsBsonDocument)) throw B.Broken();
        var person = row["people"].AsBsonArray;
        if (person.Count != 1 || person[0]["userId"] != row["actorId"] || person[0]["verification"] != "Verified") throw B.Broken();
        await catalog.HistoricalAsync(tx, input, row["catalog"].AsBsonDocument, ct);
        await EffectsAsync(tx, row, B.ProposeBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<BsonDocument?> DecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var found = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (found is null) {
            if (await Rows(B.Rules).Find(tx, new BsonDocument("proposalId", proposal["_id"])).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var row = await RowAsync(tx, B.Decisions, found["_id"].AsInt64, ct);
        var decision = row["decision"] == "approve" ? PackConversionRuleDecision.Approve : PackConversionRuleDecision.Reject;
        if (row["operation"] != B.Decide || row["proposalHash"] != proposal["snapshotHash"] || row["binding"] != B.DecisionBinding(proposal, decision) ||
            row["recordedAt"].CompareTo(proposal["recordedAt"]) < 0 || row["actorId"] == proposal["actorId"] ||
            row["ruleId"] != (decision == PackConversionRuleDecision.Approve ? proposal["_id"] : BsonNull.Value)) throw B.Broken();
        var people = row["people"].AsBsonArray.Select(Person).ToArray(); var maker = Person(proposal["people"][0]);
        if (people.Length != 2 || !people.Select(person => person.UserId).Order().SequenceEqual(new[] { row["actorId"].AsInt64, proposal["actorId"].AsInt64 }.Order()) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == row["actorId"].AsInt64), people.Single(person => person.UserId == maker.UserId), maker)) throw B.Broken();
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), ct);
        var rule = await Rows(B.Rules).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (decision == PackConversionRuleDecision.Approve) {
            if (rule is null || rule != B.Rule(proposal, row) || !await Rows(B.Rules).Find(tx, new BsonDocument { { "_id", rule["_id"] },
                { "$and", new BsonArray { PackConversionRuleSchemas.Validator(B.Rules) } } }).AnyAsync(ct)) throw B.Broken();
        } else if (rule is not null) throw B.Broken();
        return row;
    }
    internal async Task<(BsonDocument Proposal, BsonDocument Decision, BsonDocument Rule)> RuleAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var rule = await RowAsync(tx, B.Rules, id, ct); var proposal = await ProposalAsync(tx, rule["proposalId"].AsInt64, ct);
        var decision = await DecisionAsync(tx, proposal, ct) ?? throw B.Broken(); if (decision["decision"] != "approve" || rule != B.Rule(proposal, decision)) throw B.Broken();
        return (proposal, decision, rule);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var table = operation == B.Propose ? B.Proposals : operation == B.Decide ? B.Decisions : throw PackConversionRulePolicy.Invalid();
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (name, field, selector) in new[] { ("idempotencyRecords", "key", "operation"), ("auditEvents", "requestKey", "kind"), ("outboxEvents", "requestKey", "kind") })
                if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { selector, operation }, { field, key } }).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        if (table == B.Proposals) return await ProposalAsync(tx, row["_id"].AsInt64, ct);
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct);
        var decision = await DecisionAsync(tx, proposal, ct) ?? throw B.Broken(); if (decision != row) throw B.Broken(); return decision;
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var table in new[] { B.Decisions, B.Rules }) {
            var rows = await Rows(table).Find(tx, new BsonDocument()).Limit(4097).ToListAsync(ct); if (rows.Count > 4096) throw B.Broken();
            foreach (var row in rows) if (!await Rows(B.Proposals).Find(tx, new BsonDocument("_id", row["proposalId"])).AnyAsync(ct)) throw B.Broken();
        }
        foreach (var operation in new[] { B.Propose, B.Decide }) {
            var expected = await Rows(operation == B.Propose ? B.Proposals : B.Decisions).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct);
            if (expected > 4096) throw B.Broken();
            foreach (var (table, selector) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument(selector, operation), cancellationToken: ct) != expected) throw B.Broken();
        }
    }
    internal static BsonDocument Person(PersonEvidence value) => new() { { "userId", value.UserId }, { "personId", value.PersonId },
        { "verification", value.Verification.ToString() }, { "version", value.Version } };
    internal static PersonEvidence Person(BsonValue value) => new(value["userId"].AsInt64, value["personId"].AsInt64,
        value["verification"] == "Verified" ? PersonVerification.Verified : PersonVerification.Unknown, value["version"].AsInt64);
}
