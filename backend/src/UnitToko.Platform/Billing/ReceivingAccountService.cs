using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;
using IdentityPermissions = UnitToko.Modules.Identity.Permissions;

namespace UnitToko.Platform.Billing;

/// <summary>Versioned routing details only. This owner neither verifies payments nor initiates transfers.
/// Verification policies are approved operational inputs; the application does not invent a trusted channel.</summary>
public sealed partial class ReceivingAccountService
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly ReceivingAccountRecords records;
    public ReceivingAccountService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock) {
        this.database = database; this.ids = ids; this.clock = clock; authorization = new(database, unit, clock); records = new(database);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, string permission, BsonDocument scope, bool full = true) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Snapshot { get; } = snapshot;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string[] Permissions { get; } = (full ? new[] { permission, IdentityPermissions.ReceivingAccountRead, IdentityPermissions.ReceivingAccountDetailsRead } : [permission]).Distinct().ToArray();
        internal BsonDocument Scope { get; } = scope;
        internal ResourceScope Resource => B.Scope(Scope);
        internal BsonDocument? Head { get; set; }
        internal BsonDocument? Policy { get; set; }
        internal BsonDocument? Proposal { get; set; }
        internal BsonDocument? Observation { get; set; }
        internal bool Activating { get; set; }
        internal PersonAuthorityProof? People { get; set; }
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            !ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128)) throw ReceivingAccountPolicy.Invalid();
    }
    private AuthorizationQuery[] Require(Context context) => context.Permissions.Select(permission => {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Snapshot.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }).ToArray();
    private async Task StorageAsync(CancellationToken ct) {
        foreach (var name in ReceivingAccountSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, ReceivingAccountSchemas.Validator(name), ReceivingAccountSchemas.Key(name), ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleAsync(ct);
            if (definition["options"].AsBsonDocument.Contains("encryptedFields")) throw B.Broken();
        }
        foreach (var name in new[] { "auditEvents", "idempotencyRecords", "outboxEvents" }) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    private async Task TouchAsync(Context context, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 ||
            (await Rows(name).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
    }
    private async Task CompleteAsync(Context context, CancellationToken ct) {
        var queries = Require(context);
        if (context.Head is { } head) {
            head = await records.HeadAsync(context.Tx, head["_id"].AsInt64, ct);
            await TouchAsync(context, B.Heads, head, ct);
        }
        if (context.Activating) {
            var policy = context.Policy ?? throw B.Broken();
            ReceivingAccountRecords.RequirePolicyAt(policy, Now);
            if (context.Observation is { } observed) ReceivingAccountRecords.RequireObservationAt(context.Proposal!, observed, Now);
            await TouchAsync(context, B.Policies, policy, ct);
        }
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(context.Resource, context.Snapshot.Directory)).Distinct(), [],
            context.Snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        await LiveSessionAsync(context.Actor, context.Tx, ct);
        _ = Require(context);
        if (context.Activating) {
            ReceivingAccountRecords.RequirePolicyAt(context.Policy!, Now);
            if (context.Observation is { } observed) ReceivingAccountRecords.RequireObservationAt(context.Proposal!, observed, Now);
        }
        if (context.People is { } proof && proof.Evidence.All(p => p.Verification == PersonVerification.Verified)) proof.RequireCurrent(context.Tx, clock.UtcNow);
        ct.ThrowIfCancellationRequested();
    }
    private async Task LiveSessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return;
        var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId,
            actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
        // The request can outlive its database predicate. Check the returned deadlines after the await.
        var now = clock.UtcNow.UtcDateTime;
        if (live is null || live["idleExpiresAt"].ToUniversalTime() <= now || live["absoluteExpiresAt"].ToUniversalTime() <= now)
            throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private void Final(Context context) {
        _ = Require(context);
        if (context.Activating) {
            ReceivingAccountRecords.RequirePolicyAt(context.Policy!, Now);
            if (context.Observation is { } observed) ReceivingAccountRecords.RequireObservationAt(context.Proposal!, observed, Now);
        }
        if (context.People is { } proof && proof.Evidence.All(person => person.Verification == PersonVerification.Verified)) proof.RequireCurrent(context.Tx, clock.UtcNow);
    }
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, long proposalId, string permission, CancellationToken ct) {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow));
        var raw = await records.RowAsync(tx, B.Proposals, proposalId, ct);
        var context = new Context(actor, snapshot, tx, permission, raw["scope"].AsBsonDocument); _ = Require(context);
        context.Proposal = await records.ProposalAsync(tx, proposalId, ct);
        context.Head = await records.HeadAsync(tx, raw["accountId"].AsInt64, ct);
        if (context.Head["scope"] != context.Scope) throw B.Broken();
        return context;
    }
    private async Task CurrentPolicyAsync(Context context, CancellationToken ct) {
        var original = context.Proposal?["policy"].AsBsonDocument ?? context.Policy ?? throw B.Broken();
        var current = await records.RowAsync(context.Tx, B.Policies, original["_id"].AsInt64, ct);
        if (!B.PolicySnapshot(current).Equals(B.PolicySnapshot(original)) || current["scope"] != context.Scope) throw B.Changed();
        ReceivingAccountRecords.RequirePolicyAt(current, Now); context.Policy = current; context.Activating = true;
    }
    private async Task PendingAsync(Context context, bool currentVersion, CancellationToken ct) {
        var proposal = context.Proposal ?? throw B.Broken();
        if (await Rows(B.Decisions).Find(context.Tx, F.Eq("proposalId", proposal["_id"])).AnyAsync(ct) ||
            currentVersion && context.Head!["version"] != proposal["baseVersion"]) throw B.Changed();
    }
    private async Task<BsonArray> PeopleAsync(Context context, bool independent, long? observer, CancellationToken ct) {
        var maker = context.Proposal?["actorId"].AsInt64 ?? context.Actor.UserId;
        var users = independent ? new[] { context.Actor.UserId, maker, observer ?? context.Actor.UserId }.Distinct().Order().ToArray() : [context.Actor.UserId];
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (independent) {
            var byUser = proof.Evidence.ToDictionary(p => p.UserId);
            ReceivingAccountPolicy.RequireIndependent(byUser[context.Actor.UserId], byUser[observer ?? context.Actor.UserId], byUser[maker]);
        }
        if (proof.Evidence.All(person => person.Verification == PersonVerification.Verified)) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.People = proof;
        return new(proof.Evidence.OrderBy(person => person.UserId).Select(B.Person));
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "sourceKey", B.SourceKey(row) }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] },
            { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> target,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null; var permission = B.Permission(operation);
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (snapshot, tx, token) => {
            AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow));
            var saved = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token);
            var context = selected = await target(snapshot, tx, saved, token); var queries = Require(context);
            var approval = PersonApprovalPolicy.RequiresEvidence(permission) ? new ApprovalRequest(1, 1, ProposedBy: context.Proposal!["actorId"].AsInt64) : null;
            return new(new(true, "allowed", queries.SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource)).Select(clause => clause.GrantId)).Distinct().ToImmutableArray()),
                ScopePolicy.RequiredNodes(context.Resource, snapshot.Directory), permission, new(context.Resource, approval));
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken();
            var saved = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            if (saved["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason) || saved["scope"] != context.Scope) throw B.Broken();
            context.Proposal = await records.ProposalAsync(tx, saved["proposalId"].AsInt64, token);
            context.Head = await records.HeadAsync(tx, saved["accountId"].AsInt64, token);
            if (PersonApprovalPolicy.RequiresEvidence(permission)) {
                long? observer = null;
                if (operation == B.Prefix + "approve") observer = (await records.ObservationAsync(tx, saved["observationId"].AsInt64, context.Proposal, token))["actorId"].AsInt64;
                await PeopleAsync(context, true, observer, token);
            }
            await CompleteAsync(context, token);
        }, validateCurrent: () => Final(selected ?? throw B.Broken()));
    }
}
