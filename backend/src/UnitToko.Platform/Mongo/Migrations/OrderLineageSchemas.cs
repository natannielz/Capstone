using MongoDB.Bson;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Forward version114 contracts. Earlier migration schemas are deliberately unchanged.
/// Only recognized order/fulfillment/invoice line objects gain optional amendment identity;
/// catalog definitions, price versions, inventory balances and physical lots stay unchanged.</summary>
internal static class OrderLineageSchemas
{
    internal static readonly string[] ChangedCollections = ["reservations", "orderDecisions", "shipmentPreparationVersions",
        "shipments", "shipmentReceipts", "invoices", "invoiceSourceAllocations"];
    internal static BsonDocument OrderValidator(string name) => name is "reservations" or "orderDecisions"
        ? Expand(OrderSubmissionSchemas.Validator(name)) : OrderSubmissionSchemas.Validator(name);
    internal static BsonDocument OrderKey(string name) => name == "reservations"
        ? new() { { "orderId", 1 }, { "allocation.balanceId", 1 }, { "allocation.lineRevision", 1 } } : OrderSubmissionSchemas.Key(name);
    internal static BsonDocument PreparationValidator(string name) => Expand(ShipmentPreparationSchemas.Validator(name));
    internal static BsonDocument DispatchValidator() => Expand(ShipmentDispatchSchemas.Validator());
    internal static BsonDocument ReceiptValidator(string name) => Expand(ShipmentReceiptSchemas.Validator(name));
    internal static BsonDocument InvoiceValidator(string name) => Expand(InvoiceSchemas.Validator(name));
    internal static BsonDocument InvoiceAllocationKey() => InvoiceSchemas.AllocationKey();
    internal static BsonDocument Previous(string name) => name switch {
        "reservations" or "orderDecisions" => OrderSubmissionSchemas.Validator(name),
        "shipmentPreparationVersions" => ShipmentPreparationSchemas.Validator(name),
        "shipments" => ShipmentDispatchSchemas.Validator(),
        "shipmentReceipts" => ShipmentReceiptSchemas.Validator(name),
        "invoices" or "invoiceSourceAllocations" => InvoiceSchemas.Validator(name),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    // Replace only the recognized canonical conjunct. Operator-supplied constraints are
    // retained byte-for-byte; they are never interpreted or expanded by this migration.
    internal static BsonDocument UpgradeRetained(BsonDocument current, BsonDocument previous, BsonDocument next) {
        if (CatalogSchemas.SemanticEquals(current, previous)) return next.DeepClone().AsBsonDocument;
        var result = current.DeepClone().AsBsonDocument;
        if (result.TryGetValue("$and", out var terms) && terms is BsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (array[index] is BsonDocument child && InventorySchemas.Contains(child, previous))
                    array[index] = UpgradeRetained(child, previous, next);
        if (!InventorySchemas.Contains(result, next)) throw new InvalidOperationException("The retained canonical line schema is unavailable");
        return result;
    }
    internal static BsonDocument Expand(BsonDocument original) {
        var result = original.DeepClone().AsBsonDocument;
        Visit(result); return result;
    }
    private static void Visit(BsonValue value) {
        if (value is BsonArray array) { foreach (var child in array) Visit(child); return; }
        if (value is not BsonDocument document) return;
        if (document.TryGetValue("properties", out var propertyValue) && propertyValue is BsonDocument properties) {
            var quoted = properties.Contains("familyId") && properties.Contains("definition") && properties.Contains("price") && properties.Contains("packages");
            var line = properties.Contains("skuId") && (
                properties.Contains("authority") && properties.Contains("balanceId") ||
                properties.Contains("packages") && !properties.Contains("definitionId") && !properties.Contains("_id") ||
                properties.Contains("definition") && properties.Contains("orderedPackages") ||
                properties.Contains("dispatchedBaseUnits") && properties.Contains("acceptedBaseUnits") ||
                properties.Contains("receiptId") && (properties.Contains("net") || properties.Contains("tax")));
            if (quoted || line) {
                if (properties.Contains("lineRevision")) throw new InvalidOperationException("A historical line schema already defines amendment identity");
                properties.Add("lineRevision", StockSourceSetupSchemas.Number(2, 103));
            }
        }
        foreach (var element in document.ToArray()) Visit(element.Value);
    }
}
