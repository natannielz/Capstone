using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierService
{
    private async Task<BsonDocument?> ForKeyAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "propose" ? B.Proposals : operation == B.Prefix + "observe" ? B.Observations : B.Decisions;
        var raw = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", key } }).Project(new BsonDocument("owner", 1)).SingleOrDefaultAsync(ct);
        if (raw is not null) _ = Clauses(new(actor, authority, tx, B.Owner(raw["owner"]), B.Permission(operation), new(), null));
        return await records.ForKeyAsync(tx, actor.UserId, operation, key, ct);
    }
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long proposalId, string permission, CancellationToken ct) {
        _ = Query(actor, authority, permission);
        var route = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", proposalId)).Project(new BsonDocument("owner", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, B.Owner(route["owner"]), permission, ct);
        context.Proposal = await records.ProposalAsync(tx, proposalId, ct); context.Head = await records.HeadAsync(tx, context.Proposal["supplierId"].AsInt64, ct);
        Equal(context.Head["owner"], B.Owner(context.Owner)); BindPeople(context, context.Proposal); return context;
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", B.SourceKey(row) },
            { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "owner", row["owner"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> capture,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            var permission = B.Permission(operation); _ = Query(actor, authority, permission);
            var saved = await ForKeyAsync(actor, authority, tx, operation, metadata.IdempotencyKey, token); selected = await capture(authority, tx, saved, token);
            return new(new(true, "allowed", Clauses(selected).Select(clause => clause.GrantId).Distinct().ToImmutableArray()),
                ScopePolicy.RequiredNodes(selected.Resource, authority.Directory), permission,
                new(selected.Resource, permission == Permissions.SupplierChangeApprove ? new ApprovalRequest(1, 1, ProposedBy: selected.Proposal!["actorId"].AsInt64) : null));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            context.Proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token); context.Head = await records.HeadAsync(tx, row["supplierId"].AsInt64, token);
            BsonDocument? observed = operation == B.Prefix + "approve" ? await records.ObservationAsync(tx, row["observationId"].AsInt64, context.Proposal, token) : null;
            BindPeople(context, context.Proposal, row, observed); await PeopleAsync(context, operation != B.Prefix + "propose", observed?["actorId"].AsInt64, token);
            await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    private async Task PrepareProposalAsync(Context context, SupplierChangeInput input, CancellationToken ct) {
        if (input.SupplierId is { } supplierId) {
            context.Head = await records.HeadAsync(context.Tx, supplierId, ct); Equal(context.Head["owner"], B.Owner(input.Owner)); Equal(context.Head["subjectReference"], input.Details.SubjectReference);
            Equal(context.Head["version"], input.BaseVersion);
        } else if (await Rows(B.Heads).Find(context.Tx, new BsonDocument { { "owner", B.Owner(input.Owner) }, { "subjectReference", input.Details.SubjectReference } }).AnyAsync(ct)) throw B.Changed();
        await PolicyAsync(context, input, ct); await CaptureMappingsAsync(context, input, ct); context.UnexpiredMappings = input; Current(context);
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, SupplierChangeInput input, CancellationToken ct = default) {
        input = SupplierRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Owner, Permissions.SupplierChangePropose, token); await PeopleAsync(context, false, null, token);
            await PrepareProposalAsync(context, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "source", B.SourceView(context.Sources) }, { "mappingSources", B.MappingView(context.MappingSources) },
                { "policy", B.PolicyView(context.Policy!) }, { "binding", B.Binding(actor, input, context.Sources, context.MappingSources, context.Policy!) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, SupplierChangeInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = SupplierRules.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var supplierId = input.SupplierId ?? (await ids.NextAsync(B.Heads, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding);
        return await CommandAsync(actor, B.Prefix + "propose", metadata, body, async (authority, tx, saved, token) => {
            if (saved is not null) { var original = await ProposalContextAsync(actor, authority, tx, saved["proposalId"].AsInt64, Permissions.SupplierChangePropose, token); await PeopleAsync(original, false, null, token); return original; }
            var context = await ContextAsync(actor, authority, tx, input.Owner, Permissions.SupplierChangePropose, token);
            await PeopleAsync(context, false, null, token); await PrepareProposalAsync(context, input, token);
            Equal(binding, B.Binding(actor, input, context.Sources, context.MappingSources, context.Policy!)); return context;
        }, async (context, token) => {
            Current(context); var now = Now;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", id }, { "supplierId", supplierId }, { "version", 1L }, { "owner", B.Owner(input.Owner) },
                { "input", B.Input(input) }, { "sources", context.Sources.DeepClone() }, { "mappingSources", context.MappingSources.DeepClone() }, { "policy", B.PolicySnapshot(context.Policy!) },
                { "binding", binding }, { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "propose", auditId, actor, metadata, body, now);
            if (input.SupplierId is null) {
                context.Head = new BsonDocument { { "_id", supplierId }, { "schemaVersion", 1 }, { "owner", B.Owner(input.Owner) }, { "subjectReference", input.Details.SubjectReference },
                    { "rootProposalId", id }, { "version", 0L }, { "versionId", BsonNull.Value }, { "fence", 0L }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } };
                await Rows(B.Heads).InsertOneAsync(context.Tx, context.Head, cancellationToken: token);
            }
            await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task PendingAsync(Context context, bool currentBase, CancellationToken ct) {
        if (context.Proposal is null || await records.DecisionForAsync(context.Tx, context.Proposal, ct) is not null ||
            currentBase && context.Head!["version"] != context.Proposal["input"]["baseVersion"]) throw B.Changed();
    }
    public async Task<BsonDocument> ObserveAsync(ActorContext actor, long proposalId, long expectedVersion, SupplierDetails observed, long policyId, long policyRevision,
        string trustedChannelReference, string sourceReference, bool attested, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        observed = SupplierRules.Details(observed); SupplierRules.Text(trustedChannelReference, 256); SupplierRules.Text(sourceReference, 256); Metadata(metadata);
        if (proposalId <= 0 || expectedVersion != 1 || policyId <= 0 || policyRevision <= 0 || !attested) throw SupplierRules.Invalid(); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Observations, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ObserveBody(proposalId, expectedVersion, observed, policyId, policyRevision, trustedChannelReference, sourceReference, attested);
        return await CommandAsync(actor, B.Prefix + "observe", metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierChangeApprove, token);
            if (saved is null) {
                await PendingAsync(context, true, token); await PolicyAsync(context, B.Input(context.Proposal!["input"]), token, true);
                Equal(context.Policy!["_id"], policyId); Equal(context.Policy["revision"], policyRevision); Equal(context.Policy["trustedChannelReference"], trustedChannelReference);
                if (await records.ObservationForAsync(tx, context.Proposal!, token) is not null) throw B.Changed();
            } else BindPeople(context, saved);
            await PeopleAsync(context, true, null, token); return context;
        }, async (context, token) => {
            Current(context); var now = Now; var proposal = context.Proposal!;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "supplierId", proposal["supplierId"] }, { "owner", proposal["owner"] },
                { "proposalHash", proposal["snapshotHash"] }, { "observed", B.Details(observed) }, { "policyId", policyId }, { "policyRevision", policyRevision }, { "policyHash", B.Hash(proposal["policy"].AsBsonDocument) },
                { "trustedChannelReference", trustedChannelReference }, { "sourceReference", sourceReference }, { "attested", true }, { "checkedAt", now.UtcDateTime },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "observe", auditId, actor, metadata, body, now);
            await Rows(B.Observations).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task PrepareDecisionAsync(Context context, SupplierDecision decision, long? observationId, CancellationToken ct) {
        await PendingAsync(context, decision == SupplierDecision.Approve, ct); var proposal = context.Proposal!;
        if (decision == SupplierDecision.Approve) {
            var input = B.Input(proposal["input"]); await PolicyAsync(context, input, ct, true); context.UnexpiredMappings = input;
            context.Observation = await records.ObservationAsync(context.Tx, observationId!.Value, proposal, ct); Equal(context.Observation["observed"], proposal["input"]["details"]);
            SupplierRecords.ObservationAt(proposal, context.Observation, Now); BindPeople(context, context.Observation);
            Equal(context.Sources, proposal["sources"]); await CaptureMappingsAsync(context, input, ct); Equal(context.MappingSources, proposal["mappingSources"]);
        }
        await PeopleAsync(context, true, context.Observation?["actorId"].AsInt64, ct); Current(context);
    }
    private static string DecisionBinding(Context context, SupplierDecision decision) => B.DecisionBinding(context.Actor.UserId, context.Actor.GrantVersion, context.Proposal!, decision,
        context.Observation, context.Sources, context.MappingSources, context.Policy, context.Head!["version"].AsInt64);
    private static void DecisionInput(long proposalId, SupplierDecision decision, long? observationId) {
        if (proposalId <= 0 || !Enum.IsDefined(decision) || (decision == SupplierDecision.Approve ? observationId is null or <= 0 : observationId is not null)) throw SupplierRules.Invalid();
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, SupplierDecision decision, long? observationId, CancellationToken ct = default) {
        DecisionInput(proposalId, decision, observationId); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierChangeApprove, token); await PrepareDecisionAsync(context, decision, observationId, token);
            var result = new BsonDocument { { "proposal", await ProposalViewAsync(context, context.Proposal!, token) }, { "decision", B.Decision(decision) },
                { "observationId", observationId is { } value ? new BsonInt64(value) : BsonNull.Value }, { "binding", DecisionBinding(context, decision) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion, SupplierDecision decision, long? observationId,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        DecisionInput(proposalId, decision, observationId); if (expectedVersion != 1) throw SupplierRules.Invalid(); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var versionId = decision == SupplierDecision.Approve ? (await ids.NextAsync(B.Versions, ct)).Value : (long?)null;
        var operation = B.Prefix + B.Decision(decision); var body = B.DecideBody(proposalId, expectedVersion, decision, observationId, binding);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierChangeApprove, token);
            if (saved is null) { await PrepareDecisionAsync(context, decision, observationId, token); Equal(binding, DecisionBinding(context, decision)); }
            else { BindPeople(context, saved); await PeopleAsync(context, true, observationId is { } observedId ? (await records.ObservationAsync(tx, observedId, context.Proposal!, token))["actorId"].AsInt64 : null, token); }
            return context;
        }, async (context, token) => {
            var proposal = context.Proposal!; var head = context.Head!; var namespaces = decision == SupplierDecision.Approve ? await NamespacesAsync(context, B.Input(proposal["input"]), token) : new BsonArray();
            Current(context); var now = Now; if (now.UtcDateTime < proposal["recordedAt"].ToUniversalTime() || now.UtcDateTime < head["updatedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "supplierId", proposal["supplierId"] }, { "owner", proposal["owner"] },
                { "proposalHash", proposal["snapshotHash"] }, { "decision", B.Decision(decision) }, { "observationId", observationId is { } observed ? new BsonInt64(observed) : BsonNull.Value },
                { "versionId", versionId is { } nextId ? new BsonInt64(nextId) : BsonNull.Value }, { "supplierVersion", versionId.HasValue ? new BsonInt64(checked(head["version"].AsInt64 + 1)) : BsonNull.Value },
                { "currentVersion", head["version"] }, { "reviewSources", context.Sources.DeepClone() }, { "reviewMappingSources", context.MappingSources.DeepClone() },
                { "reviewPolicy", context.Policy is null ? BsonNull.Value : B.PolicySnapshot(context.Policy) }, { "namespaceIds", namespaces }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(B.Person)) } };
            B.Metadata(row, operation, auditId, actor, metadata, body, now);
            if (versionId is { } value) {
                var previous = head["versionId"].IsBsonNull ? null : await records.RowAsync(context.Tx, B.Versions, head["versionId"].AsInt64, token);
                var version = new BsonDocument { { "_id", value }, { "schemaVersion", 1 }, { "supplierId", head["_id"] }, { "owner", head["owner"] }, { "version", row["supplierVersion"] },
                    { "proposalId", proposalId }, { "observationId", observationId!.Value }, { "decisionId", id }, { "previousVersionId", head["versionId"] }, { "previousHash", previous?["snapshotHash"] ?? BsonNull.Value },
                    { "details", proposal["input"]["details"] }, { "mappings", proposal["input"]["mappings"] }, { "mappingSources", proposal["mappingSources"] }, { "namespaceIds", namespaces }, { "effectiveAt", now.UtcDateTime } };
                version.Add("snapshotHash", B.Hash(version)); await Rows(B.Versions).InsertOneAsync(context.Tx, version, cancellationToken: token);
                var next = head.DeepClone().AsBsonDocument; next["version"] = row["supplierVersion"]; next["versionId"] = value; next["updatedAt"] = now.UtcDateTime; next["fence"] = checked(head["fence"].AsInt64 + 1);
                if ((await Rows(B.Heads).ReplaceOneAsync(context.Tx, head, next, cancellationToken: token)).ModifiedCount != 1) throw B.Changed(); context.Head = next;
            }
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task<BsonArray> NamespacesAsync(Context context, SupplierChangeInput input, CancellationToken ct) {
        var keys = input.Mappings.Select(mapping => B.Namespace(input.Owner, mapping)).Distinct().ToArray(); var oldKeys = new List<BsonDocument>();
        if (!context.Head!["versionId"].IsBsonNull) {
            var old = await records.RowAsync(context.Tx, B.Versions, context.Head["versionId"].AsInt64, ct);
            oldKeys.AddRange(old["mappings"].AsBsonArray.Select(B.Mapping).Select(mapping => B.Namespace(input.Owner, mapping)));
        }
        var identities = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in keys.Concat(oldKeys).Distinct().OrderBy(B.Hash, StringComparer.Ordinal)) {
            var row = await Rows(B.Namespaces).Find(context.Tx, key).SingleOrDefaultAsync(ct);
            if (row is null) {
                var id = (await ids.NextAsync(B.Namespaces, ct)).Value; row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "fence", 0L }, { "createdAt", Now.UtcDateTime } }; row.AddRange(key);
                await Rows(B.Namespaces).InsertOneAsync(context.Tx, row, cancellationToken: ct);
            } else row = await records.RowAsync(context.Tx, B.Namespaces, row["_id"].AsInt64, ct);
            await TouchAsync(context, B.Namespaces, row, ct); identities.Add(B.Hash(key), row["_id"].AsInt64);
        }
        // Full scoped head reconstruction also rejects missing current versions. Namespace writes serialize changes, including retirement of old codes.
        using var cursor = await Rows(B.Heads).Find(context.Tx, new BsonDocument("owner", B.Owner(input.Owner))).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            if (candidate["_id"] == context.Head["_id"]) continue;
            var head = await records.HeadAsync(context.Tx, candidate["_id"].AsInt64, ct); if (head["versionId"].IsBsonNull) continue;
            var version = await records.RowAsync(context.Tx, B.Versions, head["versionId"].AsInt64, ct);
            foreach (var existing in version["mappings"].AsBsonArray.Select(B.Mapping))
                if (input.Mappings.Any(mapping => SupplierRules.SameNamespace(mapping, existing) && SupplierRules.Overlaps(mapping, existing))) throw B.Conflict();
        }
        return new BsonArray(keys.Select(key => new BsonInt64(identities[B.Hash(key)])));
    }
}
