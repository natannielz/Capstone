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
using B = UnitToko.Platform.Inventory.PackConversionRuleBson;

namespace UnitToko.Platform.Inventory;

/// <summary>Reviewed rule preparation only. StockConvert permits this scoped
/// workflow; an approved ratio never activates a source or moves/costs physical goods.</summary>
public sealed partial class PackConversionRuleService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private readonly PackConversionRuleRecords records = new(database, clock);
    private readonly PackConversionRuleCatalogue catalog = new(database);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, GrantScope location) {
        internal ActorContext Actor = actor;
        internal AuthorizationSnapshot Authority = authority;
        internal IClientSessionHandle Tx = tx;
        internal long Number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal GrantScope Location = location;
        internal PersonAuthorityProof? People;
        internal PackConversionRuleInput? Input;
        internal BsonDocument? Catalogue;
    }
    private AuthorizationQuery Current(Context context) {
        new InventoryRepository(database).RequireSession(context.Tx);
        if (context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Number) throw B.Broken();
        var query = ScopePolicy.Compile(context.Actor, context.Authority, Permissions.StockConvert, clock.UtcNow); AuthorizationStore.Require(query);
        var resource = CatalogRepository.Resource(context.Location);
        if (!ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        context.People?.RequireCurrent(context.Tx, clock.UtcNow); return query;
    }
    private async Task<Context> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        PackConversionRuleInput input, CancellationToken ct) {
        input = PackConversionRulePolicy.Copy(input); var context = new Context(actor, authority, tx, input.Location) { Input = input };
        _ = Current(context); context.Catalogue = await catalog.CurrentAsync(tx, input, ct); await PeopleAsync(context, null, ct); return context;
    }
    private async Task PeopleAsync(Context context, BsonDocument? proposal, CancellationToken ct) {
        var accounts = proposal is null ? new[] { context.Actor.UserId } : new[] { context.Actor.UserId, proposal["actorId"].AsInt64 }.Distinct().Order().ToArray();
        context.People = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. accounts], ct);
        if (proposal is not null) {
            var original = PackConversionRuleRecords.Person(proposal["people"][0]);
            var checker = context.People.Evidence.Single(person => person.UserId == context.Actor.UserId);
            var maker = context.People.Evidence.Single(person => person.UserId == original.UserId);
            if (!PersonIndependencePolicy.AreIndependent(checker, maker, original)) throw new AuthorizationDeniedException("independent_person_required");
        }
        _ = Current(context);
    }
    private async Task<bool> ReviewEligibilityAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        var maker = PackConversionRuleRecords.Person(proposal["people"][0]);
        context.People = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx,
            [.. new[] { context.Actor.UserId, maker.UserId }.Distinct().Order()], ct);
        _ = Current(context);
        return PersonIndependencePolicy.AreIndependent(context.People.Evidence.Single(person => person.UserId == context.Actor.UserId),
            context.People.Evidence.Single(person => person.UserId == maker.UserId), maker);
    }
    private async Task FenceAsync(Context context, bool currentCatalog, CancellationToken ct) {
        var query = Current(context);
        if (currentCatalog) await catalog.FenceAsync(context.Tx, context.Input ?? throw B.Broken(), context.Catalogue ?? throw B.Broken(), ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        var resource = CatalogRepository.Resource(context.Location); var clauses = query.Clauses.Where(clause => clause.Matches(resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(resource, context.Authority.Directory)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        _ = Current(context); ct.ThrowIfCancellationRequested();
    }
    private CommandAuthorization Command(Context context) {
        var resource = CatalogRepository.Resource(context.Location);
        return new(ScopePolicy.Authorize(context.Actor, context.Authority, Permissions.StockConvert, resource, clock.UtcNow),
            ScopePolicy.RequiredNodes(resource, context.Authority.Directory), Permissions.StockConvert, new(resource));
    }
    private static void Metadata(AuthorizationCommandMetadata value) {
        AuthorizationStore.ValidateMetadata(value);
        if (!StockCountBson.Key(value.IdempotencyKey) || !StockCountPolicy.CanonicalText(value.Reason, 1000) ||
            !StockCountPolicy.CanonicalText(value.CorrelationId, 128)) throw PackConversionRulePolicy.Invalid();
    }
    private static void Binding(string value) { if (value is null || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigitLower(character))) throw PackConversionRulePolicy.Invalid(); }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "kind", row["operation"] }, { "sourceKey", row["operation"].AsString + ":" + row["_id"].AsInt64 }, { "actorId", row["actorId"] },
            { "requestKey", row["requestKey"] }, { "payload", B.Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] },
            { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}
