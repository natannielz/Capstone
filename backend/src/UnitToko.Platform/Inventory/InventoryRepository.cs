using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

internal enum InventoryBalanceAction { Receive = 1, Reserve, Release, Dispatch, Hold, ReleaseHold, Expire, ExpireHeld }
internal sealed record InventoryBalanceDocument(long Id, long PhysicalPoolId, StockBalance Balance);
internal sealed record InventoryBalanceChange(long Id, long PhysicalPoolId, StockBalanceKey Key, long ExpectedRevision, InventoryBalanceAction Action, long Packages);

/// <summary>Private storage mechanics only. The owning service must authorize/fence server-loaded
/// pool, source, catalog and lot facts and persist movements, costs, audit and the whole-command receipt
/// in this transaction. A component failure always aborts the caller's entire transaction.</summary>
internal sealed class InventoryRepository(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows => database.GetCollection<BsonDocument>("stockBalances");

    internal async Task<InventoryBalanceDocument> CreateAsync(IClientSessionHandle tx, InventoryBalanceDocument row, CancellationToken ct)
    {
        try
        {
            RequireSession(tx); ct.ThrowIfCancellationRequested();
            var document = InventoryBson.Balance(row);
            if (row.Balance.Revision != 1) throw Invalid("invalid_inventory_balance");
            await RequireStorageAsync(ct);
            await new InventoryGraph(database).RequireAsync(tx, row, true, ct);
            await Rows.InsertOneAsync(tx, document, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            return row;
        }
        catch { await AbortAsync(tx); throw; }
    }

    internal async Task<InventoryBalanceDocument> ReadAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        try
        {
            RequireSession(tx); ct.ThrowIfCancellationRequested();
            if (id <= 0) throw InventoryBson.Invalid();
            await RequireStorageAsync(ct);
            var result = InventoryBson.ReadBalance(await Rows.Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct));
            await new InventoryGraph(database).RequireAsync(tx, result, false, ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch { await AbortAsync(tx); throw; }
    }

    internal async Task<ImmutableArray<InventoryBalanceDocument>> ApplyAsync(IClientSessionHandle tx,
        IReadOnlyList<InventoryBalanceChange> changes, CancellationToken ct)
    {
        try
        {
            RequireSession(tx); ct.ThrowIfCancellationRequested();
            if (changes is null || changes.Count is < 1 or > 100) throw Invalid("invalid_inventory_change");
            var frozen = changes.ToImmutableArray();
            foreach (var change in frozen)
            {
                if (change is null || change.Id <= 0 || change.PhysicalPoolId <= 0 || change.ExpectedRevision <= 0 ||
                    change.Packages <= 0 || !Enum.IsDefined(change.Action)) throw Invalid("invalid_inventory_change");
                _ = StockBalancePolicy.AvailablePackages(new(change.Key, 0, 0, 0, 0, 1));
            }
            if (frozen.Select(change => change.Id).Distinct().Count() != frozen.Length) throw Invalid("invalid_inventory_change");
            await RequireStorageAsync(ct);
            var result = ImmutableArray.CreateBuilder<InventoryBalanceDocument>(frozen.Length);
            foreach (var change in frozen)
            {
                ct.ThrowIfCancellationRequested();
                var previous = await Rows.Find(tx, new BsonDocument("_id", change.Id)).SingleOrDefaultAsync(ct);
                var current = InventoryBson.ReadBalance(previous);
                if (current.PhysicalPoolId != change.PhysicalPoolId || current.Balance.Key != change.Key)
                    throw Invalid("inventory_balance_unavailable");
                if (current.Balance.Revision != change.ExpectedRevision)
                    throw new PersistenceConflictException("stock_revision_conflict", "Stock changed; reload its current revision");
                await new InventoryGraph(database).RequireAsync(tx, current, true, ct);
                var state = Transition(current.Balance, change);
                var next = current with { Balance = state };
                // Compare the complete server-loaded image, not caller-provided quantities or a floating-point version.
                var written = await Rows.ReplaceOneAsync(tx, previous!, InventoryBson.Balance(next), cancellationToken: ct);
                if (written.ModifiedCount != 1)
                    throw new PersistenceConflictException("stock_revision_conflict", "Stock changed; reload its current revision");
                result.Add(next);
            }
            ct.ThrowIfCancellationRequested();
            return result.ToImmutable();
        }
        catch { await AbortAsync(tx); throw; }
    }

    private static StockBalance Transition(StockBalance state, InventoryBalanceChange change) => change.Action switch
    {
        InventoryBalanceAction.Receive => StockBalancePolicy.Receive(state, change.ExpectedRevision, change.Packages),
        InventoryBalanceAction.Reserve => StockBalancePolicy.Reserve(state, change.ExpectedRevision, change.Packages),
        InventoryBalanceAction.Release => StockBalancePolicy.Release(state, change.ExpectedRevision, change.Packages),
        InventoryBalanceAction.Dispatch => StockBalancePolicy.Dispatch(state, change.ExpectedRevision, change.Packages),
        InventoryBalanceAction.Hold => StockBalancePolicy.Hold(state, change.ExpectedRevision, change.Packages),
        InventoryBalanceAction.ReleaseHold => StockBalancePolicy.ReleaseHold(state, change.ExpectedRevision, change.Packages),
        InventoryBalanceAction.Expire => StockBalancePolicy.Expire(state, change.ExpectedRevision, change.Packages),
        InventoryBalanceAction.ExpireHeld => StockBalancePolicy.ExpireHeld(state, change.ExpectedRevision, change.Packages),
        _ => throw Invalid("invalid_inventory_change")
    };

    private void RequireSession(IClientSessionHandle? tx)
    {
        if (tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client))
            throw Invalid("inventory_transaction_required");
    }

    private async Task RequireStorageAsync(CancellationToken ct)
    {
        // Read-only metadata checks. A reviewed deployment migration owns DDL and quiesces writers.
        var definition = await (await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", "stockBalances") }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null || definition.GetValue("type", "") != "collection") throw Invalid("inventory_storage_unavailable");
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
            options.TryGetValue("collation", out var collation) && !Simple(collation) ||
            !options.TryGetValue("validator", out var validator) || !validator.IsBsonDocument ||
            !InventorySchemas.Contains(validator.AsBsonDocument, InventorySchemas.BalanceValidator()))
            throw Invalid("inventory_storage_unavailable");
        var indexes = await (await Rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        bool Required(BsonDocument index) => index["key"].AsBsonDocument.Equals(InventorySchemas.BalanceKey()) &&
            index.GetValue("unique", false).AsBoolean && !Unsafe(index);
        if (!indexes.Any(index => index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && !Unsafe(index)) ||
            !indexes.Any(Required) || indexes.Any(index => index.Contains("expireAfterSeconds") ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !Required(index)))
            throw Invalid("inventory_storage_unavailable");
        ct.ThrowIfCancellationRequested();
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.ElementCount == 1 && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") ||
        index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static async Task AbortAsync(IClientSessionHandle? tx)
    {
        if (tx is null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { if (tx.IsInTransaction) await tx.AbortTransactionAsync(cleanup.Token); }
        catch (MongoException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        try { if (tx.IsInTransaction) tx.Dispose(); }
        catch (ObjectDisposedException) { }
    }
    private static DomainValidationException Invalid(string code) => new(code, "Inventory storage preconditions are not satisfied");
}
