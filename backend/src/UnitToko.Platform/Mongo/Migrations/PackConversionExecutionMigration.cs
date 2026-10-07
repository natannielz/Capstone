using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Reviewed empty conversion owner and additive movement actions only.
/// Existing quantities/value/history are retained, never inferred or reinitialized.</summary>
internal sealed class PackConversionExecutionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 104;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Conversion storage requires preserved103 history and reviewed movement definitions");
    internal static BsonDocument ExtendMovementValidator(BsonDocument installed) {
        var legacy = InventoryMovementSchemas.LegacyValidator(); var required = InventoryMovementSchemas.Validator();
        var oldActions = legacy["$and"][0]["$jsonSchema"]["properties"]["legs"]["items"]["properties"]["action"]["enum"];
        var newActions = required["$and"][0]["$jsonSchema"]["properties"]["legs"]["items"]["properties"]["action"]["enum"];
        var oldEffects = legacy["$and"][1]; var newEffects = required["$and"][1]; var replacedActions = 0; var replacedEffects = 0;
        BsonValue Rewrite(BsonValue value) {
            if (value == oldEffects) { replacedEffects++; return newEffects.DeepClone(); }
            if (value.IsBsonArray) return new BsonArray(value.AsBsonArray.Select(Rewrite));
            if (!value.IsBsonDocument) return value.DeepClone();
            var result = new BsonDocument();
            foreach (var field in value.AsBsonDocument) {
                if (field.Name == "action" && field.Value.IsBsonDocument && field.Value.AsBsonDocument.GetValue("enum", BsonNull.Value) == oldActions) {
                    var action = field.Value.DeepClone().AsBsonDocument; action["enum"] = newActions.DeepClone(); result.Add(field.Name, action); replacedActions++;
                } else result.Add(field.Name, Rewrite(field.Value));
            }
            return result;
        }
        var updated = Rewrite(installed).AsBsonDocument;
        if (replacedActions != 1 || replacedEffects != 1) throw Invalid();
        return updated;
    }
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(105).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(103).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        var name = PackConversionBson.Conversions;
        var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
        if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, PackConversionSchemas.Validator()) ||
            !OrderStockSourceStorage.SafeIndexes((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray(), PackConversionSchemas.Keys()))) throw Invalid();
        if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var counter = counters.SingleOrDefault(row => row["_id"] == name);
        if (counter is not null && (!counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
        var retained = (await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct))
            .Where(row => row.GetValue("type", "") == "collection" && row["name"] != name).ToArray();
        var indexes = new Dictionary<string, BsonDocument[]>();
        foreach (var retainedDefinition in retained) indexes.Add(retainedDefinition["name"].AsString,
            (await (await Rows(retainedDefinition["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        var movement = retained.SingleOrDefault(row => row["name"] == "movements") ?? throw Invalid();
        var installed = movement["options"].AsBsonDocument.GetValue("validator", BsonNull.Value);
        if (!installed.IsBsonDocument) throw Invalid();
        BsonDocument? extended = null;
        if (!AccountContactStorage.SafeDefinition(movement, InventoryMovementSchemas.Validator())) {
            if (!AccountContactStorage.SafeDefinition(movement, InventoryMovementSchemas.LegacyValidator())) throw Invalid();
            extended = ExtendMovementValidator(installed.AsBsonDocument);
            var prospective = movement.DeepClone().AsBsonDocument; prospective["options"]["validator"] = extended;
            if (!AccountContactStorage.SafeDefinition(prospective, InventoryMovementSchemas.Validator())) throw Invalid();
        }
        await lease.RenewAsync(ct);
        if (definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "validator", PackConversionSchemas.Validator() },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        var ownerIndexes = (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
        foreach (var key in PackConversionSchemas.Keys().Where(key => !ownerIndexes.Any(index => AccountContactStorage.Matches(index, key))))
            await Rows(name).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        if (extended is not null) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "movements" }, { "validator", extended },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
        foreach (var before in retained) {
            var collection = before["name"].AsString; var after = await AccountContactStorage.DefinitionAsync(database, collection, ct) ?? throw Invalid();
            var expectedOptions = before.GetValue("options", new BsonDocument()).DeepClone().AsBsonDocument;
            if (collection == "movements" && extended is not null) expectedOptions["validator"] = extended;
            var actualIndexes = (await (await Rows(collection).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (before["info"]["uuid"] != after["info"]["uuid"] || after.GetValue("options", new BsonDocument()) != expectedOptions ||
                indexes[collection].Length != actualIndexes.Length || indexes[collection].Any(index => !actualIndexes.Contains(index))) throw Invalid();
        }
        if (!counters.SequenceEqual(await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct))) throw Invalid();
        await PackConversionSchemas.RequireStorageAsync(database, ct);
        await InventoryRepository.RequireStorageAsync(database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), ct);
    }
}
