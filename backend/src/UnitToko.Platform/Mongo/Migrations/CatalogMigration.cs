using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v11 validates explicit counted-catalog masters; it never infers or seeds catalog data.
internal sealed class CatalogMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 11;
    private sealed record RequiredIndex(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Plan(string Name, bool Exists, BsonDocument Validator, List<RequiredIndex> Indexes, long Maximum);
    private static RequiredIndex[] Indexes(string collection) => collection switch
    {
        "catalogCategories" => [new("ux_organization_code", new() { { "organizationId", 1 }, { "code", 1 } }, true), new("ix_authoring_owner", OwnerKey())],
        "productFamilies" => [new("ux_publicId", new("publicId", 1), true), new("ux_publicUuid", new("publicUuid", 1), true), new("ix_authoring_owner_category", OwnerKey("categoryId"))],
        "skus" => [new("uq_organizationId_sku", new() { { "organizationId", 1 }, { "sku", 1 } }, true), new("ux_publicId", new("publicId", 1), true), new("ux_publicUuid", new("publicUuid", 1), true), new("ix_family_id", new() { { "familyId", 1 }, { "_id", 1 } })],
        "variantDefinitions" => [new("ux_sku_version", new() { { "skuId", 1 }, { "version", 1 } }, true)],
        "catalogIdentifiers" => [new("ux_reserved_identifier", new() { { "organizationId", 1 }, { "kind", 1 }, { "namespace", 1 }, { "canonicalValue", 1 } }, true), new("ux_sku_primary", new("skuId", 1), true)],
        "catalogIdentifierHistory" => [new("ux_identifier_assignment", new() { { "identifierId", 1 }, { "assignmentVersion", 1 } }, true)],
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };
    private static BsonDocument OwnerKey(string? last = null)
    {
        var key = new BsonDocument { { "authoringOwner.organizationId", 1 }, { "authoringOwner.shopId", 1 }, { "authoringOwner.warehouseId", 1 }, { "authoringOwner.channelId", 1 } };
        if (last is not null) key.Add(last, 1);
        key.Add("_id", 1); return key;
    }
    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var plans = new List<Plan>();
        var documents = new Dictionary<string, Dictionary<long, BsonDocument>>();
        foreach (var name in CatalogSchemas.Collections)
        {
            await lease.RenewAsync(token);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "collection") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !SimpleCollation(collation))
                throw Incompatible(name, "collection options");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = CatalogSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var records = await rows.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(token);
            if (records.Any(row => !CatalogSchemas.IsValid(name, row)) ||
                await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token)) throw Incompatible(name, "records");
            documents[name] = records.ToDictionary(row => row["_id"].AsInt64);
            var existing = definition is null ? [] : await (await rows.Indexes.ListAsync(token)).ToListAsync(token);
            if (existing.Any(index => index.Contains("expireAfterSeconds")) || definition is not null && !existing.Any(IsIdentityIndex)) throw Incompatible(name, "index");
            var create = new List<RequiredIndex>();
            foreach (var planned in Indexes(name))
            {
                var equivalent = false;
                foreach (var index in existing)
                {
                    var sameKey = index["key"].AsBsonDocument.Equals(planned.Key);
                    if (index["name"] != planned.Name && !sameKey) continue;
                    if (!sameKey || index.GetValue("unique", false).AsBoolean != planned.Unique || UnsafeRequiredIndex(name, index)) throw Incompatible(name, "index");
                    equivalent = true;
                }
                if (planned.Unique && records.GroupBy(row => new BsonDocument(planned.Key.Names.Select(field => new BsonElement(field, row[field])))).Any(group => group.Count() > 1))
                    throw Incompatible(name, "duplicate unique keys");
                if (!equivalent) create.Add(planned);
            }
            var maximum = records.Count == 0 ? 0 : records.Max(row => row["_id"].AsInt64);
            {
                var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(token);
                if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < 0 || value.AsInt64 == long.MaxValue) || maximum == long.MaxValue)
                    throw Incompatible(name, "idCounters high-water mark");
            }
            plans.Add(new(name, definition is not null, validator, create, maximum));
        }
        await CatalogMigrationGraph.ValidateAsync(database, documents, token);

        // Catalog commands and staff-read evidence share the existing audit allocator.
        // Its high-water mark covers all audit kinds without inspecting unrelated payloads.
        var auditIds = await database.GetCollection<BsonDocument>("auditEvents").WithReadConcern(ReadConcern.Majority)
            .Find(FilterDefinition<BsonDocument>.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(token);
        if (auditIds.Any(row => !row.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 <= 0 || id.AsInt64 == long.MaxValue))
            throw Incompatible("auditEvents", "idCounters high-water mark");
        var auditMaximum = auditIds.Count == 0 ? 0 : auditIds.Max(row => row["_id"].AsInt64);
        var auditCounter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("_id", "auditEvents")).SingleOrDefaultAsync(token);
        if (auditCounter is not null && (!auditCounter.TryGetValue("value", out var auditValue) || !auditValue.IsInt64 || auditValue.AsInt64 < 0 || auditValue.AsInt64 == long.MaxValue))
            throw Incompatible("auditEvents", "idCounters high-water mark");

        // Every collection, row, relationship, index and sequence has passed before the first DDL/counter write.
        foreach (var plan in plans)
        {
            await lease.RenewAsync(token);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { plan.Exists ? "collMod" : "create", plan.Name }, { "validator", plan.Validator }, { "validationLevel", "strict" },
                { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: token);
        }
        foreach (var plan in plans)
        {
            if (plan.Maximum > 0)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", plan.Name),
                    Builders<BsonDocument>.Update.Max("value", plan.Maximum), new UpdateOptions { IsUpsert = true }, token);
            }
            foreach (var index in plan.Indexes)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>(plan.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: token);
            }
        }
        if (auditMaximum > 0)
        {
            await lease.RenewAsync(token);
            await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", "auditEvents"),
                Builders<BsonDocument>.Update.Max("value", auditMaximum), new UpdateOptions { IsUpsert = true }, token);
        }
    }

    private static bool IsIdentityIndex(BsonDocument index) => index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && index["name"] == "_id_" && !UnsafeIndexOptions(index);
    private static bool UnsafeRequiredIndex(string collection, BsonDocument index)
    {
        // v1 installed this exact partial unique index while the keys were optional.
        // Both keys are now mandatory, so its predicate covers every valid v11 row.
        if (collection == "skus" && index["name"] == "uq_organizationId_sku" && index.TryGetValue("partialFilterExpression", out var partial) &&
            CatalogSchemas.SemanticEquals(partial, new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("organizationId", new BsonDocument("$exists", true)), new BsonDocument("sku", new BsonDocument("$exists", true))
            })))
        {
            var options = index.DeepClone().AsBsonDocument; options.Remove("partialFilterExpression"); return UnsafeIndexOptions(options);
        }
        return UnsafeIndexOptions(index);
    }
    private static bool UnsafeIndexOptions(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var collation) && !SimpleCollation(collation);
    private static bool SimpleCollation(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) || validator.TryGetValue("$and", out var value) && value.IsBsonArray && value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
    private static InvalidOperationException Incompatible(string collection, string reason) => new($"Catalog migration found incompatible {reason} in {collection}; reconcile them before retrying");
}
