using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Draft contract only; a separate forward migration must preflight and install this storage.</summary>
internal static class StockSourceRevisionSchemas
{
    internal static readonly string[] Collections = ["stockSourceRevisionProposals", "stockSourceRevisionDecisions", "stockSourceRevisions"];
    internal static BsonDocument Key(string collection) => collection switch
    {
        "stockSourceRevisionProposals" => new("_id", 1),
        "stockSourceRevisionDecisions" => new("proposalId", 1),
        "stockSourceRevisions" => new() { { "poolId", 1 }, { "poolRevision", 1 } },
        _ => throw StockSourceRevisionBson.Invalid()
    };
    internal static BsonDocument Validator(string collection)
    {
        var fields = new BsonDocument
        {
            { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(StockSourceRevisionBson.Kind(collection)) }, { "poolId", S.Number(1) }
        };
        var terms = new BsonArray();
        switch (collection)
        {
            case "stockSourceRevisionProposals":
                fields.Add("basePoolRevision", S.Number(1, long.MaxValue - 1));
                fields.Add("baseInput", S.Input()); fields.Add("input", S.Input()); fields.Add("revision", S.Number(1, 1));
                fields.Add("proposedBy", S.Number(1)); fields.Add("proposedAt", new BsonDocument("bsonType", "date")); S.Metadata(fields);
                terms.Add(S.DateRange("proposedAt"));
                terms.Add(ValidSettings("input")); terms.Add(ValidSettings("baseInput"));
                terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { "$input.location", "$baseInput.location" }),
                    new BsonDocument("$eq", new BsonArray { "$input.physicalKey", "$baseInput.physicalKey" }),
                    new BsonDocument("$ne", new BsonArray { "$input", "$baseInput" })
                })));
                break;
            case "stockSourceRevisionDecisions":
                fields.Add("proposalId", S.Number(1)); fields.Add("revision", S.Number(1, 1));
                fields.Add("basePoolRevision", S.Number(1, long.MaxValue - 1));
                fields.Add("poolRevision", new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 2L } });
                fields.Add("decision", S.Choice("approve", "reject", "withdraw")); fields.Add("decidedBy", S.Number(1));
                fields.Add("decidedAt", new BsonDocument("bsonType", "date")); fields.Add("reviewReference", S.Text(160)); S.Metadata(fields);
                terms.Add(S.DateRange("decidedAt"));
                terms.Add(new BsonDocument("$expr", new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { "$decision", "approve" }),
                    new BsonDocument("$eq", new BsonArray { "$poolRevision", new BsonDocument("$add", new BsonArray { "$basePoolRevision", 1L }) }),
                    new BsonDocument("$eq", new BsonArray { "$poolRevision", BsonNull.Value })
                })));
                break;
            case "stockSourceRevisions":
                fields.Add("poolRevision", S.Number(2)); fields.Add("proposalId", S.Number(1));
                fields.Add("decisionId", S.Number(1)); fields.Add("input", S.Input());
                terms.Add(ValidSettings("input"));
                break;
            default: throw StockSourceRevisionBson.Invalid();
        }
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields)));
        return new("$and", terms);
    }
    private static BsonDocument ValidSettings(string field) => new("$or", new BsonArray
    {
        new BsonDocument
        {
            { field + ".mode", "WebLedger" }, { field + ".control", new BsonDocument("$in", new BsonArray { "ExclusiveWebAllocation", "SharedWebLedger" }) },
            { field + ".externalNamespace", BsonNull.Value }, { field + ".reservationProtocolReference", BsonNull.Value }
        },
        new BsonDocument
        {
            { field + ".mode", "ExternalLedger" }, { field + ".control", "ExternalHold" },
            { field + ".externalNamespace", new BsonDocument { { "$type", "string" }, { "$regex", @"\A[a-z][a-z0-9._-]{0,63}\z" } } },
            { field + ".reservationProtocolReference", new BsonDocument("$type", "string") }
        }
    });
}
