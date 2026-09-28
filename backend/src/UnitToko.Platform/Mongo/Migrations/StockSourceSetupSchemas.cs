using MongoDB.Bson;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Draft only. Installation and historical preflight belong to the reviewed forward migration.</summary>
internal static class StockSourceSetupSchemas
{
    internal static readonly string[] Collections = ["stockSourceProposals", "stockSourceDecisions", "inventoryPoolRegistrations", "inventoryPools"];
    internal static BsonDocument Key(string collection) => collection switch
    {
        "stockSourceDecisions" => new("proposalId", 1),
        "inventoryPoolRegistrations" => new() { { "organizationId", 1 }, { "physicalKey", 1 } },
        _ => new("_id", 1)
    };
    internal static BsonDocument Validator(string collection)
    {
        var fields = Header(collection);
        switch (collection)
        {
            case "stockSourceProposals":
                fields.Add("input", Input());
                fields.Add("revision", Number(1, 1));
                fields.Add("proposedBy", Number(1));
                fields.Add("proposedAt", new BsonDocument("bsonType", "date"));
                Metadata(fields);
                break;
            case "stockSourceDecisions":
                fields.Add("proposalId", Number(1)); fields.Add("revision", Number(1, 1));
                fields.Add("decision", Choice("approve", "reject", "withdraw"));
                fields.Add("decidedBy", Number(1)); fields.Add("decidedAt", new BsonDocument("bsonType", "date"));
                fields.Add("reviewReference", Text(160));
                fields.Add("poolId", new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } });
                Metadata(fields);
                break;
            case "inventoryPoolRegistrations":
                fields.Add("organizationId", Number(1)); fields.Add("physicalKey", Code());
                fields.Add("proposalId", Number(1)); fields.Add("decisionId", Number(1));
                break;
            case "inventoryPools":
                fields.Add("location", Location()); fields.Add("active", new BsonDocument("bsonType", "bool"));
                fields.Add("revision", Number(1)); fields.Add("fence", Number(0));
                break;
            default: throw new ArgumentException("Unknown stock source collection", nameof(collection));
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", Object(fields)) };
        if (collection == "stockSourceProposals")
        {
            terms.Add(DateRange("proposedAt"));
            terms.Add(new BsonDocument("$or", new BsonArray
            {
                new BsonDocument
                {
                    { "input.mode", "WebLedger" }, { "input.control", new BsonDocument("$in", new BsonArray { "ExclusiveWebAllocation", "SharedWebLedger" }) },
                    { "input.externalNamespace", BsonNull.Value }, { "input.reservationProtocolReference", BsonNull.Value }
                },
                new BsonDocument
                {
                    { "input.mode", "ExternalLedger" }, { "input.control", "ExternalHold" },
                    { "input.externalNamespace", new BsonDocument { { "$type", "string" }, { "$regex", CodePattern } } },
                    { "input.reservationProtocolReference", new BsonDocument("$type", "string") }
                }
            }));
        }
        if (collection == "stockSourceDecisions")
        {
            terms.Add(DateRange("decidedAt"));
            terms.Add(new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray
                { new BsonDocument("$eq", new BsonArray { "$decision", "approve" }), new BsonDocument("$ne", new BsonArray { "$poolId", BsonNull.Value }) })));
        }
        return terms.Count == 1 ? terms[0].AsBsonDocument : new("$and", terms);
    }
    private static BsonDocument DateRange(string field) => new(field, new BsonDocument
        { { "$gt", new BsonDateTime(-62135596800000L) }, { "$lte", new BsonDateTime(253402300799999L) } });
    // PCRE absolute anchors reject the otherwise accepted final newline.
    private const string CodePattern = @"\A[a-z][a-z0-9._-]{0,63}\z";
    private const string TextPattern = @"\A[^\p{Z}\s\p{Cc}](?:[^\p{Cc}]*[^\p{Z}\s\p{Cc}])?\z";
    private static BsonDocument Header(string collection) => new()
    {
        { "_id", Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "kind", Choice(StockSourceSetupBson.Kind(collection)) }
    };
    private static void Metadata(BsonDocument fields)
    {
        fields.Add("reason", Text(1000)); fields.Add("correlationId", Text(128)); fields.Add("auditId", Number(1));
    }
    private static BsonDocument Input() => Object(new()
    {
        { "location", Location() }, { "physicalKey", Code() }, { "label", Text(160) },
        { "mode", Choice("WebLedger", "ExternalLedger") },
        { "control", Choice("ExclusiveWebAllocation", "SharedWebLedger", "ExternalHold") },
        { "maximumAgeMilliseconds", Number(1, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) },
        { "safetyBufferBaseUnits", Number(0) },
        { "externalNamespace", NullableText(64) }, { "reservationProtocolReference", NullableText(160) },
        { "discrepancyOwnerReference", Text(160) }, { "handoffReference", Text(160) }
    });
    private static BsonDocument Location() => Object(new()
        { { "organizationId", Number(1) }, { "shopId", Number(1) }, { "warehouseId", Number(1) }, { "channelId", Number(1) } });
    private static BsonDocument Number(long minimum, long? maximum = null)
    {
        var schema = new BsonDocument { { "bsonType", "long" }, { "minimum", minimum } };
        if (maximum.HasValue) schema.Add("maximum", maximum.Value);
        return schema;
    }
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", TextPattern } };
    private static BsonDocument NullableText(int maximum) => new()
        { { "bsonType", new BsonArray { "string", "null" } }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", TextPattern } };
    private static BsonDocument Code() => new()
        { { "bsonType", "string" }, { "pattern", CodePattern }, { "maxLength", 64 } };
    private static BsonDocument Choice(params string[] values) => new()
        { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Object(BsonDocument fields) => new()
        { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
}
