using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderCommercialPolicyService
{
    private static void Expected(Context context, OrderCommercialPolicyInput input) {
        if (context.Source["target"]["version"].AsInt64 != input.ExpectedScopeVersion || context.Source["profile"]["version"].AsInt64 != input.ExpectedProfileVersion ||
            (context.Head?["version"].AsInt64 ?? 0) != input.ExpectedPolicyVersion) throw B.Changed();
        var sellers = context.SellerSources; var expected = input.SellerVersions;
        if (sellers["organization"]["target"]["version"] != expected.OrganizationScopeVersion || sellers["organization"]["profile"]["version"] != expected.OrganizationProfileVersion ||
            sellers["shop"]["target"]["version"] != expected.ShopScopeVersion || sellers["shop"]["profile"]["version"] != expected.ShopProfileVersion ||
            sellers["channel"]["target"]["version"] != expected.ChannelScopeVersion || sellers["channel"]["profile"]["version"] != expected.ChannelProfileVersion) throw B.Changed();
    }
    private static BsonDocument PreviewView(Context context, OrderCommercialPolicyInput input) => new() {
        { "target", B.Target(input.Target) }, { "code", context.Source["profile"]["code"] }, { "name", context.Source["profile"]["name"] },
        { "scopeVersion", input.ExpectedScopeVersion }, { "profileVersion", input.ExpectedProfileVersion }, { "policyVersion", input.ExpectedPolicyVersion },
        { "context", ContextView(context.Source) }, { "sellerVersions", B.SellerVersions(input.SellerVersions) },
        { "seller", new BsonDocument { { "organizationName", context.SellerSources["organization"]["profile"]["name"] },
            { "shopName", context.SellerSources["shop"]["profile"]["name"] }, { "channelName", context.SellerSources["channel"]["profile"]["name"] } } },
        { "rule", B.Rule(input.Rule) }, { "validFrom", input.ValidFrom.UtcDateTime }, { "validUntil", input.ValidUntil.UtcDateTime }, { "sourceHash", context.Source["snapshotHash"] } };
    public async Task<OrderCommercialPolicyPreview> PreviewAsync(ActorContext actor, OrderCommercialPolicyInput input, CancellationToken ct = default) {
        input = OrderCommercialPolicyRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Target, B.ProposePermission, token);
            Expected(context, input); await PeopleAsync(context, null, null, token); var binding = B.Binding(input, context.Source, context.SellerSources, context.Head);
            context.ExpiresAt = input.ValidUntil; await FenceAsync(context, token); return new OrderCommercialPolicyPreview(binding, PreviewView(context, input));
        }, ct);
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "sourceKey", B.SourceKey(row) }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> capture,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            var permission = B.Permission(operation); var allowed = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(allowed);
            var saved = await ForKeyAsync(actor, authority, tx, operation, metadata.IdempotencyKey, token);
            selected = await capture(authority, tx, saved, token); var query = Require(selected);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(selected.Resource)).Select(clause => clause.GrantId).Distinct().ToImmutableArray()),
                selected.Source["hierarchy"].AsBsonArray.Concat(selected.SellerSources.Values.SelectMany(source => source["hierarchy"].AsBsonArray)).Select(OrganizationProfileRecords.Node).Distinct().ToImmutableArray(), permission,
                new(selected.Resource, permission == B.ApprovePermission ? new ApprovalRequest(1, 1, ProposedBy: selected.Proposal!["actorId"].AsInt64) : null));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); context.Head = await records.HeadAsync(tx, row["policyId"].AsInt64, token);
            BindPeople(context, await records.ProposalAsync(tx, row["proposalId"].AsInt64, token), operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId,
                operation == B.Prefix + "propose" ? null : (await records.ProposalAsync(tx, row["proposalId"].AsInt64, token))["actorId"].AsInt64, token);
            await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, OrderCommercialPolicyInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = OrderCommercialPolicyRules.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var proposalId = (await ids.NextAsync(B.Proposals, ct)).Value; var policyId = (await ids.NextAsync(B.Heads, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding);
        return await CommandAsync(actor, B.Prefix + "propose", metadata, body, async (authority, tx, saved, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Target, B.ProposePermission, token);
            if (saved is not null) BindPeople(context, saved);
            if (saved is null) { Expected(context, input); Equal(binding, B.Binding(input, context.Source, context.SellerSources, context.Head)); context.ExpiresAt = input.ValidUntil; }
            await PeopleAsync(context, null, null, token); return context;
        }, async (context, token) => {
            Expected(context, input); Equal(binding, B.Binding(input, context.Source, context.SellerSources, context.Head)); Current(context); var now = Now;
            var row = new BsonDocument { { "_id", proposalId }, { "schemaVersion", 1 }, { "policyId", context.Head?["_id"] ?? policyId }, { "proposalId", proposalId },
                { "revision", 1L }, { "baseVersion", input.ExpectedPolicyVersion }, { "target", B.Target(input.Target) },
                { "source", context.Source.DeepClone() }, { "sellerSources", context.SellerSources.DeepClone() }, { "details", B.Details(input) }, { "input", B.Input(input) }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "propose", auditId, actor, metadata, body, now);
            if (context.Head is null) {
                var head = new BsonDocument { { "_id", policyId }, { "schemaVersion", 1 }, { "target", B.Target(input.Target) },
                    { "rootProposalId", proposalId }, { "version", 0L }, { "versionId", BsonNull.Value }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime }, { "fence", 0L } };
                await Rows(B.Heads).InsertOneAsync(context.Tx, head, cancellationToken: token); context.Head = head;
            }
            await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long proposalId, CancellationToken ct,
        string permission = B.ApprovePermission) {
        _ = Query(actor, authority, permission);
        var raw = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", proposalId)).Project(new BsonDocument("target", 1)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, B.Target(raw["target"]), permission, ct);
        context.Proposal = await records.ProposalAsync(tx, proposalId, ct); BindPeople(context, context.Proposal);
        Equal(context.Head!["_id"], context.Proposal["policyId"]); return context;
    }
    private static bool BindingCurrent(Context context) => context.Proposal is { } proposal && context.Head is { } head &&
        proposal["source"].Equals(context.Source) && proposal["sellerSources"].Equals(context.SellerSources) && proposal["baseVersion"] == head["version"];
    private async Task PendingAsync(Context context, OrderCommercialPolicyDecision decision, CancellationToken ct) {
        if (context.Proposal is null || await records.DecisionForAsync(context.Tx, context.Proposal, ct) is not null) throw B.Changed();
        if (decision == OrderCommercialPolicyDecision.Approve) {
            if (!BindingCurrent(context)) throw B.Changed();
            context.ExpiresAt = B.Date(context.Proposal["details"]["validUntil"]); Current(context);
        }
    }
    public async Task<OrderCommercialPolicyPreview> PreviewDecisionAsync(ActorContext actor, long proposalId, long expectedRevision, OrderCommercialPolicyDecision decision, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision)) throw OrderCommercialPolicyRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, token); var proposal = context.Proposal!;
            await PendingAsync(context, decision, token); await PeopleAsync(context, actor.UserId, proposal["actorId"].AsInt64, token);
            var view = await ProposalViewAsync(context, proposal, token); view["decision"] = B.Decision(decision);
            await FenceAsync(context, token); return new OrderCommercialPolicyPreview(B.DecisionBinding(proposal, decision), view);
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedRevision, OrderCommercialPolicyDecision decision, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision)) throw OrderCommercialPolicyRules.Invalid();
        B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        long? versionId = decision == OrderCommercialPolicyDecision.Approve ? (await ids.NextAsync(B.Versions, ct)).Value : null;
        var body = B.DecideBody(proposalId, expectedRevision, decision, binding); var operation = B.Prefix + B.Decision(decision);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, token);
            Equal(binding, B.DecisionBinding(context.Proposal!, decision)); if (saved is null) await PendingAsync(context, decision, token);
            await PeopleAsync(context, actor.UserId, context.Proposal!["actorId"].AsInt64, token); return context;
        }, async (context, token) => {
            await PendingAsync(context, decision, token); var proposal = context.Proposal!; var head = context.Head!; var now = Now;
            if (now.UtcDateTime < proposal["recordedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "revision", 1L }, { "decision", B.Decision(decision) },
                { "versionId", versionId is { } approvedId ? new BsonInt64(approvedId) : BsonNull.Value },
                { "policyVersion", versionId.HasValue ? new BsonInt64(checked(proposal["baseVersion"].AsInt64 + 1)) : BsonNull.Value },
                { "input", B.DecisionInput(proposalId, expectedRevision, decision) }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            foreach (var field in new[] { "policyId", "proposalId", "target", "source", "sellerSources", "details", "baseVersion" }) row.Add(field, proposal[field].DeepClone());
            B.Metadata(row, operation, auditId, actor, metadata, body, now);
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token);
            if (versionId is { } approved) {
                var previous = head["versionId"].IsBsonNull ? null : await records.RowAsync(context.Tx, B.Versions, head["versionId"].AsInt64, token);
                var version = new BsonDocument { { "_id", approved }, { "schemaVersion", 1 }, { "version", row["policyVersion"] }, { "decisionId", id },
                    { "previousVersionId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["snapshotHash"] }, { "effectiveAt", now.UtcDateTime } };
                foreach (var field in new[] { "policyId", "proposalId", "target", "source", "sellerSources", "details" }) version.Add(field, proposal[field].DeepClone());
                version.Add("snapshotHash", B.Hash(version)); await Rows(B.Versions).InsertOneAsync(context.Tx, version, cancellationToken: token);
                if (head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Heads).UpdateOneAsync(context.Tx, head,
                    Builders<BsonDocument>.Update.Set("version", row["policyVersion"]).Set("versionId", approved).Set("updatedAt", now.UtcDateTime).Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                context.Head = await Rows(B.Heads).Find(context.Tx, new BsonDocument("_id", head["_id"])).SingleAsync(token);
            }
            await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
