using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Platform.Organizations;

/// <summary>Actual approved profile provenance. This proves postal source identity, not legal registration or tax identity.</summary>
internal sealed class OrganizationProfileRecords(IMongoDatabase database, PurchaseOrderSourceReadCapture? sourceCapture = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private async Task<BsonDocument?> OneAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        sourceCapture is null ? await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct) : await sourceCapture.OneAsync(database, tx, name, filter, ct);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal static BsonDocument Node(ScopeNode node) => new() { { "id", node.Id }, { "kind", B.Kind(node.Kind) },
        { "parentId", node.ParentId is { } parent ? new BsonInt64(parent) : BsonNull.Value }, { "status", "active" }, { "version", node.Version } };
    internal static ScopeNode Node(BsonValue row) => new(row["id"].AsInt64, B.Kind(row["kind"]), row["parentId"].IsBsonNull ? null : row["parentId"].AsInt64,
        row["status"] == "active" ? AuthorizationStatus.Active : throw B.Broken(), row["version"].AsInt64);
    internal async Task<BsonDocument> ProfileAsync(IClientSessionHandle tx, long id, CancellationToken ct) =>
        await Rows("organizationProfiles").Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { LifecycleSchemas.Collections()["organizationProfiles"] } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal async Task<(BsonDocument Profile, BsonDocument History)> ApprovedProfileAsync(IClientSessionHandle tx, long id, long version, CancellationToken ct) {
        var historyFilter = new BsonDocument { { "operation", "organization.approved" }, { "after.profile._id", id }, { "after.profile.version", version } };
        var histories = await Rows("authorizationHistory").Find(tx, historyFilter).Limit(2).ToListAsync(ct);
        sourceCapture?.Record("authorizationHistory", historyFilter, histories);
        if (histories.Count != 1) throw B.Broken();
        var history = histories[0]; var after = history["after"].AsBsonDocument; var profile = after["profile"].AsBsonDocument;
        var proposal = after["proposal"].AsBsonDocument; var before = history["before"].AsBsonDocument; var pending = before["proposal"].AsBsonDocument;
        var raw = await OneAsync(tx, "organizationProposals", new BsonDocument { { "_id", proposal["_id"] },
            { "$and", new BsonArray { LifecycleSchemas.Collections()["organizationProposals"] } } }, ct) ?? throw B.Broken();
        Equal(raw, proposal); Equal(proposal["status"], "approved"); Equal(proposal["resultScopeId"], id);
        Equal(proposal["approvedBy"], history["actorId"]); Equal(proposal["approvedAt"], history["createdAt"]);
        Equal(proposal["version"], 2L); Equal(pending["version"], 1L); Equal(pending["status"], "pending");
        var expectedPending = proposal.DeepClone().AsBsonDocument;
        foreach (var field in new[] { "approvedBy", "approvedAt", "resultScopeId" }) expectedPending.Remove(field);
        expectedPending["status"] = "pending"; expectedPending["version"] = 1L; Equal(expectedPending, pending);
        if (proposal["requestedBy"] == history["actorId"] || proposal["createdAt"].ToUniversalTime() > history["createdAt"].ToUniversalTime()) throw B.Broken();
        Equal(profile["_id"], id); Equal(profile["approvedProposalId"], proposal["_id"]); Equal(profile["updatedBy"], history["actorId"]);
        Equal(profile["updatedAt"], history["createdAt"]); Equal(profile["version"], checked(proposal["expectedProfileVersion"].AsInt64 + 1));
        foreach (var field in new[] { "kind", "parentId", "code", "name" }) Equal(profile.GetValue(field, BsonNull.Value), proposal.GetValue(field, BsonNull.Value));
        Equal(profile["normalizedCode"], profile["code"]);
        var node = AuthorizationBson.Node(after["scope"].AsBsonDocument);
        if (node.Id != id || B.Kind(node.Kind) != profile["kind"].AsString || node.ParentId != (profile["parentId"].IsBsonNull ? null : profile["parentId"].AsInt64)) throw B.Broken();
        if (before["profile"].IsBsonNull) {
            Equal(profile["version"], 1L); Equal(profile["createdBy"], history["actorId"]); Equal(profile["createdAt"], history["createdAt"]);
        } else {
            var previous = before["profile"].AsBsonDocument; Equal(previous["version"], proposal["expectedProfileVersion"]);
            Equal(previous["_id"], id); Equal(profile["createdBy"], previous["createdBy"]); Equal(profile["createdAt"], previous["createdAt"]);
            if (previous["updatedAt"].ToUniversalTime() > profile["updatedAt"].ToUniversalTime()) throw B.Broken();
        }
        var request = OrganizationChangePolicy.Normalize(new(Enum.Parse<OrganizationChangeKind>(proposal["changeKind"].AsString, true),
            B.Kind(proposal["kind"]), proposal["parentId"].IsBsonNull ? null : proposal["parentId"].AsInt64,
            proposal["code"].AsString, proposal["name"].AsString, proposal["scopeId"].IsBsonNull ? null : proposal["scopeId"].AsInt64,
            proposal["expectedScopeVersion"].AsInt64, proposal["expectedProfileVersion"].AsInt64));
        if (request.Name != proposal["name"].AsString || request.Code != proposal["code"].AsString || node.Version != request.ExpectedScopeVersion + 1) throw B.Broken();
        var proposedFilter = new BsonDocument { { "operation", "organization.proposed" },
            { "actorId", proposal["requestedBy"] }, { "after._id", proposal["_id"] } };
        var proposed = await Rows("authorizationHistory").Find(tx, proposedFilter).Limit(2).ToListAsync(ct);
        sourceCapture?.Record("authorizationHistory", proposedFilter, proposed);
        if (proposed.Count != 1) throw B.Broken(); Equal(proposed[0]["after"], pending); Equal(proposed[0]["createdAt"], proposal["createdAt"]);
        await ReceiptAsync(tx, proposed[0], proposal["_id"].AsInt64, "organization.propose", request,
            new BsonDocument { { "id", proposal["_id"] }, { "subjectId", proposal["scopeId"].IsBsonNull ? proposal["_id"] : proposal["scopeId"] }, { "kind", "organization" },
                { "version", 1L }, { "status", "pending" }, { "scopeId", proposal["scopeId"] }, { "scopeVersion", BsonNull.Value }, { "profileVersion", BsonNull.Value }, { "affectedAccounts", 0L } }, ct);
        await ReceiptAsync(tx, history, proposal["_id"].AsInt64, "organization.approve", new { proposalId = proposal["_id"].AsInt64, expectedVersion = 1L },
            new BsonDocument { { "id", proposal["_id"] }, { "subjectId", id }, { "kind", "organization" }, { "version", 2L }, { "status", "approved" },
                { "scopeId", id }, { "scopeVersion", node.Version }, { "profileVersion", profile["version"] }, { "affectedAccounts", proposal["impact"]["affectedAccounts"] } }, ct);
        return (profile.DeepClone().AsBsonDocument, history);
    }
    private async Task ReceiptAsync(IClientSessionHandle tx, BsonDocument history, long proposalId, string operation, object body, BsonDocument result, CancellationToken ct) {
        var filter = new BsonDocument { { "actorId", history["actorId"] }, { "operation", operation }, { "result.id", proposalId } };
        var rows = await Rows("idempotencyRecords").Find(tx, filter).Limit(2).ToListAsync(ct);
        sourceCapture?.Record("idempotencyRecords", filter, rows);
        if (rows.Count != 1) throw B.Broken(); var row = rows[0];
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (row.ElementCount != fields.Length || fields.Any(field => !row.Contains(field)) || !row["_id"].IsObjectId || row["schemaVersion"] != 1 ||
            !row["key"].IsString || row["key"].AsString.Length is < 1 or > 200 || row["createdAt"].ToUniversalTime() > history["createdAt"].ToUniversalTime()) throw B.Broken();
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, history["reason"].AsString)); Equal(row["result"], result);
    }
    internal async Task<BsonDocument> CurrentAsync(IClientSessionHandle tx, OrganizationAddressTarget target, CancellationToken ct) {
        var profile = await ProfileAsync(tx, target.Id, ct); var approved = await ApprovedProfileAsync(tx, target.Id, profile["version"].AsInt64, ct);
        Equal(profile, approved.Profile); var hierarchy = new List<ScopeNode>(); var id = target.Id; var kind = target.Kind;
        while (true) {
            var raw = await Rows("authorizationScopes").Find(tx, new BsonDocument { { "_id", id }, { "kind", B.Kind(kind) } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var node = AuthorizationBson.Node(raw); if (node.Status != AuthorizationStatus.Active || node.Version >= long.MaxValue) throw B.Changed();
            hierarchy.Add(node); var parentKind = OrganizationChangePolicy.ExpectedParent(node.Kind);
            if (parentKind is null) { if (node.ParentId is not null) throw B.Broken(); break; }
            if (node.ParentId is not > 0 || hierarchy.Count >= 3) throw B.Broken(); id = node.ParentId.Value; kind = parentKind.Value;
        }
        var first = hierarchy[0]; if (profile["kind"] != B.Kind(first.Kind) || profile.GetValue("parentId", BsonNull.Value) != (first.ParentId is { } parent ? new BsonInt64(parent) : BsonNull.Value)) throw B.Broken();
        var history = approved.History;
        var source = new BsonDocument { { "target", Node(first) }, { "profile", new BsonDocument {
            { "id", target.Id }, { "kind", profile["kind"] }, { "parentId", profile["parentId"] }, { "code", profile["code"] }, { "name", profile["name"] },
            { "version", profile["version"] }, { "approvedProposalId", profile["approvedProposalId"] }, { "approvalHistoryId", history["_id"] },
            { "approvalHistoryHash", B.Hash(history) }, { "approvedBy", history["actorId"] }, { "approvedAt", history["createdAt"] } } },
            { "hierarchy", new BsonArray(hierarchy.OrderBy(node => node.Id).Select(Node)) } };
        source.Add("snapshotHash", B.Hash(source)); return source;
    }
    internal async Task HistoricalAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        Equal(source["snapshotHash"], B.Hash(source)); var profile = source["profile"].AsBsonDocument;
        var approved = await ApprovedProfileAsync(tx, profile["id"].AsInt64, profile["version"].AsInt64, ct);
        var actual = approved.Profile; var history = approved.History;
        foreach (var field in new[] { "kind", "parentId", "code", "name", "version", "approvedProposalId" }) Equal(profile[field], actual[field]);
        Equal(profile["approvalHistoryId"], history["_id"]); Equal(profile["approvalHistoryHash"], B.Hash(history));
        Equal(profile["approvedBy"], history["actorId"]); Equal(profile["approvedAt"], history["createdAt"]);
        var nodes = source["hierarchy"].AsBsonArray.Select(Node).ToArray();
        if (nodes.Length is < 1 or > 3 || !nodes.Select(node => node.Id).SequenceEqual(nodes.Select(node => node.Id).Distinct().Order())) throw B.Broken();
        var target = Node(source["target"]); if (!nodes.Contains(target) || target.Id != profile["id"].AsInt64 || B.Kind(target.Kind) != profile["kind"].AsString) throw B.Broken();
        var cursor = target; var visited = new HashSet<long>();
        while (true) {
            if (!visited.Add(cursor.Id)) throw B.Broken(); var parentKind = OrganizationChangePolicy.ExpectedParent(cursor.Kind);
            if (parentKind is null) { if (cursor.ParentId is not null) throw B.Broken(); break; }
            cursor = nodes.SingleOrDefault(node => node.Id == cursor.ParentId && node.Kind == parentKind) ?? throw B.Broken();
        }
        if (visited.Count != nodes.Length) throw B.Broken();
        foreach (var node in nodes) {
            var current = await OneAsync(tx, "authorizationScopes", new BsonDocument("_id", node.Id), ct) ?? throw B.Broken();
            var actualNode = AuthorizationBson.Node(current);
            if (actualNode.Kind != node.Kind || actualNode.ParentId != node.ParentId || actualNode.Version < node.Version) throw B.Broken();
        }
    }
}

public sealed partial class OrganizationMasterDataService
{
    internal IMongoDatabase AddressSourceDatabase => database;
    internal Task<BsonDocument> ReadAddressProfileAsync(IClientSessionHandle tx, OrganizationAddressTarget target, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        return new OrganizationProfileRecords(database).CurrentAsync(tx, target, ct);
    }
    internal Task VerifyAddressProfileAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct) => new OrganizationProfileRecords(database).HistoricalAsync(tx, source, ct);
    internal async Task FenceAddressProfileAsync(IClientSessionHandle tx, BsonDocument captured, bool requireCurrent, CancellationToken ct) {
        await VerifyAddressProfileAsync(tx, captured, ct);
        var target = OrganizationProfileRecords.Node(captured["target"]); var current = await ReadAddressProfileAsync(tx, new(target.Kind, target.Id), ct);
        if (requireCurrent && !current.Equals(captured)) throw B.Changed();
        // Profile changes already conditionally WRITE the target node in the existing governance
        // transaction. Consume that real contention boundary; a no-op profile update is not a fence.
        foreach (var value in current["hierarchy"].AsBsonArray) {
            var node = OrganizationProfileRecords.Node(value);
            var row = await Collection("authorizationScopes").Find(tx, new BsonDocument("_id", node.Id)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (AuthorizationBson.Node(row) != node || row["fence"].AsInt64 >= long.MaxValue - 1 ||
                (await Collection("authorizationScopes").UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        }
    }
    internal static async Task PreflightAddressSourcesAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) {
        var client = database.Client as MongoClient ?? throw B.Broken();
        using var tx = await client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        var records = new OrganizationProfileRecords(database);
        using var cursor = await database.GetCollection<BsonDocument>("organizationProfiles").Find(tx, new BsonDocument()).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) {
            await lease.RenewAsync(ct); var actual = await records.ProfileAsync(tx, row["_id"].AsInt64, ct);
            var approved = await records.ApprovedProfileAsync(tx, actual["_id"].AsInt64, actual["version"].AsInt64, ct);
            if (!approved.Profile.Equals(actual)) throw B.Broken();
            var node = await database.GetCollection<BsonDocument>("authorizationScopes").Find(tx, new BsonDocument("_id", actual["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var parsed = AuthorizationBson.Node(node);
            if (B.Kind(parsed.Kind) != actual["kind"].AsString || parsed.ParentId != (actual["parentId"].IsBsonNull ? null : actual["parentId"].AsInt64)) throw B.Broken();
        }
        await tx.AbortTransactionAsync(ct);
    }
}
