using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Inventory;

internal sealed record LotValueWriteResult(long TransitionId, string TransitionHash, Money TransferredValue,
    long HeldRevision, long LotRevision, long DestinationLotRevision);

/// <summary>Finite value storage, never a value authority. Only a genuine owning write proof
/// can initialize/transfer value; unknown existing stock is not assigned an inferred value.</summary>
internal sealed class LotValueStore(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    internal const string HeldHeads = "lotValueHeldHeads", AvailableHeads = "lotValueAvailableHeads", Transitions = "lotValueTransitions";
    internal static readonly string[] Collections = [HeldHeads, AvailableHeads, Transitions];
    private const int MaximumTransitions = 4096;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static DomainValidationException Unavailable() => new("lot_value_provenance_unavailable", "Exact selected-lot quantity and authenticated value history are required");
    private static PersistenceConflictException Changed() => new("lot_value_revision_changed", "Selected lot value changed; reload its current revision");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "transitionHash")));
    internal static BsonDocument Key(StockBalanceKey key) => new() { { "location", StockSourceSetupBson.Location(key.Location) },
        { "skuId", key.SkuId.Value }, { "definitionId", key.DefinitionId.Value }, { "lotId", key.LotId.Value } };
    private static StockBalanceKey ReadKey(BsonDocument row)
    {
        Exact(row, "location", "skuId", "definitionId", "lotId");
        foreach (var name in new[] { "skuId", "definitionId", "lotId" }) Positive(row[name]);
        return new(InventoryGraph.Location(row["location"].AsBsonDocument), new(row["skuId"].AsInt64), new(row["definitionId"].AsInt64), new(row["lotId"].AsInt64));
    }
    private static BsonDocument State(long packages, Money value, long revision) => new() {
        { "remainingPackages", packages }, { "remainingValue", value.ToString() }, { "revision", revision } };
    private static LotValueBalance ReadState(BsonDocument state, StockBalanceKey key)
    {
        Exact(state, "remainingPackages", "remainingValue", "revision"); Nonnegative(state["remainingPackages"]); Nonnegative(state["revision"]);
        var money = ReadMoney(state["remainingValue"]);
        if (state["remainingPackages"] == 0L && money.Value != 0) throw Unavailable();
        return new(key, state["remainingPackages"].AsInt64, money, state["revision"].AsInt64);
    }
    private static Money ReadMoney(BsonValue value)
    {
        if (!value.IsString) throw Unavailable(); var money = Money.Parse(value.AsString);
        if (money.ToString() != value.AsString) throw Unavailable(); return money;
    }
    private static void Exact(BsonDocument value, params string[] fields)
    {
        if (value.ElementCount != fields.Length || !fields.All(value.Contains) || value.Names.Distinct().Count() != fields.Length) throw Unavailable();
    }
    private static void Positive(BsonValue value) { if (!value.IsInt64 || value.AsInt64 <= 0) throw Unavailable(); }
    private static void Nonnegative(BsonValue value) { if (!value.IsInt64 || value.AsInt64 < 0) throw Unavailable(); }
    private static void Digest(BsonValue value) { if (!value.IsString || value.AsString.Length != 64 || value.AsString.Any(c => !char.IsAsciiHexDigitLower(c))) throw Unavailable(); }
    private static long ReleasedPackages(InventoryBalanceDocument physical) => checked(physical.Balance.OnHand - physical.Balance.Held - physical.Balance.Expired);
    private static BsonValue Link(BsonDocument? head) => head is null ? BsonNull.Value : new BsonDocument {
        { "transitionId", head["lastTransitionId"] }, { "transitionHash", head["lastHash"] } };
    private static BsonDocument HeadState(BsonDocument head) => State(head["remainingPackages"].AsInt64, ReadMoney(head["remainingValue"]), head["revision"].AsInt64);

    internal static BsonDocument Validator(string name)
    {
        var money = new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
        var hash = PaymentSubmissionSchemas.Hash();
        BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
        var key = S.Object(new() { { "location", S.Location() }, { "skuId", S.Number(1) }, { "definitionId", S.Number(1) }, { "lotId", S.Number(1) } });
        var state = S.Object(new() { { "remainingPackages", S.Number(0) }, { "remainingValue", money }, { "revision", S.Number(0, long.MaxValue - 2) } });
        var link = S.Object(new() { { "transitionId", S.Number(1) }, { "transitionHash", hash } });
        var held = S.Object(new() { { "allocationId", S.Number(1) }, { "allocationHash", hash }, { "acquisitionPackages", S.Number(1) },
            { "acquisitionValue", money }, { "balanceId", S.Number(1) }, { "key", key } });
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name is HeldHeads or AvailableHeads) {
            fields.Add("kind", S.Choice(name == HeldHeads ? "inventory.lotvalue.held" : "inventory.lotvalue.available"));
            fields.Add("poolId", S.Number(1)); fields.Add("balanceId", S.Number(1)); fields.Add("key", key);
            fields.Add("remainingPackages", S.Number(0)); fields.Add("remainingValue", money); fields.Add("revision", S.Number(1, long.MaxValue - 2));
            fields.Add("fence", S.Number(1, long.MaxValue - 2)); fields.Add("lastTransitionId", S.Number(1)); fields.Add("lastHash", hash);
            fields.Add("updatedAt", new BsonDocument("bsonType", "date"));
            if (name == HeldHeads) fields.Add("held", held);
        } else if (name == Transitions) {
            fields.Add("kind", S.Choice("inventory.lotvalue.transition")); fields.Add("operation", S.Choice("release", "consume", "convert"));
            fields.Add("sourceEvent", S.Text(169)); fields.Add("source", S.Object(new() { { "namespace", S.Text(64) }, { "documentId", S.Number(1) }, { "eventKind", S.Text(64) }, { "occurrence", S.Number(1) } }));
            fields.Add("sourceHash", hash); fields.Add("policy", S.Choice(LotValuePolicy.ProportionalFloorPolicy)); fields.Add("recordedAt", new BsonDocument("bsonType", "date"));
            fields.Add("poolId", S.Number(1)); fields.Add("availableId", S.Number(1)); fields.Add("key", key); fields.Add("packages", S.Number(1));
            fields.Add("transferredValue", money); fields.Add("held", Nullable(held)); fields.Add("heldBefore", Nullable(state)); fields.Add("heldAfter", Nullable(state));
            fields.Add("availableBefore", state); fields.Add("availableAfter", state); fields.Add("destinationId", Nullable(S.Number(1)));
            fields.Add("destinationKey", Nullable(key)); fields.Add("destinationPackages", Nullable(S.Number(1))); fields.Add("destinationBefore", Nullable(state)); fields.Add("destinationAfter", Nullable(state));
            fields.Add("previousHeld", Nullable(link)); fields.Add("previousAvailable", Nullable(link)); fields.Add("previousDestination", Nullable(link)); fields.Add("transitionHash", hash);
        } else throw new ArgumentException("Unknown lot value owner", nameof(name));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange(name == Transitions ? "recordedAt" : "updatedAt") });
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        HeldHeads => [], AvailableHeads => [("ux_lotvalue_available_key", new() { { "poolId", 1 }, { "key", 1 } }, true)],
        Transitions => [("ux_lotvalue_source_event", new("sourceEvent", 1), true),
            ("ix_lotvalue_available_history", new() { { "availableId", 1 }, { "_id", 1 } }, false),
            ("ix_lotvalue_destination_history", new() { { "destinationId", 1 }, { "_id", 1 } }, false),
            ("ix_lotvalue_held_history", new() { { "held.allocationId", 1 }, { "_id", 1 } }, false)],
        _ => throw new ArgumentException("Unknown lot value owner", nameof(name)) };
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Unavailable();
            if (!AccountContactStorage.SafeDefinition(definition, Validator(name))) throw Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct); var required = Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(indexes, required) || required.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key)))) throw Unavailable();
        }
    }

    // Callback belongs to the actual current owner capability and authenticates original source
    // receipts plus their immutable transition linkage. Hashes alone never establish a source.
    internal async Task<LotValueBalance> ReadAvailableAsync(IClientSessionHandle tx, InventoryBalanceDocument physical,
        Func<IMongoDatabase, IClientSessionHandle, InventorySource, string, CancellationToken, Task> authenticate, CancellationToken ct)
    {
        try {
            if (physical is null || authenticate is null) throw Unavailable();
            new InventoryRepository(database).RequireSession(tx); await RequireStorageAsync(database, ct);
            var head = await HeadAsync(tx, AvailableHeads, physical.Id, authenticate, ct) ?? throw Unavailable();
            RequirePhysical(head, physical, ReleasedPackages(physical));
            return ReadState(HeadState(head), physical.Balance.Key);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
    internal async Task<LotValueBalance> ReadForReleaseAsync(IClientSessionHandle tx, StockReleaseSourceProof source,
        Func<IMongoDatabase, IClientSessionHandle, InventorySource, string, CancellationToken, Task> authenticate, CancellationToken ct)
    {
        try {
            source.Require(database, tx); if (!source.IsCurrent || authenticate is null) throw Unavailable();
            await RequireStorageAsync(database, ct); var physical = InventoryBson.ReadBalance(source.Balance);
            await PhysicalAsync(tx, physical, ct);
            var head = await HeadAsync(tx, AvailableHeads, physical.Id, authenticate, ct);
            if (head is null) {
                // Only a genuine current acquisition/quality proof can review first release.
                // This creates no head or value and never admits already released legacy stock.
                if (ReleasedPackages(physical) != 0) throw Unavailable();
                return new(physical.Balance.Key, 0, new(0m), 0);
            }
            RequirePhysical(head, physical, ReleasedPackages(physical)); source.Require(database, tx);
            return ReadState(HeadState(head), physical.Balance.Key);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
    internal async Task<LotValueBalance?> ReadHeldAsync(IClientSessionHandle tx, long allocationId,
        Func<IMongoDatabase, IClientSessionHandle, InventorySource, string, CancellationToken, Task> authenticate, CancellationToken ct)
    {
        try {
            if (authenticate is null) throw Unavailable();
            new InventoryRepository(database).RequireSession(tx); await RequireStorageAsync(database, ct); Positive(new BsonInt64(allocationId));
            var head = await HeadAsync(tx, HeldHeads, allocationId, authenticate, ct);
            return head is null ? null : ReadState(HeadState(head), ReadKey(head["key"].AsBsonDocument));
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
    private async Task<BsonDocument?> HeadAsync(IClientSessionHandle tx, string name, long id,
        Func<IMongoDatabase, IClientSessionHandle, InventorySource, string, CancellationToken, Task> authenticate, CancellationToken ct)
    {
        var head = await Rows(name).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        var filter = name == HeldHeads ? new BsonDocument("held.allocationId", id) : new BsonDocument("$or", new BsonArray {
            new BsonDocument("availableId", id), new BsonDocument("destinationId", id) });
        var history = await Rows(Transitions).Find(tx, filter).Limit(MaximumTransitions + 1).ToListAsync(ct);
        if (head is null) { if (history.Count != 0) throw Unavailable(); return null; }
        if (!await Rows(name).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & Validator(name)).AnyAsync(ct) ||
            history.Count is < 1 or > MaximumTransitions || head["revision"].AsInt64 != history.Count) throw Unavailable();
        foreach (var row in history) {
            if (!await Rows(Transitions).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", row["_id"]) & Validator(Transitions)).AnyAsync(ct)) throw Unavailable();
            RequireTransition(row);
        }
        var key = ReadKey(head["key"].AsBsonDocument); var isHeld = name == HeldHeads;
        if (isHeld ? head["_id"] != head["held"]["allocationId"] || head["balanceId"] != head["held"]["balanceId"] || head["key"] != head["held"]["key"] :
            head["_id"] != head["balanceId"]) throw Unavailable();
        BsonDocument Before(BsonDocument row) => row[isHeld ? "heldBefore" : row["availableId"] == id ? "availableBefore" : "destinationBefore"].AsBsonDocument;
        BsonDocument After(BsonDocument row) => row[isHeld ? "heldAfter" : row["availableId"] == id ? "availableAfter" : "destinationAfter"].AsBsonDocument;
        var ordered = history.OrderBy(row => Before(row)["revision"].AsInt64).ToArray();
        var state = isHeld ? State(head["held"]["acquisitionPackages"].AsInt64, ReadMoney(head["held"]["acquisitionValue"]), 0) : State(0, new(0m), 0);
        BsonValue previous = BsonNull.Value; BsonValue? at = null;
        foreach (var row in ordered) {
            if (row["transitionHash"] != Hash(row) || row["poolId"] != head["poolId"] || at is not null && row["recordedAt"].CompareTo(at) < 0) throw Unavailable();
            var axis = isHeld ? "Held" : row["availableId"] == id ? "Available" : "Destination";
            if (row["previous" + axis] != previous || Before(row) != state || isHeld && row["held"] != head["held"] ||
                !isHeld && row[axis == "Available" ? "key" : "destinationKey"] != head["key"]) throw Unavailable();
            var source = ReadSource(row);
            await authenticate(database, tx, source, row["sourceHash"].AsString, ct);
            state = After(row); previous = new BsonDocument { { "transitionId", row["_id"] }, { "transitionHash", row["transitionHash"] } }; at = row["recordedAt"];
        }
        if (HeadState(head) != state || Link(head) != previous || head["updatedAt"] != at || head["fence"].AsInt64 < head["revision"].AsInt64) throw Unavailable();
        _ = ReadState(state, key); return head;
    }
    private static InventorySource ReadSource(BsonDocument row)
    {
        var source = row["source"].AsBsonDocument; Exact(source, "namespace", "documentId", "eventKind", "occurrence");
        var value = new InventorySource(source["namespace"].AsString, source["documentId"].AsInt64, source["eventKind"].AsString, source["occurrence"].AsInt64);
        value.Validate(); if (row["sourceEvent"] != value.EventKey) throw Unavailable(); return value;
    }
    private static BsonDocument Source(InventorySource source) => new() { { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", source.EventKind }, { "occurrence", source.Occurrence } };
    private static BsonDocument Take(BsonDocument before, StockBalanceKey key, long packages, out Money moved)
    {
        var value = ReadState(before, key);
        var allocation = LotValuePolicy.TakeProportionalFloor(value with { Revision = Math.Max(1, value.Revision) }, Math.Max(1, value.Revision), packages);
        moved = allocation.TransferredValue;
        return State(allocation.Remaining.RemainingPackages, allocation.Remaining.RemainingValue, checked(value.Revision + 1));
    }
    private static BsonDocument Credit(BsonDocument before, StockBalanceKey key, long packages, Money moved)
    {
        var value = ReadState(before, key); if (packages <= 0) throw Unavailable();
        return State(checked(value.RemainingPackages + packages), Money.Add(value.RemainingValue, moved), checked(value.Revision + 1));
    }
    internal static void RequireTransition(BsonDocument row)
    {
        var key = ReadKey(row["key"].AsBsonDocument); var packages = row["packages"].AsInt64; var moved = ReadMoney(row["transferredValue"]);
        var before = row["availableBefore"].AsBsonDocument; var operation = row["operation"].AsString;
        if (operation == "release") {
            if (row["held"].IsBsonNull || row["heldBefore"].IsBsonNull || row["heldAfter"].IsBsonNull || !row["destinationId"].IsBsonNull) throw Unavailable();
            var heldAfter = Take(row["heldBefore"].AsBsonDocument, key, packages, out var taken);
            if (taken != moved || row["heldAfter"] != heldAfter || row["availableAfter"] != Credit(before, key, packages, moved) || row["held"]["key"] != row["key"] ||
                row["held"]["balanceId"] != row["availableId"]) throw Unavailable();
        } else {
            if (!row["held"].IsBsonNull || !row["heldBefore"].IsBsonNull || !row["heldAfter"].IsBsonNull || !row["previousHeld"].IsBsonNull) throw Unavailable();
            if (row["availableAfter"] != Take(before, key, packages, out var taken) || taken != moved) throw Unavailable();
        }
        if (operation == "convert") {
            if (row["destinationId"].IsBsonNull || row["destinationId"] == row["availableId"] || row["destinationKey"].IsBsonNull ||
                row["destinationPackages"].IsBsonNull || row["destinationBefore"].IsBsonNull || row["destinationAfter"].IsBsonNull) throw Unavailable();
            var destinationKey = ReadKey(row["destinationKey"].AsBsonDocument);
            if (destinationKey.Location != key.Location || destinationKey == key || row["destinationAfter"] != Credit(row["destinationBefore"].AsBsonDocument,
                destinationKey, row["destinationPackages"].AsInt64, moved)) throw Unavailable();
        } else if (!row["destinationId"].IsBsonNull || !row["destinationKey"].IsBsonNull || !row["destinationPackages"].IsBsonNull ||
            !row["destinationBefore"].IsBsonNull || !row["destinationAfter"].IsBsonNull || !row["previousDestination"].IsBsonNull) throw Unavailable();
    }
    private static void RequirePhysical(BsonDocument head, InventoryBalanceDocument physical, long packages)
    {
        if (head["balanceId"] != physical.Id || head["poolId"] != physical.PhysicalPoolId || head["key"] != Key(physical.Balance.Key) || head["remainingPackages"] != packages) throw Unavailable();
    }
    private async Task PhysicalAsync(IClientSessionHandle tx, InventoryBalanceDocument expected, CancellationToken ct)
    {
        var actual = await Rows("stockBalances").Find(tx, new BsonDocument("_id", expected.Id)).SingleOrDefaultAsync(ct);
        if (actual != InventoryBson.Balance(expected)) throw Changed();
    }
    private void Current(InventorySource source, string sourceHash, string policy, DateTimeOffset at)
    {
        source.Validate(); Digest(new BsonString(sourceHash));
        if (policy != LotValuePolicy.ProportionalFloorPolicy || at == default || at.Offset != TimeSpan.Zero || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero || at > clock.UtcNow) throw Unavailable();
    }
    private async Task<long> FreshAsync(IClientSessionHandle tx, InventorySource source, CancellationToken ct)
    {
        if (await Rows(Transitions).Find(tx, new BsonDocument("sourceEvent", source.EventKey)).AnyAsync(ct))
            throw new PersistenceConflictException("lot_value_source_already_applied", "Recover the original whole command; this source already transferred value");
        return (await ids.NextAsync(Transitions, ct)).Value;
    }
    private static BsonDocument Transition(long id, string operation, InventorySource source, string hash, DateTimeOffset at,
        InventoryBalanceDocument physical, long packages, Money moved, BsonDocument? held, BsonDocument? heldBefore, BsonDocument? heldAfter,
        BsonDocument before, BsonDocument after, InventoryBalanceDocument? destination, long? destinationPackages, BsonDocument? destinationBefore,
        BsonDocument? destinationAfter, BsonDocument? heldHead, BsonDocument? availableHead, BsonDocument? destinationHead)
    {
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "inventory.lotvalue.transition" }, { "operation", operation },
            { "sourceEvent", source.EventKey }, { "source", Source(source) }, { "sourceHash", hash }, { "policy", LotValuePolicy.ProportionalFloorPolicy },
            { "recordedAt", at.UtcDateTime }, { "poolId", physical.PhysicalPoolId }, { "availableId", physical.Id }, { "key", Key(physical.Balance.Key) },
            { "packages", packages }, { "transferredValue", moved.ToString() }, { "held", held is null ? BsonNull.Value : held },
            { "heldBefore", heldBefore is null ? BsonNull.Value : heldBefore }, { "heldAfter", heldAfter is null ? BsonNull.Value : heldAfter },
            { "availableBefore", before }, { "availableAfter", after }, { "destinationId", destination is null ? BsonNull.Value : new BsonInt64(destination.Id) },
            { "destinationKey", destination is null ? BsonNull.Value : Key(destination.Balance.Key) },
            { "destinationPackages", destinationPackages is null ? BsonNull.Value : new BsonInt64(destinationPackages.Value) },
            { "destinationBefore", destinationBefore is null ? BsonNull.Value : destinationBefore }, { "destinationAfter", destinationAfter is null ? BsonNull.Value : destinationAfter },
            { "previousHeld", Link(heldHead) }, { "previousAvailable", Link(availableHead) }, { "previousDestination", Link(destinationHead) } };
        row.Add("transitionHash", Hash(row)); RequireTransition(row); return row;
    }
    private async Task SaveHeadAsync(IClientSessionHandle tx, string name, long id, InventoryBalanceDocument physical,
        BsonDocument? before, BsonDocument state, BsonDocument transition, BsonDocument? held, CancellationToken ct)
    {
        if (before is not null && (before["fence"].AsInt64 >= long.MaxValue - 2 || transition["recordedAt"].CompareTo(before["updatedAt"]) < 0)) throw Unavailable();
        var after = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", name == HeldHeads ? "inventory.lotvalue.held" : "inventory.lotvalue.available" },
            { "poolId", physical.PhysicalPoolId }, { "balanceId", physical.Id }, { "key", Key(physical.Balance.Key) },
            { "remainingPackages", state["remainingPackages"] }, { "remainingValue", state["remainingValue"] }, { "revision", state["revision"] },
            { "fence", before is null ? 1L : checked(before["fence"].AsInt64 + 1) }, { "lastTransitionId", transition["_id"] },
            { "lastHash", transition["transitionHash"] }, { "updatedAt", transition["recordedAt"] } };
        if (name == HeldHeads) after.Add("held", held ?? throw Unavailable());
        if (before is null) await Rows(name).InsertOneAsync(tx, after, cancellationToken: ct);
        else if ((await Rows(name).ReplaceOneAsync(tx, before, after, cancellationToken: ct)).ModifiedCount != 1) throw Changed();
    }

    internal async Task<LotValueWriteResult> ReleaseAsync(IClientSessionHandle tx, HeldStockReleaseValueProof proof, CancellationToken ct)
    {
        try {
            new InventoryRepository(database).RequireSession(tx); proof.Require(database, tx); await RequireStorageAsync(database, ct);
            Current(proof.Source, proof.SourceHash, proof.Policy, proof.At); var before = proof.Before; var after = proof.After;
            if (before.Id != after.Id || before.PhysicalPoolId != after.PhysicalPoolId || before.Balance.Key != after.Balance.Key ||
                StockBalancePolicy.ReleaseHold(before.Balance, before.Balance.Revision, proof.Packages) != after.Balance) throw Unavailable();
            await PhysicalAsync(tx, after, ct);
            Func<IMongoDatabase, IClientSessionHandle, InventorySource, string, CancellationToken, Task> authenticate = proof.AuthenticateSourceAsync;
            var available = await HeadAsync(tx, AvailableHeads, before.Id, authenticate, ct);
            if (available is null ? proof.ExpectedLotRevision != 0 || ReleasedPackages(before) != 0 : available["revision"] != proof.ExpectedLotRevision) throw Changed();
            if (available is not null) RequirePhysical(available, before, ReleasedPackages(before));
            Positive(new BsonInt64(proof.AllocationId)); Positive(new BsonInt64(proof.AcquisitionPackages)); Digest(new BsonString(proof.AllocationHash));
            var held = new BsonDocument { { "allocationId", proof.AllocationId }, { "allocationHash", proof.AllocationHash },
                { "acquisitionPackages", proof.AcquisitionPackages }, { "acquisitionValue", proof.AcquisitionValue.ToString() },
                { "balanceId", before.Id }, { "key", Key(before.Balance.Key) } };
            var heldHead = await HeadAsync(tx, HeldHeads, proof.AllocationId, authenticate, ct);
            if (heldHead is null ? proof.ExpectedHeldRevision != 0 : heldHead["revision"] != proof.ExpectedHeldRevision || heldHead["held"] != held || heldHead["poolId"] != before.PhysicalPoolId) throw Changed();
            var heldBefore = heldHead is null ? State(proof.AcquisitionPackages, proof.AcquisitionValue, 0) : HeadState(heldHead);
            var heldAfter = Take(heldBefore, before.Balance.Key, proof.Packages, out var moved);
            var availableBefore = available is null ? State(0, new(0m), 0) : HeadState(available);
            var availableAfter = Credit(availableBefore, before.Balance.Key, proof.Packages, moved);
            if (availableAfter["remainingPackages"] != ReleasedPackages(after)) throw Unavailable();
            var id = await FreshAsync(tx, proof.Source, ct);
            var row = Transition(id, "release", proof.Source, proof.SourceHash, proof.At, after, proof.Packages, moved,
                held, heldBefore, heldAfter, availableBefore, availableAfter, null, null, null, null, heldHead, available, null);
            proof.Require(database, tx);
            await Rows(Transitions).InsertOneAsync(tx, row, cancellationToken: ct);
            await SaveHeadAsync(tx, HeldHeads, proof.AllocationId, after, heldHead, heldAfter, row, held, ct);
            await SaveHeadAsync(tx, AvailableHeads, after.Id, after, available, availableAfter, row, null, ct);
            proof.Require(database, tx); ct.ThrowIfCancellationRequested();
            return new(id, row["transitionHash"].AsString, moved, heldAfter["revision"].AsInt64, availableAfter["revision"].AsInt64, 0);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
    internal async Task<LotValueWriteResult> TakeAsync(IClientSessionHandle tx, LotValueConsumptionProof proof, CancellationToken ct)
    {
        try {
            new InventoryRepository(database).RequireSession(tx); proof.Require(database, tx); await RequireStorageAsync(database, ct);
            Current(proof.Source, proof.SourceHash, proof.Policy, proof.At); var before = proof.Before; var after = proof.After;
            if (before.Id != after.Id || before.PhysicalPoolId != after.PhysicalPoolId || before.Balance.Key != after.Balance.Key || proof.Packages <= 0 ||
                ReleasedPackages(before) - proof.Packages != ReleasedPackages(after)) throw Unavailable();
            await PhysicalAsync(tx, after, ct);
            Func<IMongoDatabase, IClientSessionHandle, InventorySource, string, CancellationToken, Task> authenticate = proof.AuthenticateSourceAsync;
            var available = await HeadAsync(tx, AvailableHeads, before.Id, authenticate, ct) ?? throw Unavailable();
            if (available["revision"] != proof.ExpectedLotRevision) throw Changed(); RequirePhysical(available, before, ReleasedPackages(before));
            var availableBefore = HeadState(available); var availableAfter = Take(availableBefore, before.Balance.Key, proof.Packages, out var moved);
            BsonDocument? destination = null, destinationBefore = null, destinationAfter = null; long? destinationPackages = null;
            if ((proof.DestinationBefore is null) != (proof.DestinationAfter is null)) throw Unavailable();
            if (proof.DestinationBefore is { } start && proof.DestinationAfter is { } end) {
                if (start.Id == before.Id || start.Id != end.Id || start.PhysicalPoolId != before.PhysicalPoolId || end.PhysicalPoolId != before.PhysicalPoolId ||
                    start.Balance.Key != end.Balance.Key || end.Balance.Key.Location != before.Balance.Key.Location || end.Balance.Key == before.Balance.Key) throw Unavailable();
                await PhysicalAsync(tx, end, ct); destinationPackages = checked(ReleasedPackages(end) - ReleasedPackages(start));
                if (destinationPackages <= 0) throw Unavailable();
                destination = await HeadAsync(tx, AvailableHeads, start.Id, authenticate, ct);
                if (destination is null ? proof.ExpectedDestinationLotRevision != 0 || ReleasedPackages(start) != 0 : destination["revision"] != proof.ExpectedDestinationLotRevision) throw Changed();
                if (destination is not null) RequirePhysical(destination, start, ReleasedPackages(start));
                destinationBefore = destination is null ? State(0, new(0m), 0) : HeadState(destination);
                destinationAfter = Credit(destinationBefore, start.Balance.Key, destinationPackages.Value, moved);
            } else if (proof.ExpectedDestinationLotRevision != 0) throw Unavailable();
            var id = await FreshAsync(tx, proof.Source, ct);
            var row = Transition(id, destinationAfter is null ? "consume" : "convert", proof.Source, proof.SourceHash, proof.At, after, proof.Packages, moved,
                null, null, null, availableBefore, availableAfter, proof.DestinationAfter, destinationPackages, destinationBefore, destinationAfter, null, available, destination);
            proof.Require(database, tx); await Rows(Transitions).InsertOneAsync(tx, row, cancellationToken: ct);
            await SaveHeadAsync(tx, AvailableHeads, after.Id, after, available, availableAfter, row, null, ct);
            if (proof.DestinationAfter is { } final && destinationAfter is not null) await SaveHeadAsync(tx, AvailableHeads, final.Id, final, destination, destinationAfter, row, null, ct);
            proof.Require(database, tx); ct.ThrowIfCancellationRequested();
            return new(id, row["transitionHash"].AsString, moved, 0, availableAfter["revision"].AsInt64, destinationAfter?["revision"].AsInt64 ?? 0);
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
}
