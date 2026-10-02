using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.SupplierBankBson;
using S = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

internal sealed class SupplierBankRecords(IMongoDatabase database, CatalogAuthoringService catalog)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) => await Rows(name)
        .Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { SupplierBankSchemas.Validator(name) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal async Task<BsonDocument> SupplierAsync(IClientSessionHandle tx, long supplierId, long versionId, long version, CancellationToken ct) {
        var records = new SupplierRecords(database, catalog); var head = await records.HeadAsync(tx, supplierId, ct);
        var value = await records.RowAsync(tx, S.Versions, versionId, ct); Equal(value["supplierId"], supplierId); Equal(value["version"], version); Equal(value["owner"], head["owner"]);
        var proposal = await records.ProposalAsync(tx, value["proposalId"].AsInt64, ct); var decision = await records.DecisionAsync(tx, value["decisionId"].AsInt64, proposal, ct);
        var observation = await records.ObservationAsync(tx, value["observationId"].AsInt64, proposal, ct);
        Equal(decision["decision"], "approve"); Equal(decision["versionId"], versionId); Equal(decision["supplierVersion"], version);
        return new() { { "version", value }, { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] },
            { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] }, { "observationId", observation["_id"] }, { "observationHash", observation["snapshotHash"] }, { "policy", proposal["policy"] } };
    }
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at, bool referenceOnly = false) {
        ReceivingAccountRecords.RequirePolicyAt(policy, at);
        if (policy["currency"] != "IDR" || policy["method"].AsString is not ("bank-contact" or "authenticated-bank-record")) throw B.Broken();
        if (referenceOnly && !policy["allowReferenceOnly"].AsBoolean) throw new UnitToko.Platform.Mongo.PersistenceConflictException("supplier_bank_evidence_required", "This policy requires private evidence admission that is not available for this beneficiary");
    }
    internal static void ObservationAt(BsonDocument proposal, BsonDocument observation, DateTimeOffset at) {
        var policy = proposal["policy"].AsBsonDocument; PolicyAt(policy, at, true); var checkedAt = new DateTimeOffset(observation["checkedAt"].ToUniversalTime());
        if (checkedAt > at || at - checkedAt >= TimeSpan.FromSeconds(policy["maxObservationAgeSeconds"].AsInt64)) throw B.Changed();
    }
    internal static void PolicySource(BsonDocument policy, SupplierBankInput input, BsonDocument source) {
        Equal(policy["owner"], S.Owner(input.Owner)); Equal(policy["supplierId"], input.SupplierId); Equal(policy["supplierVersionId"], input.SupplierVersionId);
        Equal(policy["supplierVersion"], input.SupplierVersion); Equal(policy["subjectReference"], source["version"]["details"]["subjectReference"]);
        Equal(policy["bankCode"], input.Details.BankCode); Equal(policy["currency"], input.Details.Currency); Equal(policy["_id"], input.PolicyId); Equal(policy["revision"], input.PolicyRevision);
    }
    private static void People(BsonDocument row, params long[] users) {
        var values = row["people"].AsBsonArray.Select(S.Person).ToArray();
        if (!values.Select(value => value.UserId).SequenceEqual(users.Distinct().Order()) || values.Any(value => value is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw B.Broken();
    }
    private static void Independent(BsonDocument row, BsonDocument proposal, BsonDocument? observation = null) {
        var people = row["people"].AsBsonArray.Select(S.Person).ToDictionary(person => person.UserId); var original = proposal["people"].AsBsonArray.Select(S.Person).Single();
        Equal(people[original.UserId].PersonId!.Value, original.PersonId!.Value);
        var observer = observation is null ? people[row["actorId"].AsInt64] : people[observation["actorId"].AsInt64];
        if (observation is not null) Equal(observer.PersonId!.Value, observation["people"].AsBsonArray.Select(S.Person).Single(value => value.UserId == observer.UserId).PersonId!.Value);
        ReceivingAccountPolicy.RequireIndependent(people[row["actorId"].AsInt64], observer, people[original.UserId]);
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Proposals, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(row["input"]); Equal(row["input"], B.Input(input));
        Equal(row["proposalId"], id); Equal(row["supplierId"], input.SupplierId); Equal(row["owner"], S.Owner(input.Owner));
        var source = await SupplierAsync(tx, input.SupplierId, input.SupplierVersionId, input.SupplierVersion, ct); Equal(row["supplier"], source); Equal(source["version"]["owner"], row["owner"]);
        if (source["version"]["effectiveAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        SupplierRecords.PolicyAt(source["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
        await new SupplierRecords(database, catalog).SourcesAsync(tx, input.Owner, row["sources"].AsBsonDocument, ct);
        PolicySource(row["policy"].AsBsonDocument, input, source); PolicyAt(row["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime())); People(row, row["actorId"].AsInt64);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, source, row["sources"].AsBsonDocument, row["policy"].AsBsonDocument));
        await EffectsAsync(tx, row, B.ProposeBody(input, row["binding"].AsString), null, ct); return row;
    }
    private static void Link(BsonDocument row, BsonDocument proposal) {
        foreach (var field in new[] { "beneficiaryId", "proposalId", "supplierId", "owner" }) Equal(row[field], proposal[field]); Equal(row["proposalHash"], proposal["snapshotHash"]);
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
    }
    internal async Task<BsonDocument> ObservationAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Observations, id, ct); Equal(row["snapshotHash"], B.Hash(row)); Link(row, proposal);
        var observed = ReceivingAccountBson.Details(row["observed"]); Equal(row["observed"], ReceivingAccountBson.Details(observed)); Equal(row["checkedAt"], row["recordedAt"]);
        Equal(row["policyHash"], B.Hash(proposal["policy"].AsBsonDocument)); Equal(row["attested"], true); ObservationAt(proposal, row, new(row["recordedAt"].ToUniversalTime()));
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64); Independent(row, proposal);
        Equal(row["binding"], B.ObservationBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal, observed, row["sourceReference"].AsString));
        await EffectsAsync(tx, row, B.ObserveBody(proposal["_id"].AsInt64, 1, observed, row["sourceReference"].AsString, row["binding"].AsString, true), proposal, ct); return row;
    }
    internal async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, BsonDocument proposal, CancellationToken ct) {
        var row = await RowAsync(tx, B.Decisions, id, ct); Equal(row["snapshotHash"], B.Hash(row)); Link(row, proposal); var decision = B.Decision(row["decision"]); BsonDocument? observed = null;
        if (decision == SupplierBankDecision.Approve) {
            observed = await ObservationAsync(tx, row["observationId"].AsInt64, proposal, ct); ObservationAt(proposal, observed, new(row["recordedAt"].ToUniversalTime()));
            SupplierRecords.PolicyAt(proposal["supplier"]["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
            ReceivingAccountPolicy.RequireMatch(B.Input(proposal["input"]).Details, ReceivingAccountBson.Details(observed["observed"]));
            Equal(row["currentVersion"], proposal["input"]["baseVersion"]); Equal(row["bankVersion"], checked(row["currentVersion"].AsInt64 + 1)); Equal(row["reviewSources"], proposal["sources"]);
        } else if (!row["observationId"].IsBsonNull || !row["versionId"].IsBsonNull || !row["bankVersion"].IsBsonNull) throw B.Broken();
        await new SupplierRecords(database, catalog).SourcesAsync(tx, S.Owner(row["owner"]), row["reviewSources"].AsBsonDocument, ct);
        People(row, row["actorId"].AsInt64, proposal["actorId"].AsInt64, observed?["actorId"].AsInt64 ?? row["actorId"].AsInt64); Independent(row, proposal, observed);
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal, decision, observed, row["currentVersion"].AsInt64, row["reviewSources"].AsBsonDocument));
        await EffectsAsync(tx, row, B.DecideBody(proposal["_id"].AsInt64, 1, decision, observed?["_id"].AsInt64, row["binding"].AsString), proposal, ct); return row;
    }
    private async Task NoOrphansAsync(IClientSessionHandle tx, long proposalId, string[] operations, CancellationToken ct) {
        foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
            if (await Rows(table).Find(tx, new BsonDocument { { field, new BsonDocument("$in", new BsonArray(operations)) }, { result + ".proposalId", proposalId } }).AnyAsync(ct)) throw B.Broken();
    }
    internal async Task<BsonDocument?> ObservationForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await Rows(B.Observations).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (row is null) await NoOrphansAsync(tx, proposal["_id"].AsInt64, [B.Prefix + "observe"], ct);
        return row is null ? null : await ObservationAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task<BsonDocument?> DecisionForAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (row is null) await NoOrphansAsync(tx, proposal["_id"].AsInt64, [B.Prefix + "approve", B.Prefix + "reject"], ct);
        return row is null ? null : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
    internal async Task<BsonDocument> HeadAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await RowAsync(tx, B.Heads, id, ct); var root = await ProposalAsync(tx, head["rootProposalId"].AsInt64, ct);
        foreach (var field in new[] { "supplierId", "owner" }) Equal(head[field], root[field]); Equal(head["_id"], root["beneficiaryId"]); Equal(root["input"]["baseVersion"], 0L); Equal(head["createdAt"], root["recordedAt"]);
        var versions = await Rows(B.Versions).Find(tx, new BsonDocument("beneficiaryId", id)).Sort(new BsonDocument("version", 1)).ToListAsync(ct); long number = 0; BsonDocument? previous = null;
        foreach (var raw in versions) {
            var row = await RowAsync(tx, B.Versions, raw["_id"].AsInt64, ct); Equal(row["snapshotHash"], B.Hash(row)); Equal(row["version"], ++number);
            foreach (var field in new[] { "supplierId", "owner" }) Equal(row[field], head[field]); Equal(row["previousVersionId"], previous?["_id"] ?? BsonNull.Value); Equal(row["previousHash"], previous?["snapshotHash"] ?? BsonNull.Value);
            var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct); var decision = await DecisionAsync(tx, row["decisionId"].AsInt64, proposal, ct);
            Equal(proposal["beneficiaryId"], id); Equal(proposal["input"]["baseVersion"], number - 1); Equal(decision["decision"], "approve"); Equal(decision["versionId"], row["_id"]); Equal(decision["bankVersion"], row["version"]);
            Equal(row["details"], proposal["input"]["details"]); Equal(row["physicalIdentity"], ReceivingAccountBson.PhysicalIdentity(B.Input(proposal["input"]).Details));
            Equal(row["supplier"], proposal["supplier"]); Equal(row["policy"], proposal["policy"]); Equal(row["observationId"], decision["observationId"]); Equal(row["effectiveAt"], decision["recordedAt"]);
            if (previous is not null && previous["effectiveAt"].ToUniversalTime() > row["effectiveAt"].ToUniversalTime()) throw B.Broken(); previous = row;
        }
        Equal(head["version"], number); Equal(head["versionId"], previous?["_id"] ?? BsonNull.Value); Equal(head["updatedAt"], previous?["effectiveAt"] ?? head["createdAt"]);
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, new BsonDocument { { "beneficiaryId", id }, { "decision", "approve" } }, cancellationToken: ct) != number) throw B.Broken(); return head;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, BsonDocument? proposal, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString)); Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
        if (proposal is not null) { fields = [.. fields, PersonApprovalReceipt.Field]; if (PersonApprovalReceipt.Read(receipt, Permissions.SupplierBankApprove) != new ApprovalRequest(1, 1, ProposedBy: proposal["actorId"].AsInt64)) throw B.Broken(); }
        if (receipt.ElementCount != fields.Length || fields.Any(field => !receipt.Contains(field)) || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "owner", "supplierId" }) Equal(effect[field], row[field]); Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actorId, string operation, string key, CancellationToken ct) {
        var table = operation == B.Prefix + "propose" ? B.Proposals : operation == B.Prefix + "observe" ? B.Observations : B.Decisions;
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actorId }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) {
            foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
                if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actorId }, { field, operation }, { name == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken(); return null;
        }
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct); return table == B.Proposals ? proposal : table == B.Observations ? await ObservationAsync(tx, row["_id"].AsInt64, proposal, ct) : await DecisionAsync(tx, row["_id"].AsInt64, proposal, ct);
    }
}
