using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class ShipmentDispatchMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 24;
    private sealed record Index(string Name, BsonDocument Key, bool Unique = false);
    private static Index[] Indexes() => [new("ux_shipment_order_sequence", ShipmentDispatchSchemas.Key(), true),
        new("ux_shipment_number", ShipmentDispatchSchemas.NumberKey(), true),
        new("ix_shipment_request", new() { { "dispatchedBy", 1 }, { "requestKey", 1 } }),
        new("ix_shipment_preparation_version", new("preparationVersionId", 1)),
        new("ix_shipment_suborder", new() { { "suborderId", 1 }, { "_id", -1 } }),
        new("ix_shipment_store", new() { { "fulfillmentOrganizationId", 1 }, { "shopId", 1 }, { "channelId", 1 }, { "_id", -1 } }),
        new("ix_shipment_courier", new() { { "courierId", 1 }, { "_id", -1 } })];
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        const string name = ShipmentDispatchBson.Collection; await lease.RenewAsync(ct);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted shipment data");
        var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
        if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
            options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
        var required = ShipmentDispatchSchemas.Validator(); var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
        var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct); var expected = Indexes();
        if (indexes.Any(Unsafe) || indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean &&
            !expected.Any(wanted => wanted.Unique && index["key"].Equals(wanted.Key)))) throw Invalid("retention or extra uniqueness");
        if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
        var create = new List<Index>();
        foreach (var wanted in expected) {
            var matching = indexes.Where(index => !FoundationNumberIndex(index) && (index["name"] == wanted.Name || index["key"].Equals(wanted.Key))).ToArray();
            if (matching.Any(index => !index["key"].Equals(wanted.Key) || index.GetValue("unique", false).AsBoolean != wanted.Unique)) throw Invalid("required index");
            if (matching.Length == 0) create.Add(wanted);
        }
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
        if (await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("_id", new BsonRegularExpression(@"^shipment-number\."))).AnyAsync(ct)) throw Invalid("unaccepted document numbering");
        var prefix = new BsonRegularExpression(@"^fulfillment\.shipment(?:[.:]|$)");
        foreach (var (store, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", prefix)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("operation", prefix), new BsonDocument("kind", prefix) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("sourceKey", prefix) })),
            ("movements", new("$or", new BsonArray { new BsonDocument("source.namespace", prefix), new BsonDocument("sourceEvent", prefix) }))
        }) {
            await lease.RenewAsync(ct);
            if (await database.GetCollection<BsonDocument>(store).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted shipment effects");
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { definition is null ? "create" : "collMod", name }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var index in create) {
            await lease.RenewAsync(ct);
            await rows.WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key,
                new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: ct);
        }
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    // Retain the exact v1 partial index and install a full unique index alongside it. Mandatory
    // number fields now cover every dispatch. No unfamiliar partial predicate is accepted.
    internal static bool FoundationNumberIndex(BsonDocument index) => index["name"] == "uq_numberEntity_numberChannel_numberYear_number" &&
        index["key"].Equals(ShipmentDispatchSchemas.NumberKey()) && index.GetValue("unique", false).AsBoolean &&
        index.TryGetValue("partialFilterExpression", out var partial) && CatalogSchemas.SemanticEquals(partial,
            new BsonDocument("$and", new BsonArray(ShipmentDispatchSchemas.NumberKey().Names.Select(name => new BsonDocument(name, new BsonDocument("$exists", true))))));
    private static bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.Contains("partialFilterExpression") && !FoundationNumberIndex(index) ||
        index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static InvalidOperationException Invalid(string reason) => new("Shipment dispatch migration preflight failed: " + reason);
}
