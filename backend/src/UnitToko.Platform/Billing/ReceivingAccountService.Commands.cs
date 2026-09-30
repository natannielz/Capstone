using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Platform.Billing;

public sealed partial class ReceivingAccountService
{
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, long? accountId, long baseVersion, long policyId, long policyRevision,
        ReceivingAccountDetails details, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata); details = ReceivingAccountPolicy.Copy(details);
        if (accountId is <= 0 || baseVersion is < 0 or >= long.MaxValue - 1 || accountId is null && baseVersion != 0 || policyId <= 0 || policyRevision <= 0) throw ReceivingAccountPolicy.Invalid();
        await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Proposals, ct)).Value; var newAccountId = accountId ?? (await ids.NextAsync(B.Heads, ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(accountId, baseVersion, policyId, policyRevision, details);
        return await CommandAsync(actor, B.Prefix + "propose", metadata, body, async (snapshot, tx, saved, token) => {
            if (saved is not null) return await ProposalContextAsync(actor, snapshot, tx, saved["proposalId"].AsInt64, Permissions.ReceivingAccountPropose, token);
            var policy = await records.RowAsync(tx, B.Policies, policyId, token);
            var context = new Context(actor, snapshot, tx, Permissions.ReceivingAccountPropose, policy["scope"].AsBsonDocument) { Policy = policy }; _ = Require(context);
            if (accountId is { } existing) {
                context.Head = await records.HeadAsync(tx, existing, token);
                if (context.Head["scope"] != context.Scope) throw new AuthorizationDeniedException("scope_denied");
            }
            return context;
        }, async (context, token) => {
            var policy = context.Policy ?? throw B.Broken();
            if (policy["revision"] != policyRevision || policy["bankCode"] != details.BankCode || (context.Head?["version"].AsInt64 ?? 0) != baseVersion) throw B.Changed();
            await CurrentPolicyAsync(context, token); var now = Now;
            if (context.Head is { } old && old["updatedAt"].ToUniversalTime() > now.UtcDateTime) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "accountId", newAccountId }, { "proposalId", id }, { "scope", context.Scope },
                { "isNewAccount", accountId is null }, { "baseVersion", baseVersion }, { "details", B.Details(details) }, { "policy", B.PolicySnapshot(context.Policy!) },
                { "people", await PeopleAsync(context, false, null, token) } };
            B.Metadata(row, "propose", auditId, actor, metadata, body, now);
            if (accountId is null) {
                context.Head = new BsonDocument { { "_id", newAccountId }, { "schemaVersion", 1 }, { "scope", context.Scope }, { "rootProposalId", id },
                    { "version", 0L }, { "versionId", BsonNull.Value }, { "fence", 0L }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } };
                await Rows(B.Heads).InsertOneAsync(context.Tx, context.Head, cancellationToken: token);
            }
            await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token);
            await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    public async Task<BsonDocument> ObserveAsync(ActorContext actor, long proposalId, long expectedRevision, ReceivingAccountDetails observed,
        string sourceReference, bool attested, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata); observed = ReceivingAccountPolicy.Copy(observed);
        if (proposalId <= 0 || expectedRevision != 1 || !attested || !ReceivingAccountPolicy.Text(sourceReference, 256)) throw ReceivingAccountPolicy.Invalid();
        await StorageAsync(ct); var id = (await ids.NextAsync(B.Observations, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ObserveBody(proposalId, expectedRevision, observed, sourceReference, attested);
        return await CommandAsync(actor, B.Prefix + "observe", metadata, body,
            (snapshot, tx, _, token) => ProposalContextAsync(actor, snapshot, tx, proposalId, Permissions.ReceivingAccountVerify, token), async (context, token) => {
                await PendingAsync(context, true, token); await CurrentPolicyAsync(context, token);
                if (await Rows(B.Observations).Find(context.Tx, F.Eq("proposalId", proposalId)).AnyAsync(token)) throw B.Changed();
                var now = Now; var proposal = context.Proposal!;
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "accountId", proposal["accountId"] }, { "proposalId", proposalId }, { "scope", context.Scope },
                    { "details", B.Details(observed) }, { "policyHash", B.PolicyHash(proposal["policy"].AsBsonDocument) }, { "sourceReference", sourceReference },
                    { "checkedAt", now.UtcDateTime }, { "attested", true }, { "people", await PeopleAsync(context, true, null, token) } };
                B.Metadata(row, "observe", auditId, actor, metadata, body, now);
                await Rows(B.Observations).InsertOneAsync(context.Tx, row, cancellationToken: token);
                await EffectsAsync(context, row, token); return B.Result(row);
            }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedRevision, ReceivingAccountDecision decision, long? observationId,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Metadata(metadata);
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision) ||
            (decision == ReceivingAccountDecision.Approve ? observationId is null or <= 0 : observationId is not null)) throw ReceivingAccountPolicy.Invalid();
        await StorageAsync(ct); var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var versionId = decision == ReceivingAccountDecision.Approve ? (await ids.NextAsync(B.Versions, ct)).Value : (long?)null;
        var operation = B.Prefix + B.Name(decision); var body = B.DecideBody(proposalId, expectedRevision, decision, observationId);
        return await CommandAsync(actor, operation, metadata, body, async (snapshot, tx, _, token) => {
            var context = await ProposalContextAsync(actor, snapshot, tx, proposalId, B.Permission(operation), token);
            if (decision == ReceivingAccountDecision.Withdraw && context.Proposal!["actorId"].AsInt64 != actor.UserId) throw new AuthorizationDeniedException("original_proposer_required");
            return context;
        }, async (context, token) => {
            await PendingAsync(context, decision == ReceivingAccountDecision.Approve, token);
            var proposal = context.Proposal!; var head = context.Head!; var now = Now; long? observer = null;
            if (decision == ReceivingAccountDecision.Approve) {
                await CurrentPolicyAsync(context, token);
                context.Observation = await records.ObservationAsync(context.Tx, observationId!.Value, proposal, token);
                ReceivingAccountPolicy.RequireMatch(B.Details(proposal["details"]), B.Details(context.Observation["details"]));
                ReceivingAccountRecords.RequireObservationAt(proposal, context.Observation, now);
                observer = context.Observation["actorId"].AsInt64;
            }
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "accountId", proposal["accountId"] }, { "proposalId", proposalId }, { "scope", context.Scope },
                { "decision", B.Name(decision) }, { "observationId", observationId is { } observedId ? new BsonInt64(observedId) : BsonNull.Value },
                { "versionId", versionId is { } approvedId ? new BsonInt64(approvedId) : BsonNull.Value },
                { "accountVersion", versionId.HasValue ? new BsonInt64(checked(head["version"].AsInt64 + 1)) : BsonNull.Value },
                { "people", await PeopleAsync(context, decision != ReceivingAccountDecision.Withdraw, observer, token) } };
            B.Metadata(row, B.Name(decision), auditId, actor, metadata, body, now);
            if (versionId is { } nextId) {
                var previous = head["versionId"].IsBsonNull ? null : await records.RowAsync(context.Tx, B.Versions, head["versionId"].AsInt64, token);
                var version = new BsonDocument { { "_id", nextId }, { "schemaVersion", 1 }, { "accountId", proposal["accountId"] }, { "version", row["accountVersion"] }, { "scope", context.Scope },
                    { "proposalId", proposalId }, { "observationId", observationId!.Value }, { "decisionId", id }, { "previousVersionId", head["versionId"] },
                    { "previousHash", previous is null ? BsonNull.Value : previous["snapshotHash"] }, { "details", proposal["details"] },
                    { "physicalIdentity", B.PhysicalIdentity(B.Details(proposal["details"])) }, { "effectiveAt", now.UtcDateTime } };
                version.Add("snapshotHash", B.Hash(version));
                var next = head.DeepClone().AsBsonDocument; next["version"] = version["version"]; next["versionId"] = nextId; next["updatedAt"] = now.UtcDateTime;
                next["fence"] = checked(head["fence"].AsInt64 + 1);
                await Rows(B.Versions).InsertOneAsync(context.Tx, version, cancellationToken: token);
                if ((await Rows(B.Heads).ReplaceOneAsync(context.Tx, head, next, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                context.Head = next;
            }
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token);
            await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
