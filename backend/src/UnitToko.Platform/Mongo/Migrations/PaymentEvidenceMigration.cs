using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class PaymentEvidenceMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 31;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var replacements = new List<(string Name, BsonDocument Validator)>();
        foreach (var name in EvidenceSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (definition is null || definition.GetValue("type", "") != "collection") throw Bad(name);
            var options = definition["options"].AsBsonDocument;
            if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad(name);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var replacement = PaymentEvidenceSchemas.Collections.Contains(name) ? PaymentEvidenceSchemas.Extend(name, current) : current;
            if (!PaymentEvidenceSchemas.Collections.Contains(name) && !InventorySchemas.Contains(current, EvidenceSchemas.Validator(name))) throw Bad(name);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            // No payment writer can run before this migration's acceptance. Resume only v13 graphs.
            if (await rows.Find(new BsonDocument("$nor", new BsonArray { current })).Limit(1).AnyAsync(ct) ||
                await rows.Find(new BsonDocument("$nor", new BsonArray { EvidenceSchemas.Validator(name) })).Limit(1).AnyAsync(ct)) throw Bad(name);
            var indexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            RequireIndexes(name, indexes);
            if (PaymentEvidenceSchemas.Collections.Contains(name)) replacements.Add((name, replacement));
        }
        await EvidenceMigrationGraph.ValidateAsync(database, lease, ct);
        foreach (var (counterName, collections) in new (string, string[])[] {
            ("evidenceIds", ["evidenceLineages", "documentVersions", "evidenceUploadReservations", "evidenceUploadAttempts"]),
            ("evidenceStorePolicies", ["evidenceStorePolicies"]), ("auditEvents", ["auditEvents"]) }) {
            long maximum = 0;
            foreach (var name in collections) {
                var last = await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Limit(1).SingleOrDefaultAsync(ct);
                if (last is not null) { if (!last["_id"].IsInt64 || last["_id"].AsInt64 is <= 0 or long.MaxValue) throw Bad(name); maximum = Math.Max(maximum, last["_id"].AsInt64); }
            }
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", counterName)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or long.MaxValue) throw Bad(counterName);
        }
        foreach (var item in replacements) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", item.Name }, { "validator", item.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
    }
    internal static void RequireIndexes(string name, IReadOnlyList<BsonDocument> indexes) {
        if (!indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad(name);
        foreach (var index in indexes) {
            if (InvoiceStorage.Unsafe(index)) throw Bad(name);
            if (index.TryGetValue("partialFilterExpression", out var partial) && !ValidPartial(name, index, partial)) throw Bad(name);
            if ((name == "auditEvents" && index["key"].Equals(new BsonDocument("observationId", 1)) || name == "documentVersions" && index["key"].Equals(UniqueKeys(name)[0])) && !index.Contains("partialFilterExpression")) throw Bad(name);
            if (index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !UniqueKeys(name).Any(key => key.Equals(index["key"]))) throw Bad(name);
        }
        foreach (var key in UniqueKeys(name))
            if (!indexes.Any(index => index["key"].Equals(key) && index.GetValue("unique", false).AsBoolean)) throw Bad(name);
    }
    private static BsonDocument[] UniqueKeys(string name) => name switch {
        "evidenceStorePolicies" => [new("storeId", 1)],
        "documentVersions" => [new() { { "documentId", 1 }, { "version", 1 } }, new() { { "lineageId", 1 }, { "contentVersion", 1 } }, new("reservationId", 1)],
        "evidenceUploadReservations" => [new("attemptId", 1), new("versionRowId", 1), new("lineageId", 1)],
        "evidenceUploadAttempts" => [new("reservationId", 1), new() { { "storeId", 1 }, { "stageToken", 1 } }, new() { { "storeId", 1 }, { "generation", 1 } }],
        "auditEvents" => [new("observationId", 1)],
        "idempotencyRecords" => [new() { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } }], "outboxEvents" => [new("sourceKey", 1)], _ => [] };
    private static bool ValidPartial(string name, BsonDocument index, BsonValue partial) {
        if (name == "auditEvents" && index["name"] == "ux_stock_count_audit_observation" && index["key"].Equals(new BsonDocument("observationId", 1)))
            return CatalogSchemas.SemanticEquals(partial, new BsonDocument("kind", UnitToko.Platform.Inventory.StockCountBson.AuditKind));
        string[] fields = name switch { "documentVersions" when index["key"].Equals(UniqueKeys(name)[0]) => ["documentId", "version"],
            "idempotencyRecords" when index["key"].Equals(UniqueKeys(name)[0]) => ["actorId", "operation", "key"],
            "outboxEvents" when index["key"].Equals(UniqueKeys(name)[0]) => ["sourceKey"], _ => [] };
        return fields.Length > 0 && CatalogSchemas.SemanticEquals(partial, new BsonDocument("$and", new BsonArray(fields.Select(field => new BsonDocument(field, new BsonDocument("$exists", true))))));
    }
    private static InvalidOperationException Bad(string name) => new("Payment evidence migration preflight failed: " + name);
}
