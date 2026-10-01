using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;
internal static class ReturnFinancialApprovalStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in ReturnFinancialApprovalSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, ReturnFinancialApprovalSchemas.Validator(name), ReturnFinancialApprovalSchemas.Keys(name), ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (definition["options"].AsBsonDocument.Contains("encryptedFields")) throw B.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
        }
    }
}
