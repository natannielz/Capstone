using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;
namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Suspension storage installed by migration 18 after complete historical preflight.</summary>
internal static class StockSourceSuspensionSchemas
{
    internal const string Collection = "stockSourceSuspensions";
    internal static BsonDocument Key() => new() { { "poolId", 1 }, { "suspensionRevision", 1 } };
    internal sealed record LookupIndex(string Collection, string Name, BsonDocument Key);
    internal static LookupIndex[] LookupIndexes() =>
    [
        new("auditEvents", "ix_stock_source_suspension_audit", new() { { "kind", 1 }, { "record.poolId", 1 }, { "record.suspensionRevision", 1 } }),
        new("idempotencyRecords", "ix_stock_source_suspension_receipt", new() { { "operation", 1 }, { "result.poolId", 1 }, { "result.suspensionId", 1 } })
    ];
    internal static BsonDocument Validator()
    {
        var location = S.Input()["properties"]["location"].AsBsonDocument;
        var date = new BsonDocument("bsonType", "date");
        var boolean = new BsonDocument("bsonType", "bool");
        var source = S.Object(new()
        {
            { "poolRevision", S.Number(1) }, { "active", boolean }, { "input", S.Input() }, { "approvedAt", date },
            { "creationProposalId", S.Number(1) },
            { "revisionProposalId", new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } } }
        });
        var mapping = S.Object(new()
        {
            { "aliasId", S.Number(1) }, { "aliasRevision", S.Number(1) }, { "poolRevision", S.Number(1) },
            { "requestedLocation", location }, { "enabled", boolean }, { "proposalId", S.Number(1) }, { "decisionId", S.Number(1) }
        });
        var fields = new BsonDocument
        {
            { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice("stock.source.suspension") }, { "poolId", S.Number(1) }, { "poolRevision", S.Number(1) },
            { "ownerLocation", location }, { "physicalKey", S.Input()["properties"]["physicalKey"].DeepClone() },
            { "suspensionRevision", S.Number(1) },
            { "configuration", S.Object(new() { { "source", source }, { "mappings", new BsonDocument { { "bsonType", "array" }, { "items", mapping } } } }) },
            { "configurationHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } } },
            { "suspendedBy", S.Number(1) }, { "suspendedAt", date }
        };
        S.Metadata(fields);
        return new("$and", new BsonArray
        {
            new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("suspendedAt"), S.DateRange("configuration.source.approvedAt"),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$poolRevision", "$configuration.source.poolRevision" }),
                new BsonDocument("$eq", new BsonArray { "$ownerLocation", "$configuration.source.input.location" }),
                new BsonDocument("$eq", new BsonArray { "$physicalKey", "$configuration.source.input.physicalKey" }),
                new BsonDocument("$lte", new BsonArray { "$configuration.source.approvedAt", "$suspendedAt" })
            }))
        });
    }
}
