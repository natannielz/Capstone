using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockCountService
{
    private StockSourceSetupService CountSources => new(database, unit, ids, clock);
    public async Task<StockCountSourcePage> SourcesAsync(ActorContext actor, GrantScope location, int offset, int limit, CancellationToken ct = default) {
        StockCountPolicy.Location(location); AuthorizationStore.ValidatePage(offset, limit);
        await InventoryCountStockSourceStorage.RequireAsync(database, ct);
        return await unit.ExecuteAsync(async (tx, token) => {
            var at = clock.UtcNow; var context = await BeginAsync(actor, Permissions.StockCount, tx, token); RequireLocation(context, location);
            await new StockSourceTopologyFence(database).FenceAsync(tx, location.OrganizationId ?? throw InventoryCountStockSourceRecords.Unavailable(), token);
            var sources = CountSources; var person = await sources.ReadInventoryCountPersonAsync(tx, actor.UserId, token);
            var candidates = await Rows("inventoryPools").Find(tx, new BsonDocument { { "location.organizationId", location.OrganizationId ?? throw InventoryCountStockSourceRecords.Unavailable() },
                { "location.shopId", location.ShopId ?? throw InventoryCountStockSourceRecords.Unavailable() }, { "location.warehouseId", location.WarehouseId ?? throw InventoryCountStockSourceRecords.Unavailable() } }).Sort(new BsonDocument("_id", 1))
                .Limit(InventoryCountStockSourceRecords.MaximumRows + 1).ToListAsync(token);
            if (candidates.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
            var items = ImmutableArray.CreateBuilder<StockCountSourceChoice>();
            foreach (var candidate in candidates) {
                InventoryGraph.RequirePool(candidate);
                if (!Current(context).Matches(CatalogRepository.Resource(InventoryGraph.Location(candidate["location"].AsBsonDocument).ToScope()))) continue;
                InventoryCountSourceFacts facts;
                try { facts = await sources.ReadInventoryCountSourceFactsAsync(actor, context.Snapshot, tx, candidate["_id"].AsInt64, Permissions.StockCount, token); }
                catch (AuthorizationDeniedException denied) when (denied.Code == "scope_denied") { continue; }
                if (!StockSourceSetupService.CountSourceMatches(facts, location)) continue;
                // A missing legacy/new producer is unavailable, not a silently
                // fabricated eligible empty source. Unbound counting still works.
                var expected = new StockSourceObligationExpectation(facts.Binding.PoolId, InventoryGraph.Location(facts.Pool["location"].AsBsonDocument),
                    facts.Binding.PoolRevision, facts.Pool["fence"].AsInt64);
                _ = await new InventoryCountStockSourceRecords(database).ReadAsync(tx, this, sources, expected, token);
                await sources.FenceInventoryCountAuthorityAsync(actor, context.Snapshot, tx, facts.Resources, Permissions.StockCount, token);
                items.Add(new(facts.Binding, location));
            }
            await sources.FenceInventoryCountPersonAsync(tx, person, token); await CompleteAsync(context, token);
            person.RequireCurrent(tx, clock.UtcNow); if (clock.UtcNow < at) throw InventoryCountStockSourceRecords.Unavailable();
            return new StockCountSourcePage(items.Count, [.. items.Skip(offset).Take(limit)]);
        }, ct);
    }
    private async Task<StockCountObservation> RecordBoundCountAsync(ActorContext actor, StockCountInput input,
        AuthorizationCommandMetadata metadata, CancellationToken ct) {
        var selected = input.PoolSelection ?? throw InventoryCountStockSourceRecords.Unavailable(); selected.Validate();
        await InventoryCountStockSourceStorage.RequireAsync(database, ct);
        var hash = StockCountBson.BodyHash(input, metadata.Reason);
        var id = await ids.NextAsync(StockCountBson.Collection, ct); var auditId = await ids.NextAsync("auditEvents", ct); var receiptId = ObjectId.GenerateNewId();
        return await unit.ExecuteAsync(async (tx, token) => {
            // Capture is fresh on every UOW attempt. No source/person/authority
            // handle is imported across retries or recreated from an old fence.
            var context = await BeginAsync(actor, Permissions.StockCount, tx, token);
            var receipt = await Rows("idempotencyRecords").Find(tx, ReceiptFilter(actor.UserId, metadata.IdempotencyKey)).SingleOrDefaultAsync(token);
            if (receipt is not null) {
                var original = await ReceiptAsync(context, receipt, token);
                if (receipt["bodyHash"] != hash) throw new UnitToko.Platform.Mongo.PersistenceConflictException("idempotency_key_reused", "Request key was used with a different count source");
                await CompleteAsync(context, token); return original;
            }
            await RequireMissingBoundKeyAsync(context, metadata.IdempotencyKey, token);
            RequireLocation(context, input.Location); var sources = CountSources;
            await new StockSourceTopologyFence(database).FenceAsync(tx, input.Location.OrganizationId ?? throw InventoryCountStockSourceRecords.Unavailable(), token);
            var capture = await InventoryCountPoolCapture.ReadAsync(sources, actor, context.Snapshot, tx, input.Location, selected, token);
            var result = await catalog.WithStockCountDefinitionAsync(database, tx, actor, input.Location, input.SkuId,
                input.ExpectedSkuRevision, input.ExpectedDefinitionId, async (definition, callbackToken) => {
                    capture.Require(database, tx); Current(context);
                    var row = StockCountBson.Create(id.Value, auditId.Value, actor, input, StockCountBson.Definition(definition), metadata,
                        clock.UtcNow, capture.Binding, capture.SourceHash);
                    await Rows(StockCountBson.Collection).InsertOneAsync(tx, row, cancellationToken: callbackToken);
                    await Rows("auditEvents").InsertOneAsync(tx, StockCountBson.Audit(row), cancellationToken: callbackToken);
                    // Custom count protocol persists the complete observation,
                    // with its original marker and no empty generic result/outbox.
                    await Rows("idempotencyRecords").InsertOneAsync(tx, StockCountBson.Receipt(row, receiptId), cancellationToken: callbackToken);
                    var proof = await new InventoryCountStockSourceProducer(database, ids, clock).RecordFreshAsync(tx, this, sources, row, capture, callbackToken);
                    await new InventoryCountCoverage(database).AppendFreshAsync(tx, this, sources, row, callbackToken);
                    await capture.FenceAsync(database, tx, callbackToken); Current(context);
                    await proof.RequireFinalAsync(database, tx, row, callbackToken);
                    return (Observation: await GraphAsync(tx, row, callbackToken), Row: row, Proof: proof);
                }, token);
            await CompleteAsync(context, token); capture.Require(database, tx); Current(context);
            await result.Proof.RequireFinalAsync(database, tx, result.Row, token); return result.Observation;
        }, ct);
    }
    private async Task<StockCountObservation> GraphAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken token, BsonDocument? receipt = null) {
        var result = await GraphObservationAsync(tx, row, token, receipt);
        var records = new InventoryCountStockSourceRecords(database);
        if (result.PoolBinding is not null) await records.RequireObservationAsync(tx, this, CountSources, row, token);
        else if (await Rows(InventoryCountStockSourceRecords.Associations).Find(tx, new BsonDocument("$or", new BsonArray {
            new BsonDocument("_id", result.Id), new BsonDocument("observationId", result.Id) })).AnyAsync(token)) throw InventoryCountStockSourceRecords.Unavailable();
        return result;
    }
    private Task FenceBoundContextAsync(Context context, StockCountObservation observation, CancellationToken ct) => observation.PoolBinding is { } binding
        ? CountSources.FenceInventoryCountScopeAsync(context.Actor, context.Snapshot, context.Tx, binding.PoolId, context.Permission, ct)
        : Task.CompletedTask;
    private async Task RequireMissingBoundKeyAsync(Context context, string key, CancellationToken ct) {
        var association = await Rows(InventoryCountStockSourceRecords.Associations).Find(context.Tx, new BsonDocument {
            { "actorId", context.Actor.UserId }, { "requestKey", key } }).Limit(2).ToListAsync(ct);
        var count = await Rows(StockCountBson.Collection).Find(context.Tx, new BsonDocument {
            { "actorId", context.Actor.UserId }, { "requestKey", key }, { "schemaVersion", 2 } }).Limit(2).ToListAsync(ct);
        if (association.Count == 0 && count.Count == 0) return;
        foreach (var row in association.Concat(count)) RequireLocation(context, StockCountBson.Scope(StockCountBson.Document(row, "location")));
        throw InventoryCountStockSourceRecords.Unavailable();
    }
    private async Task RequireMissingBoundObservationAsync(Context context, long id, CancellationToken ct) {
        var association = await Rows(InventoryCountStockSourceRecords.Associations).Find(context.Tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        if (association is null) return; var location = StockCountBson.Scope(StockCountBson.Document(association, "location"));
        if (!Current(context).Matches(CatalogRepository.Resource(location))) return;
        RequireLocation(context, location); throw InventoryCountStockSourceRecords.Unavailable();
    }
    private async Task RequireBoundListMembershipAsync(Context context, GrantScope location, CancellationToken ct) {
        var rows = await Rows(InventoryCountStockSourceRecords.Associations).Find(context.Tx, CatalogRepository.OwnerFilter("location", location))
            .Limit(InventoryCountStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
        if (rows.Count > InventoryCountStockSourceRecords.MaximumRows) throw InventoryCountStockSourceRecords.Unavailable();
        foreach (var association in rows)
            if (!await Rows(StockCountBson.Collection).Find(context.Tx, new BsonDocument { { "_id", association["observationId"] },
                { "location", association["location"] }, { "schemaVersion", 2 } }).AnyAsync(ct)) throw InventoryCountStockSourceRecords.Unavailable();
    }
}
