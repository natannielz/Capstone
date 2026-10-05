using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

/// <summary>Governed configuration workflow. Approval creates an inactive pool; no
/// writer authority, source observation or external reservation capability is activated.</summary>
public sealed partial class StockSourceSetupService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public Task<BsonDocument> ProposeAsync(ActorContext actor, StockSourceSetupInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => ProposeCoreAsync(actor, input, metadata, false, ct);
    private async Task<BsonDocument> ProposeCoreAsync(ActorContext actor, StockSourceSetupInput input,
        AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        StockSourceSetupPolicy.Validate(input); Metadata(metadata);
        await StorageAsync(ct);
        var id = (await ids.NextAsync("stockSourceProposals", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await CommandAsync(actor, "stock.source.propose", Permissions.StockSourcePropose, metadata, input,
            (_, _, _) => Task.FromResult(new AuthorizationTarget(Resource(input.Location))),
            async (tx, token) =>
            {
                var now = Now();
                var row = StockSourceSetupBson.Header("stockSourceProposals", id);
                row.Add("input", StockSourceSetupBson.Input(input)); row.Add("revision", 1L);
                row.Add("proposedBy", actor.UserId); row.Add("proposedAt", now.UtcDateTime);
                StockSourceSetupBson.Metadata(row, auditId, metadata);
                StockSourceSetupBson.RequireProposal(row);
                await Rows("stockSourceProposals").InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourceSetupBson.Audit(row, actor, "stock.source.propose", now), cancellationToken: token);
                return new BsonDocument { { "proposalId", id }, { "revision", 1L } };
            }, ct, recoveryOnly);
    }

    public Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        DecideCoreAsync(actor, proposalId, expectedRevision, decision, reviewReference, metadata, false, ct);
    private async Task<BsonDocument> DecideCoreAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        if (proposalId <= 0 || expectedRevision <= 0 || !Enum.IsDefined(decision) || !StockSourceSetupPolicy.Text(reviewReference, 160))
            throw StockSourceSetupBson.Invalid();
        Metadata(metadata); await StorageAsync(ct);
        var decisionId = (await ids.NextAsync("stockSourceDecisions", ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        long? poolId = decision == StockSourceSetupDecision.Approve ? (await ids.NextAsync("inventoryPools", ct)).Value : null;
        var name = decision.ToString().ToLowerInvariant();
        var permission = decision == StockSourceSetupDecision.Withdraw ? Permissions.StockSourcePropose : Permissions.StockSourceApprove;
        return await CommandAsync(actor, "stock.source." + name, permission, metadata,
            new { proposalId, expectedRevision, decision, reviewReference },
            async (snapshot, tx, token) =>
            {
                // Read only the scope first. Missing and inaccessible IDs have the same response,
                // and private history is loaded only after authorization for its stored location.
                var scoped = await Rows("stockSourceProposals").Find(tx, F.Eq("_id", proposalId))
                    .Project(new BsonDocument("input.location", 1)).SingleOrDefaultAsync(token);
                if (scoped is null) throw new AuthorizationDeniedException("scope_denied");
                var location = InventoryGraph.Location(scoped["input"]["location"].AsBsonDocument);
                Require(actor, snapshot, permission, Resource(location));
                var proposal = await ProposalAsync(tx, proposalId, token);
                if (decision == StockSourceSetupDecision.Withdraw && proposal["proposedBy"].AsInt64 != actor.UserId)
                    throw new AuthorizationDeniedException("stock_source_proposer_required");
                return new(Resource(StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument).Location),
                    decision == StockSourceSetupDecision.Withdraw ? null : new(1, expectedRevision, ProposedBy: proposal["proposedBy"].AsInt64));
            },
            async (tx, token) =>
            {
                var proposal = await ProposalAsync(tx, proposalId, token);
                if (expectedRevision != proposal["revision"].AsInt64)
                    throw new PersistenceConflictException("stock_source_revision_changed", "The proposed setup revision changed");
                if (await Rows("stockSourceDecisions").Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(token))
                    throw new PersistenceConflictException("stock_source_already_decided", "The proposed setup already has a decision");
                var input = StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument);
                var now = Now();
                if (now < StockSourceSetupBson.Time(proposal["proposedAt"])) throw StockSourceSetupBson.Invalid();
                var row = StockSourceSetupBson.Header("stockSourceDecisions", decisionId);
                row.Add("proposalId", proposalId); row.Add("revision", expectedRevision); row.Add("decision", name);
                row.Add("decidedBy", actor.UserId); row.Add("decidedAt", now.UtcDateTime); row.Add("reviewReference", reviewReference);
                row.Add("poolId", poolId.HasValue ? new BsonInt64(poolId.Value) : BsonNull.Value);
                StockSourceSetupBson.Metadata(row, auditId, metadata);
                if (poolId.HasValue)
                {
                    var key = F.Eq("organizationId", input.Location.OrganizationId.Value) & F.Eq("physicalKey", input.PhysicalKey);
                    if (await Rows("inventoryPoolRegistrations").Find(tx, key).AnyAsync(token))
                        throw new PersistenceConflictException("stock_pool_key_registered", "This physical stock pool is already registered");
                    var registration = StockSourceSetupBson.Header("inventoryPoolRegistrations", poolId.Value);
                    registration.Add("organizationId", input.Location.OrganizationId.Value); registration.Add("physicalKey", input.PhysicalKey);
                    registration.Add("proposalId", proposalId); registration.Add("decisionId", decisionId);
                    var pool = StockSourceSetupBson.Header("inventoryPools", poolId.Value);
                    pool.Add("location", StockSourceSetupBson.Location(input.Location)); pool.Add("active", false);
                    pool.Add("revision", 1L); pool.Add("fence", 0L);
                    await Rows("inventoryPoolRegistrations").InsertOneAsync(tx, registration, cancellationToken: token);
                    await Rows("inventoryPools").InsertOneAsync(tx, pool, cancellationToken: token);
                }
                await Rows("stockSourceDecisions").InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourceSetupBson.Audit(row, actor, "stock.source." + name, now), cancellationToken: token);
                if (poolId.HasValue)
                    await new UnitToko.Platform.Ordering.OrderStockSourceProducer(database, ids, clock).InitializeAsync(tx, decisionId, token);
                return new BsonDocument { { "proposalId", proposalId }, { "decisionId", decisionId },
                    { "poolId", poolId.HasValue ? new BsonInt64(poolId.Value) : BsonNull.Value }, { "status", name } };
            }, ct, recoveryOnly);
    }

    public async Task<BsonDocument?> ReadAsync(ActorContext actor, StockLocation location, long proposalId, CancellationToken ct = default)
    {
        location.Validate(); if (proposalId <= 0) throw StockSourceSetupBson.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, Permissions.StockSourceRead, Resource(location));
            var proposal = await Rows("stockSourceProposals").Find(tx,
                F.Eq("_id", proposalId) & F.Eq("input.location", StockSourceSetupBson.Location(location))).SingleOrDefaultAsync(token);
            var result = proposal is null ? null : await ViewAsync(tx, proposal, token);
            await CompleteAsync(actor, snapshot, Permissions.StockSourceRead, Resource(location), tx, token);
            return result;
        }, ct);
    }

    public async Task<AuthorizedPage> ListAsync(ActorContext actor, StockLocation location, int offset, int limit, CancellationToken ct = default)
    {
        location.Validate(); AuthorizationStore.ValidatePage(offset, limit); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, Permissions.StockSourceRead, Resource(location));
            var filter = F.Eq("input.location", StockSourceSetupBson.Location(location));
            var total = await Rows("stockSourceProposals").CountDocumentsAsync(tx, filter, cancellationToken: token);
            var proposals = await Rows("stockSourceProposals").Find(tx, filter).Sort(FSort).Skip(offset).Limit(limit).ToListAsync(token);
            var items = new List<BsonDocument>();
            foreach (var proposal in proposals) items.Add(await ViewAsync(tx, proposal, token));
            await CompleteAsync(actor, snapshot, Permissions.StockSourceRead, Resource(location), tx, token);
            return new AuthorizedPage(total, items);
        }, ct);
    }
    private static readonly BsonDocument FSort = new("_id", 1);

    /// <summary>Receipt-only lookup followed by current-authority replay of server-reconstructed facts.
    /// A missing receipt never submits new work and does not require the separate history-read grant.</summary>
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, StockLocation location, string operation, string key,
        string correlationId, CancellationToken ct = default)
    {
        location.Validate();
        if (!StockSourceSetupPolicy.Text(key, 200) || !StockSourceSetupPolicy.Text(correlationId, 128) ||
            operation is not ("stock.source.propose" or "stock.source.approve" or "stock.source.reject" or "stock.source.withdraw"))
            throw StockSourceSetupBson.Invalid();
        await StorageAsync(ct);
        var permission = operation is "stock.source.approve" or "stock.source.reject" ? Permissions.StockSourceApprove : Permissions.StockSourcePropose;
        var recovery = await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            Require(actor, snapshot, permission, Resource(location));
            var receipt = await Rows("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            BsonDocument? result = null;
            if (receipt is not null)
            {
                if (!receipt["actorId"].IsInt64 || !receipt["result"].IsBsonDocument ||
                    !receipt["result"]["proposalId"].IsInt64 || receipt["result"]["proposalId"].AsInt64 <= 0)
                    throw StockSourceSetupBson.Invalid();
                var proposal = await ProposalAsync(tx, receipt["result"]["proposalId"].AsInt64, token);
                if (StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument).Location != location)
                    throw new AuthorizationDeniedException("scope_denied");
                BsonDocument? decision = null;
                BsonDocument expected;
                if (operation == "stock.source.propose")
                {
                    if (proposal["proposedBy"].AsInt64 != actor.UserId) throw StockSourceSetupBson.Invalid();
                    expected = new() { { "proposalId", proposal["_id"] }, { "revision", 1L } };
                }
                else
                {
                    decision = await Rows("stockSourceDecisions").Find(tx, F.Eq("proposalId", proposal["_id"])).SingleOrDefaultAsync(token);
                    if (decision is null) throw StockSourceSetupBson.Invalid();
                    StockSourceSetupBson.RequireDecision(decision, proposal);
                    if (decision["decidedBy"].AsInt64 != actor.UserId || operation != "stock.source." + decision["decision"].AsString)
                        throw StockSourceSetupBson.Invalid();
                    await AuditAsync(tx, decision, operation, actor.UserId, decision["decidedAt"], token);
                    expected = new() { { "proposalId", proposal["_id"] }, { "decisionId", decision["_id"] },
                        { "poolId", decision["poolId"] }, { "status", decision["decision"] } };
                }
                if (!receipt["result"].Equals(expected)) throw StockSourceSetupBson.Invalid();
                result = new() { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value }, { "expected", expected } };
            }
            await CompleteAsync(actor, snapshot, permission, Resource(location), tx, token);
            return result;
        }, ct);
        if (recovery is null) return null;
        BsonDocument replayed;
        if (operation == "stock.source.propose")
        {
            var proposal = recovery["proposal"].AsBsonDocument;
            replayed = await ProposeCoreAsync(actor, StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument),
                new(key, proposal["reason"].AsString, correlationId), true, ct);
        }
        else
        {
            var decision = recovery["decision"].AsBsonDocument;
            var outcome = operation switch
            {
                "stock.source.approve" => StockSourceSetupDecision.Approve,
                "stock.source.reject" => StockSourceSetupDecision.Reject,
                _ => StockSourceSetupDecision.Withdraw
            };
            replayed = await DecideCoreAsync(actor, decision["proposalId"].AsInt64, decision["revision"].AsInt64, outcome,
                decision["reviewReference"].AsString, new(key, decision["reason"].AsString, correlationId), true, ct);
        }
        if (!replayed.Equals(recovery["expected"])) throw StockSourceSetupBson.Invalid();
        return replayed;
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, string permission,
        AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<AuthorizationTarget>> targetLoader,
        Func<IClientSessionHandle, CancellationToken, Task<BsonDocument>> write, CancellationToken ct, bool recoveryOnly = false)
    {
        ResourceScope? resource = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body,
            async (snapshot, tx, token) =>
            {
                AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, Now()));
                var target = await targetLoader(snapshot, tx, token); resource = target.Resource;
                var query = Require(actor, snapshot, permission, resource);
                var grantIds = query.Clauses.Where(clause => clause.Matches(resource)).Select(clause => clause.GrantId).ToImmutableArray();
                return new(new(true, "allowed", grantIds), ScopePolicy.RequiredNodes(resource, snapshot.Directory), permission, target);
            }, write, ct, async (snapshot, tx, token) =>
                await CompleteAsync(actor, snapshot, permission, resource ?? throw StockSourceSetupBson.Invalid(), tx, token), requireReceipt: recoveryOnly);
    }
    private async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = await Rows("stockSourceProposals").Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct);
        if (row is null) throw StockSourceSetupBson.Invalid();
        StockSourceSetupBson.RequireProposal(row);
        await AuditAsync(tx, row, "stock.source.propose", row["proposedBy"].AsInt64, row["proposedAt"], ct);
        return row;
    }
    private async Task AuditAsync(IClientSessionHandle tx, BsonDocument row, string operation, long actor, BsonValue time, CancellationToken ct)
    {
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"].AsInt64)).SingleOrDefaultAsync(ct);
        if (audit is null || audit.GetValue("kind", "") != "stock.source.audit" || audit.GetValue("operation", "") != operation ||
            audit.GetValue("actorId", BsonNull.Value) != new BsonInt64(actor) || audit.GetValue("createdAt", BsonNull.Value) != time ||
            !audit.GetValue("record", BsonNull.Value).Equals(row)) throw StockSourceSetupBson.Invalid();
    }
    private async Task<BsonDocument> ViewAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct)
    {
        proposal = await ProposalAsync(tx, proposal["_id"].AsInt64, ct);
        var decision = await Rows("stockSourceDecisions").Find(tx, F.Eq("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        BsonDocument? pool = null;
        if (decision is not null)
        {
            StockSourceSetupBson.RequireDecision(decision, proposal);
            await AuditAsync(tx, decision, "stock.source." + decision["decision"].AsString,
                decision["decidedBy"].AsInt64, decision["decidedAt"], ct);
            if (!decision["poolId"].IsBsonNull)
            {
                var input = StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument);
                var registration = await Rows("inventoryPoolRegistrations").Find(tx, F.Eq("_id", decision["poolId"])).SingleOrDefaultAsync(ct);
                var expected = StockSourceSetupBson.Header("inventoryPoolRegistrations", decision["poolId"].AsInt64);
                expected.Add("organizationId", input.Location.OrganizationId.Value); expected.Add("physicalKey", input.PhysicalKey);
                expected.Add("proposalId", proposal["_id"]); expected.Add("decisionId", decision["_id"]);
                if (!expected.Equals(registration)) throw StockSourceSetupBson.Invalid();
                pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", decision["poolId"])).SingleOrDefaultAsync(ct);
                InventoryGraph.RequirePool(pool);
                if (!pool!["location"].Equals(StockSourceSetupBson.Location(input.Location))) throw StockSourceSetupBson.Invalid();
            }
        }
        return new() { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value }, { "pool", (BsonValue?)pool ?? BsonNull.Value } };
    }
    private Task CompleteAsync(ActorContext actor, AuthorizationSnapshot snapshot, string permission, ResourceScope resource,
        IClientSessionHandle tx, CancellationToken ct) => CompleteAuthorityAsync(actor, snapshot, permission, [resource], tx, ct);

    private async Task CompleteAuthorityAsync(ActorContext actor, AuthorizationSnapshot snapshot, string permission, ResourceScope[] resources,
        IClientSessionHandle tx, CancellationToken ct)
    {
        var query = ScopePolicy.Compile(actor, snapshot, permission, Now()); AuthorizationStore.Require(query);
        foreach (var resource in resources) _ = Require(actor, snapshot, permission, resource);
        var clauses = query.Clauses.Where(clause => resources.Length == 0 || resources.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, snapshot.Directory))).Distinct(),
            [], snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (actor.Session is { } reference)
        {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(reference, actor.UserId,
                actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).SingleOrDefaultAsync(ct);
            var now = new BsonDateTime(Now().UtcDateTime).MillisecondsSinceEpoch;
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, Now()));
        foreach (var resource in resources) _ = Require(actor, snapshot, permission, resource);
        ct.ThrowIfCancellationRequested();
    }
    private AuthorizationQuery Require(ActorContext actor, AuthorizationSnapshot snapshot, string permission, ResourceScope resource)
    {
        var query = ScopePolicy.Compile(actor, snapshot, permission, Now());
        AuthorizationStore.Require(query);
        if (!ScopePolicy.IsResourceActive(resource, snapshot.Directory) || !query.Matches(resource))
            throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private DateTimeOffset Now()
    {
        var now = clock.UtcNow;
        if (now == default || now.Offset != TimeSpan.Zero) throw StockSourceSetupBson.Invalid();
        return now;
    }
    private async Task StorageAsync(CancellationToken ct)
    {
        foreach (var name in StockSourceSetupSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourceSetupSchemas.Validator(name), StockSourceSetupSchemas.Key(name), ct);
    }
    private static ResourceScope Resource(StockLocation location) => CatalogRepository.Resource(location.ToScope());
    private static void Metadata(AuthorizationCommandMetadata metadata)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!StockSourceSetupPolicy.Text(metadata.IdempotencyKey, 200) || !StockSourceSetupPolicy.Text(metadata.Reason, 1000) ||
            !StockSourceSetupPolicy.Text(metadata.CorrelationId, 128)) throw StockSourceSetupBson.Invalid();
    }
}
