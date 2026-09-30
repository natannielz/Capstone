using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Platform.Billing;

public sealed partial class ReceivingAccountService
{
    private async Task AuditReadAsync(ActorContext actor, IClientSessionHandle tx, string operation, BsonDocument selection, int count, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument { { "_id", (await ids.NextAsync("auditEvents", ct)).Value }, { "schemaVersion", 1 },
            { "kind", B.Prefix + "read" }, { "operation", operation }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
            { "selection", selection }, { "resultCount", count }, { "createdAt", Now.UtcDateTime } }, cancellationToken: ct);
    }
    private async Task GlobalFenceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, string[] permissions, CancellationToken ct) {
        AuthorizationQuery[] Queries() => permissions.Select(permission => { var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query); return query; }).ToArray();
        var clauses = Queries().SelectMany(query => query.Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        await LiveSessionAsync(actor, tx, ct);
        _ = Queries(); ct.ThrowIfCancellationRequested();
    }
    private async Task<IReadOnlyList<BsonDocument>> ListAsync(ActorContext actor, string collection, long? afterId, bool full, CancellationToken ct) {
        if (afterId is <= 0) throw ReceivingAccountPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<IReadOnlyList<BsonDocument>>(actor, async (snapshot, tx, token) => {
            var permissions = full ? new[] { Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead } : [Permissions.ReceivingAccountRead];
            var queries = permissions.Select(permission => { var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query); return query; }).ToArray();
            var scoped = new BsonDocument("$and", new BsonArray(queries.Select(query => ScopeQuery.Create(query).Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry)))));
            var pipeline = new List<BsonDocument> { new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument("$mergeObjects", new BsonArray { "$scope", new BsonDocument("_id", "$_id") }))),
                new("$match", scoped) };
            if (afterId.HasValue) pipeline.Add(new("$match", new BsonDocument("_id", new BsonDocument("$gt", afterId.Value))));
            pipeline.Add(new("$sort", new BsonDocument("_id", 1))); pipeline.Add(new("$limit", 26));
            var selected = await Rows(collection).Aggregate<BsonDocument>(tx, pipeline).ToListAsync(token);
            var output = new List<BsonDocument>(); var contexts = new List<Context>();
            foreach (var item in selected) {
                var row = await records.RowAsync(tx, collection, item["_id"].AsInt64, token);
                var context = new Context(actor, snapshot, tx, Permissions.ReceivingAccountRead, row["scope"].AsBsonDocument, full); _ = Require(context);
                contexts.Add(context);
                if (collection == B.Policies) {
                    // Return expired/revoked policy metadata explicitly; it never authorizes a new command.
                    var copy = B.PolicySnapshot(row); output.Add(copy); await TouchAsync(context, B.Policies, row, token);
                } else {
                    context.Head = await records.HeadAsync(tx, row["accountId"].AsInt64, token);
                    var proposal = await records.ProposalAsync(tx, row["_id"].AsInt64, token);
                    var decision = await Rows(B.Decisions).Find(tx, F.Eq("proposalId", row["_id"])).SingleOrDefaultAsync(token);
                    if (decision is not null) _ = await records.DecisionAsync(tx, decision["_id"].AsInt64, proposal, token);
                    var details = B.Details(proposal["details"]);
                    output.Add(new BsonDocument { { "proposalId", proposal["_id"] }, { "accountId", proposal["accountId"] }, { "scope", proposal["scope"] },
                        { "bankCode", details.BankCode }, { "maskedAccount", ReceivingAccountPolicy.Mask(details) }, { "baseVersion", proposal["baseVersion"] },
                        { "currentVersion", context.Head["version"] }, { "actorId", proposal["actorId"] }, { "recordedAt", proposal["recordedAt"] },
                        { "state", decision is null ? "pending" : decision["decision"] } });
                }
                await CompleteAsync(context, token);
            }
            await AuditReadAsync(actor, tx, "list-" + collection, new("afterId", afterId is { } cursor ? new BsonInt64(cursor) : BsonNull.Value), output.Count, token);
            await GlobalFenceAsync(actor, snapshot, tx, permissions, token);
            foreach (var context in contexts) _ = Require(context);
            return output;
        }, ct);
    }
    public Task<IReadOnlyList<BsonDocument>> ListPoliciesAsync(ActorContext actor, long? afterId = null, CancellationToken ct = default) => ListAsync(actor, B.Policies, afterId, true, ct);
    public Task<IReadOnlyList<BsonDocument>> ListProposalsAsync(ActorContext actor, long? afterId = null, CancellationToken ct = default) => ListAsync(actor, B.Proposals, afterId, false, ct);
    public async Task<BsonDocument> ReadProposalAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        if (proposalId <= 0) throw ReceivingAccountPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var context = await ProposalContextAsync(actor, snapshot, tx, proposalId, Permissions.ReceivingAccountDetailsRead, token);
            var proposal = context.Proposal!;
            var observation = await Rows(B.Observations).Find(tx, F.Eq("proposalId", proposalId)).SingleOrDefaultAsync(token);
            var decision = await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposalId)).SingleOrDefaultAsync(token);
            if (observation is not null) observation = await records.ObservationAsync(tx, observation["_id"].AsInt64, proposal, token);
            if (decision is not null) decision = await records.DecisionAsync(tx, decision["_id"].AsInt64, proposal, token);
            var current = context.Head!["versionId"].IsBsonNull ? null : await records.RowAsync(tx, B.Versions, context.Head["versionId"].AsInt64, token);
            var result = new BsonDocument { { "proposal", proposal }, { "observation", (BsonValue?)observation ?? BsonNull.Value }, { "decision", (BsonValue?)decision ?? BsonNull.Value },
                { "currentVersion", (BsonValue?)current ?? BsonNull.Value } };
            await AuditReadAsync(actor, tx, "proposal-details", new("proposalId", proposalId), 1, token); await CompleteAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Metadata(new(key, "Recover receiving account operation", "receiving-account-recovery")); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) => {
            var permissions = new[] { permission, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead };
            foreach (var required in permissions) AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, required, clock.UtcNow));
            var saved = await records.ForKeyAsync(tx, actor.UserId, operation, key, token); Context? captured = null;
            if (saved is not null) {
                var context = captured = await ProposalContextAsync(actor, snapshot, tx, saved["proposalId"].AsInt64, permission, token);
                if (PersonApprovalPolicy.RequiresEvidence(permission)) {
                    long? observer = null;
                    if (operation == B.Prefix + "approve") observer = (await records.ObservationAsync(tx, saved["observationId"].AsInt64, context.Proposal!, token))["actorId"].AsInt64;
                    await PeopleAsync(context, true, observer, token);
                }
                await CompleteAsync(context, token);
            }
            await AuditReadAsync(actor, tx, "operation-recovery", new() { { "command", operation }, { "key", key } }, saved is null ? 0 : 1, token);
            await GlobalFenceAsync(actor, snapshot, tx, permissions, token);
            if (captured is not null) { _ = Require(captured); captured.People?.RequireCurrent(tx, clock.UtcNow); }
            return saved is null ? null : B.Result(saved);
        }, ct);
    }
}
