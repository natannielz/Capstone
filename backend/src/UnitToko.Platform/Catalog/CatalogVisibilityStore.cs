using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using MongoQuery = UnitToko.Platform.Authorization.CatalogVisibilityQuery;
using DomainQuery = UnitToko.Modules.Catalog.CatalogVisibilityQuery;

namespace UnitToko.Platform.Catalog;

public sealed record CatalogMetadata(long Id, string? PublicId, string? DisplayName);
public sealed record CatalogMetadataPage(long Total, ImmutableArray<CatalogMetadata> Items);
public enum CatalogMetadataPurpose { Image, Export, Event }
public sealed record CatalogMetadataReference(CatalogMetadataPurpose Purpose, CatalogMetadata Item);

/// <summary>Metadata-only catalog reads. Every call, including deferred reference delivery, loads and fences current authority.</summary>
public sealed class CatalogVisibilityStore
{
    private const int MaximumAuthorityRecords = 256;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly ProjectionDefinition<BsonDocument> MetadataProjection = new BsonDocument
        { { "_id", 1 }, { "publicId", 1 }, { "displayName", 1 } };
    private readonly IMongoDatabase database;
    private readonly AuthorizationStore authorization;
    private readonly IClock clock;
    private readonly Func<CancellationToken, Task>? afterRead;

    public CatalogVisibilityStore(IMongoDatabase database, AuthorizationStore authorization, IClock clock)
        : this(database, authorization, clock, null) { }

    // Deterministic transaction-race tests pause after the read but before time revalidation and write fences.
    internal CatalogVisibilityStore(IMongoDatabase database, AuthorizationStore authorization, IClock clock,
        Func<CancellationToken, Task>? afterRead)
    {
        this.database = database;
        this.authorization = authorization;
        this.clock = clock;
        this.afterRead = afterRead;
    }

    public Task<CatalogMetadataPage> QueryAsync(ActorContext actor, int offset = 0, int limit = 25,
        string? search = null, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        var searchFilter = Search(search);
        return ReadCurrentAsync(actor, searchFilter, async (session, filter, token) =>
        {
            var skus = database.GetCollection<BsonDocument>("skus");
            var total = await skus.CountDocumentsAsync(session, filter, cancellationToken: token);
            var rows = await skus.Find(session, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit)
                .Project<BsonDocument>(MetadataProjection).ToListAsync(token);
            return new CatalogMetadataPage(total, rows.Select(Metadata).ToImmutableArray());
        }, cancellationToken);
    }

    public Task<long> CountAsync(ActorContext actor, string? search = null, CancellationToken cancellationToken = default) =>
        ReadCurrentAsync(actor, Search(search), (session, filter, token) =>
            database.GetCollection<BsonDocument>("skus").CountDocumentsAsync(session, filter, cancellationToken: token), cancellationToken);

    public Task<CatalogMetadata?> ReadAsync(ActorContext actor, long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new DomainValidationException("invalid_id", "Resource ID must be positive");
        return ReadMetadataAsync(actor, F.Eq("_id", id), cancellationToken);
    }

    public Task<CatalogMetadata?> ReadPublicAsync(ActorContext actor, string publicId, CancellationToken cancellationToken = default)
    {
        if (publicId is null || publicId.Length != 36 || !Regex.IsMatch(publicId, MongoQuery.PublicIdPattern, RegexOptions.CultureInvariant))
            throw new DomainValidationException("invalid_public_id", "A canonical UUID version 4 is required");
        return ReadMetadataAsync(actor, F.Eq("publicId", publicId), cancellationToken);
    }

    /// <summary>This returns no file, export, or event content. Owning delivery adapters must call it again at delivery time.</summary>
    public async Task<CatalogMetadataReference?> ReadReferenceAsync(ActorContext actor, long id, CatalogMetadataPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(purpose)) throw new DomainValidationException("invalid_catalog_purpose", "A supported metadata purpose is required");
        var item = await ReadAsync(actor, id, cancellationToken);
        return item is null ? null : new(purpose, item);
    }

    private Task<CatalogMetadata?> ReadMetadataAsync(ActorContext actor, FilterDefinition<BsonDocument> selector, CancellationToken token) =>
        ReadCurrentAsync<CatalogMetadata?>(actor, selector, async (session, filter, cancellation) =>
        {
            // More than one public identifier must never resolve nondeterministically before CAT01 adds its uniqueness constraint.
            var rows = await database.GetCollection<BsonDocument>("skus").Find(session, filter)
                .Project<BsonDocument>(MetadataProjection).Limit(2).ToListAsync(cancellation);
            if (rows.Count > 1) throw new AuthorizationDeniedException("ambiguous_catalog_identifier");
            return rows.Count == 0 ? null : Metadata(rows[0]);
        }, token);

    internal Task<T> ReadCurrentAsync<T>(ActorContext actor, FilterDefinition<BsonDocument> selector,
        Func<IClientSessionHandle, FilterDefinition<BsonDocument>, CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        authorization.ReadAsync(actor, (snapshot, session, token) =>
            ReadWithinAsync(actor, snapshot, session, selector, null, read, token), cancellationToken);

    // Ordering owns the surrounding transaction. A branch selection may narrow, never broaden,
    // the actor's catalog entitlements. Keep all read/session/publication fences in this owner.
    internal async Task<T> ReadWithinAsync<T>(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle session,
        FilterDefinition<BsonDocument> selector, GrantScope? buyingScope,
        Func<IClientSessionHandle, FilterDefinition<BsonDocument>, CancellationToken, Task<T>> read, CancellationToken token)
        {
            if (buyingScope is not null && (buyingScope.Kind != GrantScopeKind.BuyerOwned || buyingScope.BranchId is not > 0 ||
                !ScopePolicy.IsScopeActive(buyingScope, snapshot.Directory))) throw new AuthorizationDeniedException("scope_denied");
            var now = clock.UtcNow;
            var rawEntitlements = await database.GetCollection<BsonDocument>("catalogEntitlements")
                .Find(session, EligibleEntitlements(actor.UserId, now)).Limit(MaximumAuthorityRecords + 1).ToListAsync(token);
            if (rawEntitlements.Count > MaximumAuthorityRecords) throw new AuthorizationDeniedException("catalog_authority_limit");
            var entitlements = rawEntitlements.Select(CatalogVisibilityBson.Entitlement)
                .Where(value => buyingScope is null || ScopePolicy.ContainsScope(value.AudienceScope, buyingScope)).ToImmutableArray();
            var assortmentIds = entitlements.Select(value => value.AssortmentId).Distinct().ToArray();
            var rawAssortments = await database.GetCollection<BsonDocument>("catalogAssortments")
                .Find(session, F.In("_id", assortmentIds)).Limit(MaximumAuthorityRecords + 1).ToListAsync(token);
            if (rawAssortments.Count > MaximumAuthorityRecords) throw new AuthorizationDeniedException("catalog_authority_limit");
            var assortments = rawAssortments.Select(CatalogVisibilityBson.Assortment).ToImmutableArray();
            var query = CatalogVisibilityPolicy.Compile(actor, snapshot, entitlements, assortments, clock.UtcNow);
            Require(query);
            var result = await read(session, F.And(MongoQuery.Create(query), selector), token);
            if (afterRead is not null) await afterRead(token);
            RequireStillEffective(actor, snapshot, entitlements, assortments, query);
            await FenceAsync(actor, query, session, token);
            await RequireLiveSessionAsync(actor, session, token);
            RequireStillEffective(actor, snapshot, entitlements, assortments, query);
            return result;
        }

    private async Task RequireLiveSessionAsync(ActorContext actor, IClientSessionHandle session, CancellationToken token)
    {
        if (actor.Session is not { } reference) return;
        var live = await database.GetCollection<BsonDocument>("identitySessions").Find(session,
            SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow))
            .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).FirstOrDefaultAsync(token);
        var now = new BsonDateTime(clock.UtcNow.UtcDateTime).MillisecondsSinceEpoch;
        if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
            !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
            idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
            throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private void RequireStillEffective(ActorContext actor, AuthorizationSnapshot snapshot,
        ImmutableArray<CatalogEntitlement> entitlements, ImmutableArray<CatalogAssortment> assortments, DomainQuery original)
    {
        var current = CatalogVisibilityPolicy.Compile(actor, snapshot, entitlements, assortments, clock.UtcNow);
        Require(current);
        if (original.Clauses.Any(before => !current.Clauses.Any(after => after.Entitlement.Id == before.Entitlement.Id &&
                after.Grant.Id == before.Grant.Id && after.Membership.Id == before.Membership.Id && after.Assortment.Id == before.Assortment.Id)))
            throw new AuthorizationDeniedException("catalog_authority_expired");
    }

    private async Task FenceAsync(ActorContext actor, DomainQuery query, IClientSessionHandle session, CancellationToken token)
    {
        await authorization.FenceAsync(actor, query.Clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), session, token);
        foreach (var grant in query.Clauses.Select(clause => clause.Grant).DistinctBy(value => value.Id).OrderBy(value => value.Id))
            await FenceDocumentAsync("authorizationGrants", AuthorizationBson.Grant(grant), session, token);
        foreach (var membership in query.Clauses.Select(clause => clause.Membership).DistinctBy(value => value.Id).OrderBy(value => value.Id))
            await FenceDocumentAsync("memberships", AuthorizationBson.Membership(membership), session, token);
        foreach (var entitlement in query.Clauses.Select(clause => clause.Entitlement).DistinctBy(value => value.Id).OrderBy(value => value.Id))
            await FenceDocumentAsync("catalogEntitlements", CatalogVisibilityBson.Entitlement(entitlement), session, token);
        foreach (var assortment in query.Clauses.Select(clause => clause.Assortment).DistinctBy(value => value.Id).OrderBy(value => value.Id))
            await FenceDocumentAsync("catalogAssortments", CatalogVisibilityBson.Assortment(assortment), session, token);
        await authorization.FenceAsync(actor, [], session, token);
    }

    private async Task FenceDocumentAsync(string collection, BsonDocument expected, IClientSessionHandle session, CancellationToken token)
    {
        var filters = expected.Where(element => element.Name != "fence").Select(element =>
            element.Name is "scope" or "ownerScope" or "audienceScope"
                ? MongoQuery.Owner(element.Name, AuthorizationBson.Scope(element.Value.AsBsonDocument))
                : F.Eq(element.Name, element.Value)).ToList();
        filters.Add(new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "properties", new BsonDocument("fence", new BsonDocument
                { { "bsonType", "long" }, { "minimum", 0L } }) }
        }));
        // Absent and explicit-null lifetime fields are equivalent, but newly populated fields must invalidate the read.
        if (collection is "authorizationGrants" or "memberships")
            foreach (var field in new[] { "expiresAt", "revokedAt" })
                if (!expected.Contains(field)) filters.Add(F.Eq(field, BsonNull.Value));
        var changed = await database.GetCollection<BsonDocument>(collection).UpdateOneAsync(session, F.And(filters),
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
        if (changed.ModifiedCount != 1) throw new AuthorizationDeniedException("catalog_authority_changed");
    }

    private static void Require(DomainQuery query)
    {
        if (!query.IsAllowed) throw new AuthorizationDeniedException(query.DenialCode ?? "catalog_scope_denied");
    }
    private static FilterDefinition<BsonDocument> EligibleEntitlements(long actorId, DateTimeOffset now) => F.And(
        new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray { "userId", "status", "effectiveAt" } },
            { "properties", new BsonDocument
                {
                    { "userId", new BsonDocument("bsonType", "long") }, { "status", new BsonDocument("bsonType", "string") },
                    { "effectiveAt", new BsonDocument("bsonType", "date") },
                    { "expiresAt", new BsonDocument("bsonType", new BsonArray { "date", "null" }) },
                    { "revokedAt", new BsonDocument("bsonType", new BsonArray { "date", "null" }) }
                }
            }
        }),
        F.Eq("userId", actorId), F.Eq("status", "active"), F.Lte("effectiveAt", now.UtcDateTime), F.Eq("revokedAt", BsonNull.Value),
        F.Or(F.Eq("expiresAt", BsonNull.Value), F.Gt("expiresAt", now.UtcDateTime)));
    private static CatalogMetadata Metadata(BsonDocument row) => new(row["_id"].AsInt64,
        row.TryGetValue("publicId", out var publicId) ? publicId.AsString : null,
        row.TryGetValue("displayName", out var displayName) ? displayName.AsString : null);
    private static FilterDefinition<BsonDocument> Search(string? search)
    {
        if (search is null or "") return F.Empty;
        if (search.Length > 128 || search.Any(char.IsControl))
            throw new DomainValidationException("invalid_catalog_search", "Search text must contain at most 128 printable characters");
        return F.Regex("displayName", new BsonRegularExpression(Regex.Escape(search), "i"));
    }
}
