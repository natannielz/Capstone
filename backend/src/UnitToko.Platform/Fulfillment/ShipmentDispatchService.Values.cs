using System.Collections.Concurrent;
using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentDispatchService
{
    private readonly ConcurrentDictionary<IClientSessionHandle, ValueContext> valueCommands = new();
    internal sealed class ValueCapture(ShipmentDispatchService owner, IClientSessionHandle tx,
        ImmutableArray<ShipmentDispatchFiniteSource.Capture> lots, ShipmentDispatchAccounting.Binding accounting, BsonDocument snapshot)
    {
        private readonly long number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        private readonly string seal = UnitToko.Platform.Catalog.CatalogRepository.Digest(snapshot);
        internal ImmutableArray<ShipmentDispatchFiniteSource.Capture> Lots => lots;
        internal ShipmentDispatchAccounting.Binding Accounting => accounting;
        internal void Require(ShipmentDispatchService current, IClientSessionHandle session, BsonDocument expected) {
            if (!ReferenceEquals(owner, current) || !ReferenceEquals(tx, session) || !session.IsInTransaction ||
                session.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || UnitToko.Platform.Catalog.CatalogRepository.Digest(expected) != seal)
                throw ShipmentDispatchRecords.Invalid();
        }
        internal async Task RequireOriginAsync(IMongoDatabase database, IClientSessionHandle session, CancellationToken ct) {
            foreach (var lot in lots) await lot.RequireOriginAsync(database, session, ct);
            await owner.dispatchAccounting.RequireCurrentAsync(session, accounting, ct);
        }
        internal async Task RequireBeforeAsync(IMongoDatabase database, IClientSessionHandle session, CancellationToken ct) {
            foreach (var lot in lots) await lot.RequireBeforeAsync(database, session, ct);
            await owner.dispatchAccounting.RequireCurrentAsync(session, accounting, ct);
        }
        internal Task<ShipmentDispatchAccounting.Binding> RequireAccountingAsync(IClientSessionHandle session, CancellationToken ct) =>
            owner.dispatchAccounting.RequireCurrentAsync(session, accounting, ct);
    }
    private async Task<ShipmentDispatchAdmission> BindValueAsync(ActorContext actor, IClientSessionHandle tx, OrderDispatchSource source,
        ImmutableArray<ShipmentLotSelection> selected, ShipmentDispatchAdmission admission, CancellationToken ct) {
        // Legacy unvalued fixture stock retains its existing closed admission route.
        // Any real finite head requires the genuine owner, never descriptive cost.
        var ids = selected.Select(chosen => source.Reservations.Single(row => row.Fact.ReservationId == chosen.ReservationId).Allocation.Change.Id).ToArray();
        var linked = await Rows(LotValueStore.AvailableHeads).Find(tx, new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids))), new BsonDocument("balanceId", new BsonDocument("$in", new BsonArray(ids))) })).AnyAsync(ct) ||
            await Rows(LotValueStore.Transitions).Find(tx, new BsonDocument("$or", new BsonArray {
                new BsonDocument("availableId", new BsonDocument("$in", new BsonArray(ids))), new BsonDocument("destinationId", new BsonDocument("$in", new BsonArray(ids))) })).AnyAsync(ct);
        if (!linked) return admission with { FiniteValue = null, CapturedValue = null };
        if (ids.Distinct().Count() != ids.Length) throw ShipmentDispatchRecords.Invalid();
        var captures = ImmutableArray.CreateBuilder<ShipmentDispatchFiniteSource.Capture>(); var reviewed = new BsonArray(); var total = new Money(0m);
        OperationalBookKey? key = null; BsonDocument? account = null;
        foreach (var chosen in selected) {
            var allocation = source.Reservations.Single(row => row.Fact.ReservationId == chosen.ReservationId).Allocation;
            var physical = await new InventoryRepository(database).ReadAsync(tx, allocation.Change.Id, ct);
            if (physical.PhysicalPoolId != allocation.Pool.Id || physical.Balance.Key != allocation.Change.Key || physical.Balance.Revision != allocation.Change.ExpectedRevision)
                throw ShipmentDispatchRecords.Invalid();
            var capture = admissionReader is ApprovedShipmentDispatchAdmissionReader approved
                ? approved.RetainedFiniteCapture(this, database, tx, actor, source, selected, admission, chosen, physical)
                : await finiteSources.ReadAsync(tx, physical, ct);
            captures.Add(capture);
            var location = physical.Balance.Key.Location; var book = new OperationalBookKey(location.OrganizationId.Value, location.ShopId.Value, location.ChannelId.Value, "IDR");
            if (key is not null && key != book || account is not null && account != capture.InventoryAccount) throw ShipmentDispatchAdmissionPolicy.Invalid();
            key = book; account = capture.InventoryAccount;
            var take = LotValuePolicy.TakeProportionalFloor(capture.Value, capture.Value.Revision, chosen.Packages); total = Money.Add(total, take.TransferredValue);
            reviewed.Add(new BsonDocument { { "reservationId", chosen.ReservationId.Value }, { "balanceId", physical.Id },
                { "physicalBefore", InventoryBson.Balance(physical) },
                { "policy", LotValuePolicy.ProportionalFloorPolicy }, { "before", ShipmentDispatchValueRecords.State(capture.Value) },
                { "after", ShipmentDispatchValueRecords.State(take.Remaining) }, { "transferredValue", take.TransferredValue.ToString() } });
        }
        var accounting = await dispatchAccounting.ReadAndFenceAsync(tx, key!, AccountingCalendar.BusinessDate(clock.UtcNow), account, ct);
        var finite = new BsonDocument { { "lots", reviewed }, { "total", total.ToString() }, { "accounting", accounting.Snapshot } };
        return admission with { FiniteValue = finite, CapturedValue = new(this, tx, captures.ToImmutable(), accounting, finite) };
    }
    private sealed class ValueContext(ShipmentDispatchService owner, ActorContext actor, IClientSessionHandle tx, BsonDocument row,
        ShipmentDispatchAdmission admission, ValueCapture capture, Func<CancellationToken, Task> finalAuthority)
    {
        internal ActorContext Actor => actor;
        internal IClientSessionHandle Tx => tx;
        internal BsonDocument Row { get; set; } = row.DeepClone().AsBsonDocument;
        internal ValueCapture Capture => capture;
        internal Func<CancellationToken, Task> FinalAuthority => finalAuthority;
        private readonly long number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal void Require(ShipmentDispatchService current) {
            new InventoryRepository(current.database).RequireSession(tx);
            if (!ReferenceEquals(owner, current) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number ||
                Row["dispatchedBy"] != actor.UserId || Row["actorGrantVersion"] != actor.GrantVersion || Row["causeHash"] != B.CauseHash(Row)) throw ShipmentDispatchRecords.Invalid();
            capture.Require(current, tx, admission.FiniteValue!); ShipmentDispatchAdmissionPolicy.RequireCurrent(admission, owner.clock.UtcNow);
        }
    }
    private ValueContext BeginValue(ActorContext actor, IClientSessionHandle tx, BsonDocument row, ShipmentDispatchAdmission admission,
        ValueCapture capture, Func<CancellationToken, Task> finalAuthority) {
        var context = new ValueContext(this, actor, tx, row, admission, capture, finalAuthority); context.Require(this);
        if (!valueCommands.TryAdd(tx, context)) throw ShipmentDispatchRecords.Invalid(); return context;
    }
    private void EndValue(ValueContext context) => valueCommands.TryRemove(new KeyValuePair<IClientSessionHandle, ValueContext>(context.Tx, context));
    internal static bool IsLedgerDispatchSource(IInventorySourceReader reader) => reader is LedgerSource;
    private sealed class LedgerSource(ShipmentDispatchService owner, ValueContext context) : IInventorySourceReader {
        public string Namespace => "fulfillment.shipment";
        public IMongoDatabase Database => owner.database;
        public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long revision, CancellationToken ct) {
            context.Require(owner);
            if (!ReferenceEquals(tx, context.Tx) || actor != context.Actor || context.Row["phase"] != "preparing" || source.DocumentId != context.Row["_id"].AsInt64)
                throw ShipmentDispatchRecords.Invalid();
            return await new ShipmentDispatchStockSource(owner.database, owner.orders, context.FinalAuthority).ReadAndFenceAsync(tx, actor, source, revision, ct);
        }
    }
    internal static InventorySource ValueSource(long shipmentId, long balanceId) => new("fulfillment.shipment", shipmentId, "consumeLot", balanceId);
    private async Task SaveValueRowAsync(ValueContext context, BsonDocument updated, CancellationToken ct) {
        context.Require(this);
        if ((await Rows(B.Collection).ReplaceOneAsync(context.Tx, context.Row, updated, cancellationToken: ct)).ModifiedCount != 1) throw ShipmentDispatchRecords.Invalid();
        context.Row = updated; context.Require(this);
    }
    private async Task RecordMovementAsync(ValueContext context, InventoryMovementReceipt movement, long pool, CancellationToken ct) {
        var next = context.Row.DeepClone().AsBsonDocument;
        foreach (var effect in next["valueEffects"].AsBsonArray.Where(value => value["before"]["physicalPoolId"] == pool)) effect["movementId"] = movement.Id;
        await SaveValueRowAsync(context, next, ct);
    }
    internal void RequireValueAuthority(IMongoDatabase current, IClientSessionHandle tx, long shipmentId, long balanceId, string hash) {
        if (!ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace) || !valueCommands.TryGetValue(tx, out var context))
            throw ShipmentDispatchRecords.Invalid();
        context.Require(this);
        if (context.Row["_id"] != shipmentId || context.Row["phase"] != "preparing" || context.Row["causeHash"] != hash ||
            context.Row["valueEffects"].AsBsonArray.Count(effect => effect["balanceId"] == balanceId) != 1) throw ShipmentDispatchRecords.Invalid();
    }
    internal async Task<BsonDocument> ValueAuthorityAsync(IMongoDatabase current, IClientSessionHandle tx, long shipmentId, long balanceId, CancellationToken ct) {
        if (!valueCommands.TryGetValue(tx, out var context)) throw ShipmentDispatchRecords.Invalid();
        RequireValueAuthority(current, tx, shipmentId, balanceId, context.Row["causeHash"].AsString);
        if (await Rows(B.Collection).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw ShipmentDispatchRecords.Invalid();
        var effect = context.Row["valueEffects"].AsBsonArray.Single(value => value["balanceId"] == balanceId).AsBsonDocument;
        var movement = await Rows("movements").Find(tx, new BsonDocument("_id", effect["movementId"])).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
        ShipmentDispatchValueRecords.RequireMovement(context.Row, effect, movement);
        if (await new InventoryRepository(database).ReadAsync(tx, balanceId, ct) != InventoryBson.ReadBalance(effect["after"].AsBsonDocument)) throw ShipmentDispatchRecords.Invalid();
        await context.FinalAuthority(ct);
        return new() { { "before", effect["before"] }, { "after", effect["after"] }, { "sourceHash", context.Row["causeHash"] },
            { "recordedAt", context.Row["recordedAt"] }, { "policy", LotValuePolicy.ProportionalFloorPolicy },
            { "expectedLotRevision", effect["valueBefore"]["revision"] }, { "packages", effect["packages"] } };
    }
    internal Task AuthenticateValueSourceAsync(IMongoDatabase current, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) {
        if (!ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw ShipmentDispatchRecords.Invalid();
        return source.Namespace == "fulfillment.shipment" && source.EventKind == "consumeLot"
            ? ShipmentDispatchValueRecords.AuthenticateAsync(database, clock, tx, source, hash, ct)
            : finiteSources.AuthenticateSourceAsync(current, tx, source, hash, ct);
    }
    internal async Task AuthenticateValueSourceAsync(IMongoDatabase current, IClientSessionHandle tx,
        long shipmentId, long balanceId, string consumptionHash, InventorySource source, string hash, CancellationToken ct) {
        RequireValueAuthority(current, tx, shipmentId, balanceId, consumptionHash);
        var context = valueCommands[tx];
        var effect = context.Row["valueEffects"].AsBsonArray.Single(value => value["balanceId"] == balanceId);
        var capture = context.Capture.Lots.SingleOrDefault(lot => lot.Before.Id == balanceId) ?? throw ShipmentDispatchRecords.Invalid();
        capture.Require(current, tx);
        if (effect["before"] != InventoryBson.Balance(capture.Before) || effect["valueBefore"] != ShipmentDispatchValueRecords.State(capture.Value) ||
            await Rows(B.Collection).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw ShipmentDispatchRecords.Invalid();
        await capture.AuthenticateCapturedSourceAsync(current, tx, source, hash, ct);
        RequireValueAuthority(current, tx, shipmentId, balanceId, consumptionHash);
        if (!valueCommands.TryGetValue(tx, out var stillCurrent) || !ReferenceEquals(context, stillCurrent) ||
            await Rows(B.Collection).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw ShipmentDispatchRecords.Invalid();
        capture.Require(current, tx); context.Require(this); ct.ThrowIfCancellationRequested();
    }
    internal static Task RequireValueSourceAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, IClientSessionHandle tx,
        InventorySource source, string hash, CancellationToken ct) => ShipmentDispatchValueRecords.AuthenticateAsync(database, clock, tx, source, hash, ct);
    internal async Task AuthenticateValueHeadAsync(IMongoDatabase current, IClientSessionHandle tx,
        long shipmentId, long balanceId, string consumptionHash, BsonDocument head, IReadOnlyList<BsonDocument> history, CancellationToken ct) {
        RequireValueAuthority(current, tx, shipmentId, balanceId, consumptionHash);
        var context = valueCommands[tx];
        var effect = context.Row["valueEffects"].AsBsonArray.Single(value => value["balanceId"] == balanceId);
        var capture = context.Capture.Lots.SingleOrDefault(lot => lot.Before.Id == balanceId) ?? throw ShipmentDispatchRecords.Invalid();
        capture.Require(current, tx);
        if (effect["before"] != InventoryBson.Balance(capture.Before) || effect["valueBefore"] != ShipmentDispatchValueRecords.State(capture.Value) ||
            await Rows(B.Collection).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw ShipmentDispatchRecords.Invalid();
        await capture.AuthenticateCapturedHeadAsync(current, tx, head, history, ct);
        RequireValueAuthority(current, tx, shipmentId, balanceId, consumptionHash);
        if (!valueCommands.TryGetValue(tx, out var stillCurrent) || !ReferenceEquals(context, stillCurrent) ||
            await Rows(B.Collection).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw ShipmentDispatchRecords.Invalid();
        capture.Require(current, tx); context.Require(this); ct.ThrowIfCancellationRequested();
    }
    internal static Task RequireValueSourceAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, IClientSessionHandle tx,
        InventorySource source, string hash, CancellationToken ct, ShipmentDispatchValueRecords.HistoricalRead? history) =>
        ShipmentDispatchValueRecords.AuthenticateAsync(database, clock, tx, source, hash, ct, history);
    private async Task ConsumeValuesAsync(ValueContext context, AuthorizationCommandMetadata metadata, CancellationToken ct) {
        foreach (var captured in context.Capture.Lots) {
            var proof = await LotValueConsumptionProof.CreateAsync(this, database, context.Tx, context.Row["_id"].AsInt64, captured.Before.Id, ct);
            var result = await new LotValueStore(database, ids, clock).TakeAsync(context.Tx, proof, ct);
            var next = context.Row.DeepClone().AsBsonDocument;
            var effect = next["valueEffects"].AsBsonArray.Single(value => value["balanceId"] == captured.Before.Id);
            if (effect["transferredValue"] != result.TransferredValue.ToString() || effect["valueAfter"]["revision"] != result.LotRevision) throw ShipmentDispatchRecords.Invalid();
            effect["transitionId"] = result.TransitionId; effect["transitionHash"] = result.TransitionHash;
            await SaveValueRowAsync(context, next, ct);
        }
        var amount = Money.Parse(context.Row["admission"]["finiteValue"]["total"].AsString);
        var completed = context.Row.DeepClone().AsBsonDocument;
        if (amount.Value > 0) {
            var receipt = await new PostJournal(database, clock, [new PostingSource(this, context)]).PostAsync(context.Tx, context.Actor,
                PostingSource.Source(context.Row["_id"].AsInt64), 1, new(metadata.Reason, metadata.CorrelationId), ct);
            completed = context.Row.DeepClone().AsBsonDocument; completed["journalId"] = receipt.Id; completed["journalFingerprint"] = receipt.Fingerprint;
        } else completed["postingFence"] = 1L;
        completed["phase"] = "committed"; await SaveValueRowAsync(context, completed, ct);
        await RequireValueOutcomesAsync(context, ct);
    }
    private async Task RequireFinishedValueAsync(ValueContext context, CancellationToken ct) {
        await RequireValueOutcomesAsync(context, ct);
        await ShipmentDispatchValueRecords.VerifyLiveAsync(this, database, clock, context.Tx, context.Row, ct);
    }
    private async Task RequireValueOutcomesAsync(ValueContext context, CancellationToken ct) {
        context.Require(this);
        foreach (var captured in context.Capture.Lots) {
            var effect = context.Row["valueEffects"].AsBsonArray.Single(value => value["balanceId"] == captured.Before.Id);
            var transition = await Rows(LotValueStore.Transitions).Find(context.Tx, new BsonDocument("_id", effect["transitionId"])).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
            var physical = await new InventoryRepository(database).ReadAsync(context.Tx, captured.Before.Id, ct);
            var head = await Rows(LotValueStore.AvailableHeads).Find(context.Tx, new BsonDocument { { "_id", captured.Before.Id },
                { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.AvailableHeads) } } }).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
            ShipmentDispatchValueRecords.RequireHead(effect.AsBsonDocument, physical, head, transition);
        }
        // The mandatory after-receipt owner callback subsequently authenticates
        // every origin, exact consumed history, acquisition account and current book.
        context.Require(this); ct.ThrowIfCancellationRequested();
    }
    internal async Task<ValueCapture> RequireLiveValueCaptureAsync(IMongoDatabase current, IClock expectedClock,
        IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        if (!ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace) || !ReferenceEquals(clock, expectedClock) ||
            !valueCommands.TryGetValue(tx, out var context)) throw ShipmentDispatchRecords.Invalid();
        context.Require(this);
        if (context.Row != row || context.Row["phase"] != "committed" || context.Row["postingFence"] != 1L ||
            await Rows(B.Collection).Find(tx, context.Row).SingleOrDefaultAsync(ct) is null) throw ShipmentDispatchRecords.Invalid();
        foreach (var capture in context.Capture.Lots) capture.Require(database, tx);
        context.Require(this); ct.ThrowIfCancellationRequested(); return context.Capture;
    }
    internal LiveValueRead LiveRead(IClientSessionHandle tx, long shipmentId) => LiveValueRead.Create(this, tx, shipmentId);
    internal sealed class LiveValueRead
    {
        private readonly ShipmentDispatchService owner;
        private readonly ValueContext context;
        private readonly BsonDocument row;
        private LiveValueRead(ShipmentDispatchService owner, ValueContext context) {
            this.owner = owner; this.context = context; row = context.Row.DeepClone().AsBsonDocument;
        }
        internal static LiveValueRead Create(ShipmentDispatchService owner, IClientSessionHandle tx, long shipmentId) {
            if (!owner.valueCommands.TryGetValue(tx, out var context)) throw ShipmentDispatchRecords.Invalid();
            context.Require(owner);
            if (context.Row["_id"] != shipmentId || context.Row["phase"] != "committed" || context.Row["postingFence"] != 1L) throw ShipmentDispatchRecords.Invalid();
            return new(owner, context);
        }
        internal void Require(IMongoDatabase database, IClock expectedClock, IClientSessionHandle tx) {
            if (!ReferenceEquals(owner.database.Client, database.Client) || !owner.database.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
                !ReferenceEquals(owner.clock, expectedClock) || !ReferenceEquals(context.Tx, tx) ||
                !owner.valueCommands.TryGetValue(tx, out var current) || !ReferenceEquals(current, context)) throw ShipmentDispatchRecords.Invalid();
            context.Require(owner);
            if (context.Row != row) throw ShipmentDispatchRecords.Invalid();
            foreach (var capture in context.Capture.Lots) capture.Require(database, tx);
        }
        internal void RequirePublicationSource(IMongoDatabase database, IClock expectedClock, IClientSessionHandle tx, OrderStockSourceEvent source) {
            Require(database, expectedClock, tx);
            if (source.Kind != "shipment" || source.Id != row["_id"].AsInt64 || source.Record != row) throw ShipmentDispatchRecords.Invalid();
        }
        internal async Task<bool> VerifyAsync(IMongoDatabase database, IClock expectedClock, IClientSessionHandle tx, BsonDocument selected,
            CancellationToken ct, ShipmentDispatchValueRecords.HistoricalRead? history = null) {
            Require(database, expectedClock, tx);
            if (selected["_id"] != row["_id"]) return false;
            if (selected != row) throw ShipmentDispatchRecords.Invalid();
            await ShipmentDispatchValueRecords.VerifyLiveAsync(owner, database, expectedClock, tx, selected, ct, history);
            Require(database, expectedClock, tx); ct.ThrowIfCancellationRequested(); return true;
        }
    }
    private sealed class PostingSource(ShipmentDispatchService owner, ValueContext context) : IAccountingSourceReader {
        public string Namespace => "fulfillment.shipment";
        public IMongoDatabase Database => owner.database;
        internal static JournalSource Source(long id) => new("fulfillment.shipment", id, "dispatchCogs", 1);
        public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long revision, CancellationToken ct) {
            context.Require(owner);
            if (!ReferenceEquals(tx, context.Tx) || actor != context.Actor || revision != 1 || source != Source(context.Row["_id"].AsInt64) ||
                context.Row["phase"] != "preparing" || context.Row["postingFence"] != 0L || !context.Row["journalId"].IsBsonNull ||
                context.Row["valueEffects"].AsBsonArray.Any(effect => effect["transitionId"].IsBsonNull)) throw ShipmentDispatchRecords.Invalid();
            await owner.dispatchAccounting.RequireCurrentAsync(tx, context.Capture.Accounting, ct);
            var updated = context.Row.DeepClone().AsBsonDocument; updated["postingFence"] = 1L; await owner.SaveValueRowAsync(context, updated, ct);
            return ShipmentDispatchValueRecords.Posting(context.Row);
        }
        public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long revision, CancellationToken ct) =>
            throw ShipmentDispatchRecords.Invalid();
    }
}
