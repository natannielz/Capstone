using MongoDB.Bson;
using UnitToko.Platform.Identity;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class AccountContactSchemas
{
    internal static readonly string[] Collections = [AccountContactRecords.Heads, AccountContactRecords.History,
        AccountContactRecords.Receipts];
    // Frozen v2 identity validator, used only to recognize its retained conjunction. Do not replace or rewrite it.
    internal static BsonDocument PriorIdentityValidator() => new("$jsonSchema", new BsonDocument {
        { "bsonType", "object" }, { "required", new BsonArray { "_id", "schemaVersion", "issuer", "subject", "status", "grantVersion", "fence" } },
        { "properties", new BsonDocument {
            { "_id", new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } } },
            { "schemaVersion", new BsonDocument("enum", new BsonArray { 1 }) },
            { "issuer", PriorText(2048) }, { "subject", PriorText(512) },
            { "status", new BsonDocument("enum", new BsonArray { "pending", "active", "suspended", "revoked" }) },
            { "grantVersion", new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } } },
            { "fence", new BsonDocument { { "bsonType", "long" }, { "minimum", 0L } } } } }
    });
    private static BsonDocument PriorText(int max) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", max } };
    private static BsonDocument Long(long min = 1, long max = long.MaxValue - 2) => S.Number(min, max);
    private static BsonDocument Int(int min, int max) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Fields() => new() { { "name", S.Text(160) }, { "phone", Nullable(S.Text(40)) },
        { "position", Nullable(S.Text(160)) }, { "address", Nullable(S.Text(1000)) } };

    internal static BsonDocument Validator(string name)
    {
        if (name == AccountContactRecords.Heads) {
            var fields = Fields();
            fields.InsertAt(0, new BsonElement("_id", Long()));
            fields.Add("schemaVersion", Int(1, 1)); fields.Add("revision", Long(1, 256));
            fields.Add("lastHistoryId", Long()); fields.Add("lastHash", Hash());
            fields.Add("updatedAt", Date()); fields.Add("fence", Long(0));
            return new("$jsonSchema", S.Object(fields));
        }
        if (name == AccountContactRecords.History) {
            var fields = Fields();
            fields.InsertAt(0, new BsonElement("_id", Long()));
            fields.Add("schemaVersion", Int(1, 1)); fields.Add("userId", Long());
            fields.Add("revision", Long(1, 256)); fields.Add("actorId", Long());
            fields.Add("actorGrantVersion", Long(0)); fields.Add("requestKey", S.Text(200));
            fields.Add("reason", S.Text(1000)); fields.Add("correlationId", S.Text(128));
            fields.Add("recordedAt", Date()); fields.Add("previousHistoryId", Nullable(Long()));
            fields.Add("previousHash", Nullable(Hash())); fields.Add("bodyHash", Hash());
            fields.Add("snapshotHash", Hash());
            return new("$jsonSchema", S.Object(fields));
        }
        if (name == AccountContactRecords.Receipts) return new("$jsonSchema", S.Object(new() {
            { "_id", new BsonDocument("bsonType", "objectId") }, { "schemaVersion", Int(1, 1) },
            { "actorId", Long() }, { "key", S.Text(200) }, { "bodyHash", Hash() },
            { "historyId", Long() }, { "historyHash", Hash() }, { "recordedAt", Date() } }));
        throw new ArgumentException("Unknown account contact collection", nameof(name));
    }

    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        AccountContactRecords.Heads => [],
        AccountContactRecords.History => [("ux_accountContact_user_revision", new BsonDocument { { "userId", 1 }, { "revision", 1 } }, true),
            ("ix_accountContact_user_id", new BsonDocument { { "userId", 1 }, { "_id", -1 } }, false)],
        AccountContactRecords.Receipts => [("ux_accountContact_actor_key", new BsonDocument { { "actorId", 1 }, { "key", 1 } }, true),
            ("ux_accountContact_history", new BsonDocument("historyId", 1), true)],
        _ => throw new ArgumentException("Unknown account contact collection", nameof(name))
    };
}
