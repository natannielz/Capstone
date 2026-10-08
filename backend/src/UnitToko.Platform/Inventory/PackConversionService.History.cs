using System.Collections.Concurrent;
using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class PackConversionService
{
    // The map carries only a live owning read through the existing held-prefix
    // callback. It is emptied when that read ends, never shared across attempts.
    private readonly ConcurrentDictionary<HeldReceiptOriginProof, ConversionHistoryRead> conversionReads = new();
    private sealed class ConversionHistoryRead(PackConversionService owner, IClientSessionHandle tx, DateTimeOffset at) {
        internal IClientSessionHandle Tx => tx;
        private readonly long number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        private readonly Dictionary<long, CompletedConversionProof> completed = [];
        internal readonly HashSet<HeldReceiptOriginProof> Origins = [];
        internal void Require(PackConversionService current, IClientSessionHandle session) {
            new InventoryRepository(owner.database).RequireSession(session);
            if (!ReferenceEquals(owner, current) || !ReferenceEquals(tx, session) || session.WrappedCoreSession.CurrentTransaction.TransactionNumber != number ||
                owner.Now < at) throw B.Broken();
        }
        internal async Task<BsonDocument?> CompletedAsync(PackConversionService current, long id, CancellationToken ct) {
            Require(current, tx);
            if (!completed.TryGetValue(id, out var proof)) return null;
            // Flatten the already authenticated dependency graph. Each immutable
            // capture still rereads its actual rows and complete query membership;
            // every source/private owner is freshly authenticated below.
            foreach (var dependency in completed.Values) await dependency.RequireAsync(current, tx, ct);
            Require(current, tx); return proof.Row;
        }
        internal void Add(PackConversionService current, CompletedConversionProof proof) {
            Require(current, tx);
            if (completed.Count >= LotValueStore.MaximumTransitions || !completed.TryAdd(proof.Id, proof)) throw B.Broken();
        }
        internal void Bind(PackConversionService current, HeldReceiptOriginProof origin) {
            Require(current, tx); origin.Require(current.database, tx);
            if (current.conversionReads.TryGetValue(origin, out var existing) && !ReferenceEquals(existing, this)) throw B.Broken();
            current.conversionReads[origin] = this; Origins.Add(origin);
        }
        internal void End(PackConversionService current) {
            foreach (var origin in Origins) current.conversionReads.TryRemove(new KeyValuePair<HeldReceiptOriginProof, ConversionHistoryRead>(origin, this));
        }
    }
    private sealed class CompletedConversionProof {
        private readonly PackConversionService owner;
        private readonly IClientSessionHandle tx;
        private readonly long number;
        private readonly DateTimeOffset capturedAt;
        private readonly BsonDocument row;
        private readonly PurchaseOrderSourceReadCapture capture;
        private readonly HeldReceiptOriginProof origin;
        private CompletedConversionProof(PackConversionService owner, IClientSessionHandle tx, BsonDocument row,
            PurchaseOrderSourceReadCapture capture, HeldReceiptOriginProof origin) {
            this.owner = owner; this.tx = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; capturedAt = owner.Now;
            this.row = row.DeepClone().AsBsonDocument; this.capture = capture; this.origin = origin;
        }
        internal long Id => row["_id"].AsInt64;
        internal BsonDocument Row => row.DeepClone().AsBsonDocument;
        internal async Task RequireAsync(PackConversionService current, IClientSessionHandle session, CancellationToken ct) {
            new InventoryRepository(current.database).RequireSession(session);
            if (!ReferenceEquals(owner, current) || !ReferenceEquals(tx, session) || session.WrappedCoreSession.CurrentTransaction.TransactionNumber != number ||
                current.Now < capturedAt || row["snapshotHash"] != B.Hash(row)) throw B.Broken();
            await capture.RequireAsync(current.database, session, ct);
            var approved = await current.rules.RuleAsync(session, row["input"]["ruleId"].AsInt64, ct);
            if (approved.Rule != row["rule"]) throw B.Broken();
            await current.sources.RequirePackConversionSourceHistoryAsync(session, row["poolSource"].AsBsonDocument,
                new DateTimeOffset(row["recordedAt"].ToUniversalTime()), ct);
            await origin.RequireAsync(current.database, session, ct);
            if (current.Now < capturedAt || session.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw B.Broken();
        }
        internal static async Task<CompletedConversionProof> CreateAsync(PackConversionService owner, IClientSessionHandle tx,
            BsonDocument row, PurchaseOrderSourceReadCapture capture, HeldReceiptOriginProof origin, CancellationToken ct) {
            capture.Seal(); var proof = new CompletedConversionProof(owner, tx, row, capture, origin);
            await proof.RequireAsync(owner, tx, ct); return proof;
        }
    }
    private async Task<List<BsonDocument>> CapturedManyAsync(IClientSessionHandle tx, string collection, BsonDocument filter,
        int limit, PurchaseOrderSourceReadCapture? capture, CancellationToken ct) {
        var actual = await Rows(collection).Find(tx, filter).Limit(limit).ToListAsync(ct);
        capture?.Record(collection, filter, actual); return actual;
    }
    private static string HistoricalBinding(BsonDocument row) => CatalogRepository.Digest(new BsonDocument {
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "input", row["input"] },
        { "expected", row["expected"] }, { "rule", row["rule"] }, { "catalog", row["catalog"] }, { "poolSource", BindingSource(row["poolSource"].AsBsonDocument) },
        { "sourceLot", row["sourceLot"] }, { "sourceValue", row["sourceValue"] }, { "quantities", row["quantities"] },
        { "before", row["before"] }, { "after", row["after"] }, { "policy", row["policy"] } });
    private async Task<BsonDocument> ConversionAsync(IClientSessionHandle tx, long id, CancellationToken ct,
        HeldReceiptOriginProof? retainedOrigin = null, ImmutableHashSet<long>? ancestors = null, ConversionHistoryRead? read = null) {
        new InventoryRepository(database).RequireSession(tx);
        ancestors ??= ImmutableHashSet<long>.Empty;
        if (ancestors.Count >= LotValueStore.MaximumTransitions || ancestors.Contains(id)) throw B.Broken();
        var ownsRead = read is null; read ??= new(this, tx, Now); read.Require(this, tx);
        try {
        var completed = await read.CompletedAsync(this, id, ct);
        if (completed is not null) {
            if (retainedOrigin is not null && retainedOrigin.ReceiptId != completed["sourceLot"]["sourceId"].AsInt64) throw B.Broken();
            return completed;
        }
        var path = ancestors.Add(id);
        var capture = new PurchaseOrderSourceReadCapture();
        var row = await capture.OneAsync(database, tx, B.Conversions, new BsonDocument { { "_id", id },
            { "$and", new BsonArray { PackConversionSchemas.Validator() } } }, ct) ?? throw B.Broken();
        if (row["phase"] != "committed" || row["snapshotHash"] != B.Hash(row) || row["causeHash"] != B.CauseHash(row) ||
            row["policy"] != LotValuePolicy.ProportionalFloorPolicy || row["binding"] != HistoricalBinding(row)) throw B.Broken();
        var input = B.Input(row["input"]); var expected = B.Expected(row["expected"]);
        if (row["input"] != B.Input(input) || row["expected"] != B.Expected(expected) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(new { input, expected, binding = row["binding"].AsString }, row["reason"].AsString) ||
            !StockCountBson.Key(row["requestKey"].AsString) || !StockCountPolicy.CanonicalText(row["reason"].AsString, 1000) ||
            !StockCountPolicy.CanonicalText(row["correlationId"].AsString, 128)) throw B.Broken();
        var people = row["people"].AsBsonArray;
        if (people.Count != 1 || people[0]["userId"] != row["actorId"] || people[0]["verification"] != "Verified" ||
            people[0]["personId"].AsInt64 <= 0 || people[0]["version"].AsInt64 <= 0) throw B.Broken();
        var approved = await rules.RuleAsync(tx, input.RuleId, ct);
        var ruleInput = PackConversionRuleBson.Input(approved.Rule["input"].AsBsonDocument);
        if (approved.Rule != row["rule"] || row["catalog"] != approved.Rule["catalog"] || input.RuleVersion != approved.Rule["version"].AsInt64 ||
            input.Location != ruleInput.Location) throw B.Broken();
        var quantities = PackConversionRuleCatalogue.Quantities(ruleInput, row["catalog"].AsBsonDocument, input.RuleId, input.Steps);
        if (row["quantities"] != Quantities(quantities)) throw B.Broken();
        var before = InventoryBson.ReadBalance(row["before"].AsBsonDocument); var after = InventoryBson.ReadBalance(row["after"].AsBsonDocument);
        var start = InventoryBson.ReadBalance(row["destinationBefore"].AsBsonDocument); var end = InventoryBson.ReadBalance(row["destinationAfter"].AsBsonDocument);
        if (before.Id != input.SourceBalanceId || before.Balance.Revision != expected.SourceBalanceRevision || before.Balance.Key.Location.ToScope() != input.Location ||
            before.Balance.Key.SkuId.Value != ruleInput.SourceSkuId || before.Balance.Key.DefinitionId.Value != ruleInput.SourceDefinitionId ||
            after != (before with { Balance = StockBalancePolicy.ConvertOut(before.Balance, before.Balance.Revision, quantities.SourcePackages) }) ||
            start.Id == before.Id || start.PhysicalPoolId != before.PhysicalPoolId || start.Balance.Key.Location != before.Balance.Key.Location ||
            start.Balance.Key.SkuId.Value != ruleInput.DestinationSkuId || start.Balance.Key.DefinitionId.Value != ruleInput.DestinationDefinitionId ||
            start.Balance.OnHand != 0 || start.Balance.Reserved != 0 || start.Balance.Held != 0 || start.Balance.Expired != 0 || start.Balance.Revision != 1 ||
            end != (start with { Balance = StockBalancePolicy.ConvertIn(start.Balance, 1, quantities.DestinationPackages) })) throw B.Broken();
        var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime());
        if (at > Now || row["sourceLot"]["origin"] != "procurementReceipt" || row["poolSource"]["poolId"] != before.PhysicalPoolId ||
            row["poolSource"]["revision"] != expected.PoolRevision || row["poolSource"]["poolFence"] != expected.PoolFence ||
            InventoryGraph.Location(row["poolSource"]["requestedLocation"].AsBsonDocument) != before.Balance.Key.Location) throw B.Broken();
        var receiptId = row["sourceLot"]["sourceId"].AsInt64;
        if (retainedOrigin is not null && retainedOrigin.ReceiptId != receiptId) throw B.Broken();
        var origin = retainedOrigin ?? await HeldReceiptOriginProof.CaptureAsync(database, unit, clock, catalog, orders, sources, tx, receiptId, ct);
        origin.Require(database, tx);
        read.Bind(this, origin);
        if (row["sourceLot"] != InventoryHeldReceiptWriter.Lot(origin.Receipt) || before.Id != origin.Receipt["balanceId"].AsInt64 ||
            before.PhysicalPoolId != origin.Receipt["poolId"].AsInt64 || before.Balance.Key != InventoryHeldReceiptWriter.After(origin.Receipt).Balance.Key) throw B.Broken();
        var sourceFacts = InventoryGraph.ReadLot(row["sourceLot"].AsBsonDocument);
        if (sourceFacts.RecordedAt > at.ToUnixTimeMilliseconds() || sourceFacts.ExpiresOn is { } expiry && expiry <= DateOnly.FromDateTime(at.UtcDateTime)) throw B.Broken();
        // Capture the full reverse membership before the semantic prefix read.
        // Publication below rereads it, so changes during reconstruction refuse.
        await CaptureSourceMembershipAsync(tx, origin, capture, ct);
        await new HeldReceiptMovementHistory(database, unit, clock).RequireBeforeAsync(tx, origin, before, at, path, ct, AuthenticateCapturedMovementAsync);
        await sources.RequirePackConversionSourceHistoryAsync(tx, row["poolSource"].AsBsonDocument, at, ct);
        if (row["destinationLot"] != DestinationLot(id, start.Balance.Key.LotId.Value, ruleInput, row["sourceLot"].AsBsonDocument, at)) throw B.Broken();
        var lots = await CapturedManyAsync(tx, "lots", new BsonDocument { { "origin", "packageConversion" }, { "sourceId", id } }, 2, capture, ct);
        if (lots.Count != 1 || lots[0] != row["destinationLot"]) throw B.Broken();
        var balances = await CapturedManyAsync(tx, "stockBalances", new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", start.Id), new BsonDocument("lotId", start.Balance.Key.LotId.Value) }), 2, capture, ct);
        if (balances.Count != 1) throw B.Broken();
        var actualDestination = InventoryBson.ReadBalance(balances[0]);
        if (actualDestination.Id != start.Id || actualDestination.PhysicalPoolId != start.PhysicalPoolId || actualDestination.Balance.Key != start.Balance.Key ||
            actualDestination.Balance.Revision < end.Balance.Revision) throw B.Broken();
        await PhysicalMovementAsync(tx, row, ct, capture);
        await ValueTransitionAsync(tx, row, path, read, capture, ct);
        await EffectsProofAsync(tx, row, ct, capture);
        await origin.RequireAsync(database, tx, ct);
        read.Add(this, await CompletedConversionProof.CreateAsync(this, tx, row, capture, origin, ct));
        return row;
        } finally { if (ownsRead) read.End(this); }
    }
    private async Task CaptureSourceMembershipAsync(IClientSessionHandle tx, HeldReceiptOriginProof origin,
        PurchaseOrderSourceReadCapture capture, CancellationToken ct) {
        var original = InventoryHeldReceiptWriter.After(origin.Receipt); var key = InventoryBson.Balance(original);
        await capture.OneAsync(database, tx, "stockBalances", new BsonDocument("_id", original.Id), ct);
        var filter = new BsonDocument("$or", new BsonArray {
            new BsonDocument("legs.balanceId", original.Id), new BsonDocument("legs.before._id", original.Id), new BsonDocument("legs.after._id", original.Id),
            new BsonDocument("_id", origin.Receipt["movementId"]), new BsonDocument("sourceEvent", InventoryHeldReceiptWriter.Source(origin.ReceiptId).EventKey),
            new BsonDocument("legs", new BsonDocument("$elemMatch", new BsonDocument { { "before.physicalPoolId", original.PhysicalPoolId },
                { "before.location", key["location"] }, { "before.skuId", key["skuId"] }, { "before.definitionId", key["definitionId"] }, { "before.lotId", key["lotId"] } })),
            new BsonDocument("legs", new BsonDocument("$elemMatch", new BsonDocument { { "after.physicalPoolId", original.PhysicalPoolId },
                { "after.location", key["location"] }, { "after.skuId", key["skuId"] }, { "after.definitionId", key["definitionId"] }, { "after.lotId", key["lotId"] } })) });
        var rows = await CapturedManyAsync(tx, "movements", filter, HeldReceiptMovementHistory.MaximumMovements + 1, capture, ct);
        if (rows.Count is < 1 or > HeldReceiptMovementHistory.MaximumMovements) throw B.Broken();
    }
    private async Task AuthenticateCapturedMovementAsync(BsonDocument movement, HeldReceiptOriginProof origin,
        ImmutableHashSet<long>? ancestors, CancellationToken ct) {
        if (!conversionReads.TryGetValue(origin, out var read)) throw B.Broken();
        read.Require(this, read.Tx); origin.Require(database, read.Tx);
        var source = movement["source"];
        if (source["namespace"] != B.Namespace || source["eventKind"] != "convertPackages") throw B.Broken();
        var row = await ConversionAsync(read.Tx, source["documentId"].AsInt64, ct, origin, ancestors, read);
        if (row["movementId"] != movement["_id"] || source["occurrence"] != row["before"]["physicalPoolId"] ||
            await Rows("movements").Find(read.Tx, new BsonDocument("_id", movement["_id"])).SingleOrDefaultAsync(ct) != movement) throw B.Broken();
    }
    private async Task PhysicalMovementAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct, PurchaseOrderSourceReadCapture? capture = null) {
        var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64);
        var movements = await CapturedManyAsync(tx, "movements", new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", row["movementId"]), new BsonDocument("sourceEvent", source.EventKey),
            new BsonDocument { { "source.namespace", source.Namespace }, { "source.documentId", source.DocumentId }, { "source.eventKind", source.EventKind } }
        }), 2, capture, ct);
        if (movements.Count != 1) throw B.Broken();
        var movement = movements[0];
        if (movement["_id"] != row["movementId"] || !await Rows("movements").Find(tx, new BsonDocument { { "_id", movement["_id"] },
            { "$and", new BsonArray { InventoryMovementSchemas.Validator() } } }).AnyAsync(ct) ||
            movement["sourceEvent"] != source.EventKey || movement["source"] != new BsonDocument {
                { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", source.EventKind }, { "occurrence", source.Occurrence } } ||
            movement["sourceRevision"] != 1L || movement["physicalPoolId"] != source.Occurrence || movement["poolRevision"] != row["expected"]["poolRevision"] ||
            movement["actorId"] != row["actorId"] || movement["actorGrantVersion"] != row["actorGrantVersion"] ||
            movement["recordedAt"].CompareTo(row["recordedAt"]) < 0 || movement["recordedAt"].ToUniversalTime() > Now.UtcDateTime ||
            movement["reason"] != row["reason"] || movement["correlationId"] != row["correlationId"]) throw B.Broken();
        var expectedLegs = new BsonArray {
            new BsonDocument { { "balanceId", row["before"]["_id"] }, { "action", "ConvertOut" }, { "packages", row["quantities"]["sourcePackages"] }, { "before", row["before"] }, { "after", row["after"] } },
            new BsonDocument { { "balanceId", row["destinationBefore"]["_id"] }, { "action", "ConvertIn" }, { "packages", row["quantities"]["destinationPackages"] }, { "before", row["destinationBefore"] }, { "after", row["destinationAfter"] } } };
        if (movement["legs"] != expectedLegs) throw B.Broken();
    }
    private async Task ValueTransitionAsync(IClientSessionHandle tx, BsonDocument row, ImmutableHashSet<long> path,
        ConversionHistoryRead read, PurchaseOrderSourceReadCapture capture, CancellationToken ct) {
        var before = InventoryBson.ReadBalance(row["before"].AsBsonDocument); var after = InventoryBson.ReadBalance(row["after"].AsBsonDocument);
        var source = B.Source(row["_id"].AsInt64, before.PhysicalPoolId);
        var transitions = await CapturedManyAsync(tx, LotValueStore.Transitions, new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", row["valueTransitionId"]), new BsonDocument("sourceEvent", source.EventKey), new BsonDocument("sourceHash", row["causeHash"]),
            new BsonDocument { { "source.namespace", source.Namespace }, { "source.documentId", source.DocumentId }, { "source.eventKind", source.EventKind } }
        }), 2, capture, ct);
        if (transitions.Count != 1) throw B.Broken();
        var transition = transitions[0];
        if (transition["_id"] != row["valueTransitionId"] || transition["transitionHash"] != row["valueTransitionHash"] ||
            transition["transitionHash"] != LotValueStore.Hash(transition) || !await Rows(LotValueStore.Transitions).Find(tx,
                new BsonDocument { { "_id", transition["_id"] }, { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.Transitions) } } }).AnyAsync(ct)) throw B.Broken();
        LotValueStore.RequireTransition(transition);
        var value = row["sourceValue"];
        var allocation = LotValuePolicy.TakeProportionalFloor(new(before.Balance.Key, value["remainingPackages"].AsInt64,
            Money.Parse(value["remainingValue"].AsString), value["revision"].AsInt64), row["expected"]["lotValueRevision"].AsInt64, row["quantities"]["sourcePackages"].AsInt64);
        var state = new BsonDocument { { "remainingPackages", value["remainingPackages"] }, { "remainingValue", value["remainingValue"] }, { "revision", value["revision"] } };
        if (value.AsBsonDocument.ElementCount != 4 || value["key"] != LotValueStore.Key(before.Balance.Key) || value["remainingPackages"].AsInt64 != ReleasedPackages(before) ||
            allocation.Remaining.RemainingPackages != ReleasedPackages(after) || transition["availableBefore"] != state || transition["operation"] != "convert" ||
            transition["sourceEvent"] != source.EventKey || transition["sourceHash"] != row["causeHash"] || transition["source"] != new BsonDocument {
                { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", source.EventKind }, { "occurrence", source.Occurrence } } ||
            transition["policy"] != row["policy"] || transition["recordedAt"] != row["recordedAt"] || transition["poolId"] != before.PhysicalPoolId ||
            transition["availableId"] != before.Id || transition["key"] != value["key"] || transition["packages"] != row["quantities"]["sourcePackages"] ||
            transition["transferredValue"] != row["transferredValue"] || row["transferredValue"] != allocation.TransferredValue.ToString() ||
            transition["destinationId"] != row["destinationBefore"]["_id"] ||
            transition["destinationKey"] != LotValueStore.Key(InventoryBson.ReadBalance(row["destinationBefore"].AsBsonDocument).Balance.Key) ||
            transition["destinationPackages"] != row["quantities"]["destinationPackages"] || transition["destinationBefore"] != new BsonDocument {
                { "remainingPackages", 0L }, { "remainingValue", "0.00" }, { "revision", 0L } } || !transition["previousDestination"].IsBsonNull) throw B.Broken();
        var link = transition["previousAvailable"];
        if (!link.IsBsonDocument) throw B.Broken();
        var previous = await capture.OneAsync(database, tx, LotValueStore.Transitions, new BsonDocument { { "_id", link["transitionId"] },
            { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.Transitions) } } }, ct) ?? throw B.Broken();
        LotValueStore.RequireTransition(previous);
        if (previous["transitionHash"] != link["transitionHash"] || previous["transitionHash"] != LotValueStore.Hash(previous) ||
            previous["poolId"] != before.PhysicalPoolId || previous["recordedAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        var ownAxis = previous["availableId"] == before.Id;
        if (ownAxis ? previous["key"] != value["key"] || previous["availableAfter"] != state :
            previous["destinationId"] != before.Id || previous["destinationKey"] != value["key"] || previous["destinationAfter"] != state) throw B.Broken();
        var previousSource = previous["source"];
        await AuthenticateSourceAsync(database, tx, new(previousSource["namespace"].AsString, previousSource["documentId"].AsInt64,
            previousSource["eventKind"].AsString, previousSource["occurrence"].AsInt64), previous["sourceHash"].AsString, path, ct, read);
    }
    private async Task EffectsProofAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct, PurchaseOrderSourceReadCapture? capture = null) {
        var audits = await CapturedManyAsync(tx, "auditEvents", new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", row["auditId"]),
            new BsonDocument { { "kind", B.Operation }, { "record._id", row["_id"] } },
            new BsonDocument { { "kind", B.Operation }, { "result.conversionId", row["_id"] } },
            new BsonDocument { { "kind", B.Operation }, { "actorId", row["actorId"] }, { "record.requestKey", row["requestKey"] } },
            new BsonDocument { { "kind", B.Operation }, { "record.actorId", row["actorId"] }, { "record.requestKey", row["requestKey"] } },
            new BsonDocument { { "kind", B.Operation }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] } }
        }), 2, capture, ct);
        if (audits.Count != 1 || audits[0] != B.Audit(row)) throw B.Broken();
        var receipts = await CapturedManyAsync(tx, "idempotencyRecords", new BsonDocument { { "operation", B.Operation },
            { "$or", new BsonArray { new BsonDocument { { "actorId", row["actorId"] }, { "key", row["requestKey"] } },
                new BsonDocument("result.conversionId", row["_id"]) } } }, 2, capture, ct);
        if (receipts.Count != 1) throw B.Broken();
        var receipt = receipts[0];
        if (receipt["actorId"] != row["actorId"] || receipt["key"] != row["requestKey"]) throw B.Broken();
        if (receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != B.Result(row) || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        var key = B.Operation + ":" + row["_id"].AsInt64;
        var effects = await CapturedManyAsync(tx, "outboxEvents", new BsonDocument("$or", new BsonArray {
            new BsonDocument("sourceKey", key),
            new BsonDocument { { "kind", B.Operation }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] } },
            new BsonDocument { { "kind", B.Operation }, { "payload.conversionId", row["_id"] } }
        }), 2, capture, ct);
        if (effects.Count != 1) throw B.Broken();
        var effect = effects[0];
        if (effect["sourceKey"] != key) throw B.Broken();
        if (effect["kind"] != B.Operation || effect["actorId"] != row["actorId"] || effect["requestKey"] != row["requestKey"] ||
            effect["location"] != row["input"]["location"] || effect["payload"] != B.Result(row) || effect["createdAt"] != row["recordedAt"]) throw B.Broken();
    }
    internal Task AuthenticateSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) =>
        AuthenticateSourceAsync(owner, tx, source, hash, ImmutableHashSet<long>.Empty, ct);
    private async Task AuthenticateSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, InventorySource source, string hash,
        ImmutableHashSet<long> path, CancellationToken ct, ConversionHistoryRead? read = null) {
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw B.Broken();
        if (source.Namespace == StockReleaseBson.Namespace && source.EventKind == "releaseHeld") {
            await releases.AuthenticateSourceAsync(owner, tx, source, hash, ct); return;
        }
        if (source.Namespace != B.Namespace || source.EventKind != "convertPackages") throw B.Broken();
        var row = await ConversionAsync(tx, source.DocumentId, ct, ancestors: path, read: read);
        if (row["causeHash"] != hash || source != B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64)) throw B.Broken();
    }
    internal static async Task RequireMovementSourceAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        IClientSessionHandle tx, BsonDocument movement, HeldReceiptOriginProof origin, CancellationToken ct, ImmutableHashSet<long>? ancestors = null) {
        origin.Require(database, tx);
        var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, new SupplierService(database, unit, ids, clock, catalog));
        var sources = new StockSourceSetupService(database, unit, ids, clock);
        var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests);
        var valuations = new ProcurementStockValuationService(database, unit, ids, clock, catalog, orders, receiving, sources);
        var releases = new StockReleaseService(database, unit, ids, clock, catalog, orders, sources, valuations);
        var owner = new PackConversionService(database, unit, ids, clock, catalog, orders, sources, valuations, releases);
        await owner.StorageAsync(ct);
        var source = movement["source"];
        if (source["namespace"] != B.Namespace || source["eventKind"] != "convertPackages") throw B.Broken();
        var row = await owner.ConversionAsync(tx, source["documentId"].AsInt64, ct, origin, ancestors);
        if (row["movementId"] != movement["_id"] || source["occurrence"] != row["before"]["physicalPoolId"] ||
            await owner.Rows("movements").Find(tx, new BsonDocument("_id", movement["_id"])).SingleOrDefaultAsync(ct) != movement) throw B.Broken();
    }
    private async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actorId, string key, CancellationToken ct) {
        var found = await Rows(B.Conversions).Find(tx, new BsonDocument { { "actorId", actorId }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (found is null) {
            if (await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actorId }, { "operation", B.Operation }, { "key", key } }).AnyAsync(ct) ||
                await Rows("outboxEvents").Find(tx, new BsonDocument { { "actorId", actorId }, { "kind", B.Operation }, { "requestKey", key } }).AnyAsync(ct) ||
                await Rows("auditEvents").Find(tx, new BsonDocument { { "kind", B.Operation }, { "$or", new BsonArray {
                    new BsonDocument { { "actorId", actorId }, { "record.requestKey", key } },
                    new BsonDocument { { "record.actorId", actorId }, { "record.requestKey", key } },
                    new BsonDocument { { "actorId", actorId }, { "requestKey", key } } } } }).AnyAsync(ct)) throw B.Broken();
        }
        return found;
    }
    public Task<BsonDocument?> ReadAsync(ActorContext actor, long conversionId, CancellationToken ct = default) =>
        ReadConversionAsync(actor, conversionId, false, ct);
    private async Task<BsonDocument?> ReadConversionAsync(ActorContext actor, long conversionId, bool includeLabels, CancellationToken ct) {
        if (conversionId <= 0) throw PackConversionExecutionPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var raw = await Rows(B.Conversions).Find(tx, new BsonDocument("_id", conversionId)).SingleOrDefaultAsync(token);
            if (raw is null) {
                AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, Permissions.StockConvert, Now));
                if (await Rows("auditEvents").Find(tx, new BsonDocument { { "kind", B.Operation }, { "$or", new BsonArray {
                        new BsonDocument("record._id", conversionId), new BsonDocument("result.conversionId", conversionId) } } }).AnyAsync(token) ||
                    await Rows("outboxEvents").Find(tx, new BsonDocument("$or", new BsonArray {
                        new BsonDocument("sourceKey", B.Operation + ":" + conversionId),
                        new BsonDocument { { "kind", B.Operation }, { "payload.conversionId", conversionId } } })).AnyAsync(token) ||
                    await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "operation", B.Operation }, { "result.conversionId", conversionId } }).AnyAsync(token)) throw B.Broken();
                return null;
            }
            var context = new Context(actor, authority, tx, B.Input(raw["input"])); Current(context);
            var row = await ConversionAsync(tx, conversionId, token);
            var view = includeLabels ? await ExecutionLabelsAsync(tx, row, token) : new BsonDocument("conversion", row);
            await PeopleAsync(context, token); await FenceAsync(context, false, token);
            return view;
        }, ct);
    }
    public Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) => RecoverAsync(actor, B.Operation, key, ct);
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation != B.Operation || !StockCountBson.Key(key)) throw PackConversionExecutionPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var found = await ForKeyAsync(tx, actor.UserId, key, token);
            if (found is null) {
                var query = ScopePolicy.Compile(actor, authority, Permissions.StockConvert, Now); AuthorizationStore.Require(query);
                await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, query.Clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
                    authority.Grants.Where(grant => query.Clauses.Any(clause => clause.GrantId == grant.Id)),
                    authority.Memberships.Where(member => query.Clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
                AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, Permissions.StockConvert, Now)); return null;
            }
            var context = new Context(actor, authority, tx, B.Input(found["input"])); Current(context);
            var row = await ConversionAsync(tx, found["_id"].AsInt64, token);
            if (row["actorId"] != actor.UserId) throw B.Broken();
            await PeopleAsync(context, token); await FenceAsync(context, false, token); return B.Result(row);
        }, ct);
    }
}
