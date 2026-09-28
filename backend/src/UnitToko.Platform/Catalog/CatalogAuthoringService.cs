using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly CatalogRepository repository;
    private readonly AuthorizationStore authorization;
    public CatalogAuthoringService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock;
        repository = new(database); authorization = new(database, unit, clock);
    }
    private sealed class Context(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx)
    {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Snapshot { get; } = snapshot;
        internal IClientSessionHandle Tx { get; } = tx;
        internal HashSet<GrantScope> Owners { get; } = [];
        internal Dictionary<(string Collection, long Id), BsonDocument> Heads { get; } = [];
        internal void Head(string collection, BsonDocument row) => Heads[(collection, row["_id"].AsInt64)] = row.DeepClone().AsBsonDocument;
    }
    private async Task<Context> BeginAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct)
    {
        var snapshot = await authorization.LoadAsync(actor, tx, ct);
        AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, Permissions.CatalogManage, clock.UtcNow));
        var identity = await repository.Rows("identities").Find(tx, F.Eq("_id", actor.UserId)).FirstOrDefaultAsync(ct);
        if (actor.GrantVersion <= 0 || identity is null || !identity.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !identity.TryGetValue("status", out var status) || !status.IsString || status.AsString != "active" ||
            !identity.TryGetValue("issuer", out var issuer) || !issuer.IsString || issuer.AsString != actor.ProviderIssuer ||
            !identity.TryGetValue("subject", out var subject) || !subject.IsString || subject.AsString != actor.ProviderSubject ||
            !identity.TryGetValue("grantVersion", out var version) || !version.IsInt64 || version.AsInt64 != actor.GrantVersion ||
            !identity.TryGetValue("fence", out var fence) || !fence.IsInt64 || fence.AsInt64 < 0 || fence.AsInt64 > long.MaxValue - 8 ||
            (actor.Session is { } reference && (!identity.TryGetValue("securityVersion", out var security) || !security.IsInt64 || security.AsInt64 != reference.SecurityVersion)))
            throw CatalogRepository.Invalid("stale_or_inactive_actor");
        return new(actor, snapshot, tx);
    }
    private void RequireOwner(Context context, GrantScope owner)
    {
        CatalogAuthoringPolicy.Owner(owner);
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, Permissions.CatalogManage, clock.UtcNow);
        if (!query.Matches(CatalogRepository.Resource(owner))) throw CatalogRepository.Invalid("catalog_scope_denied");
        context.Owners.Add(owner);
        if (context.Owners.Count > 256) throw CatalogRepository.Invalid("catalog_authority_limit");
    }
    private static GrantScope RowOwner(BsonDocument row) => CatalogRepository.Owner(row["authoringOwner"].AsBsonDocument);
    private async Task<BsonDocument> MasterAsync(Context context, string collection, long id, CancellationToken ct)
    {
        var row = await repository.GetAsync(context.Tx, collection, id, ct);
        RequireOwner(context, RowOwner(row)); context.Head(collection, row); return row;
    }
    private async Task<(BsonDocument Family, BsonDocument Category)> FamilyGraphAsync(Context context, long id, CancellationToken ct)
    {
        var family = await MasterAsync(context, "productFamilies", id, ct);
        var category = await repository.GetAsync(context.Tx, "catalogCategories", family["categoryId"].AsInt64, ct);
        if (RowOwner(category) != RowOwner(family) || category["code"] != family["categoryCode"]) throw CatalogRepository.Invalid();
        context.Head("catalogCategories", category); return (family, category);
    }
    private async Task<(BsonDocument Sku, BsonDocument Definition, BsonDocument Family, BsonDocument Category)> SkuGraphAsync(Context context, long id, CancellationToken ct)
    {
        var sku = await MasterAsync(context, "skus", id, ct);
        var graph = await FamilyGraphAsync(context, sku["familyId"].AsInt64, ct);
        if (RowOwner(sku) != RowOwner(graph.Family)) throw CatalogRepository.Invalid();
        var definition = await repository.GetAsync(context.Tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
        RequirePair(sku, definition);
        return (sku, definition, graph.Family, graph.Category);
    }
    internal static void RequirePair(BsonDocument sku, BsonDocument definition)
    {
        if (sku["currentDefinitionId"] != definition["_id"] || definition["skuId"] != sku["_id"] ||
            sku["displayName"].AsString != sku["sku"].AsString + " · " + definition["packageLabel"].AsString) throw CatalogRepository.Invalid();
        try { CatalogPolicy.Validate(CatalogRepository.Sku(sku), CatalogRepository.Definition(definition)); }
        catch (DomainValidationException) { throw CatalogRepository.Invalid(); }
    }
    private async Task CompleteAsync(Context context, CancellationToken ct)
    {
        ScopeClause[] Current()
        {
            var query = ScopePolicy.Compile(context.Actor, context.Snapshot, Permissions.CatalogManage, clock.UtcNow);
            AuthorizationStore.Require(query);
            if (context.Owners.Any(owner => !query.Matches(CatalogRepository.Resource(owner)))) throw CatalogRepository.Invalid("catalog_authority_expired");
            return query.Clauses.Where(clause => context.Owners.Count == 0 || context.Owners.Any(owner => clause.Matches(CatalogRepository.Resource(owner)))).ToArray();
        }
        var clauses = Current();
        foreach (var head in context.Heads.OrderBy(pair => pair.Key.Collection, StringComparer.Ordinal).ThenBy(pair => pair.Key.Id))
            await repository.TouchAsync(context.Tx, head.Key.Collection, head.Value, ct);
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(context.Owners.SelectMany(owner => ScopePolicy.RequiredNodes(owner, context.Snapshot.Directory))).Distinct(),
            [], context.Snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.Actor.Session is { } reference)
        {
            var live = await repository.Rows("identitySessions").Find(context.Tx,
                SessionPersistence.LiveFilter(reference, context.Actor.UserId, context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).FirstOrDefaultAsync(ct);
            var now = new BsonDateTime(clock.UtcNow.UtcDateTime).MillisecondsSinceEpoch;
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw CatalogRepository.Invalid("stale_or_inactive_session");
        }
        _ = Current();
    }
    private static void ValidateRead(AuthorizationReadMetadata audit, bool recovery = false)
    {
        if (audit is null || audit.Purpose != (recovery ? "operation-recovery" : "catalog-authoring"))
            throw new DomainValidationException("invalid_catalog_purpose", "A supported catalog purpose is required");
        if (CatalogAuthoringPolicy.Text(audit.CorrelationId, 128) != audit.CorrelationId) throw BadRequest();
    }
    private async Task AuditReadAsync(Context context, long id, string operation, AuthorizationReadMetadata audit, BsonValue selection, int count, CancellationToken ct)
    {
        await repository.Rows("auditEvents").InsertOneAsync(context.Tx, new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "kind", "catalog.read" }, { "actorId", context.Actor.UserId },
            { "actorGrantVersion", context.Actor.GrantVersion }, { "operation", operation }, { "purpose", audit.Purpose },
            { "correlationId", audit.CorrelationId }, { "createdAt", clock.UtcNow.UtcDateTime },
            { "owners", OwnerArray(context.Owners) }, { "selectionDigest", CatalogRepository.Digest(selection) }, { "resultCount", count }
        }, cancellationToken: ct);
        await CompleteAsync(context, ct);
    }
    internal static BsonArray OwnerArray(IEnumerable<GrantScope> owners) => new(owners.Distinct().OrderBy(owner => owner.OrganizationId)
        .ThenBy(owner => owner.ShopId).ThenBy(owner => owner.ChannelId).ThenBy(owner => owner.WarehouseId).ThenBy(owner => owner.Kind).Select(CatalogRepository.Owner));
    private static string RequestHash(object request, string reason) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Request = request, Reason = reason })))).ToLowerInvariant();
    private static void ValidateCommand(AuthorizationCommandMetadata metadata)
    {
        if (metadata is null) throw new DomainValidationException("invalid_catalog_request", "Command metadata is required");
        if (!CatalogReceiptRules.Key(metadata.IdempotencyKey)) throw BadRequest(); _ = CatalogAuthoringPolicy.Text(metadata.Reason, 1000);
        if (CatalogAuthoringPolicy.Text(metadata.CorrelationId, 128) != metadata.CorrelationId) throw BadRequest();
        if (metadata.IdempotencyKey != metadata.IdempotencyKey.Trim()) throw new DomainValidationException("invalid_catalog_request", "Request key must be canonical");
    }
    private sealed record Change(BsonDocument Before, BsonDocument After, CatalogMutationResult Result);
    private async Task<CatalogMutationResult> ExecuteAsync(ActorContext actor, string operation, object request,
        AuthorizationCommandMetadata metadata, Func<Context, long, CancellationToken, Task<Change>> write, CancellationToken ct)
    {
        ValidateCommand(metadata); var hash = RequestHash(request, metadata.Reason.Trim());
        var auditId = await ids.NextAsync("auditEvents", ct);
        return await unit.ExecuteAsync(async (tx, token) =>
        {
            var context = await BeginAsync(actor, tx, token);
            var filter = F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", metadata.IdempotencyKey);
            var receipt = await repository.Rows("idempotencyRecords").Find(tx, filter).FirstOrDefaultAsync(token);
            if (receipt is not null)
            {
                var saved = await ReceiptAsync(context, receipt, operation, metadata.IdempotencyKey, token);
                if (receipt["bodyHash"].AsString != hash) throw new PersistenceConflictException("idempotency_key_reused", "Request key was already used for different catalog data");
                await CompleteAsync(context, token); return saved;
            }
            var change = await write(context, auditId.Value, token);
            var result = CatalogReceiptRules.Result(change.Result);
            var audit = new BsonDocument
            {
                { "_id", auditId.Value }, { "schemaVersion", 1 }, { "kind", "catalog.command" }, { "actorId", actor.UserId },
                { "actorGrantVersion", actor.GrantVersion }, { "operation", operation }, { "bodyHash", hash }, { "reason", metadata.Reason.Trim() },
                { "correlationId", metadata.CorrelationId }, { "createdAt", change.Result.RecordedAt.UtcDateTime },
                { "owners", OwnerArray(context.Owners) }, { "result", result }, { "before", change.Before }, { "after", change.After }
            };
            CatalogReceiptRules.RequireAudit(audit);
            await repository.Rows("auditEvents").InsertOneAsync(tx, audit, cancellationToken: token);
            await repository.Rows("idempotencyRecords").InsertOneAsync(tx, new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "operation", operation },
                { "key", metadata.IdempotencyKey }, { "bodyHash", hash }, { "createdAt", change.Result.RecordedAt.UtcDateTime },
                { "result", result.DeepClone() }, { "catalogAuthoring", new BsonDocument { { "schemaVersion", 1 }, { "auditId", auditId.Value } } }
            }, cancellationToken: token);
            await repository.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", "catalog.command:" + auditId.Value },
                { "kind", "catalog.changed" }, { "createdAt", change.Result.RecordedAt.UtcDateTime }, { "status", "pending" }, { "attempts", 0 }, { "dueAt", change.Result.RecordedAt.UtcDateTime },
                { "payload", new BsonDocument { { "kind", change.Result.Kind }, { "id", change.Result.Id.Value }, { "version", change.Result.Version } } }
            }, cancellationToken: token);
            await CompleteAsync(context, token); return change.Result;
        }, ct);
    }
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
}
