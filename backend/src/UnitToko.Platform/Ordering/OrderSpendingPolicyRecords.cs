using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSpendingPolicyBson;

namespace UnitToko.Platform.Ordering;

/// <summary>Immutable structural provenance only. Callers must authorize and fence the real target and current people.</summary>
internal sealed class OrderSpendingPolicyRecords(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) {
        if (id <= 0) throw B.Broken();
        return await Rows(name).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { OrderSpendingPolicySchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    }
    private static void Hash(BsonDocument row) => Equal(row["snapshotHash"], B.Hash(row));
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!people.Select(p => p.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(p => p is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    private async Task SourceAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var target = B.Target(row["target"]); OrderSpendingPolicyRules.RequireTarget(target);
        Equal(row["details"], B.Details(row["details"]));
        var source = row["source"].AsBsonDocument; await new OrganizationProfileRecords(database).HistoricalAsync(tx, source, ct);
        Equal(source["target"]["id"], target.Id); Equal(source["target"]["kind"], B.Kind(target.Kind));
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); Hash(row); await SourceAsync(tx, row, ct);
        Equal(row["proposalId"], id); Equal(row["operation"], B.Prefix + "propose"); Equal(row["input"], B.Input(B.Input(row["input"])));
        var input = B.Input(row["input"]); Equal(row["details"], B.Details(input)); Equal(row["target"], B.Target(input.Target));
        Equal(row["source"]["target"]["version"], input.ExpectedScopeVersion); Equal(row["source"]["profile"]["version"], input.ExpectedProfileVersion);
        Equal(row["baseVersion"], input.ExpectedPolicyVersion); People(row, row["actorId"].AsInt64);
        var head = await RowAsync(tx, B.Heads, row["policyId"].AsInt64, ct); Equal(head["target"], row["target"]);
        BsonDocument? baseline = null;
        if (head["rootProposalId"] != id) {
            BsonValue versionId = BsonNull.Value;
            if (input.ExpectedPolicyVersion > 0) {
                var version = await Rows(B.Versions).Find(tx, new BsonDocument { { "policyId", head["_id"] }, { "version", input.ExpectedPolicyVersion } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                Hash(version); versionId = version["_id"];
                if (version["effectiveAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
            }
            baseline = new() { { "_id", head["_id"] }, { "version", input.ExpectedPolicyVersion }, { "versionId", versionId } };
        } else { Equal(input.ExpectedPolicyVersion, 0L); Equal(head["createdAt"], row["recordedAt"]); }
        Equal(row["binding"], B.Binding(input, row["source"].AsBsonDocument, baseline));
        await EffectsAsync(tx, row, B.ProposeBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Hash(row); await SourceAsync(tx, row, ct);
        var decision = B.Decision(row["decision"]); Equal(row["operation"], B.Prefix + B.Decision(decision));
        foreach (var field in new[] { "policyId", "proposalId", "target", "source", "details", "baseVersion" })
            Equal(row[field], field == "proposalId" ? proposal["_id"] : proposal[field]);
        Equal(row["input"], B.DecisionInput(proposal["_id"].AsInt64, 1, decision)); Equal(row["binding"], B.DecisionBinding(proposal, decision));
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64);
        var people = row["people"].AsBsonArray.Select(B.Person).ToDictionary(p => p.UserId);
        var originalMaker = proposal["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
        if (people[originalMaker.UserId].PersonId != originalMaker.PersonId) throw B.Broken();
        if (!PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[proposal["actorId"].AsInt64], originalMaker)) throw B.Broken();
        if (decision == OrderSpendingPolicyDecision.Approve) Equal(row["policyVersion"], checked(proposal["baseVersion"].AsInt64 + 1));
        else { Equal(row["versionId"], BsonNull.Value); Equal(row["policyVersion"], BsonNull.Value); }
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), ct,
            new ApprovalRequest(1, 1, ProposedBy: proposal["actorId"].AsInt64)); return row;
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await RowAsync(tx, B.Heads, id, ct); var root = await ProposalAsync(tx, head["rootProposalId"].AsInt64, ct);
        Equal(root["policyId"], id); Equal(root["target"], head["target"]); Equal(root["recordedAt"], head["createdAt"]);
        long number = 0; BsonDocument? previous = null;
        using var cursor = await Rows(B.Versions).Find(tx, new BsonDocument("policyId", id)).Sort(new BsonDocument("version", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var stored in cursor.Current) {
            var version = await RowAsync(tx, B.Versions, stored["_id"].AsInt64, ct); Hash(version); await SourceAsync(tx, version, ct);
            Equal(version["version"], ++number); Equal(version["target"], head["target"]);
            Equal(version["previousVersionId"], previous is null ? BsonNull.Value : previous["_id"]); Equal(version["previousHash"], previous is null ? BsonNull.Value : previous["snapshotHash"]);
            var proposal = await ProposalAsync(tx, version["proposalId"].AsInt64, ct); var decision = await DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
            Equal(decision["decision"], "approve"); Equal(decision["versionId"], version["_id"]); Equal(decision["policyVersion"], number);
            foreach (var field in new[] { "policyId", "target", "source", "details" }) Equal(version[field], proposal[field]);
            Equal(version["effectiveAt"], decision["recordedAt"]);
            if (previous is not null && previous["effectiveAt"].ToUniversalTime() > version["effectiveAt"].ToUniversalTime()) throw B.Broken();
            previous = version;
        }
        Equal(head["version"], number); Equal(head["versionId"], previous is null ? BsonNull.Value : previous["_id"]);
        Equal(head["updatedAt"], previous is null ? head["createdAt"] : previous["effectiveAt"]);
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument { { "policyId", id }, { "decision", "approve" } }, cancellationToken: ct) != number) throw B.Broken();
        return head;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, CancellationToken ct, ApprovalRequest? approval = null) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(audit, B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (approval is not null) {
            fields = [.. fields, PersonApprovalReceipt.Field];
            if (PersonApprovalReceipt.Read(receipt, Permissions.OrderSpendingPolicyApprove) != approval) throw B.Broken();
        }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 ||
            receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"], row["operation"]); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]);
        Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "propose" ? B.Proposals : B.Decisions;
        var row = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (collection, filter) in new (string, BsonDocument)[] {
                ("idempotencyRecords", new() { { "actorId", actor }, { "operation", operation }, { "key", key } }),
                ("auditEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }),
                ("outboxEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }) })
                if (await Rows(collection).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct);
        return name == B.Proposals ? proposal : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
}
