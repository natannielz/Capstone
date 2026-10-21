using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using P = UnitToko.Platform.Catalog.CatalogPublicShowcaseBson;
using S = UnitToko.Platform.Mongo.Migrations.CatalogPublicSampleSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Replaces the assortment-level guest showcase (version 121) with chosen sample products. Every assortment that was
/// approved public becomes sample rows for the products it publishes now, so nothing a guest could see disappears silently and
/// nothing new appears. The 121 records stay as history. Resumable: an interrupted conversion continues with the same rows.</summary>
internal sealed class CatalogPublicSampleMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 122;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); await RequireHistoryAsync(ct);
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in S.Collections) {
            await lease.RenewAsync(ct);
            var rows = Rows(name);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            // Proposals start empty. Samples may hold only rows this conversion wrote before an interruption.
            if (name == S.Proposals && await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            if (name == S.Samples && await rows.Find(new BsonDocument("migratedFrom", BsonNull.Value)).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = S.Keys(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var matching = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                if (matching.Any(index => index["key"] != keys[i] || !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matching.Length == 0) missing.Add(keys[i]);
            }
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = S.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        var prefix = new BsonRegularExpression(@"^catalog\.public\.sample(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("outboxEvents", "sourceKey") })
            if (await Rows(table).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Bad();
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name },
                { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(S.Keys(item.Name), expected => expected == key) }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await ConvertAsync(ct);
    }

    /// <summary>Each assortment approved public (and still active) contributes the active products it currently publishes. A product
    /// in several public assortments takes the lowest assortment as its source.</summary>
    private async Task ConvertAsync(CancellationToken ct) {
        var expected = new SortedDictionary<long, BsonDocument>();
        var heads = await Rows(P.Heads).Find(new BsonDocument("isPublic", true)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var head in heads) {
            if (!head.TryGetValue("_id", out var assortmentId) || !assortmentId.IsInt64 || !head.TryGetValue("decisionId", out var decisionId) ||
                !decisionId.IsInt64 || !head.TryGetValue("updatedAt", out var updatedAt) || !updatedAt.IsBsonDateTime) throw Bad();
            var assortment = CatalogRepository.Assortment(await Rows("catalogAssortments").Find(new BsonDocument("_id", assortmentId)).SingleOrDefaultAsync(ct));
            if (assortment["status"] != "active") continue;
            var owner = CatalogRepository.Owner(assortment["ownerScope"].AsBsonDocument);
            var listed = Builders<BsonDocument>.Filter.And(Builders<BsonDocument>.Filter.Eq("active", true),
                Builders<BsonDocument>.Filter.Eq("organizationId", owner.OrganizationId!.Value),
                Builders<BsonDocument>.Filter.ElemMatch<BsonDocument>("catalogListings", Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("assortmentId", assortmentId), UnitToko.Platform.Authorization.CatalogVisibilityQuery.Owner("ownerScope", owner))));
            using var cursor = await Rows("skus").DistinctAsync<long>("familyId", listed, cancellationToken: ct);
            var families = (await cursor.ToListAsync(ct)).Order().ToArray();
            foreach (var familyId in families) {
                if (expected.ContainsKey(familyId)) continue;
                var family = await Rows("productFamilies").Find(new BsonDocument { { "_id", familyId }, { "active", true } }).SingleOrDefaultAsync(ct);
                if (family is null) continue;
                var category = await Rows("catalogCategories").Find(new BsonDocument { { "_id", family["categoryId"] }, { "active", true } }).SingleOrDefaultAsync(ct);
                if (category is null) continue;
                expected[familyId] = new BsonDocument { { "_id", familyId }, { "schemaVersion", 1 },
                    { "ownerScope", CatalogRepository.Owner(CatalogRepository.Owner(family["authoringOwner"].AsBsonDocument)) }, { "revision", 1L },
                    { "addedAt", updatedAt }, { "approvedProposalId", BsonNull.Value },
                    { "migratedFrom", new BsonDocument { { "assortmentId", assortmentId }, { "showcaseDecisionId", decisionId } } } };
            }
        }
        var samples = Rows(S.Samples);
        var existing = await samples.Find(new BsonDocument()).ToListAsync(ct);
        // A resumed run may only find rows it would write itself.
        if (existing.Any(row => !expected.TryGetValue(row["_id"].AsInt64, out var wanted) || wanted != row)) throw Bad();
        foreach (var row in expected.Values.Where(row => existing.All(present => present["_id"] != row["_id"])))
            await samples.InsertOneAsync(row, cancellationToken: ct);
        if (expected.Count > 0)
            await Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", S.SetCounter), Builders<BsonDocument>.Update.Max("value", 1L),
                new UpdateOptions { IsUpsert = true }, ct);
    }

    private IMongoCollection<BsonDocument> Rows(string name) =>
        database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).WithWriteConcern(WriteConcern.WMajority);
    private async Task RequireHistoryAsync(CancellationToken ct) {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Bad();
    }
    private static InvalidOperationException Bad() => new("Catalog public sample migration preflight failed");
}
