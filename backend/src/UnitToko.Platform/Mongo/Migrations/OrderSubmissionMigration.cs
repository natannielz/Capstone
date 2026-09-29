using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Installs the first canonical order writer under a deployment lease and operator quiescence.
/// No pre-22 order writer was authorized. Existing business/effect records require an explicit reviewed
/// conversion; this step never guesses their commercial, permission or reservation provenance.</summary>
internal sealed class OrderSubmissionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 22;
    private sealed record Index(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, Index[] Create);
    private static Index[] RequiredIndexes(string name) => name switch {
        "orders" => [new("ix_order_request", new() { { "createdBy", 1 }, { "requestKey", 1 } }),
            new("ix_order_buyer", new() { { "buyingOrganizationId", 1 }, { "branchId", 1 }, { "createdBy", 1 }, { "_id", -1 } })],
        "orderSuborders" => [new("ix_order_suborder_parent", new("orderId", 1)),
            new("ix_order_suborder_shop", new() { { "fulfillmentOrganizationId", 1 }, { "shopId", 1 }, { "channelId", 1 }, { "_id", -1 } })],
        "reservations" => [new("ux_order_reservation_balance", OrderSubmissionSchemas.Key(name), true),
            new("ix_order_reservation_pool", new() { { "allocation.poolId", 1 }, { "remaining", 1 } })],
        "orderDecisions" => [new("ux_order_decision_revision", OrderSubmissionSchemas.Key(name), true),
            new("ix_order_decision_request", new() { { "decidedBy", 1 }, { "stage", 1 }, { "action", 1 }, { "requestKey", 1 } })],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var targets = new List<Target>();
        // Preflight every target and reverse effect reference before the first target DDL.
        foreach (var name in OrderSubmissionSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unconverted order or reservation data");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !Simple(collation)) throw Invalid("collection options");
            var required = OrderSubmissionSchemas.Validator(name); var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            var expected = RequiredIndexes(name);
            if (indexes.Any(Unsafe) || indexes.Any(index => index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean &&
                !expected.Any(wanted => wanted.Unique && index["key"].Equals(wanted.Key)))) throw Invalid("retention or extra uniqueness");
            if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
            var create = new List<Index>();
            foreach (var wanted in expected) {
                var matching = indexes.Where(index => index["name"] == wanted.Name || index["key"].Equals(wanted.Key)).ToArray();
                if (matching.Any(index => !index["key"].Equals(wanted.Key) || index.GetValue("unique", false).AsBoolean != wanted.Unique)) throw Invalid("required index");
                if (matching.Length == 0) create.Add(wanted);
            }
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
            targets.Add(new(name, definition is not null, validator, [.. create]));
        }
        var operation = new BsonRegularExpression(@"^ordering\.order\.");
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", operation)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("operation", operation), new BsonDocument("kind", operation) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", operation), new BsonDocument("sourceKey", new BsonRegularExpression(@"^ordering\.order[:.]")) })),
            ("movements", new("$or", new BsonArray {
                new BsonDocument("source.namespace", new BsonDocument("$in", new BsonArray { "ordering.order", "ordering.decision" })),
                new BsonDocument("sourceEvent", new BsonRegularExpression(@"^ordering\.(order|decision):")) }))
        }) {
            await lease.RenewAsync(ct);
            if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted order effect evidence");
        }
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var index in target.Create) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: ct);
            }
        }
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.Contains("partialFilterExpression") || index.GetValue("sparse", false).AsBoolean ||
        index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var collation) && !Simple(collation);
    private static InvalidOperationException Invalid(string reason) => new("Order submission migration found incompatible " + reason + "; preserve and review before retrying");
}
