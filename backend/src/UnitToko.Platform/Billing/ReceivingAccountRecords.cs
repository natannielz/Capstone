using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Platform.Billing;

// Private graph validation; the caller must authorize and fence the owning scope in this transaction.
internal sealed class ReceivingAccountRecords(IMongoDatabase database)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) {
        if (id <= 0) throw B.Broken();
        return await Rows(name).Find(tx, F.Eq("_id", id) & ReceivingAccountSchemas.Validator(name)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    }
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal static void Hash(BsonDocument row) => Equal(row["snapshotHash"], B.Hash(row));
    private static void Details(BsonValue value) => Equal(value, B.Details(B.Details(value)));
    private static void People(BsonDocument row, params long[] users) {
        var values = row["people"].AsBsonArray.Select(B.Person).ToArray();
        if (!values.Select(p => p.UserId).SequenceEqual(users.Distinct().Order())) throw B.Broken();
        // Unknown is explicit provenance for a maker; it never satisfies independent approval.
        foreach (var person in values) if (person.Verification == PersonVerification.Unknown && (person.PersonId is not null || person.Version != 0)) throw B.Broken();
    }
    private static void Independent(BsonDocument row, long maker, long observer) {
        var values = row["people"].AsBsonArray.Select(B.Person).ToDictionary(p => p.UserId);
        ReceivingAccountPolicy.RequireIndependent(values[row["actorId"].AsInt64], values[observer], values[maker]);
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); Hash(row); Details(row["details"]);
        Equal(row["operation"], B.Prefix + "propose"); Equal(row["proposalId"], row["_id"]);
        var policy = row["policy"].AsBsonDocument;
        Equal(row["scope"], policy["scope"]); Equal(row["details"]["bankCode"], policy["bankCode"]);
        if (row["isNewAccount"].AsBoolean && row["baseVersion"].AsInt64 != 0) throw B.Broken();
        RequirePolicyAt(policy, new(row["recordedAt"].ToUniversalTime()));
        People(row, row["actorId"].AsInt64);
        var body = B.ProposeBody(row["isNewAccount"].AsBoolean ? null : row["accountId"].AsInt64, row["baseVersion"].AsInt64,
            policy["_id"].AsInt64, policy["revision"].AsInt64, B.Details(row["details"]));
        await EffectsAsync(tx, row, body, null, ct); return row;
    }
    internal async Task<BsonDocument> ObservationAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Observations, id, ct); Hash(row); Details(row["details"]); Link(row, proposal);
        Equal(row["operation"], B.Prefix + "observe"); Equal(row["attested"], true);
        Equal(row["policyHash"], B.PolicyHash(proposal["policy"].AsBsonDocument)); Equal(row["checkedAt"], row["recordedAt"]);
        if (!ReceivingAccountPolicy.Text(row["sourceReference"].AsString, 256)) throw B.Broken();
        RequirePolicyAt(proposal["policy"].AsBsonDocument, new(row["checkedAt"].ToUniversalTime()));
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64); Independent(row, proposal["actorId"].AsInt64, row["actorId"].AsInt64);
        await EffectsAsync(tx, row, B.ObserveBody(proposal["_id"].AsInt64, 1, B.Details(row["details"]), row["sourceReference"].AsString, true), proposal, ct);
        return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Hash(row); Link(row, proposal);
        var decision = row["decision"].AsString switch { "approve" => ReceivingAccountDecision.Approve, "reject" => ReceivingAccountDecision.Reject, "withdraw" => ReceivingAccountDecision.Withdraw, _ => throw B.Broken() };
        Equal(row["operation"], B.Prefix + B.Name(decision));
        if (decision == ReceivingAccountDecision.Approve) {
            if (row["observationId"].IsBsonNull || row["versionId"].IsBsonNull || row["accountVersion"].IsBsonNull) throw B.Broken();
            var observed = await ObservationAsync(tx, row["observationId"].AsInt64, proposal, ct);
            Equal(row["accountVersion"], checked(proposal["baseVersion"].AsInt64 + 1));
            ReceivingAccountPolicy.RequireMatch(B.Details(proposal["details"]), B.Details(observed["details"]));
            RequireObservationAt(proposal, observed, new(row["recordedAt"].ToUniversalTime()));
            People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64, observed["actorId"].AsInt64);
            Independent(row, proposal["actorId"].AsInt64, observed["actorId"].AsInt64);
        } else {
            if (!row["observationId"].IsBsonNull || !row["versionId"].IsBsonNull || !row["accountVersion"].IsBsonNull) throw B.Broken();
            if (decision == ReceivingAccountDecision.Withdraw) { Equal(row["actorId"], proposal["actorId"]); People(row, row["actorId"].AsInt64); }
            else { People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64); Independent(row, proposal["actorId"].AsInt64, row["actorId"].AsInt64); }
        }
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["observationId"].IsBsonNull ? null : row["observationId"].AsInt64),
            decision == ReceivingAccountDecision.Withdraw ? null : proposal, ct);
        return row;
    }
    private static void Link(BsonDocument row, BsonDocument proposal) {
        Equal(row["proposalId"], proposal["_id"]); Equal(row["accountId"], proposal["accountId"]); Equal(row["scope"], proposal["scope"]);
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
    }
    internal static void RequirePolicyAt(BsonDocument policy, DateTimeOffset now) {
        if (!policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > now.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= now.UtcDateTime ||
            policy["maxObservationAgeSeconds"].AsInt64 is < 1 or > 31536000) throw B.Changed();
        foreach (var name in new[] { "bankName", "trustedChannelReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[name].AsString, name == "bankName" ? 160 : 256)) throw B.Broken();
        _ = ReceivingAccountPolicy.Copy(new(policy["bankCode"].AsString, "0000", "Policy bank code validation", "IDR"));
    }
    internal static void RequireObservationAt(BsonDocument proposal, BsonDocument observed, DateTimeOffset now) {
        var policy = proposal["policy"].AsBsonDocument; RequirePolicyAt(policy, now);
        var at = new DateTimeOffset(observed["checkedAt"].ToUniversalTime());
        if (at > now || now - at >= TimeSpan.FromSeconds(policy["maxObservationAgeSeconds"].AsInt64)) throw B.Changed();
    }
    internal async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long accountId, CancellationToken ct) {
        var head = await RowAsync(tx, B.Heads, accountId, ct);
        var root = await ProposalAsync(tx, head["rootProposalId"].AsInt64, ct);
        if (!root["isNewAccount"].AsBoolean || root["accountId"] != accountId || root["scope"] != head["scope"] || root["recordedAt"] != head["createdAt"] ||
            head["updatedAt"].ToUniversalTime() < head["createdAt"].ToUniversalTime()) throw B.Broken();
        long number = 0; BsonDocument? previous = null;
        using var cursor = await Rows(B.Versions).Find(tx, F.Eq("accountId", accountId)).Sort(new BsonDocument("version", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var stored in cursor.Current) {
            var version = await RowAsync(tx, B.Versions, stored["_id"].AsInt64, ct); Hash(version); Details(version["details"]);
            Equal(version["version"], ++number); Equal(version["scope"], head["scope"]); Equal(version["physicalIdentity"], B.PhysicalIdentity(B.Details(version["details"])));
            Equal(version["previousVersionId"], previous is null ? BsonNull.Value : previous["_id"]); Equal(version["previousHash"], previous is null ? BsonNull.Value : previous["snapshotHash"]);
            var proposal = await ProposalAsync(tx, version["proposalId"].AsInt64, ct);
            var decision = await DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
            Equal(decision["decision"], "approve"); Equal(decision["versionId"], version["_id"]); Equal(decision["accountVersion"], number);
            Equal(proposal["accountId"], accountId); Equal(proposal["scope"], head["scope"]); Equal(version["details"], proposal["details"]);
            Equal(version["observationId"], decision["observationId"]); Equal(version["effectiveAt"], decision["recordedAt"]);
            if (previous is not null && previous["effectiveAt"].ToUniversalTime() > version["effectiveAt"].ToUniversalTime()) throw B.Broken();
            previous = version;
        }
        Equal(head["version"], number); Equal(head["versionId"], previous is null ? BsonNull.Value : previous["_id"]);
        Equal(head["updatedAt"], previous is null ? head["createdAt"] : previous["effectiveAt"]);
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, F.Eq("accountId", accountId) & F.Eq("decision", "approve"), cancellationToken: ct) != number) throw B.Broken();
        return head;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, BsonDocument? proposal, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(audit, B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) & F.Eq("operation", row["operation"]) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (proposal is not null) fields = [.. fields, "personApproval"];
        if (receipt.ElementCount != fields.Length || fields.Any(name => !receipt.Contains(name)) || !receipt["_id"].IsObjectId || !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"] != 1 ||
            !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        if (proposal is not null) Equal(receipt["personApproval"], PersonApprovalReceipt.Create(B.Permission(row["operation"].AsString), new(1, 1, ProposedBy: proposal["actorId"].AsInt64)));
        var effect = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"], row["operation"]); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]);
        Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actorId, string operation, string key, CancellationToken ct) {
        var collection = operation == B.Prefix + "propose" ? B.Proposals : operation == B.Prefix + "observe" ? B.Observations : B.Decisions;
        var filter = F.Eq("actorId", actorId) & F.Eq("operation", operation) & F.Eq("requestKey", key);
        var row = await Rows(collection).Find(tx, filter).SingleOrDefaultAsync(ct);
        if (row is null) {
            if (await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actorId) & F.Eq("operation", operation) & F.Eq("key", key)).AnyAsync(ct) ||
                await Rows("auditEvents").Find(tx, F.Eq("actorId", actorId) & F.Eq("kind", operation) & F.Eq("requestKey", key)).AnyAsync(ct) ||
                await Rows("outboxEvents").Find(tx, F.Eq("actorId", actorId) & F.Eq("kind", operation) & F.Eq("requestKey", key)).AnyAsync(ct)) throw B.Broken();
            return null;
        }
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct);
        return collection == B.Proposals ? proposal : collection == B.Observations ? await ObservationAsync(tx, row["_id"].AsInt64, proposal, ct) : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
}
