using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Ordering;

/// <summary>Read-only commercial preview. A submitted order must recompute and persist its accepted snapshot
/// in the owner transaction; this result grants no stock reservation, credit, tax approval or payable amount.</summary>
public sealed class OrderQuoteService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly CatalogDefinitionQueryService catalog;
    public OrderQuoteService(IMongoDatabase database, MongoUnitOfWork unit, IClock clock)
    {
        this.database = database; this.clock = clock; authorization = new(database, unit, clock);
        catalog = new(database, authorization, clock);
    }
    public Task<ImmutableArray<OrderBuyingOption>> ContextsAsync(ActorContext actor, CancellationToken ct = default) =>
        authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var query = Query(actor, snapshot);
            var contexts = new List<OrderBuyingContext>();
            foreach (var branch in snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Branch && node.ParentId is > 0).OrderBy(node => node.Id)) {
                var options = new[] { new OrderBuyingContext(new(branch.ParentId!.Value), new(branch.Id)) }
                    .Concat(snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Division && node.ParentId == branch.Id).OrderBy(node => node.Id)
                        .Select(node => new OrderBuyingContext(new(branch.ParentId.Value), new(branch.Id), new(node.Id))));
                foreach (var option in options) if (query.Matches(option.Resource(actor.UserId))) contexts.Add(option);
            }
            if (contexts.Count > 256) throw new AuthorizationDeniedException("order_context_limit");
            var ids = contexts.SelectMany(context => new long?[] { context.OrganizationId.Value, context.BranchId.Value, context.DivisionId?.Value }).OfType<long>().Distinct();
            var profiles = await database.GetCollection<BsonDocument>("organizationProfiles").Find(tx,
                Builders<BsonDocument>.Filter.In("_id", ids)).ToListAsync(token);
            string? Name(long? id) {
                var profile = profiles.SingleOrDefault(row => row["_id"].AsInt64 == id);
                return profile is not null && profile.TryGetValue("name", out var name) && name.IsString ? name.AsString : null;
            }
            var result = contexts.Select(context => new OrderBuyingOption(context, Name(context.OrganizationId.Value), Name(context.BranchId.Value), Name(context.DivisionId?.Value))).ToImmutableArray();
            await FenceAsync(actor, snapshot, tx, contexts, token);
            return result;
        }, ct);

    public Task<OrderQuotePreview> PreviewAsync(ActorContext actor, OrderBuyingContext context,
        ImmutableArray<OrderPackageSelection> selections, CancellationToken ct = default)
    {
        context.Validate();
        if (selections.IsDefaultOrEmpty || selections.Length > 100 || selections.Any(value => value is null))
            throw new DomainValidationException("invalid_order_quote", "Choose up to 100 distinct packages");
        return authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            Require(actor, snapshot, context);
            var facts = await catalog.ReadSelectedAsync(actor, snapshot, tx, context.Scope,
                selections.Select(value => value.SkuId.Value).ToImmutableArray(),
                fenceToken => FenceAsync(actor, snapshot, tx, [context], fenceToken), token);
            var quote = OrderQuotePolicy.Create(selections, facts.Select(value =>
                new OrderCatalogFact(value.Family, value.Sku, value.Definition, value.CurrentPrice)).ToImmutableArray());
            Require(actor, snapshot, context); token.ThrowIfCancellationRequested();
            return new OrderQuotePreview(context, DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds()), quote);
        }, ct);
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot snapshot)
    {
        var query = ScopePolicy.Compile(actor, snapshot, Permissions.OrderSubmit, clock.UtcNow);
        AuthorizationStore.Require(query); return query;
    }
    internal void Require(ActorContext actor, AuthorizationSnapshot snapshot, OrderBuyingContext context)
    {
        if (!Query(actor, snapshot).Matches(context.Resource(actor.UserId))) throw new AuthorizationDeniedException("scope_denied");
    }
    internal async Task FenceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        IReadOnlyCollection<OrderBuyingContext> contexts, CancellationToken ct)
    {
        var clauses = Query(actor, snapshot).Clauses.Where(clause => contexts.Count == 0 || contexts.Any(context => clause.Matches(context.Resource(actor.UserId)))).ToArray();
        var nodes = clauses.SelectMany(clause => clause.RequiredNodes).Concat(contexts.SelectMany(context => ScopePolicy.RequiredNodes(context.Resource(actor.UserId), snapshot.Directory))).Distinct();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, nodes, [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (actor.Session is { } reference) {
            var live = await database.GetCollection<BsonDocument>("identitySessions").Find(tx, SessionPersistence.LiveFilter(reference,
                actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).SingleOrDefaultAsync(ct);
            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        _ = Query(actor, snapshot);
        foreach (var context in contexts) Require(actor, snapshot, context);
        ct.ThrowIfCancellationRequested();
    }
}
