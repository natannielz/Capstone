using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

internal sealed record InventorySource(string Namespace, long DocumentId, string EventKind, long Occurrence)
{
    internal string EventKey => string.Create(CultureInfo.InvariantCulture, $"{Namespace}:{DocumentId}:{EventKind}:{Occurrence}");
    internal void Validate()
    {
        RequireCode(Namespace); RequireCode(EventKind);
        if (DocumentId <= 0 || Occurrence <= 0) throw InventoryLedger.Invalid("invalid_inventory_source");
    }
    internal static void RequireCode(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant))
            throw InventoryLedger.Invalid("invalid_inventory_source");
    }
}
internal sealed record InventoryPoolExpectation(long Id, StockLocation Location, long Revision);
internal sealed record InventorySourceSnapshot(InventorySource Source, long Revision, InventoryPoolExpectation Pool, ImmutableArray<InventoryBalanceChange> Changes);
internal sealed record InventoryWriteMetadata(string Reason, string CorrelationId);
internal sealed record InventoryMovementReceipt(long Id, string SourceEvent, ImmutableArray<InventoryBalanceDocument> Balances);
/// <summary>Trusted owning-module adapter, never request input. Load and conditionally WRITE the actual
/// approved source revision, recheck actor/scope/source authority and supply its permitted stock effects.
/// The caller owns whole-command idempotency, financial posting, audit/outbox and final authority fences.</summary>
internal interface IInventorySourceReader
{
    string Namespace { get; }
    IMongoDatabase Database { get; }
    Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source, long expectedRevision, CancellationToken ct);
}

/// <summary>Joins the owning command transaction. Source duplicates ALWAYS abort, even identical ones;
/// only an outer whole-command receipt can safely replay before any business effects. No reader,
/// source policy, migration, cost policy or business workflow is activated by this component.</summary>
internal sealed class InventoryLedger
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly IReadOnlyDictionary<string, IInventorySourceReader> readers;
    private readonly InventoryRepository repository;
    private readonly IdAllocator ids;
    internal InventoryLedger(IMongoDatabase database, IClock clock, IEnumerable<IInventorySourceReader> readers)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(readers);
        this.database = database; this.clock = clock; repository = new(database); ids = new(database);
        var configured = readers.ToArray();
        foreach (var reader in configured)
        {
            if (reader is null || reader.Database is null || !ReferenceEquals(reader.Database.Client, database.Client) ||
                reader.Database.DatabaseNamespace != database.DatabaseNamespace)
                throw new ArgumentException("Inventory source readers must own the same application database and client", nameof(readers));
            InventorySource.RequireCode(reader.Namespace);
        }
        this.readers = configured.ToDictionary(reader => reader.Namespace, StringComparer.Ordinal);
    }
    internal async Task<InventoryMovementReceipt> ApplyAsync(IClientSessionHandle tx, ActorContext actor, InventorySource source,
        long expectedRevision, InventoryWriteMetadata metadata, CancellationToken ct = default)
    {
        try
        {
            repository.RequireSession(tx); ct.ThrowIfCancellationRequested();
            if (actor is null || source is null || metadata is null || expectedRevision <= 0) throw Invalid("invalid_inventory_request");
            actor.Validate(); source.Validate();
            if (!Enum.IsDefined(actor.Assurance)) throw Invalid("invalid_inventory_actor");
            RequireText(metadata.Reason, 2000); RequireText(metadata.CorrelationId, 128);
            if (!readers.TryGetValue(source.Namespace, out var reader)) throw Invalid("inventory_source_unavailable");
            await InventoryRepository.RequireStorageAsync(database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), ct);
            var movements = database.GetCollection<BsonDocument>("movements");
            if (await movements.Find(tx, new BsonDocument("sourceEvent", source.EventKey)).AnyAsync(ct))
                throw new PersistenceConflictException("inventory_source_already_applied", "Replay the owning command receipt; this source event already changed stock");
            var snapshot = await reader.ReadAndFenceAsync(tx, actor, source, expectedRevision, ct);
            if (snapshot is null || snapshot.Source != source || snapshot.Revision != expectedRevision || snapshot.Pool is null ||
                snapshot.Pool.Id <= 0 || snapshot.Pool.Revision <= 0 || snapshot.Pool.Location is null ||
                snapshot.Changes.IsDefaultOrEmpty || snapshot.Changes.Length > 100) throw Invalid("inventory_source_mismatch");
            snapshot.Pool.Location.Validate();
            // Copy even immutable-array storage supplied by a trusted adapter before awaiting database work.
            var changes = snapshot.Changes.ToArray().ToImmutableArray();
            foreach (var change in changes)
                if (change is null || change.PhysicalPoolId != snapshot.Pool.Id || change.Key is null ||
                    change.Key.Location != snapshot.Pool.Location) throw Invalid("inventory_source_scope_mismatch");
            var pool = await database.GetCollection<BsonDocument>("inventoryPools").Find(tx, new BsonDocument("_id", snapshot.Pool.Id)).SingleOrDefaultAsync(ct);
            InventoryGraph.RequirePool(pool);
            if (!pool!["active"].AsBoolean || pool["revision"].AsInt64 != snapshot.Pool.Revision ||
                InventoryGraph.Location(pool["location"].AsBsonDocument) != snapshot.Pool.Location) throw Invalid("inventory_source_pool_changed");
            var client = database.Client as MongoClient ?? throw Invalid("inventory_source_unavailable");
            await new StockSourceSetupService(database, new(client), ids, clock).RequireActivatedPoolAsync(tx, pool, ct);
            var before = ImmutableArray.CreateBuilder<InventoryBalanceDocument>(changes.Length);
            foreach (var change in changes) before.Add(await repository.ReadAsync(tx, change.Id, ct));
            foreach (var change in changes)
                await RequireValueOwnerAsync(tx, reader, change, before.Single(value => value.Id == change.Id), ct);
            var id = await ids.NextAsync("movements", ct);
            var after = await repository.ApplyAsync(tx, changes, ct);
            var now = clock.UtcNow;
            if (now == default || now.Offset != TimeSpan.Zero) throw Invalid("invalid_inventory_clock");
            var row = InventoryMovementBson.Create(id.Value, actor, snapshot with { Changes = changes }, before.ToImmutable(), after, metadata, now);
            await movements.InsertOneAsync(tx, row, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            return new(id.Value, source.EventKey, after);
        }
        catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
    private async Task RequireValueOwnerAsync(IClientSessionHandle tx, IInventorySourceReader reader,
        InventoryBalanceChange change, InventoryBalanceDocument before, CancellationToken ct)
    {
        var conversion = change.Action is InventoryBalanceAction.ConvertOut or InventoryBalanceAction.ConvertIn;
        if (conversion && !PackConversionService.IsLedgerConversionSource(reader)) throw Invalid("inventory_lot_value_owner_required");
        // Reservations retain the value of undelivered packages. ReceiveHeld/ExpireHeld move
        // entirely unavailable quantities and also preserve onHand-held-expired.
        if (change.Action is InventoryBalanceAction.Reserve or InventoryBalanceAction.Release or
            InventoryBalanceAction.ReceiveHeld or InventoryBalanceAction.ExpireHeld) return;
        BsonDocument FullKey(string prefix) => new() {
            { prefix + ".location.organizationId", before.Balance.Key.Location.OrganizationId.Value },
            { prefix + ".location.shopId", before.Balance.Key.Location.ShopId.Value },
            { prefix + ".location.warehouseId", before.Balance.Key.Location.WarehouseId.Value },
            { prefix + ".location.channelId", before.Balance.Key.Location.ChannelId.Value },
            { prefix + ".skuId", before.Balance.Key.SkuId.Value }, { prefix + ".definitionId", before.Balance.Key.DefinitionId.Value },
            { prefix + ".lotId", before.Balance.Key.LotId.Value } };
        // These are refusal signals, including malformed heads and orphan transitions.
        // A schema filter here would turn missing/corrupt value ownership into unvalued stock.
        var heads = new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", before.Id), new BsonDocument("balanceId", before.Id), FullKey("key") });
        var history = new BsonDocument("$or", new BsonArray {
            new BsonDocument("availableId", before.Id), new BsonDocument("destinationId", before.Id), new BsonDocument("held.balanceId", before.Id),
            FullKey("key"), FullKey("destinationKey"), FullKey("held.key") });
        var linked = await database.GetCollection<BsonDocument>(LotValueStore.AvailableHeads).Find(tx, heads).Limit(1).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>(LotValueStore.Transitions).Find(tx, history).Limit(1).AnyAsync(ct);
        if (linked && !conversion && (change.Action != InventoryBalanceAction.ReleaseHold || !StockReleaseService.IsLedgerReleaseSource(reader)))
            throw Invalid("inventory_lot_value_owner_required");
    }
    internal static void RequireText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value != value.Trim() || value.Any(char.IsControl))
            throw Invalid("invalid_inventory_metadata");
    }
    internal static DomainValidationException Invalid(string code) => new(code, "Inventory source, scope or transaction preconditions are not satisfied");
}
