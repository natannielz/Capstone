using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Draft storage: production installation requires a forward migration with complete historical preflight.</summary>
internal static class StockSourceAliasSchemas
{
    internal static readonly string[] Collections = ["stockSourceAliasProposals", "stockSourceAliasDecisions", "stockSourceAliases"];
    internal static BsonDocument Key(string name) => name switch
    {
        "stockSourceAliasProposals" => new("_id", 1),
        "stockSourceAliasDecisions" => new("proposalId", 1),
        "stockSourceAliases" => new() { { "poolId", 1 }, { "requestedLocation.channelId", 1 }, { "aliasRevision", 1 } },
        _ => throw StockSourceAliasBson.Invalid()
    };
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument
        {
            { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(StockSourceAliasBson.Kind(name)) }, { "poolId", S.Number(1) }, { "poolRevision", S.Number(1) }
        };
        var terms = new BsonArray();
        if (name is "stockSourceAliasProposals" or "stockSourceAliases")
        {
            var location = S.Input()["properties"]["location"].AsBsonDocument;
            fields.Add("ownerLocation", location.DeepClone()); fields.Add("requestedLocation", location.DeepClone());
            fields.Add("physicalKey", S.Input()["properties"]["physicalKey"].DeepClone());
            fields.Add("enabled", new BsonDocument("bsonType", "bool"));
            terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                Same("organizationId"), Same("shopId"), Same("warehouseId"),
                new BsonDocument("$ne", new BsonArray { "$ownerLocation.channelId", "$requestedLocation.channelId" })
            })));
        }
        switch (name)
        {
            case "stockSourceAliasProposals":
                fields.Add("baseAliasRevision", S.Number(0, long.MaxValue - 1)); fields.Add("revision", S.Number(1, 1));
                fields.Add("proposedBy", S.Number(1)); fields.Add("proposedAt", new BsonDocument("bsonType", "date")); S.Metadata(fields);
                terms.Add(S.DateRange("proposedAt"));
                terms.Add(new BsonDocument("$or", new BsonArray
                    { new BsonDocument("baseAliasRevision", new BsonDocument("$gt", 0L)), new BsonDocument("enabled", true) }));
                break;
            case "stockSourceAliasDecisions":
                fields.Add("proposalId", S.Number(1)); fields.Add("revision", S.Number(1, 1)); fields.Add("requestedLocation", S.Input()["properties"]["location"].DeepClone());
                fields.Add("aliasRevision", new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } });
                fields.Add("decision", S.Choice("approve", "reject", "withdraw")); fields.Add("decidedBy", S.Number(1));
                fields.Add("decidedAt", new BsonDocument("bsonType", "date")); fields.Add("reviewReference", S.Text(160)); S.Metadata(fields);
                terms.Add(S.DateRange("decidedAt"));
                terms.Add(new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray
                    { new BsonDocument("$eq", new BsonArray { "$decision", "approve" }), new BsonDocument("$ne", new BsonArray { "$aliasRevision", BsonNull.Value }) })));
                break;
            case "stockSourceAliases":
                fields.Add("aliasRevision", S.Number(1)); fields.Add("proposalId", S.Number(1)); fields.Add("decisionId", S.Number(1));
                break;
            default: throw StockSourceAliasBson.Invalid();
        }
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields)));
        return new("$and", terms);
    }
    private static BsonDocument Same(string field) => new("$eq", new BsonArray { "$ownerLocation." + field, "$requestedLocation." + field });
}
