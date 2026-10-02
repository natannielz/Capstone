using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.SupplierBankBson;
using S = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierBankService
{
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long proposalId, string permission, bool full, CancellationToken ct) {
        _ = Query(actor, authority, permission);
        var route = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", proposalId)).Project(new BsonDocument("owner", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, S.Owner(route["owner"]), permission, full, ct); context.Proposal = await records.ProposalAsync(tx, proposalId, ct);
        context.Head = await records.HeadAsync(tx, context.Proposal["beneficiaryId"].AsInt64, ct); BindPeople(context, context.Proposal); return context;
    }
    private async Task<BsonDocument?> ForKeyAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string operation, string key, CancellationToken ct) {
        var table = operation == B.Prefix + "propose" ? B.Proposals : operation == B.Prefix + "observe" ? B.Observations : B.Decisions;
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", key } }).Project(new BsonDocument("owner", 1)).SingleOrDefaultAsync(ct);
        if (row is not null) _ = Clauses(new(actor, authority, tx, S.Owner(row["owner"]), B.Permission(operation), true));
        return await records.ForKeyAsync(tx, actor.UserId, operation, key, ct);
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", B.SourceKey(row) },
            { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "owner", row["owner"] }, { "supplierId", row["supplierId"] },
            { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> capture, Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            var permission = B.Permission(operation); _ = Query(actor, authority, permission); _ = Query(actor, authority, Permissions.SupplierBankDetailsRead);
            var saved = await ForKeyAsync(actor, authority, tx, operation, metadata.IdempotencyKey, token); selected = await capture(authority, tx, saved, token); var clauses = Clauses(selected);
            return new(new(true, "allowed", clauses.Select(clause => clause.GrantId).Distinct().ToImmutableArray()), ScopePolicy.RequiredNodes(selected.Resource, authority.Directory), permission,
                new(selected.Resource, permission == Permissions.SupplierBankApprove ? new ApprovalRequest(1, 1, ProposedBy: selected.Proposal!["actorId"].AsInt64) : null));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); context.Proposal = await records.ProposalAsync(tx, row["proposalId"].AsInt64, token);
            context.Head = await records.HeadAsync(tx, row["beneficiaryId"].AsInt64, token); BindPeople(context, context.Proposal, row);
            long? observer = null;
            if (operation == B.Prefix + "approve") { var observed = await records.ObservationAsync(tx, row["observationId"].AsInt64, context.Proposal, token); observer = observed["actorId"].AsInt64; BindPeople(context, observed); }
            await PeopleAsync(context, operation != B.Prefix + "propose", observer, token); await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    private async Task PrepareProposalAsync(Context context, SupplierBankInput input, CancellationToken ct) {
        await SupplierAsync(context, input.SupplierId, input.SupplierVersionId, input.SupplierVersion, true, ct); await HeadAsync(context, input.SupplierId, ct);
        Equal(context.Head?["version"] ?? new BsonInt64(0), input.BaseVersion); await PolicyAsync(context, input, false, ct); await PeopleAsync(context, false, null, ct); Current(context);
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, SupplierBankInput input, CancellationToken ct = default) {
        input = SupplierBankRules.Copy(input); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Owner, Permissions.SupplierBankPropose, true, token); await PrepareProposalAsync(context, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "source", S.SourceView(context.Sources) }, { "supplier", B.SupplierView(context.Supplier!) }, { "policy", B.PolicyView(context.Policy!) },
                { "binding", B.Binding(actor.UserId, actor.GrantVersion, input, context.Supplier!, context.Sources, context.Policy!) }, { "observedAt", Now.UtcDateTime } };
            await AuditReadAsync(context, "proposal-preview", new("supplierId", input.SupplierId), token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, SupplierBankInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = SupplierBankRules.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var beneficiaryId = (await ids.NextAsync(B.Heads, ct)).Value; var body = B.ProposeBody(input, binding);
        return await CommandAsync(actor, B.Prefix + "propose", metadata, body, async (authority, tx, saved, token) => {
            if (saved is not null) { var original = await ProposalContextAsync(actor, authority, tx, saved["proposalId"].AsInt64, Permissions.SupplierBankPropose, true, token); await PeopleAsync(original, false, null, token); return original; }
            var context = await ContextAsync(actor, authority, tx, input.Owner, Permissions.SupplierBankPropose, true, token); await PrepareProposalAsync(context, input, token);
            Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, input, context.Supplier!, context.Sources, context.Policy!)); return context;
        }, async (context, token) => {
            Current(context); var now = Now; var bankId = context.Head?["_id"] ?? new BsonInt64(beneficiaryId);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", id }, { "beneficiaryId", bankId }, { "supplierId", input.SupplierId }, { "version", 1L }, { "owner", S.Owner(input.Owner) },
                { "input", B.Input(input) }, { "supplier", context.Supplier!.DeepClone() }, { "sources", context.Sources.DeepClone() }, { "policy", B.Snapshot(context.Policy!) }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(S.Person)) } }; B.Metadata(row, B.Prefix + "propose", auditId, actor, metadata, body, now);
            if (context.Head is null) {
                context.Head = new() { { "_id", bankId }, { "schemaVersion", 1 }, { "owner", S.Owner(input.Owner) }, { "supplierId", input.SupplierId }, { "rootProposalId", id },
                    { "version", 0L }, { "versionId", BsonNull.Value }, { "fence", 0L }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } };
                await Rows(B.Heads).InsertOneAsync(context.Tx, context.Head, cancellationToken: token);
            }
            await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task PendingAsync(Context context, bool currentBase, CancellationToken ct) {
        if (await records.DecisionForAsync(context.Tx, context.Proposal!, ct) is not null) throw B.Changed();
        if (currentBase) Equal(context.Head!["version"], context.Proposal!["input"]["baseVersion"]);
    }
    private async Task PrepareObservationAsync(Context context, CancellationToken ct) {
        await PendingAsync(context, true, ct); if (await records.ObservationForAsync(context.Tx, context.Proposal!, ct) is not null) throw B.Changed();
        var input = B.Input(context.Proposal!["input"]); await SupplierAsync(context, input.SupplierId, input.SupplierVersionId, input.SupplierVersion, true, ct);
        Equal(context.Supplier!, context.Proposal["supplier"]); Equal(context.Sources, context.Proposal["sources"]); await PolicyAsync(context, input, true, ct); await PeopleAsync(context, true, null, ct);
    }
    private static void ObservationInput(ReceivingAccountDetails observed, string sourceReference) { ReceivingAccountPolicy.Copy(observed); if (!ReceivingAccountPolicy.Text(sourceReference, 256)) throw SupplierBankRules.Invalid(); }
    public async Task<BsonDocument> PreviewObservationAsync(ActorContext actor, long proposalId, ReceivingAccountDetails observed, string sourceReference, CancellationToken ct = default) {
        if (proposalId <= 0) throw SupplierBankRules.Invalid(); ObservationInput(observed, sourceReference); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierBankApprove, true, token); await PrepareObservationAsync(context, token);
            var result = new BsonDocument { { "proposal", await ProposalViewAsync(context, context.Proposal!, token) }, { "observed", ReceivingAccountBson.Details(observed) }, { "sourceReference", sourceReference },
                { "binding", B.ObservationBinding(actor.UserId, actor.GrantVersion, context.Proposal!, observed, sourceReference) }, { "observedAt", Now.UtcDateTime } };
            await AuditReadAsync(context, "observation-preview", new("proposalId", proposalId), token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ObserveAsync(ActorContext actor, long proposalId, long expectedVersion, ReceivingAccountDetails observed, string sourceReference, string binding, bool attested, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedVersion != 1 || !attested) throw SupplierBankRules.Invalid(); ObservationInput(observed, sourceReference); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Observations, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.ObserveBody(proposalId, expectedVersion, observed, sourceReference, binding, true);
        return await CommandAsync(actor, B.Prefix + "observe", metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierBankApprove, true, token);
            if (saved is null) { await PrepareObservationAsync(context, token); Equal(binding, B.ObservationBinding(actor.UserId, actor.GrantVersion, context.Proposal!, observed, sourceReference)); }
            else { BindPeople(context, saved); await PeopleAsync(context, true, null, token); } return context;
        }, async (context, token) => {
            Current(context); var proposal = context.Proposal!; var now = Now;
            if (now.UtcDateTime < proposal["recordedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "beneficiaryId", proposal["beneficiaryId"] }, { "supplierId", proposal["supplierId"] }, { "owner", proposal["owner"] },
                { "proposalHash", proposal["snapshotHash"] }, { "observed", ReceivingAccountBson.Details(observed) }, { "sourceReference", sourceReference }, { "policyHash", B.Hash(proposal["policy"].AsBsonDocument) },
                { "attested", true }, { "checkedAt", now.UtcDateTime }, { "binding", binding }, { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(S.Person)) } };
            B.Metadata(row, B.Prefix + "observe", auditId, actor, metadata, body, now); await Rows(B.Observations).InsertOneAsync(context.Tx, row, cancellationToken: token);
            context.Observation = row; await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task PrepareDecisionAsync(Context context, SupplierBankDecision decision, long? observationId, CancellationToken ct) {
        await PendingAsync(context, decision == SupplierBankDecision.Approve, ct);
        if (decision == SupplierBankDecision.Approve) {
            var input = B.Input(context.Proposal!["input"]); await SupplierAsync(context, input.SupplierId, input.SupplierVersionId, input.SupplierVersion, true, ct);
            Equal(context.Supplier!, context.Proposal["supplier"]); Equal(context.Sources, context.Proposal["sources"]); await PolicyAsync(context, input, true, ct);
            context.Observation = await records.ObservationAsync(context.Tx, observationId!.Value, context.Proposal, ct);
            ReceivingAccountPolicy.RequireMatch(input.Details, ReceivingAccountBson.Details(context.Observation["observed"])); SupplierBankRecords.ObservationAt(context.Proposal, context.Observation, Now); BindPeople(context, context.Observation);
        }
        await PeopleAsync(context, true, context.Observation?["actorId"].AsInt64, ct); Current(context);
    }
    private static void DecisionInput(long id, SupplierBankDecision decision, long? observationId) { if (id <= 0 || !Enum.IsDefined(decision) ||
        (decision == SupplierBankDecision.Approve ? observationId is null or <= 0 : observationId is not null)) throw SupplierBankRules.Invalid(); }
    private static string DecisionBinding(Context context, SupplierBankDecision decision) => B.DecisionBinding(context.Actor.UserId, context.Actor.GrantVersion,
        context.Proposal!, decision, context.Observation, context.Head!["version"].AsInt64, context.Sources);
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, SupplierBankDecision decision, long? observationId, CancellationToken ct = default) {
        DecisionInput(proposalId, decision, observationId); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierBankApprove, true, token); await PrepareDecisionAsync(context, decision, observationId, token);
            var result = new BsonDocument { { "proposal", await ProposalViewAsync(context, context.Proposal!, token) }, { "decision", B.Decision(decision) },
                { "observationId", observationId is { } id ? new BsonInt64(id) : BsonNull.Value }, { "binding", DecisionBinding(context, decision) }, { "observedAt", Now.UtcDateTime } };
            await AuditReadAsync(context, "decision-preview", new("proposalId", proposalId), token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedVersion, SupplierBankDecision decision, long? observationId, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        DecisionInput(proposalId, decision, observationId); if (expectedVersion != 1) throw SupplierBankRules.Invalid(); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var versionId = decision == SupplierBankDecision.Approve ? (await ids.NextAsync(B.Versions, ct)).Value : (long?)null; var operation = B.Prefix + B.Decision(decision);
        var body = B.DecideBody(proposalId, expectedVersion, decision, observationId, binding);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, Permissions.SupplierBankApprove, true, token);
            if (saved is null) { await PrepareDecisionAsync(context, decision, observationId, token); Equal(binding, DecisionBinding(context, decision)); }
            else { BindPeople(context, saved); if (observationId is { } oldId) BindPeople(context, await records.ObservationAsync(tx, oldId, context.Proposal!, token)); await PeopleAsync(context, true, null, token); } return context;
        }, async (context, token) => {
            Current(context); var proposal = context.Proposal!; var head = context.Head!; var now = Now;
            if (now.UtcDateTime < proposal["recordedAt"].ToUniversalTime() || now.UtcDateTime < head["updatedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposalId }, { "beneficiaryId", proposal["beneficiaryId"] }, { "supplierId", proposal["supplierId"] }, { "owner", proposal["owner"] },
                { "proposalHash", proposal["snapshotHash"] }, { "decision", B.Decision(decision) }, { "observationId", observationId is { } observation ? new BsonInt64(observation) : BsonNull.Value },
                { "versionId", versionId is { } approved ? new BsonInt64(approved) : BsonNull.Value }, { "bankVersion", versionId.HasValue ? new BsonInt64(checked(head["version"].AsInt64 + 1)) : BsonNull.Value },
                { "currentVersion", head["version"] }, { "reviewSources", context.Sources.DeepClone() }, { "binding", binding }, { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(S.Person)) } };
            B.Metadata(row, operation, auditId, actor, metadata, body, now);
            if (versionId is { } nextId) {
                BsonValue previousHash = head["versionId"].IsBsonNull ? BsonNull.Value : (await records.RowAsync(context.Tx, B.Versions, head["versionId"].AsInt64, token))["snapshotHash"];
                var version = new BsonDocument { { "_id", nextId }, { "schemaVersion", 1 }, { "beneficiaryId", head["_id"] }, { "owner", head["owner"] }, { "supplierId", proposal["supplierId"] },
                    { "version", row["bankVersion"] }, { "proposalId", proposalId }, { "observationId", row["observationId"] }, { "decisionId", id }, { "previousVersionId", head["versionId"] }, { "previousHash", previousHash },
                    { "details", proposal["input"]["details"] }, { "physicalIdentity", ReceivingAccountBson.PhysicalIdentity(B.Input(proposal["input"]).Details) }, { "supplier", proposal["supplier"] }, { "policy", proposal["policy"] }, { "effectiveAt", now.UtcDateTime } };
                version.Add("snapshotHash", B.Hash(version)); await Rows(B.Versions).InsertOneAsync(context.Tx, version, cancellationToken: token);
                var replacement = head.DeepClone().AsBsonDocument; replacement["version"] = row["bankVersion"]; replacement["versionId"] = nextId; replacement["updatedAt"] = now.UtcDateTime;
                if (head["fence"].AsInt64 >= long.MaxValue - 1) throw B.Changed(); replacement["fence"] = head["fence"].AsInt64 + 1;
                if ((await Rows(B.Heads).ReplaceOneAsync(context.Tx, head, replacement, cancellationToken: token)).ModifiedCount != 1) throw B.Changed(); context.Head = replacement;
            }
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
