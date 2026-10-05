using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

internal static class PaymentSupplementCorrectionStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, string name, CancellationToken ct, bool requireAnnotated = false)
    {
        var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw PaymentCorrectionRecords.Bad();
        var validator = definition.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var successor = PaymentSupplementCorrectionSchemas.Validator(name);
        var previous = PaymentCorrectionSchemas.Validator(name);
        var required = InventorySchemas.Contains(validator, successor) ? successor
            : !requireAnnotated && InventorySchemas.Contains(validator, previous) ? previous : throw PaymentCorrectionRecords.Bad();
        if (!AccountContactStorage.SafeDefinition(definition, required)) throw PaymentCorrectionRecords.Bad();
        await InventoryRepository.RequireStorageAsync(database, name, required, PaymentCorrectionSchemas.Key(name), ct);
    }
}
