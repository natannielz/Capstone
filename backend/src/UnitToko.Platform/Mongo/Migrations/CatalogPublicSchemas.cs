using MongoDB.Bson;
using B = UnitToko.Platform.Catalog.CatalogPublicShowcaseBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Which assortments the public (signed-out) catalog shows, decided maker/checker per assortment. No price or buyer data.</summary>
internal static class CatalogPublicSchemas
{
    internal static readonly string[] Collections = [B.Heads, B.Proposals, B.Decisions];
    private static BsonDocument Format => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Flag => new("bsonType", "bool");
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Heads => [],
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    private static void Command(BsonDocument fields, params string[] operations) {
        fields.AddRange(new BsonDocument { { "operation", S.Choice(operations) }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
            { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } });
        S.Metadata(fields);
    }
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", Format } };
        var dates = new List<string>(); var checks = new BsonArray();
        switch (name) {
            case B.Heads:
                fields.AddRange(new BsonDocument { { "isPublic", Flag }, { "revision", S.Number(0, long.MaxValue - 1) }, { "decisionId", Nullable(S.Number(1)) },
                    { "updatedAt", Date }, { "fence", S.Number(0, long.MaxValue - 1) } });
                dates.Add("updatedAt");
                checks.Add(new BsonDocument("$or", new BsonArray {
                    new BsonDocument("$and", new BsonArray { Eq("$revision", 0L), Eq("$decisionId", BsonNull.Value), Eq("$isPublic", false) }),
                    new BsonDocument("$and", new BsonArray { new BsonDocument("$gte", new BsonArray { "$revision", 1L }),
                        new BsonDocument("$ne", new BsonArray { "$decisionId", BsonNull.Value }) }) }));
                break;
            case B.Proposals:
                fields.AddRange(new BsonDocument { { "assortmentId", S.Number(1) }, { "makePublic", Flag }, { "baseRevision", S.Number(0, long.MaxValue - 2) },
                    { "revision", S.Number(1, 1) }, { "proposedBy", S.Number(1) }, { "proposedAt", Date } });
                Command(fields, B.Prefix + "propose"); dates.Add("proposedAt");
                checks.Add(Eq("$proposedBy", "$actorId"));
                break;
            case B.Decisions:
                fields.AddRange(new BsonDocument { { "proposalId", S.Number(1) }, { "assortmentId", S.Number(1) }, { "decision", S.Choice("approve", "reject") },
                    { "headRevision", Nullable(S.Number(1, long.MaxValue - 1)) }, { "decidedBy", S.Number(1) }, { "decidedAt", Date } });
                Command(fields, B.Prefix + "approve", B.Prefix + "reject"); dates.Add("decidedAt");
                checks.Add(Eq("$decidedBy", "$actorId"));
                checks.Add(new BsonDocument("$or", new BsonArray {
                    new BsonDocument("$and", new BsonArray { Eq("$decision", "approve"), Eq("$operation", B.Prefix + "approve"),
                        new BsonDocument("$ne", new BsonArray { "$headRevision", BsonNull.Value }) }),
                    new BsonDocument("$and", new BsonArray { Eq("$decision", "reject"), Eq("$operation", B.Prefix + "reject"), Eq("$headRevision", BsonNull.Value) }) }));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        foreach (var date in dates) terms.Add(S.DateRange(date));
        terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        return new("$and", terms);
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
}
