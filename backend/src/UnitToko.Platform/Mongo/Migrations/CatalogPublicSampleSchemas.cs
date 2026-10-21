using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>The products (catalog families) guests may browse as samples, and the maker/checker proposals that change them.
/// No price, stock or buyer data is stored here.</summary>
internal static class CatalogPublicSampleSchemas
{
    internal const string Samples = "catalogPublicSamples", Proposals = "catalogPublicSampleProposals",
        Prefix = "catalog.public.sample.", SetCounter = "catalogPublicSampleSet";
    /// <summary>The most products guests may see at once; the public read refuses more.</summary>
    internal const int Limit = 200, ChangeLimit = 200;
    internal static readonly string[] Collections = [Samples, Proposals];
    private static BsonDocument Format => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Null => new("bsonType", "null");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, Null });
    private static BsonDocument Ids(int minimum, int maximum) => new() {
        { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "uniqueItems", true }, { "items", S.Number(1) } };
    internal static BsonDocument Owner() => S.Object(new() {
        { "kind", S.Choice("fulfillmentShop", "fulfillmentWarehouse") }, { "organizationId", S.Number(1) }, { "shopId", S.Number(1) },
        { "channelId", S.Number(1) }, { "warehouseId", Nullable(S.Number(1)) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        Samples => [],
        Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", Format }, { "ownerScope", Owner() } };
        var terms = new BsonArray();
        switch (name) {
            case Samples:
                fields.AddRange(new BsonDocument { { "revision", S.Number(1, long.MaxValue - 1) }, { "addedAt", Date },
                    { "approvedProposalId", Nullable(S.Number(1)) },
                    { "migratedFrom", Nullable(S.Object(new() { { "assortmentId", S.Number(1) }, { "showcaseDecisionId", S.Number(1) } })) } });
                terms.Add(new BsonDocument("$jsonSchema", S.Object(fields)));
                terms.Add(S.DateRange("addedAt"));
                // A sample came from exactly one source: an approved proposal, or the version 122 conversion of a public assortment.
                terms.Add(new BsonDocument("$expr", new BsonDocument("$ne", new BsonArray {
                    Eq("$approvedProposalId", BsonNull.Value), Eq("$migratedFrom", BsonNull.Value) })));
                break;
            case Proposals:
                var decision = S.Object(new() { { "outcome", S.Choice("approved", "rejected") }, { "decidedBy", S.Number(1) },
                    { "decidedAt", Date }, { "actorGrantVersion", S.Number(1) }, { "requestKey", S.Text(128) }, { "bodyHash", Hash },
                    { "reason", S.Text(1000) }, { "correlationId", S.Text(128) }, { "auditId", S.Number(1) },
                    { "setRevision", Nullable(S.Number(1, long.MaxValue - 1)) } });
                fields.AddRange(new BsonDocument { { "adds", Ids(0, ChangeLimit) }, { "removes", Ids(0, ChangeLimit) },
                    { "families", Ids(1, ChangeLimit * 2) }, { "proposedBy", S.Number(1) }, { "proposedAt", Date },
                    { "state", S.Choice("pending", "approved", "rejected") }, { "revision", S.Number(1, 2) }, { "decision", Nullable(decision) },
                    { "operation", S.Choice(Prefix + "propose") }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
                    { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash } });
                S.Metadata(fields);
                terms.Add(new BsonDocument("$jsonSchema", S.Object(fields)));
                terms.Add(S.DateRange("proposedAt"));
                terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                    Eq("$proposedBy", "$actorId"),
                    Eq(new BsonDocument("$size", "$families"), new BsonDocument("$add", new BsonArray { new BsonDocument("$size", "$adds"), new BsonDocument("$size", "$removes") })),
                    Eq(new BsonDocument("$size", new BsonDocument("$setIntersection", new BsonArray { "$adds", "$removes" })), 0),
                    new BsonDocument("$or", new BsonArray {
                        new BsonDocument("$and", new BsonArray { Eq("$state", "pending"), Eq("$revision", 1L), Eq("$decision", BsonNull.Value) }),
                        new BsonDocument("$and", new BsonArray { new BsonDocument("$ne", new BsonArray { "$state", "pending" }), Eq("$revision", 2L),
                            Eq("$decision.outcome", "$state"), new BsonDocument("$ne", new BsonArray { "$decision.decidedBy", "$proposedBy" }),
                            new BsonDocument("$gte", new BsonArray { "$decision.decidedAt", "$proposedAt" }),
                            new BsonDocument("$or", new BsonArray {
                                new BsonDocument("$and", new BsonArray { Eq("$state", "approved"), new BsonDocument("$ne", new BsonArray { "$decision.setRevision", BsonNull.Value }) }),
                                new BsonDocument("$and", new BsonArray { Eq("$state", "rejected"), Eq("$decision.setRevision", BsonNull.Value) }) }) }) }) })));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        return new("$and", terms);
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
}
