using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

// The financial lease branch is retained byte-for-byte. Only the separately
// identified current-catalog-photo lease may name a CatalogSku target.
internal static class CatalogEvidenceReadSchemas
{
    internal const string Leases = "evidenceReadLeases";
    internal const string Audits = "evidenceReadAudits";

    internal static BsonDocument PreviousValidator(string name) => name switch {
        Leases => SupplierDocumentEvidenceSchemas.Validator(name),
        Audits => EvidenceAccessSchemas.Validator(name),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static BsonDocument CatalogLeaseShape(string purpose, bool current)
    {
        var branch = EvidenceAccessSchemas.Validator(Leases);
        var properties = branch["$and"][0]["$jsonSchema"]["properties"].AsBsonDocument;
        properties["target"]["properties"]["kind"]["enum"] = new BsonArray { "CatalogSku" };
        properties["purpose"] = new BsonDocument { { "bsonType", "string" }, { "enum", new BsonArray { purpose } } };
        var required = branch["$and"][0]["$jsonSchema"]["required"].AsBsonArray;
        required.Add("purpose");
        if (current) {
            properties["associationId"] = new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } };
            properties["associationHash"] = new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" }, { "minLength", 64 }, { "maxLength", 64 } };
            required.Add("associationId"); required.Add("associationHash");
        }
        return branch;
    }

    internal static BsonDocument CatalogLeaseValidator() => new("$or", new BsonArray {
        CatalogLeaseShape("catalog-photo-association", false), CatalogLeaseShape("catalog-photo-current", true) });

    internal static BsonDocument Validator(string name) => name switch {
        Leases => new BsonDocument("$or", new BsonArray { PreviousValidator(Leases), CatalogLeaseValidator() }),
        // Audits have no target discriminator. Their existing exact shape and
        // recordHash link to the strict catalog lease already admit both owners.
        Audits => PreviousValidator(Audits),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    internal static bool SupportsFinancial(BsonDocument current, BsonDocument required) =>
        (CatalogSchemas.SemanticEquals(required, SupplierDocumentEvidenceSchemas.Validator(Leases)) ||
         CatalogSchemas.SemanticEquals(required, SupplierDocumentEvidenceSchemas.PreviousValidator(Leases)) ||
         CatalogSchemas.SemanticEquals(required, ProcurementInspectionEvidenceSchemas.Validator(Leases)) ||
         CatalogSchemas.SemanticEquals(required, ProcurementDisbursementEvidenceSchemas.Validator(Leases)) ||
         CatalogSchemas.SemanticEquals(required, RefundPayoutEvidenceSchemas.Validator(Leases)) ||
         CatalogSchemas.SemanticEquals(required, EvidenceAccessSchemas.Validator(Leases))) &&
        InventorySchemas.Contains(current, Validator(Leases));

    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in new[] { Leases, Audits }) {
            if (name == Leases)
                // M87 preserves this entire reviewed union as its exact predecessor.
                // The compatibility helper recognizes only that named successor;
                // it never searches arbitrary $or alternatives for a usable branch.
                await SupplierDocumentEvidenceSchemas.RequireCompatibleStorageAsync(database, name,
                    Validator(name), EvidenceAccessSchemas.Key(name), ct);
            else await InventoryRepository.RequireStorageAsync(database, name, Validator(name), EvidenceAccessSchemas.Key(name), ct);
        }
    }
}
