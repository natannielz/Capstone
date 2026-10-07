using MongoDB.Bson;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Inventory;

public sealed class PackConversionMovementValidatorTests
{
    [Fact]
    public void Reviewed_legacy_extension_changes_only_recognized_action_effects_and_preserves_operator_restrictions() {
        var legacy = InventoryMovementSchemas.LegacyValidator();
        var extra = new BsonDocument("reason", new BsonDocument("$regex", "approved-"));
        var installed = new BsonDocument("$and", new BsonArray { legacy.DeepClone(), extra.DeepClone() });
        var updated = PackConversionExecutionMigration.ExtendMovementValidator(installed);
        Assert.Equal(InventoryMovementSchemas.Validator(), updated["$and"][0]);
        Assert.Equal(extra, updated["$and"][1]);
        Assert.Equal(legacy, installed["$and"][0]);
        var restricted = legacy.DeepClone().AsBsonDocument;
        restricted["$and"][0]["$jsonSchema"]["properties"]["legs"]["items"]["properties"]["action"]["enum"] = new BsonArray { "ReceiveHeld" };
        Assert.Throws<InvalidOperationException>(() => PackConversionExecutionMigration.ExtendMovementValidator(restricted));
        Assert.Throws<InvalidOperationException>(() => PackConversionExecutionMigration.ExtendMovementValidator(new BsonDocument("$expr", true)));
    }
}
