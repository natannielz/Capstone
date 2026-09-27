using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;
using DomainQuery = UnitToko.Modules.Organizations.HistoricalReadQuery;

namespace UnitToko.Platform.Authorization;

/// <summary>Server-owned registrations for existing collections; this does not create their business modules or delivery endpoints.</summary>
public sealed class HistoricalReadSource
{
    private HistoricalReadSource(string key, string collectionName) { Key = key; CollectionName = collectionName; }
    public string Key { get; }
    internal string CollectionName { get; }
    public static HistoricalReadSource Orders { get; } = new("orders", "orders");
    public static HistoricalReadSource Invoices { get; } = new("invoices", "invoices");
    public static HistoricalReadSource Shipments { get; } = new("shipments", "shipments");
    public static HistoricalReadSource Resolve(string key) => key switch
    {
        "orders" => Orders, "invoices" => Invoices, "shipments" => Shipments,
        _ => throw new DomainValidationException("invalid_historical_source", "A registered historical metadata source is required")
    };
}

public sealed record HistoricalMetadata(long Id, string? PublicId, string? DisplayName);
public sealed record HistoricalMetadataPage(long Total, ImmutableArray<HistoricalMetadata> Items);
public enum HistoricalMetadataPurpose { Image, Evidence, Export, Event }
public sealed record HistoricalMetadataReference(HistoricalMetadataPurpose Purpose, HistoricalMetadata Item);

/// <summary>Metadata-only producer. Owning modules must use canonical buying scope fields and approved safe display metadata.
/// References convey no continuing authority: every delivery adapter must call this producer again at delivery time.</summary>
public sealed class HistoricalReadStore
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly ProjectionDefinition<BsonDocument> Projection = new BsonDocument
        { { "_id", 1 }, { "publicId", 1 }, { "displayName", 1 } };
    private readonly IMongoDatabase database;
    private readonly AuthorizationStore authorization;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly Func<CancellationToken, Task>? afterRead;

    public HistoricalReadStore(IMongoDatabase database, AuthorizationStore authorization, IdAllocator ids, IClock clock)
        : this(database, authorization, ids, clock, null) { }
    internal HistoricalReadStore(IMongoDatabase database, AuthorizationStore authorization, IdAllocator ids, IClock clock,
        Func<CancellationToken, Task>? afterRead)
    {
        this.database = database; this.authorization = authorization; this.ids = ids; this.clock = clock; this.afterRead = afterRead;
    }

    public Task<HistoricalMetadataPage> QueryAsync(ActorContext actor, HistoricalReadSource source, HistoricalReadRequest request,
        string correlationId, int offset = 0, int limit = 25, string? search = null, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        return ReadCurrentAsync(actor, source, request, correlationId, "query", Search(search),
            new BsonDocument { { "offset", offset }, { "limit", limit } }, async (session, filter, token) =>
            {
                var collection = database.GetCollection<BsonDocument>(source.CollectionName);
                var count = await collection.CountDocumentsAsync(session, filter, cancellationToken: token);
                var rows = await collection.Find(session, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit)
                    .Project<BsonDocument>(Projection).ToListAsync(token);
                return (new HistoricalMetadataPage(count, rows.Select(HistoricalReadBson.Metadata).ToImmutableArray()), (long)rows.Count);
            }, cancellationToken);
    }

    public Task<long> CountAsync(ActorContext actor, HistoricalReadSource source, HistoricalReadRequest request, string correlationId,
        string? search = null, CancellationToken cancellationToken = default) =>
        ReadCurrentAsync(actor, source, request, correlationId, "count", Search(search), new BsonDocument(), async (session, filter, token) =>
        {
            var count = await database.GetCollection<BsonDocument>(source.CollectionName).CountDocumentsAsync(session, filter, cancellationToken: token);
            return (count, count);
        }, cancellationToken);

    public Task<HistoricalMetadata?> ReadAsync(ActorContext actor, HistoricalReadSource source, HistoricalReadRequest request,
        string correlationId, long id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return ReadMetadataAsync(actor, source, request, correlationId, "detail", F.Eq("_id", id), new BsonDocument("resourceId", id), cancellationToken);
    }

    public Task<HistoricalMetadata?> ReadPublicAsync(ActorContext actor, HistoricalReadSource source, HistoricalReadRequest request,
        string correlationId, string publicId, CancellationToken cancellationToken = default)
    {
        if (publicId is null || publicId.Length != 36 || !Regex.IsMatch(publicId, CatalogVisibilityQuery.PublicIdPattern, RegexOptions.CultureInvariant))
            throw new DomainValidationException("invalid_public_id", "A canonical UUID version 4 is required");
        return ReadMetadataAsync(actor, source, request, correlationId, "public-detail", F.Eq("publicId", publicId),
            new BsonDocument("publicId", publicId), cancellationToken);
    }

    /// <summary>No image, evidence, export, or event payload is produced by this metadata reference surface.</summary>
    public async Task<HistoricalMetadataReference?> ReadReferenceAsync(ActorContext actor, HistoricalReadSource source,
        HistoricalReadRequest request, string correlationId, long id, HistoricalMetadataPurpose purpose, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (!Enum.IsDefined(purpose)) throw new DomainValidationException("invalid_historical_reference", "A supported metadata purpose is required");
        var item = await ReadMetadataAsync(actor, source, request, correlationId, "reference", F.Eq("_id", id),
            new BsonDocument { { "resourceId", id }, { "referencePurpose", AuthorizationBson.Name(purpose) } }, cancellationToken);
        return item is null ? null : new(purpose, item);
    }

    private Task<HistoricalMetadata?> ReadMetadataAsync(ActorContext actor, HistoricalReadSource source, HistoricalReadRequest request,
        string correlationId, string operation, FilterDefinition<BsonDocument> selector, BsonDocument selection, CancellationToken token) =>
        ReadCurrentAsync<HistoricalMetadata?>(actor, source, request, correlationId, operation, selector, selection, async (session, filter, cancellation) =>
        {
            var rows = await database.GetCollection<BsonDocument>(source.CollectionName).Find(session, filter)
                .Project<BsonDocument>(Projection).Limit(2).ToListAsync(cancellation);
            if (rows.Count > 1) throw new AuthorizationDeniedException("ambiguous_historical_identifier");
            return (rows.Count == 0 ? null : HistoricalReadBson.Metadata(rows[0]), (long)rows.Count);
        }, token);

    private async Task<T> ReadCurrentAsync<T>(ActorContext actor, HistoricalReadSource source, HistoricalReadRequest request,
        string correlationId, string operation, FilterDefinition<BsonDocument> selector, BsonDocument selection,
        Func<IClientSessionHandle, FilterDefinition<BsonDocument>, CancellationToken, Task<(T Value, long Count)>> read, CancellationToken cancellationToken)
    {
        if (source is null || request is null || !HistoricalReadPolicy.IsPurposeAllowed(request.Permission, request.Purpose) ||
            string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128 || correlationId.Any(char.IsControl))
            throw new DomainValidationException("invalid_historical_read", "A registered source, approved purpose and bounded correlation ID are required");
        var id = (await ids.NextAsync("businessReadEvents", cancellationToken)).Value;
        return await authorization.ReadAsync(actor, async (snapshot, session, token) =>
        {
            var query = HistoricalReadPolicy.Compile(actor, snapshot, request, clock.UtcNow);
            Require(query);
            if (query.Clauses.Select(clause => clause.Grant.Id).Distinct().Count() > 256 ||
                query.Clauses.Select(clause => clause.Membership.Id).Distinct().Count() > 256)
                throw new AuthorizationDeniedException("historical_authority_limit");
            var result = await read(session, F.And(HistoricalReadQuery.Create(query), selector), token);
            if (afterRead is not null) await afterRead(token);
            RequireStillEffective(actor, snapshot, query);
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                query.Clauses.SelectMany(clause => clause.AuthorityNodes), query.Clauses.SelectMany(clause => clause.TargetNodes)
                    .Where(node => node.Kind is ScopeNodeKind.Branch or ScopeNodeKind.Division),
                query.Clauses.Select(clause => clause.Grant), query.Clauses.Select(clause => clause.Membership), session, token);
            RequireStillEffective(actor, snapshot, query);
            await database.GetCollection<BsonDocument>("businessReadEvents").InsertOneAsync(session,
                HistoricalReadBson.Event(id, actor, source, query, operation, correlationId, selection, result.Count, clock.UtcNow), cancellationToken: token);
            // Audit persistence is awaited: a lifetime may expire during that write. Denial aborts its event and every fence.
            RequireStillEffective(actor, snapshot, query);
            await authorization.FenceAsync(actor, [], session, token);
            RequireStillEffective(actor, snapshot, query);
            return result.Value;
        }, cancellationToken);
    }

    private void RequireStillEffective(ActorContext actor, AuthorizationSnapshot snapshot, DomainQuery original)
    {
        var current = HistoricalReadPolicy.Compile(actor, snapshot, original.Request, clock.UtcNow);
        Require(current);
        if (original.Clauses.Any(before => !current.Clauses.Any(after => after.Grant.Id == before.Grant.Id && after.Membership.Id == before.Membership.Id)))
            throw new AuthorizationDeniedException("historical_authority_expired");
    }
    private static void Require(DomainQuery query)
    {
        if (!query.IsAllowed) throw new AuthorizationDeniedException(query.DenialCode ?? "historical_read_denied");
    }
    private static void ValidateId(long id)
    {
        if (id <= 0) throw new DomainValidationException("invalid_id", "Resource ID must be positive");
    }
    private static FilterDefinition<BsonDocument> Search(string? search)
    {
        if (search is null) return F.Empty;
        if (string.IsNullOrWhiteSpace(search) || search.Length > 128 || search.Any(char.IsControl))
            throw new DomainValidationException("invalid_search", "Search text must be nonempty and bounded");
        return F.Regex("displayName", new BsonRegularExpression(Regex.Escape(search), "i"));
    }
}
