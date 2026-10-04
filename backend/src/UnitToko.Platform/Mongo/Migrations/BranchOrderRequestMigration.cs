using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds request-only storage. Existing priced orders and reservations are never reinterpreted.</summary>
internal sealed class BranchOrderRequestMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 78;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, BsonDocument[] MissingUnique, BsonDocument[] MissingSupport);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var targets = new List<Target>();
        foreach (var name in BranchOrderRequestSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad("unexpected request data");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.GetValue("validationLevel", "strict") != "strict" ||
                options.GetValue("validationAction", "error") != "error" || options.TryGetValue("collation", out var collation) &&
                (!collation.IsBsonDocument || !collation.AsBsonDocument.Equals(new BsonDocument("locale", "simple")))) throw Bad("collection options");
            var required = BranchOrderRequestSchemas.Validator(name);
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous :
                new BsonDocument("$and", new BsonArray { previous, required });
            var installed = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            var unique = BranchOrderRequestSchemas.Keys(name); var support = BranchOrderRequestSchemas.SupportKeys(name);
            if (installed.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !unique.Any(key => index["key"] == key)) ||
                definition is not null && !installed.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1)))
                throw Bad("index options");
            foreach (var key in unique) if (installed.Any(index => index["key"] == key && !index.GetValue("unique", false).AsBoolean)) throw Bad("unique key");
            foreach (var key in support) if (installed.Any(index => index["key"] == key && index.GetValue("unique", false).AsBoolean)) throw Bad("support key");
            var counter = await database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 2)) throw Bad("allocator");
            targets.Add(new(name, definition is not null, validator,
                unique.Where(key => !installed.Any(index => index["key"] == key)).ToArray(),
                support.Where(key => !installed.Any(index => index["key"] == key)).ToArray()));
        }
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name },
                { "validator", target.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            for (var i = 0; i < target.MissingUnique.Length; i++) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(target.MissingUnique[i], new CreateIndexOptions { Name = "ux_branchRequest_" + target.Name + "_" + i, Unique = true }), cancellationToken: ct);
            }
            for (var i = 0; i < target.MissingSupport.Length; i++) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(target.MissingSupport[i], new CreateIndexOptions { Name = "ix_branchRequest_" + target.Name + "_" + i, Unique = false }), cancellationToken: ct);
            }
        }
    }
    private static InvalidOperationException Bad(string reason) => new("Branch request migration found incompatible " + reason + "; review before retrying");
}
