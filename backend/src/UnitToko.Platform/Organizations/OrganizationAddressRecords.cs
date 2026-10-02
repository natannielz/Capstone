using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Platform.Organizations;

/// <summary>Immutable structural provenance only. Callers must authorize and fence the real target and current people.</summary>
internal sealed class OrganizationAddressRecords(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) {
        if (id <= 0) throw B.Broken();
        return await Rows(name).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { OrganizationAddressSchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    }
    private static void Hash(BsonDocument row) => Equal(row["snapshotHash"], B.Hash(row));
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!people.Select(p => p.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(p => p is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    private async Task SourceAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var target = B.Target(row["target"]); var purpose = B.Purpose(row["purpose"]); OrganizationAddressPolicy.RequireTarget(target, purpose);
        Equal(row["details"], B.Details(B.Details(row["details"])));
        var source = row["source"].AsBsonDocument; await new OrganizationProfileRecords(database).HistoricalAsync(tx, source, ct);
        Equal(source["target"]["id"], target.Id); Equal(source["target"]["kind"], B.Kind(target.Kind));
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); Hash(row); await SourceAsync(tx, row, ct);
        Equal(row["proposalId"], id); Equal(row["operation"], B.Prefix + "propose"); Equal(row["input"], B.Input(B.Input(row["input"])));
        var input = B.Input(row["input"]); Equal(row["details"], B.Details(input.Address)); Equal(row["target"], B.Target(input.Target)); Equal(row["purpose"], B.Purpose(input.Purpose));
        Equal(row["source"]["target"]["version"], input.ExpectedScopeVersion); Equal(row["source"]["profile"]["version"], input.ExpectedProfileVersion);
        Equal(row["baseVersion"], input.ExpectedAddressVersion); People(row, row["actorId"].AsInt64);
        var head = await RowAsync(tx, B.Heads, row["addressId"].AsInt64, ct); Equal(head["target"], row["target"]); Equal(head["purpose"], row["purpose"]);
        BsonDocument? baseline = null;
        if (head["rootProposalId"] != id) {
            BsonValue versionId = BsonNull.Value;
            if (input.ExpectedAddressVersion > 0) {
                var version = await Rows(B.Versions).Find(tx, new BsonDocument { { "addressId", head["_id"] }, { "version", input.ExpectedAddressVersion } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                Hash(version); versionId = version["_id"];
                if (version["effectiveAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
            }
            baseline = new() { { "_id", head["_id"] }, { "version", input.ExpectedAddressVersion }, { "versionId", versionId } };
        } else { Equal(input.ExpectedAddressVersion, 0L); Equal(head["createdAt"], row["recordedAt"]); }
        Equal(row["binding"], B.Binding(input, row["source"].AsBsonDocument, baseline));
        await EffectsAsync(tx, row, B.ProposeBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Hash(row); await SourceAsync(tx, row, ct);
        var decision = B.Decision(row["decision"]); Equal(row["operation"], B.Prefix + B.Decision(decision));
        foreach (var field in new[] { "addressId", "proposalId", "target", "purpose", "source", "details", "baseVersion" })
            Equal(row[field], field == "proposalId" ? proposal["_id"] : proposal[field]);
        Equal(row["input"], B.DecisionInput(proposal["_id"].AsInt64, 1, decision)); Equal(row["binding"], B.DecisionBinding(proposal, decision));
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64);
        var people = row["people"].AsBsonArray.Select(B.Person).ToDictionary(p => p.UserId);
        var originalMaker = proposal["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
        if (people[originalMaker.UserId].PersonId != originalMaker.PersonId) throw B.Broken();
        if (!PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[proposal["actorId"].AsInt64], originalMaker)) throw B.Broken();
        if (decision == OrganizationAddressDecision.Approve) Equal(row["addressVersion"], checked(proposal["baseVersion"].AsInt64 + 1));
        else { Equal(row["versionId"], BsonNull.Value); Equal(row["addressVersion"], BsonNull.Value); }
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), ct); return row;
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await RowAsync(tx, B.Heads, id, ct); var root = await ProposalAsync(tx, head["rootProposalId"].AsInt64, ct);
        Equal(root["addressId"], id); Equal(root["target"], head["target"]); Equal(root["purpose"], head["purpose"]); Equal(root["recordedAt"], head["createdAt"]);
        long number = 0; BsonDocument? previous = null;
        using var cursor = await Rows(B.Versions).Find(tx, new BsonDocument("addressId", id)).Sort(new BsonDocument("version", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var stored in cursor.Current) {
            var version = await RowAsync(tx, B.Versions, stored["_id"].AsInt64, ct); Hash(version); await SourceAsync(tx, version, ct);
            Equal(version["version"], ++number); Equal(version["target"], head["target"]); Equal(version["purpose"], head["purpose"]);
            Equal(version["previousVersionId"], previous is null ? BsonNull.Value : previous["_id"]); Equal(version["previousHash"], previous is null ? BsonNull.Value : previous["snapshotHash"]);
            var proposal = await ProposalAsync(tx, version["proposalId"].AsInt64, ct); var decision = await DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
            Equal(decision["decision"], "approve"); Equal(decision["versionId"], version["_id"]); Equal(decision["addressVersion"], number);
            foreach (var field in new[] { "addressId", "target", "purpose", "source", "details" }) Equal(version[field], proposal[field]);
            Equal(version["effectiveAt"], decision["recordedAt"]);
            if (previous is not null && previous["effectiveAt"].ToUniversalTime() > version["effectiveAt"].ToUniversalTime()) throw B.Broken();
            previous = version;
        }
        Equal(head["version"], number); Equal(head["versionId"], previous is null ? BsonNull.Value : previous["_id"]);
        Equal(head["updatedAt"], previous is null ? head["createdAt"] : previous["effectiveAt"]);
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument { { "addressId", id }, { "decision", "approve" } }, cancellationToken: ct) != number) throw B.Broken();
        return head;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(audit, B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
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
    internal static async Task PreflightAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) {
        var client = database.Client as MongoClient ?? throw B.Broken(); using var tx = await client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority)); var records = new OrganizationAddressRecords(database);
        foreach (var name in OrganizationAddressSchemas.Collections) {
            long maximum = 0;
            using var cursor = await database.GetCollection<BsonDocument>(name).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) {
                await lease.RenewAsync(ct); var id = row["_id"].AsInt64; maximum = Math.Max(maximum, id);
                if (name == B.Heads) await records.HeadAsync(tx, id, ct);
                else if (name == B.Proposals) { await records.ProposalAsync(tx, id, ct); await records.HeadAsync(tx, row["addressId"].AsInt64, ct); }
                else if (name == B.Decisions) { var proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, ct); await records.DecisionAsync(tx, id, proposal, ct); await records.HeadAsync(tx, row["addressId"].AsInt64, ct); }
                else { await records.RowAsync(tx, name, id, ct); await records.HeadAsync(tx, row["addressId"].AsInt64, ct); }
            }
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(tx, new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (maximum > 0 && (counter is null || !counter["value"].IsInt64 || counter["value"].AsInt64 < maximum)) throw B.Broken();
        }
        var count = await database.GetCollection<BsonDocument>(B.Proposals).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) +
            await database.GetCollection<BsonDocument>(B.Decisions).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct);
        var prefix = new BsonRegularExpression(@"^organization\.address\.");
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await database.GetCollection<BsonDocument>(name).CountDocumentsAsync(tx, new BsonDocument(field, prefix), cancellationToken: ct) != count) throw B.Broken();
        await tx.AbortTransactionAsync(ct);
    }
}
