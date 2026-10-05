using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

internal sealed partial class SupplierRecords(IMongoDatabase database, CatalogAuthoringService catalog, PurchaseOrderSourceReadCapture? sourceCapture = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private async Task<BsonDocument?> OneAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        sourceCapture is null ? await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct) : await sourceCapture.OneAsync(database, tx, name, filter, ct);
    private Task<bool> AnyAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        sourceCapture is null ? Rows(name).Find(tx, filter).AnyAsync(ct) : sourceCapture.AnyAsync(database, tx, name, filter, ct);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await OneAsync(tx, name, new BsonDocument { { "_id", id }, { "$and", new BsonArray { SupplierSchemas.Validator(name) } } }, ct) ?? throw B.Broken();
    internal static void MatchPolicy(BsonDocument policy, SupplierDetails details) {
        foreach (var field in new[] { "subjectReference", "legalName", "registrationNamespace", "registrationValue" }) Equal(policy[field], B.Details(details)[field]);
    }
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset now) {
        if (!policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > now.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= now.UtcDateTime ||
            policy["maxObservationAgeSeconds"].AsInt64 is < 1 or > 31536000) throw B.Changed();
    }
    internal static void ObservationAt(BsonDocument proposal, BsonDocument observation, DateTimeOffset now) {
        var policy = proposal["policy"].AsBsonDocument; PolicyAt(policy, now);
        var checkedAt = new DateTimeOffset(observation["checkedAt"].ToUniversalTime());
        if (checkedAt > now || now - checkedAt >= TimeSpan.FromSeconds(policy["maxObservationAgeSeconds"].AsInt64)) throw B.Changed();
    }
    private static void People(BsonDocument row, params long[] users) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!people.Select(person => person.UserId).SequenceEqual(users.Distinct().Order()) || people.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    private static void Independent(BsonDocument row, BsonDocument proposal, BsonDocument? observation = null) {
        var people = row["people"].AsBsonArray.Select(B.Person).ToDictionary(person => person.UserId); var maker = proposal["people"].AsBsonArray.Select(B.Person).Single();
        if (people[maker.UserId].PersonId != maker.PersonId || !PersonIndependencePolicy.AreIndependent(people[row["actorId"].AsInt64], people[maker.UserId], maker)) throw B.Broken();
        if (observation is not null) {
            var observer = observation["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == observation["actorId"].AsInt64);
            if (people[observer.UserId].PersonId != observer.PersonId || !PersonIndependencePolicy.AreIndependent(people[observer.UserId], people[maker.UserId], maker)) throw B.Broken();
        }
    }
    internal async Task SourcesAsync(IClientSessionHandle tx, SupplierOwner owner, BsonDocument sources, CancellationToken ct) {
        foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, owner.OrganizationId), ("shop", ScopeNodeKind.Shop, owner.ShopId), ("channel", ScopeNodeKind.Channel, owner.ChannelId) }) {
            var source = sources[name].AsBsonDocument; await new OrganizationProfileRecords(database, sourceCapture).HistoricalAsync(tx, source, ct);
            Equal(source["target"]["id"], id); Equal(source["target"]["kind"], AuthorizationBson.Name(kind));
            Equal(source["target"]["parentId"], kind == ScopeNodeKind.FulfillmentOrganization ? BsonNull.Value : owner.OrganizationId);
        }
        var nodes = B.SourceNames.SelectMany(name => sources[name]["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node)).ToArray();
        if (nodes.GroupBy(node => node.Id).Any(group => group.Distinct().Count() != 1)) throw B.Broken();
    }
    internal async Task MappingSourcesAsync(IClientSessionHandle tx, SupplierChangeInput input, BsonArray sources, DateTimeOffset recordedAt, CancellationToken ct) {
        if (sources.Count != input.Mappings.Length) throw B.Broken();
        for (var index = 0; index < input.Mappings.Length; index++) {
            var mapping = input.Mappings[index]; var source = sources[index]; Equal(source["location"], ProcurementRequestBson.Location(mapping.Location));
            await new ProcurementRequestRecords(database, catalog, sourceCapture).SourcesAsync(tx, mapping.Location, source["sources"].AsBsonDocument, ct);
            await catalog.VerifyProcurementSourceAsync(tx, mapping.Location, source["catalog"].AsBsonDocument, ct, sourceCapture);
            var item = source["catalog"]; Equal(item["sku"]["_id"], mapping.SkuId); Equal(item["sku"]["revision"], mapping.ExpectedSkuRevision);
            Equal(item["definition"]["_id"], mapping.DefinitionId); Equal(item["definition"]["version"], mapping.DefinitionVersion);
            if (item["definition"]["effectiveAt"].ToUniversalTime() > recordedAt.UtcDateTime) throw B.Broken();
        }
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(row["input"]);
        Equal(row["input"], B.Input(input)); Equal(row["owner"], B.Owner(input.Owner)); Equal(row["policy"]["_id"], input.PolicyId); Equal(row["policy"]["revision"], input.PolicyRevision);
        Equal(row["policy"]["scope"], row["owner"]); MatchPolicy(row["policy"].AsBsonDocument, input.Details);
        if (input.SupplierId is { } supplierId) Equal(row["supplierId"], supplierId);
        await SourcesAsync(tx, input.Owner, row["sources"].AsBsonDocument, ct); await MappingSourcesAsync(tx, input, row["mappingSources"].AsBsonArray, new(row["recordedAt"].ToUniversalTime()), ct);
        foreach (var mapping in row["mappingSources"].AsBsonArray)
            foreach (var name in B.SourceNames) Equal(mapping["sources"][name], row["sources"][name]);
        People(row, row["actorId"].AsInt64); Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input,
            row["sources"].AsBsonDocument, row["mappingSources"].AsBsonArray, row["policy"].AsBsonDocument));
        await EffectsAsync(tx, row, B.ProposeBody(input, row["binding"].AsString), null, ct); return row;
    }
    internal async Task<BsonDocument> ObservationAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Observations, id, ct); Equal(row["snapshotHash"], B.Hash(row)); Link(row, proposal);
        Equal(row["observed"], B.Details(B.Details(row["observed"]))); Equal(row["policyId"], proposal["policy"]["_id"]); Equal(row["policyRevision"], proposal["policy"]["revision"]);
        Equal(row["policyHash"], B.Hash(proposal["policy"].AsBsonDocument)); Equal(row["trustedChannelReference"], proposal["policy"]["trustedChannelReference"]);
        Equal(row["checkedAt"], row["recordedAt"]); Equal(row["attested"], true);
        if (!proposal["policy"]["allowReferenceOnly"].AsBoolean) throw B.Broken(); ObservationAt(proposal, row, new(row["recordedAt"].ToUniversalTime()));
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64); Independent(row, proposal);
        await EffectsAsync(tx, row, B.ObserveBody(proposal["_id"].AsInt64, 1, B.Details(row["observed"]), row["policyId"].AsInt64, row["policyRevision"].AsInt64,
            row["trustedChannelReference"].AsString, row["sourceReference"].AsString, true), proposal, ct); return row;
    }
    private static void Link(BsonDocument row, BsonDocument proposal) {
        Equal(row["proposalId"], proposal["_id"]); Equal(row["supplierId"], proposal["supplierId"]); Equal(row["owner"], proposal["owner"]); Equal(row["proposalHash"], proposal["snapshotHash"]);
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Equal(row["snapshotHash"], B.Hash(row)); Link(row, proposal); var decision = B.Decision(row["decision"]);
        BsonDocument? observation = null;
        if (decision == SupplierDecision.Approve) {
            observation = await ObservationAsync(tx, row["observationId"].AsInt64, proposal, ct); ObservationAt(proposal, observation, new(row["recordedAt"].ToUniversalTime()));
            Equal(observation["observed"], proposal["input"]["details"]);
            Equal(row["reviewSources"], proposal["sources"]); Equal(row["reviewMappingSources"], proposal["mappingSources"]); Equal(row["reviewPolicy"], proposal["policy"]);
            Equal(row["currentVersion"], proposal["input"]["baseVersion"]); Equal(row["supplierVersion"], checked(row["currentVersion"].AsInt64 + 1));
            if (B.Input(proposal["input"]).Mappings.Any(mapping => mapping.ValidUntil.UtcDateTime <= row["recordedAt"].ToUniversalTime())) throw B.Broken();
        } else if (!row["observationId"].IsBsonNull || !row["versionId"].IsBsonNull || !row["supplierVersion"].IsBsonNull || !row["reviewPolicy"].IsBsonNull || row["reviewMappingSources"].AsBsonArray.Count != 0 || row["namespaceIds"].AsBsonArray.Count != 0) throw B.Broken();
        await SourcesAsync(tx, B.Owner(row["owner"]), row["reviewSources"].AsBsonDocument, ct);
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64, observation?["actorId"].AsInt64 ?? row["actorId"].AsInt64); Independent(row, proposal, observation);
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal, decision, observation,
            row["reviewSources"].AsBsonDocument, row["reviewMappingSources"].AsBsonArray, row["reviewPolicy"].IsBsonNull ? null : row["reviewPolicy"].AsBsonDocument, row["currentVersion"].AsInt64));
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["observationId"].IsBsonNull ? null : row["observationId"].AsInt64, row["binding"].AsString), proposal, ct); return row;
    }
    internal async Task<BsonDocument?> ObservationForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await OneAsync(tx, B.Observations, new BsonDocument("proposalId", proposal["_id"]), ct);
        if (row is null) await NoOrphansAsync(tx, proposal["_id"].AsInt64, [B.Prefix + "observe"], ct);
        return row is null ? null : await ObservationAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await OneAsync(tx, B.Decisions, new BsonDocument("proposalId", proposal["_id"]), ct);
        if (row is null) await NoOrphansAsync(tx, proposal["_id"].AsInt64, [B.Prefix + "approve", B.Prefix + "reject"], ct);
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    private async Task NoOrphansAsync(IClientSessionHandle tx, long proposalId, string[] operations, CancellationToken ct) {
        foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
            if (await AnyAsync(tx, table, new BsonDocument { { field, new BsonDocument("$in", new BsonArray(operations)) }, { result + ".proposalId", proposalId } }, ct)) throw B.Broken();
    }
    internal async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await RowAsync(tx, B.Heads, id, ct); var root = await ProposalAsync(tx, head["rootProposalId"].AsInt64, ct);
        Equal(root["supplierId"], id); Equal(root["owner"], head["owner"]); Equal(root["recordedAt"], head["createdAt"]); Equal(root["input"]["details"]["subjectReference"], head["subjectReference"]);
        if (!root["input"]["supplierId"].IsBsonNull || root["input"]["baseVersion"] != 0L) throw B.Broken();
        var versions = await Rows(B.Versions).Find(tx, new BsonDocument("supplierId", id)).Sort(new BsonDocument("version", 1)).ToListAsync(ct); BsonDocument? previous = null; long number = 0;
        foreach (var stored in versions) {
            var row = await RowAsync(tx, B.Versions, stored["_id"].AsInt64, ct); Equal(row["snapshotHash"], B.Hash(row)); Equal(row["version"], ++number); Equal(row["owner"], head["owner"]);
            Equal(row["previousVersionId"], previous?["_id"] ?? BsonNull.Value); Equal(row["previousHash"], previous?["snapshotHash"] ?? BsonNull.Value);
            var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct); var decision = await DecisionAsync(tx, row["decisionId"].AsInt64, proposal, ct);
            Equal(proposal["supplierId"], id); Equal(proposal["owner"], head["owner"]); Equal(proposal["input"]["baseVersion"], number - 1);
            Equal(decision["decision"], "approve"); Equal(decision["versionId"], row["_id"]); Equal(decision["supplierVersion"], row["version"]); Equal(decision["namespaceIds"], row["namespaceIds"]);
            Equal(row["details"], proposal["input"]["details"]); Equal(row["details"]["subjectReference"], head["subjectReference"]); Equal(row["mappings"], proposal["input"]["mappings"]);
            Equal(row["mappingSources"], proposal["mappingSources"]); Equal(row["observationId"], decision["observationId"]); Equal(row["effectiveAt"], decision["recordedAt"]);
            var keys = B.Input(proposal["input"]).Mappings.Select(mapping => B.Namespace(B.Owner(head["owner"]), mapping)).Distinct().ToArray();
            if (keys.Length != row["namespaceIds"].AsBsonArray.Count) throw B.Broken();
            for (var index = 0; index < keys.Length; index++) {
                var value = await RowAsync(tx, B.Namespaces, row["namespaceIds"][index].AsInt64, ct);
                foreach (var field in keys[index]) Equal(value[field.Name], field.Value);
            }
            if (previous is not null && previous["effectiveAt"].ToUniversalTime() > row["effectiveAt"].ToUniversalTime()) throw B.Broken(); previous = row;
        }
        Equal(head["version"], number); Equal(head["versionId"], previous?["_id"] ?? BsonNull.Value); Equal(head["updatedAt"], previous?["effectiveAt"] ?? head["createdAt"]);
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument { { "supplierId", id }, { "decision", "approve" } }, cancellationToken: ct) != number) throw B.Broken(); return head;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, BsonDocument? proposal, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        Equal(await OneAsync(tx, "auditEvents", new BsonDocument("_id", row["auditId"]), ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await OneAsync(tx, "idempotencyRecords", new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }, ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (proposal is not null) { fields = [.. fields, PersonApprovalReceipt.Field]; if (PersonApprovalReceipt.Read(receipt, Permissions.SupplierChangeApprove) != new ApprovalRequest(1, 1, ProposedBy: proposal["actorId"].AsInt64)) throw B.Broken(); }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await OneAsync(tx, "outboxEvents", new BsonDocument("sourceKey", B.SourceKey(row)), ct) ?? throw B.Broken();
        Equal(effect["kind"], row["operation"]); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]); Equal(effect["owner"], row["owner"]);
        Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "propose" ? B.Proposals : operation == B.Prefix + "observe" ? B.Observations : B.Decisions;
        var row = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (table, filter) in new (string, BsonDocument)[] {
                ("idempotencyRecords", new() { { "actorId", actor }, { "operation", operation }, { "key", key } }), ("auditEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }),
                ("outboxEvents", new() { { "actorId", actor }, { "kind", operation }, { "requestKey", key } }) }) if (await Rows(table).Find(tx, filter).AnyAsync(ct)) throw B.Broken(); return null;
        }
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct);
        return name == B.Proposals ? proposal : name == B.Observations ? await ObservationAsync(tx, row["_id"].AsInt64, proposal, ct) : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
}
