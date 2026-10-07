using System.Collections.Concurrent;
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
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Platform.Inventory;

/// <summary>The owning sealed-to-loose command. Physical legs, conserved finite value,
/// receipt and effects join one transaction; quantity arithmetic grants no write authority.</summary>
public sealed partial class PackConversionService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly CatalogAuthoringService catalog;
    private readonly PurchaseOrderService orders;
    private readonly StockSourceSetupService sources;
    private readonly ProcurementStockValuationService valuations;
    private readonly StockReleaseService releases;
    private readonly AuthorizationStore authorization;
    private readonly PackConversionRuleRecords rules;
    private readonly PackConversionRuleCatalogue catalogue;
    private readonly LotValueStore values;
    private readonly ConcurrentDictionary<IClientSessionHandle, Context> inFlight = new();
    public PackConversionService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources,
        ProcurementStockValuationService valuations, StockReleaseService releases) {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.catalog = catalog;
        this.orders = orders; this.sources = sources; this.valuations = valuations; this.releases = releases;
        authorization = new(database, unit, clock); rules = new(database, clock); catalogue = new(database); values = new(database, ids, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, PackConversionInput input) {
        internal readonly ActorContext Actor = actor;
        internal readonly AuthorizationSnapshot Authority = authority;
        internal readonly IClientSessionHandle Tx = tx;
        internal readonly long Number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal readonly PackConversionInput Input = input;
        internal PersonAuthorityProof? People;
        internal DateTimeOffset? SessionExpiry;
        internal long? MovementId;
        internal int PoolWrites;
        internal PackConversionRuleInput? RuleInput;
        internal PackConversionExpectation? Expected;
        internal ConversionQuantities? Quantities;
        internal BsonDocument? Rule, Catalogue, PoolSource, SourceLot, SourceValue, Row;
        internal InventoryBalanceDocument? Before, After, DestinationBefore, DestinationAfter;
        internal ResourceScope Resource => CatalogRepository.Resource(Input.Location);
    }
    private async Task StorageAsync(CancellationToken ct) {
        await PackConversionSchemas.RequireStorageAsync(database, ct);
        await PackConversionRuleStorage.RequireAsync(database, ct);
        await LotValueStore.RequireStorageAsync(database, ct);
    }
    private AuthorizationQuery Current(Context context) {
        new InventoryRepository(database).RequireSession(context.Tx);
        if (context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Number) throw B.Broken();
        context.People?.RequireCurrent(context.Tx, Now);
        if (context.Row is not null && context.SourceLot is { } sourceLot && InventoryGraph.ReadLot(sourceLot).ExpiresOn is { } sourceExpiry &&
            sourceExpiry <= DateOnly.FromDateTime(Now.UtcDateTime)) throw B.Changed();
        if (context.SessionExpiry is { } expiry && Now >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
        var query = ScopePolicy.Compile(context.Actor, context.Authority, Permissions.StockConvert, Now); AuthorizationStore.Require(query);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource))
            throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private async Task PeopleAsync(Context context, CancellationToken ct) {
        context.People = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [context.Actor.UserId], ct);
        var person = context.People.Evidence.Single();
        if (person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0)
            throw new AuthorizationDeniedException("person_evidence_invalid");
        Current(context);
    }
    private async Task FenceAsync(Context context, bool currentSource, CancellationToken ct) {
        var query = Current(context);
        if (context.RuleInput is { } ruleInput && context.Catalogue is { } pair) await catalogue.FenceAsync(context.Tx, ruleInput, pair, ct);
        if (context.People is { } people) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, people, ct);
        var clauses = query.Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (currentSource && context.Before is { } before) {
            var pool = await Rows("inventoryPools").Find(context.Tx, new BsonDocument("_id", before.PhysicalPoolId)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            await sources.RequireActivatedPoolAsync(context.Tx, pool, ct);
            if (pool["revision"].AsInt64 != context.Expected!.PoolRevision || pool["fence"].AsInt64 != checked(context.Expected.PoolFence + context.PoolWrites) ||
                InventoryGraph.Location(pool["location"].AsBsonDocument) != before.Balance.Key.Location)
                throw B.Changed();
            var actualSource = await sources.ReadPackConversionExecutionSourceAsync(context.Actor, context.Authority, context.Tx, context.Input.Location,
                before.PhysicalPoolId, context.Expected.PoolRevision, ct);
            if (SourceConfiguration(actualSource) != SourceConfiguration(context.PoolSource!)) throw B.Changed();
        }
        if (context.Actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId,
                context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, Now)).SingleOrDefaultAsync(ct)
                ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
            context.SessionExpiry = new(new[] { live["idleExpiresAt"].ToUniversalTime(), live["absoluteExpiresAt"].ToUniversalTime() }.Min());
        }
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private CommandAuthorization Command(Context context) => new(
        ScopePolicy.Authorize(context.Actor, context.Authority, Permissions.StockConvert, context.Resource, Now),
        ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory), Permissions.StockConvert, new(context.Resource));
    private async Task PrepareAsync(Context context, CancellationToken ct) {
        Current(context); await new StockSourceTopologyFence(database).FenceAsync(context.Tx, context.Input.Location.OrganizationId!.Value, ct);
        var approved = await rules.RuleAsync(context.Tx, context.Input.RuleId, ct);
        if (approved.Rule["version"] != context.Input.RuleVersion) throw B.Changed();
        var ruleInput = PackConversionRuleBson.Input(approved.Rule["input"].AsBsonDocument);
        if (ruleInput.Location != context.Input.Location) throw new AuthorizationDeniedException("scope_denied");
        var pair = await catalogue.CurrentAsync(context.Tx, ruleInput, ct);
        if (pair != approved.Rule["catalog"]) throw B.Changed();
        context.Rule = approved.Rule; context.RuleInput = ruleInput; context.Catalogue = pair;
        await FenceAsync(context, false, ct);
        var before = await new InventoryRepository(database).ReadAsync(context.Tx, context.Input.SourceBalanceId, ct);
        if (before.Balance.Key.Location.ToScope() != ruleInput.Location || before.Balance.Key.SkuId.Value != ruleInput.SourceSkuId ||
            before.Balance.Key.DefinitionId.Value != ruleInput.SourceDefinitionId) throw B.Changed();
        var poolSource = await sources.ReadPackConversionExecutionSourceAsync(context.Actor, context.Authority, context.Tx, context.Input.Location,
            before.PhysicalPoolId, (await PoolAsync(context.Tx, before.PhysicalPoolId, ct))["revision"].AsInt64, ct);
        var pool = await PoolAsync(context.Tx, before.PhysicalPoolId, ct); await sources.RequireActivatedPoolAsync(context.Tx, pool, ct);
        var lot = await Rows("lots").Find(context.Tx, new BsonDocument("_id", before.Balance.Key.LotId.Value)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var facts = InventoryGraph.ReadLot(lot);
        if (lot["origin"] != "procurementReceipt" || facts.ParentId is not null || facts.RecordedAt > Now.ToUnixTimeMilliseconds() ||
            facts.ExpiresOn is { } expiry && expiry <= DateOnly.FromDateTime(Now.UtcDateTime)) throw B.Changed();
        var origin = await HeldReceiptOriginProof.CaptureAsync(database, unit, clock, catalog, orders, sources, context.Tx, lot["sourceId"].AsInt64, ct);
        if (lot != InventoryHeldReceiptWriter.Lot(origin.Receipt)) throw B.Broken();
        var physical = await new HeldReceiptMovementHistory(database, unit, clock).RequireAsync(context.Tx, origin.Receipt, ct, retainedOrigin: origin);
        if (physical.Current != before) throw B.Broken();
        var value = await values.ReadAvailableAsync(context.Tx, before, AuthenticateSourceAsync, ct);
        var quantities = PackConversionRuleCatalogue.Quantities(ruleInput, pair, context.Input.RuleId, context.Input.Steps);
        var after = before with { Balance = StockBalancePolicy.ConvertOut(before.Balance, before.Balance.Revision, quantities.SourcePackages) };
        var allocation = LotValuePolicy.TakeProportionalFloor(value, value.Revision, quantities.SourcePackages);
        if (value.RemainingPackages != ReleasedPackages(before) || allocation.Remaining.RemainingPackages != ReleasedPackages(after)) throw B.Broken();
        pool = await PoolAsync(context.Tx, before.PhysicalPoolId, ct);
        context.Before = before; context.After = after; context.SourceLot = lot; context.PoolSource = poolSource;
        context.SourceValue = Value(value); context.Quantities = quantities;
        context.Expected = new(before.Balance.Revision, pool["revision"].AsInt64, pool["fence"].AsInt64, value.Revision);
        Current(context);
    }
    private async Task<BsonDocument> PoolAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        InventoryGraph.RequirePool(pool); return pool!;
    }
    private static long ReleasedPackages(InventoryBalanceDocument balance) => checked(balance.Balance.OnHand - balance.Balance.Held - balance.Balance.Expired);
    private static BsonDocument Value(LotValueBalance value) => new() { { "key", LotValueStore.Key(value.Key) },
        { "remainingPackages", value.RemainingPackages }, { "remainingValue", value.RemainingValue.ToString() }, { "revision", value.Revision } };
    private static BsonDocument Quantities(ConversionQuantities value) => new() { { "sourcePackages", value.SourcePackages },
        { "destinationPackages", value.DestinationPackages }, { "baseUnits", value.BaseUnits } };
    private static BsonDocument BindingSource(BsonDocument value) => new(value.Where(field => field.Name != "capturedAt"));
    private static BsonDocument SourceConfiguration(BsonDocument value) => new(value.Where(field => field.Name is not ("capturedAt" or "poolFence")));
    private string Binding(Context context) => CatalogRepository.Digest(new BsonDocument {
        { "actorId", context.Actor.UserId }, { "actorGrantVersion", context.Actor.GrantVersion }, { "input", B.Input(context.Input) },
        { "expected", B.Expected(context.Expected!) }, { "rule", context.Rule! }, { "catalog", context.Catalogue! },
        { "poolSource", BindingSource(context.PoolSource!) }, { "sourceLot", context.SourceLot! }, { "sourceValue", context.SourceValue! },
        { "quantities", Quantities(context.Quantities!) }, { "before", InventoryBson.Balance(context.Before!) },
        { "after", InventoryBson.Balance(context.After!) }, { "policy", LotValuePolicy.ProportionalFloorPolicy } });
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!StockCountBson.Key(metadata.IdempotencyKey) || !StockCountPolicy.CanonicalText(metadata.Reason, 1000) ||
            !StockCountPolicy.CanonicalText(metadata.CorrelationId, 128)) throw PackConversionExecutionPolicy.Invalid();
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, PackConversionInput input, CancellationToken ct = default) {
        input = PackConversionExecutionPolicy.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, input); await PeopleAsync(context, token); await PrepareAsync(context, token); await FenceAsync(context, true, token);
            return new BsonDocument { { "input", B.Input(input) }, { "expected", B.Expected(context.Expected!) }, { "rule", context.Rule! },
                { "sourceValue", context.SourceValue! }, { "before", InventoryBson.Balance(context.Before!) }, { "after", InventoryBson.Balance(context.After!) },
                { "quantities", Quantities(context.Quantities!) }, { "policy", LotValuePolicy.ProportionalFloorPolicy }, { "binding", Binding(context) }, { "observedAt", Now.UtcDateTime } };
        }, ct);
    }
    public async Task<BsonDocument> ExecuteAsync(ActorContext actor, PackConversionInput input, PackConversionExpectation expected, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = PackConversionExecutionPolicy.Copy(input); expected = PackConversionExecutionPolicy.Copy(expected); Metadata(metadata);
        if (binding is null || binding.Length != 64 || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw PackConversionExecutionPolicy.Invalid();
        await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Conversions, ct)).Value; var lotId = (await ids.NextAsync("lots", ct)).Value;
        var balanceId = (await ids.NextAsync("stockBalances", ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = new { input, expected, binding }; Context? context = null;
        try {
            return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, token) => {
                context = new(actor, authority, tx, input); await PeopleAsync(context, token); return Command(context);
            }, async (tx, token) => {
                var active = context ?? throw B.Broken(); await PrepareAsync(active, token);
                if (active.Expected != expected || Binding(active) != binding) throw B.Changed();
                await FenceAsync(active, true, token);
                var at = Now; var lot = DestinationLot(id, lotId, active.RuleInput!, active.SourceLot!, at);
                var key = new StockBalanceKey(active.Before!.Balance.Key.Location, new(active.RuleInput!.DestinationSkuId),
                    new(active.RuleInput.DestinationDefinitionId), new(lotId));
                active.DestinationBefore = new(balanceId, active.Before.PhysicalPoolId, new(key, 0, 0, 0, 0, 1));
                active.DestinationAfter = active.DestinationBefore with { Balance = StockBalancePolicy.ConvertIn(active.DestinationBefore.Balance, 1, active.Quantities!.DestinationPackages) };
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "pack.conversion" }, { "input", B.Input(input) },
                    { "expected", B.Expected(expected) }, { "rule", active.Rule! }, { "catalog", active.Catalogue! }, { "poolSource", active.PoolSource! },
                    { "sourceLot", active.SourceLot! }, { "sourceValue", active.SourceValue! }, { "quantities", Quantities(active.Quantities!) },
                    { "before", InventoryBson.Balance(active.Before) }, { "after", InventoryBson.Balance(active.After!) }, { "destinationLot", lot },
                    { "destinationBefore", InventoryBson.Balance(active.DestinationBefore) }, { "destinationAfter", InventoryBson.Balance(active.DestinationAfter) },
                    { "binding", binding }, { "policy", LotValuePolicy.ProportionalFloorPolicy },
                    { "people", new BsonArray(active.People!.Evidence.Select(PackConversionRuleRecords.Person)) }, { "actorId", actor.UserId },
                    { "actorGrantVersion", actor.GrantVersion }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) },
                    { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId }, { "recordedAt", at.UtcDateTime },
                    { "phase", "preparing" }, { "movementId", BsonNull.Value }, { "valueTransitionId", BsonNull.Value },
                    { "valueTransitionHash", BsonNull.Value }, { "transferredValue", BsonNull.Value } };
                row.Add("causeHash", B.CauseHash(row)); row.Add("snapshotHash", B.Hash(row)); active.Row = row; inFlight[tx] = active;
                await Rows(B.Conversions).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("lots").InsertOneAsync(tx, lot, cancellationToken: token);
                await new InventoryRepository(database).CreateAsync(tx, active.DestinationBefore, token);
                active.PoolWrites = 1;
                var movement = await new InventoryLedger(database, clock, [new LedgerSource(this, active)]).ApplyAsync(tx, actor,
                    B.Source(id, active.Before.PhysicalPoolId), 1, new(metadata.Reason, metadata.CorrelationId), token);
                active.MovementId = movement.Id; active.PoolWrites = 3;
                var proof = await LotValueConsumptionProof.CreateAsync(this, database, tx, id, token);
                var value = await values.TakeAsync(tx, proof, token);
                row["movementId"] = movement.Id;
                row["valueTransitionId"] = value.TransitionId; row["valueTransitionHash"] = value.TransitionHash; row["transferredValue"] = value.TransferredValue.ToString();
                row["phase"] = "committed"; row["snapshotHash"] = B.Hash(row);
                if ((await Rows(B.Conversions).ReplaceOneAsync(tx, new BsonDocument { { "_id", id }, { "phase", "preparing" } }, row, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
                await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox(row), cancellationToken: token);
                return B.Result(row);
            }, ct, async (_, tx, token) => {
                var active = context ?? throw B.Broken();
                var found = await ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token) ?? throw B.Broken();
                await ConversionAsync(tx, found["_id"].AsInt64, token);
                await FenceAsync(active, active.Before is not null, token);
            }, validateCurrent: () => { _ = Current(context ?? throw B.Broken()); });
        } finally { if (context is not null) inFlight.TryRemove(context.Tx, out _); }
    }
    private static BsonDocument DestinationLot(long id, long lotId, PackConversionRuleInput rule, BsonDocument parent, DateTimeOffset at) => new() {
        { "_id", lotId }, { "schemaVersion", 1 }, { "kind", "inventory.lot" }, { "organizationId", rule.Location.OrganizationId!.Value },
        { "skuId", rule.DestinationSkuId }, { "definitionId", rule.DestinationDefinitionId }, { "rootLotId", parent["rootLotId"] },
        { "parentLotId", parent["_id"] }, { "origin", "packageConversion" }, { "sourceId", id }, { "sourceLineId", 1L },
        { "recordedAt", at.UtcDateTime }, { "expiresOn", parent["expiresOn"] } };
    internal static bool IsLedgerConversionSource(IInventorySourceReader reader) => reader is LedgerSource;
    private sealed class LedgerSource(PackConversionService owner, Context context) : IInventorySourceReader {
        public string Namespace => B.Namespace;
        public IMongoDatabase Database => owner.database;
        public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long revision, CancellationToken ct) {
            owner.RequireValueAuthority(owner.database, tx, context.Row!["_id"].AsInt64, context.Row["causeHash"].AsString);
            if (actor != context.Actor || revision != 1 || source != B.Source(context.Row["_id"].AsInt64, context.Before!.PhysicalPoolId) ||
                await owner.Rows(B.Conversions).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw B.Broken();
            await owner.FenceAsync(context, true, ct);
            return new(source, 1, new(context.Before.PhysicalPoolId, context.Before.Balance.Key.Location, context.Expected!.PoolRevision), [
                new(context.Before.Id, context.Before.PhysicalPoolId, context.Before.Balance.Key, context.Before.Balance.Revision, InventoryBalanceAction.ConvertOut, context.Quantities!.SourcePackages),
                new(context.DestinationBefore!.Id, context.DestinationBefore.PhysicalPoolId, context.DestinationBefore.Balance.Key, 1, InventoryBalanceAction.ConvertIn, context.Quantities.DestinationPackages) ]);
        }
    }
    internal async Task<BsonDocument> ValueAuthorityAsync(IMongoDatabase owner, IClientSessionHandle tx, long id, CancellationToken ct) {
        if (!inFlight.TryGetValue(tx, out var context)) throw B.Broken();
        RequireValueAuthority(owner, tx, id, context.Row!["causeHash"].AsString);
        if (await Rows(B.Conversions).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw B.Broken();
        var proofRow = context.Row.DeepClone().AsBsonDocument;
        proofRow["movementId"] = context.MovementId ?? throw B.Broken();
        await PhysicalMovementAsync(tx, proofRow, ct);
        if (await Rows("stockBalances").Find(tx, InventoryBson.Balance(context.After!)).SingleOrDefaultAsync(ct) is null ||
            await Rows("stockBalances").Find(tx, InventoryBson.Balance(context.DestinationAfter!)).SingleOrDefaultAsync(ct) is null) throw B.Broken();
        await FenceAsync(context, true, ct); return proofRow;
    }
    internal void RequireValueAuthority(IMongoDatabase owner, IClientSessionHandle tx, long id, string hash) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace || !inFlight.TryGetValue(tx, out var context)) throw B.Broken();
        Current(context); var row = context.Row ?? throw B.Broken();
        if (row["_id"] != id || row["phase"] != "preparing" || row["causeHash"] != hash || B.CauseHash(row) != hash ||
            row["actorId"] != context.Actor.UserId || row["actorGrantVersion"] != context.Actor.GrantVersion) throw B.Broken();
    }
}
