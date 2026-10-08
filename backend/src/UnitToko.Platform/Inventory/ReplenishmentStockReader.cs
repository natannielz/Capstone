using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Inventory;

internal sealed class ReplenishmentStockReader(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
{
    // ponytail: bounded interactive review; larger lot sets must be narrowed/archived by their owners, never silently truncated.
    internal const int MaximumLots = 100;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private readonly StockSourceSetupService sources = new(database, unit, ids, clock);

    internal sealed record Capture(ReplenishmentStock Stock, ImmutableArray<string> Warnings,
        BsonDocument Provenance, Func<CancellationToken, Task> Complete, Action RequireCurrent);

    internal async Task<Capture> ReadAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ReplenishmentReviewInput input, CancellationToken ct)
    {
        var location = StockSourceSetupService.StockCountLocation(input.Location);
        var source = await sources.CaptureHeldReceiptAsync(actor, authority, tx, location, input.PoolId,
            input.ExpectedPoolRevision, Permissions.StockRead, ct);
        source.RequireContext(sources, actor, authority, tx);
        var sourceView = StockSourceSetupService.HeldReceiptPoolView(source.Source);
        var filter = new BsonDocument { { "physicalPoolId", input.PoolId }, { "skuId", input.SkuId } };
        var rows = await Rows("stockBalances").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumLots + 1).ToListAsync(ct);
        if (rows.Count > MaximumLots) throw new DomainValidationException("replenishment_lot_limit", "This source has too many lots for an interactive review");
        var balances = new List<InventoryBalanceDocument>(); var lots = new List<BsonDocument>(); var warnings = new List<string>();
        if (!source.Pool["active"].AsBoolean) warnings.Add("stock_source_inactive");
        if (rows.Count == 0) warnings.Add("stock_baseline_missing");
        foreach (var row in rows) {
            var balance = await new InventoryRepository(database).ReadAsync(tx, row["_id"].AsInt64, ct);
            if (balance.PhysicalPoolId != input.PoolId || balance.Balance.Key.SkuId.Value != input.SkuId ||
                balance.Balance.Key.Location != InventoryGraph.Location(source.Pool["location"].AsBsonDocument)) throw InventoryBson.Invalid();
            var lot = await Rows("lots").Find(tx, new BsonDocument("_id", balance.Balance.Key.LotId.Value)).SingleAsync(ct);
            var facts = InventoryGraph.ReadLot(lot);
            if (balance.Balance.Key.DefinitionId.Value != input.DefinitionId && balance.Balance.OnHand > 0) warnings.Add("stock_old_definition");
            if (facts.ExpiresOn is { } expiry && expiry <= AccountingCalendar.BusinessDate(clock.UtcNow) && balance.Balance.OnHand > 0)
                warnings.Add("stock_expired_lot");
            balances.Add(balance); lots.Add(lot);
        }
        var selected = balances.Where(balance => balance.Balance.Key.DefinitionId.Value == input.DefinitionId).ToArray();
        if (selected.Length == 0 && rows.Count > 0) warnings.Add("stock_baseline_missing");
        // The Ordering owner proves complete reservations, including the genuinely empty producer state.
        // Every selected reserve is subtracted once by ReplenishmentPolicy; additional demand is not assumed zero.
        var commitments = await new OrderReplenishmentCommitments(database, unit, ids, clock)
            .ReadAndFenceAsync(tx, source, input.SkuId, input.DefinitionId, selected, ct);
        var captures = new List<ShipmentDispatchFiniteSource.Capture>();
        long usable = 0;
        if (warnings.Count == 0) {
            await sources.RequireActivatedPoolAsync(tx, source.Pool, ct);
            var finite = new ShipmentDispatchFiniteSource(database, unit, ids, clock);
            foreach (var balance in selected) {
                var capture = await finite.ReadAsync(tx, balance, ct);
                var physical = checked(balance.Balance.OnHand - balance.Balance.Held - balance.Balance.Expired);
                if (capture.Value.RemainingPackages != physical) throw InventoryBson.Invalid();
                usable = checked(usable + physical); captures.Add(capture);
            }
        }
        var stock = warnings.Count == 0 ? new ReplenishmentStock(true, usable, commitments.Reserved, commitments.AdditionalUnreserved) :
            new ReplenishmentStock(false, null, null, null);
        var provenance = new BsonDocument { { "source", sourceView }, { "sourceSnapshot", source.Source.DeepClone() }, { "balances", new BsonArray(rows) },
            { "lots", new BsonArray(lots) }, { "commitments", commitments.Provenance },
            { "finiteValues", new BsonArray(captures.Select(capture => new BsonDocument { { "balanceId", capture.Before.Id },
                { "revision", capture.Value.Revision }, { "remainingPackages", capture.Value.RemainingPackages } })) } };
        var frozenRows = new BsonArray(rows.Select(row => row.DeepClone()));
        void Current() {
            source.RequireContext(sources, actor, authority, tx);
            if (stock.CurrentAuthority && lots.Any(lot => InventoryGraph.ReadLot(lot).ExpiresOn is { } expiry &&
                expiry <= AccountingCalendar.BusinessDate(clock.UtcNow)))
                throw new PersistenceConflictException("replenishment_source_changed", "A lot expired while the review was being saved; recalculate");
        }
        async Task Complete(CancellationToken token) {
            Current();
            foreach (var capture in captures) await capture.RequireBeforeAsync(database, tx, token);
            var current = await Rows("stockBalances").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumLots + 1).ToListAsync(token);
            if (new BsonArray(current) != frozenRows) throw new PersistenceConflictException("replenishment_source_changed", "Stock changed; recalculate");
            // Immutable-byte replacement makes concurrent quantity edits conflict without changing business balances.
            foreach (var row in rows) {
                if ((await Rows("stockBalances").DeleteOneAsync(tx, row, cancellationToken: token)).DeletedCount != 1) throw InventoryBson.Invalid();
                await Rows("stockBalances").InsertOneAsync(tx, row, cancellationToken: token);
            }
            await source.FenceAsync(token);
            Current();
        }
        return new(stock, [.. warnings.Distinct(StringComparer.Ordinal)], provenance, Complete, Current);
    }
}
