using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Mongo.Migrations
{
    internal sealed partial class SupplierDocumentMigration
    {
        internal Task PreflightRetainedInspectionsForClaimsAsync(MigrationLease lease, CancellationToken ct) => PreflightInspectionsAsync(lease, ct);
    }
}

namespace UnitToko.Platform.Documents
{
    internal static partial class SupplierDocumentEvidenceSchemas
    {
        // Read-only forward preflight. Existing migration factories and their empty-owner gates stay unchanged.
        internal static async Task RequireRetainedStorageForClaimsAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct) {
            foreach (var name in Sources) {
                await lease.RenewAsync(ct);
                var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
                var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
                if (definition is null || definition.GetValue("type", "") != "collection" || options is null ||
                    options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                    options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                    options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
                var required = Collections.Contains(name) ? Validator(name) : Previous(name);
                var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
                if (!InventorySchemas.Contains(current, required)) throw Bad(name);
                var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
                foreach (var validator in new[] { current, required })
                    if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad(name);
                RequireIndexes(name, await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct));
            }
            await RequireStorageAsync(database, ct);
            await RequireCountersAsync(database, lease, ct);
        }
    }
}
