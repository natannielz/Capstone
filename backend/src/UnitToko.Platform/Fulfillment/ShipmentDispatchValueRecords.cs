using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Accepted immutable dispatch effects only. Historical authentication
/// follows prior value links, never the current physical/value chain.</summary>
internal static class ShipmentDispatchValueRecords
{
    internal sealed class HistoricalRead(IMongoDatabase database, IClock clock, IClientSessionHandle tx) {
        private readonly long number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        private readonly DateTimeOffset at = clock.UtcNow;
        private readonly HashSet<long> active = [];
        private ShipmentDispatchFiniteSource? sources;
        internal void BindFiniteSources(ShipmentDispatchFiniteSource owner, PackConversionService.FiniteHistoryRead finiteHistory,
            IMongoDatabase current, IClock expectedClock, IClientSessionHandle session) {
            Require(current, session);
            if (!ReferenceEquals(clock, expectedClock) || sources is not null || active.Count != 0 ||
                !ReferenceEquals(finiteHistory.ValueHistory, this)) throw ShipmentDispatchRecords.Invalid();
            owner.RequireFiniteSources(current, expectedClock, session, this, finiteHistory);
            var client = database.Client as MongoClient ?? throw ShipmentDispatchRecords.Invalid();
            sources = ShipmentDispatchFiniteSource.ForHistory(database, new(client), new(database), clock, tx, this, owner, finiteHistory);
            Require(current, session);
        }
        internal ShipmentDispatchFiniteSource Sources(IMongoDatabase current, IClock expectedClock, IClientSessionHandle session) {
            Require(current, session);
            if (!ReferenceEquals(clock, expectedClock)) throw ShipmentDispatchRecords.Invalid();
            var client = database.Client as MongoClient ?? throw ShipmentDispatchRecords.Invalid();
            return sources ??= ShipmentDispatchFiniteSource.ForHistory(database, new(client), new(database), clock, tx, this);
        }
        internal void Require(IMongoDatabase current, IClientSessionHandle session) {
            new InventoryRepository(current).RequireSession(session);
            if (!ReferenceEquals(database.Client, current.Client) || !database.DatabaseNamespace.Equals(current.DatabaseNamespace) ||
                !ReferenceEquals(tx, session) || session.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < at)
                throw ShipmentDispatchRecords.Invalid();
            if (at == default || at.Offset != TimeSpan.Zero || clock.UtcNow.Offset != TimeSpan.Zero) throw ShipmentDispatchRecords.Invalid();
        }
        internal void Enter(long id) {
            Require(database, tx);
            if (active.Count >= LotValueStore.MaximumTransitions || !active.Add(id)) throw ShipmentDispatchRecords.Invalid();
        }
        internal void Leave(long id) => active.Remove(id);
        internal Task AuthenticateAsync(IMongoDatabase current, IClientSessionHandle session, InventorySource source, string hash, CancellationToken ct) =>
            AuthenticateAsync(current, session, source, hash, ct, null);
        internal Task AuthenticateAsync(IMongoDatabase current, IClientSessionHandle session, InventorySource source, string hash, CancellationToken ct,
            Action<ShipmentDispatchFiniteSource.AuthenticatedRelease>? releaseAuthenticated,
            PackConversionService.FiniteHistoryRead.HeadRead? headRead = null) {
            Require(current, session); return ShipmentDispatchValueRecords.AuthenticateAsync(database, clock, tx, source, hash, ct, this, releaseAuthenticated, headRead);
        }
    }
    internal static BsonDocument State(LotValueBalance value) => new() { { "remainingPackages", value.RemainingPackages },
        { "remainingValue", value.RemainingValue.ToString() }, { "revision", value.Revision } };
    private static LotValueBalance Balance(BsonValue state, StockBalanceKey key) => new(key,
        state["remainingPackages"].AsInt64, Money.Parse(state["remainingValue"].AsString), state["revision"].AsInt64);
    internal static void RequireReview(BsonDocument finite, ImmutableArray<ShipmentLotSelection> selected, ImmutableArray<OrderDispatchReservation> reservations) {
        if (finite.ElementCount != 3 || !finite.Contains("lots") || !finite.Contains("total") || !finite.Contains("accounting") ||
            finite["lots"].AsBsonArray.Count != selected.Length || finite["lots"].AsBsonArray.Select(lot => lot["reservationId"]).Distinct().Count() != selected.Length)
            throw ShipmentDispatchAdmissionPolicy.Invalid();
        var total = new Money(0m);
        foreach (var chosen in selected) {
            var reservation = reservations.Single(row => row.Fact.ReservationId == chosen.ReservationId);
            var lot = finite["lots"].AsBsonArray.Single(value => value["reservationId"] == chosen.ReservationId.Value);
            if (lot["balanceId"] != reservation.Allocation.Change.Id || lot["policy"] != LotValuePolicy.ProportionalFloorPolicy) throw ShipmentDispatchAdmissionPolicy.Invalid();
            var physical = InventoryBson.ReadBalance(lot["physicalBefore"].AsBsonDocument);
            if (physical.Id != reservation.Allocation.Change.Id || physical.PhysicalPoolId != reservation.Allocation.Pool.Id ||
                physical.Balance.Key != reservation.Allocation.Change.Key || physical.Balance.Revision != reservation.Allocation.Change.ExpectedRevision)
                throw ShipmentDispatchAdmissionPolicy.Invalid();
            var before = Balance(lot["before"], reservation.Allocation.Change.Key);
            var take = LotValuePolicy.TakeProportionalFloor(before, before.Revision, chosen.Packages);
            if (lot["after"] != State(take.Remaining) || lot["transferredValue"] != take.TransferredValue.ToString()) throw ShipmentDispatchAdmissionPolicy.Invalid();
            total = Money.Add(total, take.TransferredValue);
        }
        if (finite["total"] != total.ToString()) throw ShipmentDispatchAdmissionPolicy.Invalid();
    }
    internal static BsonArray Effects(BsonArray effects, BsonDocument finite, ImmutableArray<ShipmentDispatchFiniteSource.Capture> captures) => new(effects.Select(raw => {
        var allocation = OrderSubmissionBson.Allocation(raw["allocation"].AsBsonDocument);
        var captured = captures.Single(value => value.Before.Id == allocation.Change.Id);
        var review = finite["lots"].AsBsonArray.Single(value => value["reservationId"] == raw["reservationId"]);
        return new BsonDocument { { "reservationId", raw["reservationId"] }, { "balanceId", captured.Before.Id }, { "packages", allocation.Change.Packages },
            { "before", InventoryBson.Balance(captured.Before) }, { "after", InventoryBson.Balance(captured.Before with {
                Balance = StockBalancePolicy.Dispatch(captured.Before.Balance, captured.Before.Balance.Revision, allocation.Change.Packages) }) },
            { "valueBefore", review["before"].DeepClone() }, { "valueAfter", review["after"].DeepClone() }, { "transferredValue", review["transferredValue"] },
            { "movementId", BsonNull.Value }, { "transitionId", BsonNull.Value }, { "transitionHash", BsonNull.Value } };
    }));
    internal static void RequireMovement(BsonDocument row, BsonDocument effect, BsonDocument movement) {
        var before = InventoryBson.ReadBalance(effect["before"].AsBsonDocument); var after = InventoryBson.ReadBalance(effect["after"].AsBsonDocument);
        var source = new InventorySource("fulfillment.shipment", row["_id"].AsInt64, "dispatch", before.PhysicalPoolId);
        var allocations = row["stockEffects"].AsBsonArray.Where(value => value["allocation"]["poolId"] == before.PhysicalPoolId).ToArray();
        var stock = row["stockEffects"].AsBsonArray.Single(value => value["reservationId"] == effect["reservationId"]);
        var allocation = OrderSubmissionBson.Allocation(stock["allocation"].AsBsonDocument);
        if (movement["_id"] != effect["movementId"] || movement["sourceEvent"] != source.EventKey || movement["source"]["namespace"] != source.Namespace ||
            movement["source"]["documentId"] != source.DocumentId || movement["source"]["eventKind"] != source.EventKind || movement["source"]["occurrence"] != source.Occurrence ||
            movement["sourceRevision"] != 1L || movement["physicalPoolId"] != source.Occurrence || movement["poolRevision"] != allocation.Pool.Revision ||
            movement["actorId"] != row["dispatchedBy"] || movement["actorGrantVersion"] != row["actorGrantVersion"] || movement["reason"] != row["reason"] ||
            movement["correlationId"] != row["correlationId"] || movement["recordedAt"].CompareTo(row["recordedAt"]) < 0 || movement["legs"].AsBsonArray.Count != allocations.Length ||
            before.Id != effect["balanceId"] || before.Id != allocation.Change.Id || before.PhysicalPoolId != allocation.Pool.Id || before.Balance.Key != allocation.Change.Key ||
            before.Balance.Revision != allocation.Change.ExpectedRevision || effect["packages"] != allocation.Change.Packages ||
            after != (before with { Balance = StockBalancePolicy.Dispatch(before.Balance, before.Balance.Revision, allocation.Change.Packages) })) throw ShipmentDispatchRecords.Invalid();
        var leg = movement["legs"].AsBsonArray.Single(value => value["balanceId"] == before.Id);
        if (leg["action"] != "Dispatch" || leg["packages"] != effect["packages"] || leg["before"] != effect["before"] || leg["after"] != effect["after"])
            throw ShipmentDispatchRecords.Invalid();
    }
    internal static void RequireHead(BsonDocument effect, InventoryBalanceDocument physical, BsonDocument head, BsonDocument transition) {
        if (physical != InventoryBson.ReadBalance(effect["after"].AsBsonDocument) || head["_id"] != physical.Id || head["balanceId"] != physical.Id ||
            head["poolId"] != physical.PhysicalPoolId || head["key"] != LotValueStore.Key(physical.Balance.Key) ||
            head["remainingPackages"] != effect["valueAfter"]["remainingPackages"] || head["remainingValue"] != effect["valueAfter"]["remainingValue"] ||
            head["revision"] != effect["valueAfter"]["revision"] || head["lastTransitionId"] != effect["transitionId"] || head["lastHash"] != effect["transitionHash"] ||
            head["updatedAt"] != transition["recordedAt"] || head["fence"].AsInt64 < head["revision"].AsInt64) throw ShipmentDispatchRecords.Invalid();
    }
    internal static SourceSnapshot Posting(BsonDocument row) {
        var finite = row["admission"]["finiteValue"]; var accounting = finite["accounting"]; var key = AccountingBson.ReadKey(accounting["key"]);
        long? Dimension(string name) => row["scope"].AsBsonDocument.GetValue(name, BsonNull.Value).IsBsonNull ? null : row["scope"][name].AsInt64;
        var amount = Money.Parse(finite["total"].AsString); var zero = new Money(0m);
        return new(new("fulfillment.shipment", row["_id"].AsInt64, "dispatchCogs", 1), 1, key,
            new(key.OrganizationId, key.ShopId, key.ChannelId, Dimension("warehouseId"), Dimension("buyingOrganizationId"), Dimension("branchId")),
            DateOnly.ParseExact(accounting["postingDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            [new(accounting["cogs"]["mappingKey"].AsString, amount, zero), new(accounting["inventory"]["mappingKey"].AsString, zero, amount)]);
    }
    // Consumed-lot ancestry proofs run (tests assert that a verified shipment seal replaces them).
    internal static long AncestryProofs;
    internal static Task VerifyAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, BsonDocument row, CancellationToken ct, HistoricalRead? history = null,
        Action<ShipmentDispatchFiniteSource.AuthenticatedRelease>? releaseAuthenticated = null,
        PackConversionService.FiniteHistoryRead.HeadRead? headRead = null) =>
        VerifyCoreAsync(database, clock, tx, row, ct, history, releaseAuthenticated: releaseAuthenticated, headRead: headRead);
    internal static Task VerifyLiveAsync(ShipmentDispatchService owner, IMongoDatabase database, IClock clock, IClientSessionHandle tx,
        BsonDocument row, CancellationToken ct, HistoricalRead? history = null) => VerifyCoreAsync(database, clock, tx, row, ct, history, owner);
    private static async Task VerifyCoreAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, BsonDocument row, CancellationToken ct,
        HistoricalRead? history = null, ShipmentDispatchService? liveOwner = null,
        Action<ShipmentDispatchFiniteSource.AuthenticatedRelease>? releaseAuthenticated = null,
        PackConversionService.FiniteHistoryRead.HeadRead? headRead = null) {
        new InventoryRepository(database).RequireSession(tx);
        if (row["schemaVersion"] != 2 || row["phase"] != "committed" || row["postingFence"] != 1L || row["causeHash"] != B.CauseHash(row)) throw ShipmentDispatchRecords.Invalid();
        var live = liveOwner is null ? null : await liveOwner.RequireLiveValueCaptureAsync(database, clock, tx, row, ct);
        history ??= live is null ? new(database, clock, tx) : live.Lots[0].ValueHistory;
        var contexts = live is null ? new[] { history } : live.Lots.Select(capture => capture.ValueHistory).Append(history).Distinct().ToArray();
        var entered = new List<HistoricalRead>();
        try {
        foreach (var context in contexts) { context.Require(database, tx); context.Enter(row["_id"].AsInt64); entered.Add(context); }
        var sources = live is null ? history.Sources(database, clock, tx) : null;
        // A verified seal stands for the consumed-lot ancestry proven when it was sealed; a present seal that fails refuses.
        // Callers that observe authenticated releases or share head reads keep the full proof.
        var sealedHistory = live is null && releaseAuthenticated is null && headRead is null && await ShipmentDispatchSeals.VerifyAsync(database, tx, row, ct);
        var finite = row["admission"]["finiteValue"]; var effects = row["valueEffects"].AsBsonArray;
        if (effects.Count != row["pickedLots"].AsBsonArray.Count || effects.Select(value => value["balanceId"]).Distinct().Count() != effects.Count ||
            effects.Select(value => value["reservationId"]).Distinct().Count() != effects.Count) throw ShipmentDispatchRecords.Invalid();
        var total = new Money(0m);
        foreach (var raw in effects) {
            var effect = raw.AsBsonDocument; var before = InventoryBson.ReadBalance(effect["before"].AsBsonDocument);
            var reviewed = finite["lots"].AsBsonArray.Single(value => value["reservationId"] == effect["reservationId"]);
            var chosen = row["pickedLots"].AsBsonArray.Single(value => value["reservationId"] == effect["reservationId"]);
            if (reviewed["balanceId"] != effect["balanceId"] || reviewed["physicalBefore"] != effect["before"] || chosen["packages"].ToInt64() != effect["packages"].AsInt64 || reviewed["before"] != effect["valueBefore"] ||
                reviewed["after"] != effect["valueAfter"] || reviewed["transferredValue"] != effect["transferredValue"]) throw ShipmentDispatchRecords.Invalid();
            var take = LotValuePolicy.TakeProportionalFloor(Balance(effect["valueBefore"], before.Balance.Key), effect["valueBefore"]["revision"].AsInt64, effect["packages"].AsInt64);
            if (State(take.Remaining) != effect["valueAfter"] || take.TransferredValue.ToString() != effect["transferredValue"] ||
                effect["valueBefore"]["remainingPackages"].AsInt64 != before.Balance.OnHand - before.Balance.Held - before.Balance.Expired ||
                effect["valueAfter"]["remainingPackages"].AsInt64 != before.Balance.OnHand - before.Balance.Held - before.Balance.Expired - effect["packages"].AsInt64)
                throw ShipmentDispatchRecords.Invalid();
            total = Money.Add(total, take.TransferredValue);
            var movement = await database.GetCollection<BsonDocument>("movements").Find(tx, new BsonDocument { { "_id", effect["movementId"] },
                { "$and", new BsonArray { InventoryMovementSchemas.Validator() } } }).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
            RequireMovement(row, effect, movement);
            var source = ShipmentDispatchService.ValueSource(row["_id"].AsInt64, before.Id);
            var transition = await database.GetCollection<BsonDocument>(LotValueStore.Transitions).Find(tx, new BsonDocument { { "_id", effect["transitionId"] },
                { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.Transitions) } } }).SingleOrDefaultAsync(ct) ?? throw ShipmentDispatchRecords.Invalid();
            LotValueStore.RequireTransition(transition);
            if (transition["operation"] != "consume" || transition["sourceEvent"] != source.EventKey || transition["sourceHash"] != row["causeHash"] ||
                transition["source"]["namespace"] != source.Namespace || transition["source"]["documentId"] != source.DocumentId || transition["source"]["eventKind"] != source.EventKind ||
                transition["source"]["occurrence"] != source.Occurrence || transition["availableId"] != before.Id || transition["poolId"] != before.PhysicalPoolId ||
                transition["key"] != LotValueStore.Key(before.Balance.Key) || transition["packages"] != effect["packages"] || transition["transferredValue"] != effect["transferredValue"] ||
                transition["availableBefore"] != effect["valueBefore"] || transition["availableAfter"] != effect["valueAfter"] || transition["recordedAt"] != row["recordedAt"] ||
                transition["policy"] != LotValuePolicy.ProportionalFloorPolicy || transition["transitionHash"] != effect["transitionHash"] || transition["transitionHash"] != LotValueStore.Hash(transition))
                throw ShipmentDispatchRecords.Invalid();
            var link = transition["previousAvailable"];
            if (!link.IsBsonDocument) throw ShipmentDispatchRecords.Invalid();
            var prior = await database.GetCollection<BsonDocument>(LotValueStore.Transitions).Find(tx, new BsonDocument("_id", link["transitionId"])).SingleOrDefaultAsync(ct)
                ?? throw ShipmentDispatchRecords.Invalid();
            LotValueStore.RequireTransition(prior);
            var ownAxis = prior["availableId"] == before.Id;
            if (prior["transitionHash"] != link["transitionHash"] || prior["transitionHash"] != LotValueStore.Hash(prior) || prior["poolId"] != before.PhysicalPoolId ||
                prior["recordedAt"].CompareTo(row["recordedAt"]) > 0 || (ownAxis ? prior["key"] != transition["key"] || prior["availableAfter"] != effect["valueBefore"] :
                    prior["destinationId"] != before.Id || prior["destinationKey"] != transition["key"] || prior["destinationAfter"] != effect["valueBefore"])) throw ShipmentDispatchRecords.Invalid();
            var priorSource = prior["source"];
            var selectedSource = new InventorySource(priorSource["namespace"].AsString, priorSource["documentId"].AsInt64,
                priorSource["eventKind"].AsString, priorSource["occurrence"].AsInt64);
            BsonDocument inventoryAccount;
            if (live is null && sealedHistory) {
                inventoryAccount = finite["accounting"]["inventory"].AsBsonDocument;
            } else if (live is null) {
                Interlocked.Increment(ref AncestryProofs);
                inventoryAccount = await sources!.InventoryAccountForHistoryAsync(tx, before.Id, effect["valueBefore"]["revision"].AsInt64, ct, prior, releaseAuthenticated, headRead);
            } else {
                var capture = live.Lots.SingleOrDefault(value => value.Before == before && value.Value.Revision == effect["valueBefore"]["revision"].AsInt64)
                    ?? throw ShipmentDispatchRecords.Invalid();
                inventoryAccount = await capture.RequireConsumedInventoryAccountAsync(database, tx, before,
                    effect["valueBefore"]["revision"].AsInt64, selectedSource, prior["sourceHash"].AsString, transition, ct);
            }
            if (inventoryAccount != finite["accounting"]["inventory"])
                throw ShipmentDispatchRecords.Invalid();
        }
        if (finite["total"] != total.ToString()) throw ShipmentDispatchRecords.Invalid();
        var accounting = finite["accounting"];
        if (live is not null) {
            var checkedAccounting = (await live.RequireAccountingAsync(tx, ct)).Snapshot;
            if (checkedAccounting != accounting || checkedAccounting["setupApprovedAt"].CompareTo(row["recordedAt"]) > 0 ||
                AccountingCalendar.BusinessDate(new DateTimeOffset(row["recordedAt"].ToUniversalTime())) !=
                    DateOnly.ParseExact(checkedAccounting["postingDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture) ||
                checkedAccounting["periodMonth"] != AccountingCalendar.Month(DateOnly.ParseExact(checkedAccounting["postingDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                throw ShipmentDispatchRecords.Invalid();
        } else {
            var setup = new AccountingBookSetupRecords(database);
            var proposal = await setup.ApprovedAsync(tx, AccountingBson.ReadKey(accounting["key"]), ct) ?? throw ShipmentDispatchRecords.Invalid();
            var decision = await setup.DecisionForAsync(tx, proposal, ct) ?? throw ShipmentDispatchRecords.Invalid();
            if (proposal["_id"] != accounting["setupProposalId"] || proposal["snapshotHash"] != accounting["setupProposalHash"] ||
                decision["_id"] != accounting["setupDecisionId"] || decision["snapshotHash"] != accounting["setupDecisionHash"] ||
                AccountingBookSetupBson.Chart(decision) != accounting["chart"] || decision["recordedAt"].CompareTo(row["recordedAt"]) > 0 ||
                accounting["book"]["_id"] != decision["bookId"] || accounting["book"]["key"] != proposal["key"] || accounting["book"]["chartVersionId"] != decision["chartId"] ||
                AccountingCalendar.BusinessDate(new DateTimeOffset(row["recordedAt"].ToUniversalTime())) != DateOnly.ParseExact(accounting["postingDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture))
                throw ShipmentDispatchRecords.Invalid();
            var period = await database.GetCollection<BsonDocument>("periods").Find(tx, new BsonDocument("_id", accounting["periodId"])).SingleOrDefaultAsync(ct)
                ?? throw ShipmentDispatchRecords.Invalid();
            if (period["bookId"] != decision["bookId"] || period["month"] != accounting["periodMonth"] ||
                accounting["periodMonth"] != AccountingCalendar.Month(DateOnly.ParseExact(accounting["postingDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                throw ShipmentDispatchRecords.Invalid();
        }
        var transitions = await database.GetCollection<BsonDocument>(LotValueStore.Transitions).Find(tx, new BsonDocument("$or", new BsonArray {
            new BsonDocument { { "source.namespace", "fulfillment.shipment" }, { "source.documentId", row["_id"] }, { "source.eventKind", "consumeLot" } },
            new BsonDocument("sourceHash", row["causeHash"]) })).Limit(101).ToListAsync(ct);
        if (transitions.Count != effects.Count || transitions.Any(transition => !effects.Any(effect => effect["transitionId"] == transition["_id"]))) throw ShipmentDispatchRecords.Invalid();
        await VerifyJournalAsync(database, tx, row, total, ct);
        if (live is null && !sealedHistory) await ShipmentDispatchSeals.SealAsync(database, clock, tx, row, ct);
        if (liveOwner is not null && !ReferenceEquals(live, await liveOwner.RequireLiveValueCaptureAsync(database, clock, tx, row, ct))) throw ShipmentDispatchRecords.Invalid();
        } finally { foreach (var context in entered.AsEnumerable().Reverse()) context.Leave(row["_id"].AsInt64); }
    }
    private static async Task VerifyJournalAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument row, Money amount, CancellationToken ct) {
        var accounting = row["admission"]["finiteValue"]["accounting"]; var source = new JournalSource("fulfillment.shipment", row["_id"].AsInt64, "dispatchCogs", 1);
        var journals = await database.GetCollection<BsonDocument>("journalEntries").Find(tx, new BsonDocument { { "source.namespace", source.Namespace },
            { "source.documentId", source.DocumentId }, { "source.eventKind", source.EventKind } }).Limit(2).ToListAsync(ct);
        if (amount.Value == 0) {
            if (journals.Count != 0 || !row["journalId"].IsBsonNull || !row["journalFingerprint"].IsBsonNull) throw ShipmentDispatchRecords.Invalid(); return;
        }
        if (journals.Count != 1) throw ShipmentDispatchRecords.Invalid();
        var journal = journals[0]; var draft = AccountingBson.ReadDraft(journal); var expected = Posting(row);
        var chart = AccountingBson.ReadChart(accounting["chart"].AsBsonDocument); var resolved = JournalDraft.Create(expected, chart);
        if (journal["_id"] != row["journalId"] || journal["fingerprint"] != row["journalFingerprint"] || draft.Source != source || draft.SourceRevision != 1 ||
            draft.Fingerprint != resolved.Fingerprint || draft.Total != amount || journal["bookId"] != accounting["book"]["_id"] ||
            journal["actorId"] != row["dispatchedBy"] || journal["actorGrantVersion"] != row["actorGrantVersion"] || journal["reason"] != row["reason"] || journal["correlationId"] != row["correlationId"])
            throw ShipmentDispatchRecords.Invalid();
        foreach (var line in draft.Lines) {
            var role = line.MappingKey == "inventory" ? "inventory" : "cogs";
            if (line.MappingKey != accounting[role]["mappingKey"] || line.AccountId != accounting[role]["accountId"].AsInt64 || line.AccountCode != accounting[role]["accountCode"])
                throw ShipmentDispatchRecords.Invalid();
        }
        var audit = await database.GetCollection<BsonDocument>("accountingAuditEvents").Find(tx, new BsonDocument { { "journalId", journal["_id"] }, { "kind", "journal.posted" } }).SingleOrDefaultAsync(ct)
            ?? throw ShipmentDispatchRecords.Invalid();
        if (audit["fingerprint"] != draft.Fingerprint || audit["actorId"] != row["dispatchedBy"] || audit["actorGrantVersion"] != row["actorGrantVersion"])
            throw ShipmentDispatchRecords.Invalid();
        var delivery = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx, new BsonDocument("sourceKey", "accounting:journal:" + journal["_id"].AsInt64)).SingleOrDefaultAsync(ct)
            ?? throw ShipmentDispatchRecords.Invalid();
        if (delivery["payload"]["journalId"] != journal["_id"] || delivery["payload"]["auditId"] != audit["_id"]) throw ShipmentDispatchRecords.Invalid();
    }
    internal static async Task AuthenticateAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct,
        HistoricalRead? history = null, Action<ShipmentDispatchFiniteSource.AuthenticatedRelease>? releaseAuthenticated = null,
        PackConversionService.FiniteHistoryRead.HeadRead? headRead = null) {
        if (source.Namespace != "fulfillment.shipment" || source.EventKind != "consumeLot") throw ShipmentDispatchRecords.Invalid();
        history ??= new(database, clock, tx); history.Require(database, tx);
        var actual = await OrderSubmissionService.ReadStockSourceEventAsync(database, tx, "shipment", source.DocumentId, true, ct, history, clock,
            releaseAuthenticated: releaseAuthenticated, headRead: headRead);
        var row = actual.Record;
        if (row["schemaVersion"] != 2 || row["causeHash"] != hash || row["valueEffects"].AsBsonArray.Count(effect => effect["balanceId"] == source.Occurrence) != 1)
            throw ShipmentDispatchRecords.Invalid();
        foreach (var pool in row["stockEffects"].AsBsonArray.Select(effect => effect["allocation"]["poolId"].AsInt64).Distinct()) {
            var membership = await database.GetCollection<BsonDocument>(OrderStockSourceRecords.Transitions).Find(tx, new BsonDocument {
                { "poolId", pool }, { "sourceKind", "shipment" }, { "sourceId", source.DocumentId } }).Limit(2).ToListAsync(ct);
            if (membership.Count != 1 || membership[0]["transitionHash"] != OrderStockSourceRecords.Hash(membership[0]) || membership[0]["sourceHash"] != actual.Hash ||
                membership[0]["sourceAuditId"] != row["auditId"] || membership[0]["effects"] != OrderStockSourceRecords.Effects(actual, pool)) throw ShipmentDispatchRecords.Invalid();
        }
    }
}
