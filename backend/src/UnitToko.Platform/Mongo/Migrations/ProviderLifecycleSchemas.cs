using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v5 storage contract. Provider payloads, tokens and raw subjects do not belong here.
internal static class ProviderLifecycleSchemas
{
    public static IReadOnlyDictionary<string, BsonDocument> Collections() => new Dictionary<string, BsonDocument>
    {
        ["identitySecurityEvents"] = new("$jsonSchema", SecurityEvent()),
        ["identityAdmissionHolds"] = new("$and", new BsonArray
        {
            new BsonDocument("$jsonSchema", AdmissionHold()),
            new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$_id", "$sourceEventId" }))
        }),
        ["identityProviderSubjects"] = new("$jsonSchema", ProviderSubject())
    };

    private static BsonDocument ProviderSubject()
    {
        var schema = Document(new()
        {
            { "_id", Hash() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "issuer", Text(2048) }, { "fence", new BsonDocument { { "bsonType", "long" }, { "minimum", 0L } } },
            { "admissionRestricted", new BsonDocument("bsonType", "bool") }, { "updatedAt", Date() }
        });
        // Only new provider changes establish a cutoff; historical subjects have no inferred timestamp.
        schema["properties"].AsBsonDocument.Add("proofNotBefore", new BsonDocument("bsonType", new BsonArray { "date", "null" }));
        return schema;
    }

    private static BsonDocument SecurityEvent()
    {
        var schema = Document(new()
        {
            { "_id", Hash() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "issuer", Text(2048) }, { "eventId", Text(300) }, { "subjectHash", Hash() }, { "fingerprint", Hash() },
            { "change", Choice("accountDisabled", "accountDeleted", "passwordChanged", "factorChanged", "factorRemoved") },
            { "occurredAt", Date() }, { "receivedAt", Date() }, { "outcome", Choice("applied", "unknown") },
            { "userId", NullableLong() }, { "previousSecurityVersion", NullableLong() }, { "securityVersion", NullableLong() }
        });
        schema.Add("oneOf", new BsonArray { Outcome("applied", Long()), Outcome("unknown", new BsonDocument("bsonType", "null")) });
        return schema;
    }

    private static BsonDocument AdmissionHold() => Document(new()
    {
        { "_id", Hash() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "userId", Long() }, { "sourceEventId", Hash() }, { "reason", Choice("providerBlocked", "providerDeleted", "factorRemoved") },
        { "status", Choice("active") }, { "createdAt", Date() }, { "securityVersion", Long() }, { "version", Long() }
    });

    private static BsonDocument Outcome(string outcome, BsonDocument reference) => new("properties", new BsonDocument
    {
        { "outcome", Choice(outcome) }, { "userId", reference }, { "previousSecurityVersion", reference }, { "securityVersion", reference }
    });
    private static BsonDocument Document(BsonDocument properties) => new()
    {
        { "bsonType", "object" }, { "required", new BsonArray(properties.Names) },
        { "additionalProperties", false }, { "properties", properties }
    };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum } };
    private static BsonDocument Hash() => new()
        { { "bsonType", "string" }, { "minLength", 64 }, { "maxLength", 64 }, { "pattern", "^[a-f0-9]{64}\\z" } };
    private static BsonDocument Long() => new() { { "bsonType", "long" }, { "minimum", 1L } };
    private static BsonDocument NullableLong() => new() { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } };
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Date() => new("bsonType", "date");
}
