using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

/// <summary>
/// Captures immutable physical-count observations only. No stock writer, valuation or promise is activated.
/// Results include internal provenance and are not an approved public HTTP projection.
/// </summary>
public sealed partial class StockCountService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private readonly CatalogAuthoringService catalog = new(database, unit, ids, clock);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private sealed class Context(ActorContext actor, AuthorizationSnapshot snapshot, string permission, IClientSessionHandle tx)
    {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Snapshot { get; } = snapshot;
        internal string Permission { get; } = permission;
        internal IClientSessionHandle Tx { get; } = tx;
        internal HashSet<GrantScope> Locations { get; } = [];
    }

    public async Task<StockCountObservation> RecordCountAsync(ActorContext actor, StockCountInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        StockCountPolicy.Validate(input);
        Metadata(metadata);
        if (input.PoolSelection is not null) return await RecordBoundCountAsync(actor, input, metadata, ct);
        var hash = StockCountBson.BodyHash(input, metadata.Reason);
        // Allocation alone is outside the transaction. Aborts/replays may leave gaps, never reused IDs.
        var id = await ids.NextAsync(StockCountBson.Collection, ct);
        var auditId = await ids.NextAsync("auditEvents", ct);
        var receiptId = ObjectId.GenerateNewId();
        return await unit.ExecuteAsync(async (tx, token) =>
        {
            var context = await BeginAsync(actor, Permissions.StockCount, tx, token);
            var receipt = await Rows("idempotencyRecords").Find(tx, ReceiptFilter(actor.UserId, metadata.IdempotencyKey)).SingleOrDefaultAsync(token);
            if (receipt is not null)
            {
                var existing = await ReceiptAsync(context, receipt, token);
                if (receipt["bodyHash"] != hash) throw new PersistenceConflictException("idempotency_key_reused", "Request key was used with a different count");
                await CompleteAsync(context, token);
                return existing;
            }
            RequireLocation(context, input.Location);
            var result = await catalog.WithStockCountDefinitionAsync(database, tx, actor, input.Location,
                input.SkuId, input.ExpectedSkuRevision, input.ExpectedDefinitionId, async (definition, callbackToken) =>
                {
                    var row = StockCountBson.Create(id.Value, auditId.Value, actor, input,
                        StockCountBson.Definition(definition), metadata, clock.UtcNow);
                    var observation = StockCountBson.Read(row);
                    await Rows(StockCountBson.Collection).InsertOneAsync(tx, row, cancellationToken: callbackToken);
                    await Rows("auditEvents").InsertOneAsync(tx, StockCountBson.Audit(row), cancellationToken: callbackToken);
                    await Rows("idempotencyRecords").InsertOneAsync(tx, StockCountBson.Receipt(row, receiptId), cancellationToken: callbackToken);
                    return observation;
                }, token);
            await CompleteAsync(context, token);
            return result;
        }, ct);
    }

    public Task<StockCountObservation?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default)
    {
        if (!StockCountBson.Key(key)) throw new ArgumentException("A canonical count request key is required", nameof(key));
        return unit.ExecuteAsync<StockCountObservation?>(async (tx, token) =>
        {
            var context = await BeginAsync(actor, Permissions.StockCount, tx, token);
            var receipt = await Rows("idempotencyRecords").Find(tx, ReceiptFilter(actor.UserId, key)).SingleOrDefaultAsync(token);
            if (receipt is null) await RequireMissingBoundKeyAsync(context, key, token);
            var result = receipt is null ? null : await ReceiptAsync(context, receipt, token);
            await CompleteAsync(context, token);
            return result;
        }, ct);
    }

    public Task<StockCountObservation?> ReadAsync(ActorContext actor, long id, CancellationToken ct = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        return unit.ExecuteAsync<StockCountObservation?>(async (tx, token) =>
        {
            var context = await BeginAsync(actor, Permissions.StockRead, tx, token);
            var row = await Rows(StockCountBson.Collection).Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(token);
            StockCountObservation? result = null;
            if (row is not null)
            {
                var location = StockCountBson.Scope(StockCountBson.Document(row, "location"));
                StockCountPolicy.Location(location);
                if (Current(context).Matches(CatalogRepository.Resource(location)))
                {
                    RequireLocation(context, location);
                    result = await GraphAsync(tx, row, token);
                    await FenceBoundContextAsync(context, result, token);
                }
            }
            if (row is null) await RequireMissingBoundObservationAsync(context, id, token);
            await CompleteAsync(context, token);
            return result;
        }, ct);
    }

    public Task<StockCountPage> ListAsync(ActorContext actor, GrantScope location, int offset, int limit, CancellationToken ct = default)
    {
        StockCountPolicy.Location(location);
        AuthorizationStore.ValidatePage(offset, limit);
        return unit.ExecuteAsync(async (tx, token) =>
        {
            var context = await BeginAsync(actor, Permissions.StockRead, tx, token);
            RequireLocation(context, location);
            await RequireBoundListMembershipAsync(context, location, token);
            var filter = CatalogRepository.OwnerFilter("location", location);
            var total = await Rows(StockCountBson.Collection).CountDocumentsAsync(tx, filter, cancellationToken: token);
            var rows = await Rows(StockCountBson.Collection).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(token);
            var items = ImmutableArray.CreateBuilder<StockCountObservation>(rows.Count);
            foreach (var row in rows) { var observation = await GraphAsync(tx, row, token); await FenceBoundContextAsync(context, observation, token); items.Add(observation); }
            await CompleteAsync(context, token);
            return new StockCountPage(total, items.ToImmutable());
        }, ct);
    }

    private async Task<StockCountObservation> ReceiptAsync(Context context, BsonDocument receipt, CancellationToken token)
    {
        var row = await Rows(StockCountBson.Collection).Find(context.Tx,
            F.Eq("_id", StockCountBson.ReceiptObservationId(receipt))).SingleOrDefaultAsync(token);
        if (row is null) throw StockCountBson.Invalid();
        var location = StockCountBson.Scope(StockCountBson.Document(row, "location"));
        RequireLocation(context, location);
        var result = await GraphAsync(context.Tx, row, token, receipt);
        if (result.CountedBy != context.Actor.UserId || result.Issuer != context.Actor.ProviderIssuer || result.Subject != context.Actor.ProviderSubject)
            throw new AuthorizationDeniedException("stock_count_identity_changed");
        await FenceBoundContextAsync(context, result, token);
        return result;
    }
    internal async Task<StockCountObservation> GraphObservationAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken token, BsonDocument? receipt = null)
    {
        new InventoryRepository(database).RequireSession(tx);
        var transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var result = StockCountBson.Read(row);
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", result.AuditId)).SingleOrDefaultAsync(token);
        receipt ??= await Rows("idempotencyRecords").Find(tx, ReceiptFilter(result.CountedBy, row["requestKey"].AsString)).SingleOrDefaultAsync(token);
        StockCountBson.Graph(row, audit, receipt);
        await StockCountBson.RequireHistoricalCatalogAsync(result, async (collection, id, ct) =>
            await Rows(collection).Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct), token);
        new InventoryRepository(database).RequireSession(tx);
        if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw StockCountBson.Invalid();
        return result;
    }
    private async Task<Context> BeginAsync(ActorContext actor, string permission, IClientSessionHandle tx, CancellationToken token)
    {
        var snapshot = await authorization.LoadAsync(actor, tx, token);
        var identity = await Rows("identities").Find(tx, F.Eq("_id", actor.UserId)).SingleOrDefaultAsync(token);
        if (actor.GrantVersion <= 0 || identity is null ||
            !identity.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !identity.TryGetValue("_id", out var identityId) || !identityId.IsInt64 || identityId.AsInt64 != actor.UserId ||
            !identity.TryGetValue("status", out var status) || !status.IsString || status.AsString != "active" ||
            !identity.TryGetValue("issuer", out var issuer) || !issuer.IsString || issuer.AsString != actor.ProviderIssuer ||
            !identity.TryGetValue("subject", out var subject) || !subject.IsString || subject.AsString != actor.ProviderSubject ||
            !identity.TryGetValue("grantVersion", out var version) || !version.IsInt64 || version.AsInt64 != actor.GrantVersion ||
            !identity.TryGetValue("fence", out var fence) || !fence.IsInt64 || fence.AsInt64 < 0 || fence.AsInt64 > long.MaxValue - 8 ||
            actor.Session is { } reference && (!identity.TryGetValue("securityVersion", out var security) ||
                !security.IsInt64 || security.AsInt64 != reference.SecurityVersion))
            throw new AuthorizationDeniedException("stale_or_inactive_actor");
        var context = new Context(actor, snapshot, permission, tx);
        _ = Current(context);
        return context;
    }
    private AuthorizationQuery Current(Context context)
    {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, context.Permission, clock.UtcNow);
        AuthorizationStore.Require(query);
        foreach (var location in context.Locations)
            if (!query.Matches(CatalogRepository.Resource(location))) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private void RequireLocation(Context context, GrantScope location)
    {
        StockCountPolicy.Location(location);
        var resource = CatalogRepository.Resource(location);
        if (!ScopePolicy.IsResourceActive(resource, context.Snapshot.Directory) || !Current(context).Matches(resource))
            throw new AuthorizationDeniedException("scope_denied");
        context.Locations.Add(location);
    }
    private async Task CompleteAsync(Context context, CancellationToken token)
    {
        var query = Current(context);
        var clauses = query.Clauses.Where(clause => context.Locations.Count == 0 ||
            context.Locations.Any(location => clause.Matches(CatalogRepository.Resource(location)))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(context.Locations.SelectMany(location =>
                ScopePolicy.RequiredNodes(location, context.Snapshot.Directory))).Distinct(),
            [], context.Snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, token);
        if (context.Actor.Session is { } reference)
        {
            var live = await Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(reference, context.Actor.UserId,
                context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).FirstOrDefaultAsync(token);
            var now = new BsonDateTime(clock.UtcNow.UtcDateTime).MillisecondsSinceEpoch;
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        _ = Current(context);
        token.ThrowIfCancellationRequested();
    }
    private static FilterDefinition<BsonDocument> ReceiptFilter(long actorId, string key) =>
        F.Eq("actorId", actorId) & F.Eq("operation", StockCountBson.Operation) & F.Eq("key", key);
    private static void Metadata(AuthorizationCommandMetadata metadata)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!StockCountBson.Key(metadata.IdempotencyKey) || !StockCountPolicy.CanonicalText(metadata.Reason, 1000) ||
            !StockCountPolicy.CanonicalText(metadata.CorrelationId, 128))
            throw new UnitToko.Modules.Contracts.DomainValidationException("invalid_stock_count", "A canonical request key, reason and correlation ID are required");
    }
}
