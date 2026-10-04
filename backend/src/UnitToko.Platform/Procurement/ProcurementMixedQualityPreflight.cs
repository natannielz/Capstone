using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Procurement;

// Runs under predecessor storage, before migration 79 changes any validator or index.
// The retained readers accept both canonical v1 and explicit v2 rows and authenticate
// every private invoice, physical owner, quality decision, valuation and operation effect.
internal static class ProcurementMixedQualityPreflight
{
    internal static Task RequireAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources, CancellationToken ct) =>
        unit.ExecuteAsync(async (tx, token) => {
            var documents = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
            var drafts = database.GetCollection<BsonDocument>(SupplierDocumentBson.Drafts);
            using (var cursor = await drafts.Find(tx, FilterDefinition<BsonDocument>.Empty).ToCursorAsync(token))
                while (await cursor.MoveNextAsync(token)) foreach (var raw in cursor.Current)
                    _ = await documents.HistoryAsync(tx, raw["_id"].AsInt64, token);
            await documents.OrphansAsync(tx, token);
            await new ProcurementQualityAcceptanceRecords(database, unit, clock, catalog, orders, sources).PreflightAsync(tx, token);
            var valuations = new ProcurementStockValuationRecords(database, unit, clock, catalog, orders, sources);
            var proposals = database.GetCollection<BsonDocument>(ProcurementStockValuationBson.Proposals);
            long decided = 0;
            using (var cursor = await proposals.Find(tx, FilterDefinition<BsonDocument>.Empty).ToCursorAsync(token))
                while (await cursor.MoveNextAsync(token)) foreach (var raw in cursor.Current) {
                    var proposal = await valuations.ProposalAsync(tx, raw["_id"].AsInt64, token);
                    if (await valuations.DecisionAsync(tx, proposal, token) is not null) decided++;
                }
            if (await database.GetCollection<BsonDocument>(ProcurementStockValuationBson.Decisions)
                .CountDocumentsAsync(tx, FilterDefinition<BsonDocument>.Empty, cancellationToken: token) != decided)
                throw ProcurementStockValuationBson.Broken();
            await valuations.OrphansAsync(tx, token);
            return true;
        }, ct);
}
