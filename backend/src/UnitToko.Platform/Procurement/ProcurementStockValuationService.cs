using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementStockValuationService
{
    private readonly IMongoDatabase database; private readonly MongoUnitOfWork unit; private readonly IdAllocator ids; private readonly IClock clock;
    private readonly CatalogAuthoringService catalog; private readonly PurchaseOrderService orders; private readonly ProcurementReceivingService receiving;
    private readonly StockSourceSetupService sources; private readonly AuthorizationStore authorization; private readonly ProcurementStockValuationRecords records;
    public ProcurementStockValuationService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, ProcurementReceivingService receiving, StockSourceSetupService sources) {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.catalog = catalog; this.orders = orders; this.receiving = receiving; this.sources = sources;
        authorization = new(database, unit, clock); records = new(database, unit, clock, catalog, orders, sources);
    }
    private IMongoCollection<BsonDocument> Rows(string table) => database.GetCollection<BsonDocument>(table);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static ResourceScope Resource(ProcurementLocation location) => new(FulfillmentOrganizationId: location.OrganizationId,
        ShopId: location.ShopId, WarehouseId: location.WarehouseId, ChannelId: location.ChannelId);
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, string permission) {
        internal readonly ActorContext Actor = actor; internal readonly AuthorizationSnapshot Authority = authority; internal readonly IClientSessionHandle Tx = tx;
        internal readonly long Transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; internal readonly ProcurementLocation Location = location;
        internal readonly string Permission = permission; internal ProcurementStockValuationSources? Source; internal BsonDocument? Proposal, Decision;
        internal PersonAuthorityProof? People; internal DateTimeOffset? SessionExpiry, PolicyExpiry;
        internal readonly List<(string Table, BsonDocument Row)> Fences = [];
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery[] Require(Context context, bool posting = false) {
        var permissions = new List<string> { Permissions.ProcurementRead, Permissions.StockRead, Permissions.ProcurementStockValuationRead, context.Permission };
        if (posting) permissions.Add(Permissions.JournalPost);
        return permissions.Distinct().Select(permission => {
            var query = Query(context.Actor, context.Authority, permission); var resource = Resource(context.Location);
            if (!ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
            return query;
        }).ToArray();
    }
    private void Current(Context context, bool posting = false) {
        _ = Require(context, posting);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) ||
            context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction) throw B.Broken();
        context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (context.PolicyExpiry is { } policyExpiry && clock.UtcNow >= policyExpiry)
            throw B.Changed("procurement_stock_valuation_policy_unavailable");
        if (context.SessionExpiry is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task StorageAsync(CancellationToken ct) {
        await AccountingMigration.RequireInstalledStorageAsync(database, ct);
        foreach (var name in ProcurementStockValuationSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementStockValuationSchemas.Validator(name), ProcurementStockValuationSchemas.Keys(name), ct);
        foreach (var name in ProcurementStockValuationSchemas.Collections)
            if ((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
    }
    private static void AddFence(Context context, string table, BsonDocument row) {
        var prior = context.Fences.SingleOrDefault(value => value.Table == table && value.Row["_id"] == row["_id"]);
        if (prior.Row is not null) { ProcurementStockValuationRecords.Equal(prior.Row, row); return; }
        context.Fences.Add((table, row));
    }
    private async Task PeopleAsync(Context context, long? maker, long? matcher, CancellationToken ct) {
        var policy = context.Source?.Policy ?? context.Proposal?["policy"].AsBsonDocument;
        if (policy is null) throw B.Broken();
        var users = new[] { context.Actor.UserId, maker ?? context.Actor.UserId, matcher ?? context.Actor.UserId,
            policy["proposedBy"].AsInt64, policy["approvedBy"].AsInt64 }.Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }))
            throw new AuthorizationDeniedException("person_evidence_invalid");
        var byUser = proof.Evidence.ToDictionary(person => person.UserId);
        foreach (var original in policy["people"].AsBsonArray.Select(R.Person))
            if (byUser[original.UserId].PersonId != original.PersonId) throw new AuthorizationDeniedException("procurement_stock_valuation_person_changed");
        if (matcher is { } matchActor && context.Source is { } source) {
            var original = R.Person(source.Match["people"][0]);
            if (original.UserId != matchActor || byUser[matchActor].PersonId != original.PersonId)
                throw new AuthorizationDeniedException("procurement_stock_valuation_person_changed");
        }
        if (maker is { } proposalMaker && context.Proposal is { } proposal) {
            var original = proposal["people"].AsBsonArray.Select(R.Person).Single(person => person.UserId == proposalMaker);
            if (byUser[proposalMaker].PersonId != original.PersonId) throw new AuthorizationDeniedException("procurement_stock_valuation_person_changed");
            if (!PersonIndependencePolicy.AreIndependent(byUser[context.Actor.UserId], byUser[proposalMaker],
                byUser[matcher ?? proposalMaker])) throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer,
            actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private async Task FenceAsync(Context context, CancellationToken ct, bool posting = false) {
        Current(context, posting);
        foreach (var (table, row) in context.Fences) {
            if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(table).UpdateOneAsync(context.Tx, row,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            row["fence"] = row["fence"].AsInt64 + 1;
        }
        var resource = Resource(context.Location); var queries = Require(context, posting);
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => clause.Matches(resource))).Distinct().ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context, posting); ct.ThrowIfCancellationRequested();
    }
    private async Task<Context> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementStockValuationInput input, string permission, CancellationToken ct, BsonDocument? retainedProposal = null,
        bool posting = false) {
        var context = new Context(actor, authority, tx, input.Location, permission); _ = Require(context, posting);
        context.Source = await records.SourcesAsync(tx, input, ct, retainedProposal);
        if (retainedProposal is null) context.PolicyExpiry = new DateTimeOffset(context.Source.Policy["expiresAt"].ToUniversalTime());
        if (retainedProposal is null) AddFence(context, B.Policies, context.Source.Policy);
        AddFence(context, SupplierDocumentBson.Heads, context.Source.InvoiceHistory.Head);
        AddFence(context, SupplierDocumentBson.Identities, context.Source.InvoiceHistory.Identity);
        if (context.Source.Capacity.ReceiptHead is { } receiptHead) AddFence(context, B.ReceiptHeads, receiptHead);
        if (context.Source.Capacity.MatchHead is { } matchHead) AddFence(context, B.MatchHeads, matchHead);
        await PeopleAsync(context, null, context.Source.Match["actorId"].AsInt64, ct);
        Current(context, posting); return context;
    }
    private CommandAuthorization Command(Context context, BsonDocument? proposal = null, bool posting = false) {
        var resource = Resource(context.Location); var approval = proposal is null ? null : new ApprovalRequest(1, 1,
            RequestedBy: proposal["actorId"].AsInt64, RecordedBy: proposal["matchActorId"].AsInt64);
        return new(ScopePolicy.Authorize(context.Actor, context.Authority, context.Permission, resource, clock.UtcNow, approval),
            ScopePolicy.RequiredNodes(resource, context.Authority.Directory), context.Permission, new(resource, approval));
    }
    private static void Key(string key) { ProcurementStockReceiptRules.Text(key, 128); if (key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw ProcurementStockValuationRules.Invalid(); }
    private static void Binding(string value) { if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementStockValuationRules.Invalid(); }
}
