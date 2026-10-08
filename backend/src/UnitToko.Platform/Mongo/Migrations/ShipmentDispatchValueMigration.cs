using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Adds genuine finite dispatch without rewriting any accepted shipment
/// or removing an operator's additional validator restrictions.</summary>
internal sealed class ShipmentDispatchValueMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 105;
    private static InvalidOperationException Invalid() => new("Finite dispatch requires preserved104 shipments and reviewed storage guards");
    internal static BsonDocument ExtendValidator(BsonDocument installed) {
        var legacy = ShipmentDispatchSchemas.LegacyValidator(); var current = ShipmentDispatchSchemas.Validator(); var dispatchClauses = 0; var foundationClauses = 0;
        var foundation = FoundationMigration.BusinessValidator(); var extendedFoundation = foundation.DeepClone().AsBsonDocument;
        extendedFoundation["$and"][0]["$jsonSchema"]["properties"]["schemaVersion"]["enum"] = new BsonArray { 1, 2 };
        BsonValue Rewrite(BsonValue value) {
            if (value == legacy || value == current) { dispatchClauses++; return current.DeepClone(); }
            if (value == foundation || value == extendedFoundation) { foundationClauses++; return extendedFoundation.DeepClone(); }
            // Only owning conjuncts are replaceable. An operator's nested OR/NOT,
            // expression or unrelated schema remains its original exact bytes.
            if (value.IsBsonDocument && value.AsBsonDocument.ElementCount == 1 && value.AsBsonDocument.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                return new BsonDocument("$and", new BsonArray(terms.AsBsonArray.Select(Rewrite)));
            if (value.IsBsonDocument && UnknownVersionRule(value.AsBsonDocument)) throw Invalid();
            return value.DeepClone();
        }
        var updated = Rewrite(installed).AsBsonDocument;
        if (dispatchClauses != 1 || foundationClauses > 1) throw Invalid(); return updated;
    }
    private static bool UnknownVersionRule(BsonDocument clause) {
        bool ReferencesVersion(BsonValue value) => value.IsString ? value.AsString is "schemaVersion" or "$schemaVersion" :
            value.IsBsonArray ? value.AsBsonArray.Any(ReferencesVersion) : value.IsBsonDocument &&
                value.AsBsonDocument.Any(field => field.Name == "schemaVersion" || ReferencesVersion(field.Value));
        return ReferencesVersion(clause);
    }
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        const string name = ShipmentDispatchBson.Collection;
        var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
        var installed = definition["options"].AsBsonDocument.GetValue("validator", BsonNull.Value);
        if (!installed.IsBsonDocument) throw Invalid();
        var current = AccountContactStorage.SafeDefinition(definition, ShipmentDispatchSchemas.Validator());
        try { await ShipmentDispatchStorage.RequireAsync(database, ct, current ? ShipmentDispatchSchemas.Validator() : ShipmentDispatchSchemas.LegacyValidator()); }
        catch { throw Invalid(); }
        var valueSignals = new BsonDocument("$or", new BsonArray {
            new BsonDocument("source.namespace", "fulfillment.shipment"), new BsonDocument("sourceEvent", new BsonRegularExpression(@"^fulfillment\.shipment:")) });
        if (await database.GetCollection<BsonDocument>(UnitToko.Platform.Inventory.LotValueStore.Transitions).WithReadConcern(ReadConcern.Majority).Find(valueSignals).Limit(1).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>("journalEntries").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("$or", new BsonArray {
                new BsonDocument("source.namespace", "fulfillment.shipment"), new BsonDocument("sourceEvent", new BsonRegularExpression(@"^fulfillment\.shipment:")) })).Limit(1).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>("accountingAuditEvents").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("eventKey", new BsonRegularExpression(@"^fulfillment\.shipment:"))).Limit(1).AnyAsync(ct) ||
            await database.GetCollection<BsonDocument>("outboxEvents").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("$or", new BsonArray {
                new BsonDocument("sourceKey", new BsonRegularExpression(@"^fulfillment\.shipment:[0-9]+:dispatchCogs:")),
                new BsonDocument("kind", "fulfillment.shipment.cogs") })).Limit(1).AnyAsync(ct)) throw Invalid();
        var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        var retained = await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (await rows.Find(new BsonDocument("$nor", new BsonArray { ShipmentDispatchSchemas.LegacyValidator() })).Limit(1).AnyAsync(ct)) throw Invalid();
        var beforeIndexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (!current && !AccountContactStorage.SafeDefinition(definition, ShipmentDispatchSchemas.LegacyValidator())) throw Invalid();
        var updated = ExtendValidator(installed.AsBsonDocument);
        var prospective = definition.DeepClone().AsBsonDocument; prospective["options"]["validator"] = updated;
        if (!AccountContactStorage.SafeDefinition(prospective, ShipmentDispatchSchemas.Validator())) throw Invalid();
        await lease.RenewAsync(ct);
        if (updated != installed) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", updated },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        var after = await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var afterIndexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
        if (new BsonArray(retained) != new BsonArray(after) || new BsonArray(beforeIndexes) != new BsonArray(afterIndexes)) throw Invalid();
        var final = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
        if (final["options"]["validator"] != updated || !AccountContactStorage.SafeDefinition(final, ShipmentDispatchSchemas.Validator())) throw Invalid();
    }
}
