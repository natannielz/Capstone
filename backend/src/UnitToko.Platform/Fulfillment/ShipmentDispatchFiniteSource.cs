using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Actual physical/value owners only. This read supplies no commercial,
/// seller, operating-policy or reservation authority.</summary>
internal sealed class ShipmentDispatchFiniteSource
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IClock clock;
    private readonly CatalogAuthoringService catalog;
    private readonly PurchaseOrderService purchaseOrders;
    private readonly StockSourceSetupService sources;
    private readonly StockReleaseSource releaseSource;
    private readonly PackConversionService conversions;
    private readonly LotValueStore values;
    private readonly ShipmentDispatchValueRecords.HistoricalRead? historyRead;
    private PackConversionService.HistoricalSourceRead? historicalSources;
    private (ShipmentDispatchFiniteSource Owner, PackConversionService.FiniteHistoryRead History)? finiteSources;
    internal ShipmentDispatchFiniteSource(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        ShipmentDispatchValueRecords.HistoricalRead? historyRead = null) {
        this.database = database; this.unit = unit; this.clock = clock;
        this.historyRead = historyRead;
        catalog = new(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        purchaseOrders = new(database, unit, ids, clock, catalog, requests, new SupplierService(database, unit, ids, clock, catalog));
        sources = new(database, unit, ids, clock);
        var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, purchaseOrders, requests);
        var valuations = new ProcurementStockValuationService(database, unit, ids, clock, catalog, purchaseOrders, receiving, sources);
        var releases = new StockReleaseService(database, unit, ids, clock, catalog, purchaseOrders, sources, valuations);
        releaseSource = new(database, unit, clock, catalog, purchaseOrders, sources, valuations);
        conversions = new(database, unit, ids, clock, catalog, purchaseOrders, sources, valuations, releases);
        values = new(database, ids, clock);
    }
    internal static ShipmentDispatchFiniteSource ForHistory(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        IClientSessionHandle tx, ShipmentDispatchValueRecords.HistoricalRead history,
        ShipmentDispatchFiniteSource? finiteOwner = null, PackConversionService.FiniteHistoryRead? finiteHistory = null) {
        history.Require(database, tx);
        var owner = new ShipmentDispatchFiniteSource(database, unit, ids, clock, history);
        if (finiteOwner is null && finiteHistory is null)
            owner.historicalSources = owner.conversions.RetainHistoricalSources(tx, history);
        else {
            if (finiteOwner is null || finiteHistory is null) throw ShipmentDispatchRecords.Invalid();
            finiteOwner.RequireFiniteSources(database, clock, tx, history, finiteHistory);
            owner.finiteSources = (finiteOwner, finiteHistory);
        }
        return owner;
    }
    internal void RequireFiniteSources(IMongoDatabase current, IClock expectedClock, IClientSessionHandle tx,
        ShipmentDispatchValueRecords.HistoricalRead history, PackConversionService.FiniteHistoryRead finiteHistory) {
        history.Require(current, tx);
        if (!ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(clock, expectedClock) || !ReferenceEquals(finiteHistory.ValueHistory, history) ||
            historicalSources is not null || finiteSources is not null) throw ShipmentDispatchRecords.Invalid();
        finiteHistory.Require(conversions, current, tx);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    // A fresh account-walk result, transported only within one head read. It
    // grants neither current quantity nor authority to a later invocation.
    internal sealed class AuthenticatedRelease
    {
        private readonly ShipmentDispatchFiniteSource owner;
        private readonly PackConversionService.FiniteHistoryRead history;
        private readonly IClientSessionHandle tx;
        private readonly long number;
        private readonly DateTimeOffset at;
        internal InventorySource Source { get; }
        internal string Hash { get; }
        internal StockReleaseSourceProof Proof { get; }
        internal HeldReceiptOriginProof Origin { get; }
        private AuthenticatedRelease(ShipmentDispatchFiniteSource owner, PackConversionService.FiniteHistoryRead history,
            IClientSessionHandle tx, InventorySource source, string hash, StockReleaseSourceProof proof, HeldReceiptOriginProof origin) {
            this.owner = owner; this.history = history; this.tx = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            at = owner.clock.UtcNow; Source = source; Hash = hash; Proof = proof; Origin = origin;
        }
        internal static AuthenticatedRelease Create(ShipmentDispatchFiniteSource owner, PackConversionService.FiniteHistoryRead history,
            IClientSessionHandle tx, InventorySource source, string hash, StockReleaseSourceProof proof, HeldReceiptOriginProof origin) {
            var result = new AuthenticatedRelease(owner, history, tx, source, hash, proof, origin);
            result.Require(owner, history, owner.database, tx); return result;
        }
        internal void Require(ShipmentDispatchFiniteSource expectedOwner, PackConversionService.FiniteHistoryRead expectedHistory,
            IMongoDatabase current, IClientSessionHandle session) {
            if (!ReferenceEquals(owner, expectedOwner) || !ReferenceEquals(history, expectedHistory) || !ReferenceEquals(tx, session) ||
                session.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || owner.clock.UtcNow < at ||
                at == default || at.Offset != TimeSpan.Zero || Source.Namespace != StockReleaseBson.Namespace || Source.EventKind != "releaseHeld")
                throw ShipmentDispatchRecords.Invalid();
            owner.RequireFiniteSources(current, owner.clock, session, history.ValueHistory, history);
            Proof.Require(current, session); Origin.Require(current, session);
            // ReadOriginAsync deliberately carries no current pool/balance or
            // CurrentOrigin. Its sealed acquisition facts bind the separate
            // immutable origin; the capture still requires its original object.
            var receipt = Origin.Receipt; var lot = InventoryHeldReceiptWriter.Lot(receipt);
            var birth = InventoryHeldReceiptWriter.After(receipt); var snapshot = Proof.Snapshot;
            if (Proof.Selection.ReceiptId != Origin.ReceiptId || Proof.Receipt != receipt || Proof.Lot != lot ||
                Proof.BalanceId != birth.Id || Proof.PoolId != birth.PhysicalPoolId || Proof.Key != birth.Balance.Key || Source.Occurrence != birth.PhysicalPoolId ||
                snapshot["receiptId"] != Origin.ReceiptId || snapshot["receiptHash"] != receipt["snapshotHash"] ||
                snapshot["lotId"] != lot["_id"] || snapshot["lotHash"] != CatalogRepository.Digest(lot) ||
                snapshot["qualityAllocationId"] != Proof.Selection.QualityAllocationId || snapshot["qualityAllocationHash"] != Proof.QualityAllocation["snapshotHash"] ||
                snapshot["valuationAllocationId"] != Proof.Selection.ValuationAllocationId || snapshot["valuationAllocationHash"] != Proof.ValuationAllocation["snapshotHash"])
                throw ShipmentDispatchRecords.Invalid();
        }
    }
    internal Task AuthenticateSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) {
        return AuthenticateSourceAsync(owner, tx, source, hash, ct, historyRead);
    }
    internal Task AuthenticateSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? history) {
        if (finiteSources is { } borrowed) {
            if (!ReferenceEquals(history, historyRead)) throw ShipmentDispatchRecords.Invalid();
            borrowed.Owner.RequireFiniteSources(owner, clock, tx, history!, borrowed.History);
            return borrowed.History.AuthenticateAsync(owner, tx, source, hash, ct);
        }
        if (history is not null && source.Namespace == "fulfillment.shipment" && source.EventKind == "consumeLot")
            return history.AuthenticateAsync(owner, tx, source, hash, ct);
        if (historicalSources is not null) {
            if (!ReferenceEquals(history, historyRead)) throw ShipmentDispatchRecords.Invalid();
            historicalSources.Require(conversions, owner, tx); return historicalSources.AuthenticateAsync(owner, tx, source, hash, ct);
        }
        return conversions.AuthenticateSourceAsync(owner, tx, source, hash, ct, history);
    }
    internal async Task<BsonDocument> InventoryAccountForHistoryAsync(IClientSessionHandle tx, long balanceId, long revision, CancellationToken ct,
        BsonDocument? requiredPrevious = null, Action<AuthenticatedRelease>? releaseAuthenticated = null,
        PackConversionService.FiniteHistoryRead.HeadRead? headRead = null) {
        historyRead?.Require(database, tx);
        var membership = new PurchaseOrderSourceReadCapture(); var origins = new List<HeldReceiptOriginProof>();
        InventorySource? requiredSource = null; string? requiredHash = null;
        if (requiredPrevious is not null) {
            var expected = requiredPrevious.DeepClone().AsBsonDocument;
            var previous = await membership.OneAsync(database, tx, LotValueStore.Transitions, new BsonDocument { { "_id", expected["_id"] },
                { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.Transitions) } } }, ct) ?? throw ShipmentDispatchRecords.Invalid();
            LotValueStore.RequireTransition(previous);
            if (previous != expected || previous["transitionHash"] != LotValueStore.Hash(previous)) throw ShipmentDispatchRecords.Invalid();
            var ownAxis = previous["availableId"] == balanceId;
            if (!ownAxis && previous["destinationId"] != balanceId ||
                previous[ownAxis ? "availableAfter" : "destinationAfter"]["revision"] != revision) throw ShipmentDispatchRecords.Invalid();
            var source = previous["source"];
            requiredSource = new(source["namespace"].AsString, source["documentId"].AsInt64, source["eventKind"].AsString, source["occurrence"].AsInt64);
            requiredSource.Validate();
            if (previous["sourceEvent"] != requiredSource.EventKey) throw ShipmentDispatchRecords.Invalid();
            requiredHash = previous["sourceHash"].AsString;
        }
        var evidence = new Dictionary<string, (InventorySource Source, string Hash)>(StringComparer.Ordinal);
        var released = releaseAuthenticated is null ? null : new List<AuthenticatedRelease>();
        using var accountRead = headRead is null && finiteSources is { } boundRead
            ? boundRead.History.BeginAccountRead(boundRead.Owner, database, tx, balanceId) : null;
        headRead ??= accountRead;
        BsonDocument account;
        if (finiteSources is { } borrowed) {
            borrowed.Owner.RequireFiniteSources(database, clock, tx, historyRead!, borrowed.History);
            account = await borrowed.Owner.InventoryAccountAsync(tx, balanceId, revision, new HashSet<(long, long)>(), origins, evidence, membership, ct, borrowed.History, released, headRead);
            borrowed.Owner.RequireFiniteSources(database, clock, tx, historyRead!, borrowed.History);
        } else account = await InventoryAccountAsync(tx, balanceId, revision, new HashSet<(long, long)>(), origins, evidence, membership, ct);
        // The account walk itself authenticated this exact freshly read prior
        // source. No separate ancestor reconstruction or saved result is needed.
        if (requiredSource is not null && (!evidence.TryGetValue(requiredSource.EventKey, out var prior) ||
            prior.Source != requiredSource || prior.Hash != requiredHash)) throw ShipmentDispatchRecords.Invalid();
        membership.Seal(); await membership.RequireAsync(database, tx, ct);
        foreach (var origin in origins) {
            var bound = finiteSources;
            if (bound is null || headRead is null || !headRead.DeferOrigin(bound.Value.Owner, bound.Value.History, database, tx, origin))
                await origin.RequireAsync(database, tx, ct);
        }
        if (accountRead is not null) {
            await accountRead.RequireFinalConversionsAsync(database, tx, ct);
            await accountRead.RequireAccountOriginsAsync(database, tx, ct);
            await membership.RequireAsync(database, tx, ct);
        }
        historyRead?.Require(database, tx);
        if (finiteSources is { } retained) retained.Owner.RequireFiniteSources(database, clock, tx, historyRead!, retained.History);
        if (released is not null) foreach (var result in released) releaseAuthenticated!(result);
        return account;
    }

    internal sealed class Capture
    {
        private readonly ShipmentDispatchFiniteSource owner;
        private readonly IClientSessionHandle transaction;
        private readonly long number;
        private readonly DateTimeOffset at;
        private readonly ImmutableArray<HeldReceiptOriginProof> origins;
        private readonly ImmutableArray<(InventorySource Source, string Hash)> evidence;
        private readonly BsonDocument inventory;
        private readonly PurchaseOrderSourceReadCapture membership;
        private readonly BsonArray initialHistory;
        private readonly PackConversionService.FiniteHistoryRead conversionHistory;
        internal InventoryBalanceDocument Before { get; }
        internal LotValueBalance Value { get; }
        internal BsonDocument InventoryAccount => inventory.DeepClone().AsBsonDocument;
        internal ShipmentDispatchValueRecords.HistoricalRead ValueHistory => conversionHistory.ValueHistory;
        internal async Task BindFreshHistoryAsync(IMongoDatabase current, IClientSessionHandle tx,
            ShipmentDispatchValueRecords.HistoricalRead history, ImmutableArray<Capture> selected, CancellationToken ct) {
            Require(current, tx); history.Require(current, tx);
            if (selected.IsDefaultOrEmpty || !selected.Any(value => ReferenceEquals(value, this)) ||
                selected.Select(value => value.Before.Id).Distinct().Count() != selected.Length) throw ShipmentDispatchRecords.Invalid();
            var fresh = owner.conversions.RetainFiniteHistory(tx, history);
            foreach (var capture in selected) {
                capture.Require(current, tx);
                if (!ReferenceEquals(capture.owner, owner)) throw ShipmentDispatchRecords.Invalid();
                await fresh.ImportGenesisAsync(capture.conversionHistory, current, tx, ct);
                capture.Require(current, tx);
            }
            history.BindFiniteSources(owner, fresh, current, owner.clock, tx);
            owner.RequireFiniteSources(current, owner.clock, tx, history, fresh);
            Require(current, tx); ct.ThrowIfCancellationRequested();
        }
        internal async Task<BsonDocument> RequireConsumedInventoryAccountAsync(IMongoDatabase current, IClientSessionHandle tx,
            InventoryBalanceDocument before, long revision, InventorySource priorSource, string priorHash,
            BsonDocument transition, CancellationToken ct) {
            Require(current, tx);
            if (before != Before || revision != Value.Revision ||
                evidence.Count(item => item.Source == priorSource && item.Hash == priorHash) != 1) throw ShipmentDispatchRecords.Invalid();
            // The original acquisition mapping was derived only after actual
            // release/valuation authentication. Fresh full prefix owners and
            // captured immutable hashes/membership must still prove those exact
            // same acquisition facts; no current quantity is inferred here.
            await RequireOriginAsync(current, tx, ct);
            await RequireHistoryAfterAsync(current, tx, transition, ct);
            Require(current, tx); ct.ThrowIfCancellationRequested(); return InventoryAccount;
        }
        private Capture(ShipmentDispatchFiniteSource owner, IClientSessionHandle tx, InventoryBalanceDocument before, LotValueBalance value,
            BsonDocument inventory, IEnumerable<HeldReceiptOriginProof> origins, IEnumerable<(InventorySource, string)> evidence,
            PurchaseOrderSourceReadCapture membership, BsonArray initialHistory, PackConversionService.FiniteHistoryRead conversionHistory) {
            this.owner = owner; transaction = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; at = owner.clock.UtcNow;
            Before = before; Value = value; this.inventory = inventory.DeepClone().AsBsonDocument;
            this.origins = [.. origins]; this.evidence = [.. evidence];
            this.membership = membership; this.initialHistory = initialHistory.DeepClone().AsBsonArray;
            this.conversionHistory = conversionHistory;
        }
        internal void Require(IMongoDatabase current, IClientSessionHandle tx) {
            new InventoryRepository(current).RequireSession(tx);
            if (!ReferenceEquals(current.Client, owner.database.Client) || !current.DatabaseNamespace.Equals(owner.database.DatabaseNamespace) ||
                !ReferenceEquals(tx, transaction) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || owner.clock.UtcNow < at)
                throw ShipmentDispatchRecords.Invalid();
            conversionHistory.Require(owner.conversions, current, tx);
        }
        internal Task RequireOriginAsync(IMongoDatabase current, IClientSessionHandle tx, CancellationToken ct) =>
            RequireOriginCoreAsync(current, tx, ct);
        private void RequireAuthenticatedRelease(IMongoDatabase current, IClientSessionHandle tx,
            AuthenticatedRelease result, HashSet<string> authenticatedHere) {
            Require(current, tx); result.Require(owner, conversionHistory, current, tx);
            // A historical shipment verifies every effect. An acquisition
            // belonging to another selected lot supplies no shortcut here.
            if (!evidence.Any(item => item.Source.EventKey == result.Source.EventKey)) return;
            if (evidence.Count(item => item.Source == result.Source && item.Hash == result.Hash) != 1 ||
                !origins.Any(origin => ReferenceEquals(origin, result.Origin)) || result.Proof.ValuationDecision["accounting"]["inventory"] != inventory)
                throw ShipmentDispatchRecords.Invalid();
            authenticatedHere.Add(result.Source.EventKey);
        }
        private async Task RequireOriginCoreAsync(IMongoDatabase current, IClientSessionHandle tx, CancellationToken ct,
            HashSet<string>? authenticatedHere = null, PackConversionService.FiniteHistoryRead.HeadRead? headRead = null) {
            Require(current, tx);
            using var ownRead = headRead is null ? conversionHistory.BeginHeadRead(owner, current, tx, Before.Id, origins) : null;
            var read = headRead ?? ownRead!;
            read.Require(conversionHistory, current, tx);
            var authenticated = authenticatedHere ?? new HashSet<string>(StringComparer.Ordinal);
            // This invocation proves conversion ancestry first, then joins prior
            // dispatch accounts to those exact proofs and authenticates releases.
            foreach (var item in evidence.OrderByDescending(item => item.Source.Namespace == PackConversionBson.Namespace && item.Source.EventKind == "convertPackages" ? 2 :
                item.Source.Namespace == "fulfillment.shipment" && item.Source.EventKind == "consumeLot" ? 1 : 0))
                if (!authenticated.Contains(item.Source.EventKey)) {
                    await conversionHistory.AuthenticateAsync(current, tx, item.Source, item.Hash, ct,
                        result => RequireAuthenticatedRelease(current, tx, result, authenticated), read);
                    Require(current, tx); authenticated.Add(item.Source.EventKey);
                }
            await read.RequireFinalConversionsAsync(current, tx, ct);
            foreach (var origin in origins) await origin.RequireAsync(current, tx, ct);
            read.RequireFinalOrigins(origins, current, tx);
            await membership.RequireAsync(current, tx, ct);
            var pool = await owner.Rows("inventoryPools").Find(tx, new BsonDocument("_id", Before.PhysicalPoolId)).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
            await owner.sources.RequireActivatedPoolAsync(tx, pool, ct);
            var lot = InventoryGraph.ReadLot(await owner.Rows("lots").Find(tx, new BsonDocument("_id", Before.Balance.Key.LotId.Value)).SingleOrDefaultAsync(ct));
            if (lot.ExpiresOn is { } expiry && expiry <= AccountingCalendar.BusinessDate(owner.clock.UtcNow)) throw ShipmentDispatchAdmissionPolicy.Invalid();
            read.Require(conversionHistory, current, tx);
            Require(current, tx);
        }
        internal async Task<(InventoryBalanceDocument Physical, LotValueBalance Value)> ReadCurrentValueWithOriginAsync(IMongoDatabase current,
            IClientSessionHandle tx, CancellationToken ct) {
            Require(current, tx);
            var physical = await new InventoryRepository(current).ReadAsync(tx, Before.Id, ct);
            if (physical.Id != Before.Id || physical.PhysicalPoolId != Before.PhysicalPoolId || physical.Balance.Key != Before.Balance.Key)
                throw ShipmentDispatchRecords.Invalid();
            var head = await owner.Rows(LotValueStore.AvailableHeads).Find(tx, new BsonDocument("_id", Before.Id)).SingleOrDefaultAsync(ct)
                ?? throw ShipmentDispatchRecords.Invalid();
            using var headRead = conversionHistory.BeginHeadRead(owner, current, tx, Before.Id, origins);
            // This set lives only within this read. Every callback authenticates
            // its exact original source/hash; no completed source result is stored.
            var authenticatedHere = new HashSet<string>(StringComparer.Ordinal);
            async Task AuthenticateAsync(IMongoDatabase database, IClientSessionHandle session, InventorySource source, string hash, CancellationToken token) {
                Require(database, session);
                if (evidence.Count(item => item.Source == source && item.Hash == hash) != 1) throw ShipmentDispatchRecords.Invalid();
                await conversionHistory.AuthenticateAsync(database, session, source, hash, token,
                    result => RequireAuthenticatedRelease(current, tx, result, authenticatedHere), headRead);
                Require(database, session); authenticatedHere.Add(source.EventKey);
            }
            var value = await owner.values.ReadAvailableAsync(tx, physical, AuthenticateAsync, ct);
            await RequireOriginCoreAsync(current, tx, ct, authenticatedHere, headRead);
            if (value != Value || await new InventoryRepository(current).ReadAsync(tx, Before.Id, ct) != physical ||
                await owner.Rows(LotValueStore.AvailableHeads).Find(tx, new BsonDocument("_id", Before.Id)).SingleOrDefaultAsync(ct) != head)
                throw ShipmentDispatchRecords.Invalid();
            headRead.Require(conversionHistory, current, tx);
            Require(current, tx); ct.ThrowIfCancellationRequested(); return (physical, value);
        }
        internal async Task RequireBeforeAsync(IMongoDatabase current, IClientSessionHandle tx, CancellationToken ct) {
            var checkedValue = await ReadCurrentValueWithOriginAsync(current, tx, ct);
            if (checkedValue.Physical != Before) throw ShipmentDispatchRecords.Invalid();
        }
        private async Task RequireHistoryAfterAsync(IMongoDatabase current, IClientSessionHandle tx, BsonDocument transition, CancellationToken ct) {
            Require(current, tx);
            var actual = await owner.Rows(LotValueStore.Transitions).Find(tx, HistoryFilter(Before.Id)).Sort(new BsonDocument("_id", 1))
                .Limit(LotValueStore.MaximumTransitions + 1).ToListAsync(ct);
            var expected = initialHistory.Select(row => row.AsBsonDocument).Append(transition).OrderBy(row => row["_id"]);
            if (new BsonArray(actual) != new BsonArray(expected)) throw ShipmentDispatchRecords.Invalid();
            Require(current, tx);
        }
        internal Task AuthenticateSourceAsync(IMongoDatabase current, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) {
            Require(current, tx); return conversionHistory.AuthenticateAsync(current, tx, source, hash, ct);
        }
        internal async Task AuthenticateCapturedSourceAsync(IMongoDatabase current, IClientSessionHandle tx,
            InventorySource source, string hash, CancellationToken ct) {
            Require(current, tx);
            if (evidence.Count(item => item.Source == source && item.Hash == hash) != 1) throw ShipmentDispatchRecords.Invalid();
            await RequireOriginCoreAsync(current, tx, ct);
            Require(current, tx); ct.ThrowIfCancellationRequested();
        }
        internal async Task AuthenticateCapturedHeadAsync(IMongoDatabase current, IClientSessionHandle tx, BsonDocument head,
            IReadOnlyList<BsonDocument> history, CancellationToken ct) {
            Require(current, tx);
            var actual = await owner.Rows(LotValueStore.Transitions).Find(tx, HistoryFilter(Before.Id)).Sort(new BsonDocument("_id", 1))
                .Limit(LotValueStore.MaximumTransitions + 1).ToListAsync(ct);
            if (new BsonArray(history.OrderBy(row => row["_id"])) != initialHistory || new BsonArray(actual) != initialHistory ||
                head["_id"] != Before.Id || head["revision"] != Value.Revision ||
                head["remainingPackages"] != Value.RemainingPackages || head["remainingValue"] != Value.RemainingValue.ToString() ||
                await owner.Rows(LotValueStore.AvailableHeads).Find(tx, head).SingleOrDefaultAsync(ct) is null)
                throw ShipmentDispatchRecords.Invalid();
            var axisSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in history) {
                var raw = row["source"];
                var source = new InventorySource(raw["namespace"].AsString, raw["documentId"].AsInt64, raw["eventKind"].AsString, raw["occurrence"].AsInt64);
                if (!axisSources.Add(source.EventKey) || evidence.Count(item => item.Source == source && item.Hash == row["sourceHash"].AsString) != 1)
                    throw ShipmentDispatchRecords.Invalid();
            }
            await RequireOriginCoreAsync(current, tx, ct);
            var after = await owner.Rows(LotValueStore.Transitions).Find(tx, HistoryFilter(Before.Id)).Sort(new BsonDocument("_id", 1))
                .Limit(LotValueStore.MaximumTransitions + 1).ToListAsync(ct);
            if (new BsonArray(after) != initialHistory || await owner.Rows(LotValueStore.AvailableHeads).Find(tx, head).SingleOrDefaultAsync(ct) is null)
                throw ShipmentDispatchRecords.Invalid();
            Require(current, tx); ct.ThrowIfCancellationRequested();
        }
        internal static async Task<Capture> ReadAsync(ShipmentDispatchFiniteSource owner, IClientSessionHandle tx, InventoryBalanceDocument expected, CancellationToken ct) {
            var conversionHistory = owner.conversions.RetainFiniteHistory(tx, owner.historyRead);
            conversionHistory.ValueHistory.BindFiniteSources(owner, conversionHistory, owner.database, owner.clock, tx);
            var read = await owner.ReadCoreAsync(tx, expected, ct, conversionHistory);
            var capture = new Capture(owner, tx, read.Before, read.Value, read.Inventory, read.Origins, read.Evidence, read.Membership, read.History, conversionHistory);
            await capture.RequireOriginAsync(owner.database, tx, ct); return capture;
        }
    }

    internal Task<Capture> ReadAsync(IClientSessionHandle tx, InventoryBalanceDocument expected, CancellationToken ct) => Capture.ReadAsync(this, tx, expected, ct);
    private async Task<(InventoryBalanceDocument Before, LotValueBalance Value, BsonDocument Inventory,
        List<HeldReceiptOriginProof> Origins, IEnumerable<(InventorySource Source, string Hash)> Evidence,
        PurchaseOrderSourceReadCapture Membership, BsonArray History)> ReadCoreAsync(
        IClientSessionHandle tx, InventoryBalanceDocument expected, CancellationToken ct, PackConversionService.FiniteHistoryRead conversionHistory) {
        new InventoryRepository(database).RequireSession(tx);
        var actual = await new InventoryRepository(database).ReadAsync(tx, expected.Id, ct);
        if (actual != expected) throw ShipmentDispatchRecords.Invalid();
        var membership = new PurchaseOrderSourceReadCapture();
        var lot = await membership.OneAsync(database, tx, "lots", new BsonDocument("_id", actual.Balance.Key.LotId.Value), ct) ?? throw ShipmentDispatchRecords.Invalid();
        var facts = InventoryGraph.ReadLot(lot);
        if (facts.ExpiresOn is { } expiry && expiry <= AccountingCalendar.BusinessDate(clock.UtcNow)) throw ShipmentDispatchAdmissionPolicy.Invalid();
        var origins = new List<HeldReceiptOriginProof>();
        LotValueBalance? currentValue = null;
        if (lot["origin"] == "procurementReceipt") {
            var origin = await HeldReceiptOriginProof.CaptureAsync(database, unit, clock, catalog, purchaseOrders, sources, tx, lot["sourceId"].AsInt64, ct);
            if (lot != InventoryHeldReceiptWriter.Lot(origin.Receipt) ||
                (await new HeldReceiptMovementHistory(database, unit, clock, historyRead).RequireAsync(tx, origin.Receipt, ct, retainedOrigin: origin)).Current != actual)
                throw ShipmentDispatchRecords.Invalid();
            origins.Add(origin);
        } else if (lot["origin"] == "packageConversion") {
            var current = await conversionHistory.ReadCurrentDestinationValueAsync(conversions, tx, actual.Id, ct, historyRead);
            if (current.Physical != actual) throw ShipmentDispatchRecords.Invalid(); currentValue = current.Value;
        } else throw ShipmentDispatchAdmissionPolicy.Invalid();
        var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", actual.PhysicalPoolId)).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
        membership.RecordPool(pool);
        await sources.CaptureHistory(membership).RequireActivatedPoolAsync(tx, pool, ct);
        var value = currentValue ?? await values.ReadAvailableAsync(tx, actual, conversionHistory.AuthenticateAsync, ct);
        var history = await Rows(LotValueStore.Transitions).Find(tx, HistoryFilter(actual.Id)).Sort(new BsonDocument("_id", 1))
            .Limit(LotValueStore.MaximumTransitions + 1).ToListAsync(ct);
        var evidence = new Dictionary<string, (InventorySource Source, string Hash)>(StringComparer.Ordinal);
        var account = await InventoryAccountAsync(tx, actual.Id, value.Revision, new HashSet<(long, long)>(), origins, evidence, membership, ct, conversionHistory);
        membership.Seal(); await membership.RequireAsync(database, tx, ct);
        return (actual, value, account, origins, evidence.Values, membership, new BsonArray(history));
    }
    private static BsonDocument HistoryFilter(long balanceId) => new("$or", new BsonArray {
        new BsonDocument("availableId", balanceId), new BsonDocument("destinationId", balanceId) });
    private async Task<BsonDocument> InventoryAccountAsync(IClientSessionHandle tx, long balanceId, long revision, HashSet<(long, long)> path,
        List<HeldReceiptOriginProof> origins, Dictionary<string, (InventorySource Source, string Hash)> evidence, PurchaseOrderSourceReadCapture membership, CancellationToken ct,
        PackConversionService.FiniteHistoryRead? conversionHistory = null, List<AuthenticatedRelease>? released = null,
        PackConversionService.FiniteHistoryRead.HeadRead? headRead = null) {
        if (path.Count >= LotValueStore.MaximumTransitions || !path.Add((balanceId, revision))) throw ShipmentDispatchRecords.Invalid();
        try {
            var history = await Rows(LotValueStore.Transitions).Find(tx, new BsonDocument("$or", new BsonArray {
                new BsonDocument("availableId", balanceId), new BsonDocument("destinationId", balanceId) })).Limit(LotValueStore.MaximumTransitions + 1).ToListAsync(ct);
            if (history.Count is < 1 or > LotValueStore.MaximumTransitions) throw ShipmentDispatchRecords.Invalid();
            var prefix = new BsonDocument("$or", new BsonArray {
                new BsonDocument { { "availableId", balanceId }, { "availableAfter.revision", new BsonDocument("$lte", revision) } },
                new BsonDocument { { "destinationId", balanceId }, { "destinationAfter.revision", new BsonDocument("$lte", revision) } } });
            membership.Record(LotValueStore.Transitions, prefix, history.Where(row =>
                row[row["availableId"] == balanceId ? "availableAfter" : "destinationAfter"]["revision"].AsInt64 <= revision));
            BsonDocument? account = null;
            foreach (var transition in history) {
                var destination = transition["availableId"] != balanceId;
                var after = transition[destination ? "destinationAfter" : "availableAfter"];
                if (after["revision"].AsInt64 > revision) continue;
                LotValueStore.RequireTransition(transition);
                if (transition["transitionHash"] != LotValueStore.Hash(transition)) throw ShipmentDispatchRecords.Invalid();
                var source = transition["source"];
                var selected = new InventorySource(source["namespace"].AsString, source["documentId"].AsInt64, source["eventKind"].AsString, source["occurrence"].AsInt64);
                var hash = transition["sourceHash"].AsString;
                var combinedRelease = transition["operation"] == "release" && (conversionHistory is not null || historicalSources is not null);
                if (!combinedRelease) {
                    var joinedConversion = headRead is not null && conversionHistory is not null && headRead.TryJoin(this, conversionHistory, database, tx, selected, hash, transition, balanceId);
                    if (!joinedConversion) {
                        if (conversionHistory is null) await AuthenticateSourceAsync(database, tx, selected, hash, ct);
                        else if (headRead is not null && headRead.IsAccountAxis(this, conversionHistory, database, tx, balanceId) &&
                            selected.Namespace == PackConversionBson.Namespace && selected.EventKind == "convertPackages")
                            await conversionHistory.AuthenticateAccountConversionAsync(database, tx, selected, hash, transition, balanceId, headRead, ct);
                        else await conversionHistory.AuthenticateAsync(database, tx, selected, hash, ct);
                    }
                }
                if (evidence.TryGetValue(selected.EventKey, out var previous) && previous.Hash != hash) throw ShipmentDispatchRecords.Invalid();
                evidence[selected.EventKey] = (selected, hash);
                BsonDocument? chosen = null;
                if (transition["operation"] == "release") {
                    var decision = await membership.OneAsync(database, tx, StockReleaseBson.Decisions, new BsonDocument("_id", selected.DocumentId), ct) ?? throw ShipmentDispatchRecords.Invalid();
                    var input = decision["input"];
                    var selection = new StockReleaseSelection(input["receiptId"].AsInt64, input["qualityAllocationId"].AsInt64, input["valuationAllocationId"].AsInt64);
                    HeldReceiptOriginProof origin; StockReleaseSourceProof proof;
                    if (conversionHistory is not null) {
                        var retained = await conversionHistory.AuthenticateReleaseOriginAsync(database, tx, selected, hash, selection, ct);
                        origin = retained.Origin; proof = retained.Source;
                    } else if (historicalSources is null) {
                        origin = await releaseSource.CaptureOriginAsync(tx, selection.ReceiptId, ct);
                        proof = await releaseSource.ReadOriginAsync(tx, selection, origin, ct);
                    } else {
                        var retained = await historicalSources.AuthenticateReleaseOriginAsync(database, tx, selected, hash, selection, ct);
                        origin = retained.Origin; proof = retained.Source;
                    }
                    proof.Require(database, tx); origins.Add(origin);
                    chosen = proof.ValuationDecision["accounting"]["inventory"].DeepClone().AsBsonDocument;
                    if (conversionHistory is not null && released is not null)
                        released.Add(AuthenticatedRelease.Create(this, conversionHistory, tx, selected, hash, proof, origin));
                } else if (destination && transition["operation"] == "convert") {
                    chosen = await InventoryAccountAsync(tx, transition["availableId"].AsInt64, transition["availableBefore"]["revision"].AsInt64, path, origins, evidence, membership, ct, conversionHistory, released, headRead);
                }
                if (chosen is not null) {
                    if (account is not null && account != chosen) throw ShipmentDispatchAdmissionPolicy.Invalid();
                    account = chosen;
                }
            }
            return account ?? throw ShipmentDispatchAdmissionPolicy.Invalid();
        } finally { path.Remove((balanceId, revision)); }
    }
}
