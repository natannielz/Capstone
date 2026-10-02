using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementRequestPurchaseOrderSchemas
{
    internal const string Commitments = "procurementRequestCommitments", Allocations = "procurementRequestAllocations";
    internal static readonly string[] Collections = [Commitments, Allocations];
    private static BsonDocument Hash => Mongo.Migrations.PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    private static BsonDocument Packages(int minimum) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", 1000000 } };
    internal static BsonDocument[] Keys(string name) => name switch {
        Commitments => [new() { { "requestId", 1 }, { "skuId", 1 } }],
        Allocations => [new() { { "orderId", 1 }, { "skuId", 1 } }, new() { { "approvalId", 1 }, { "skuId", 1 } }, new() { { "requestId", 1 }, { "skuId", 1 }, { "sequence", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "requestId", S.Number(1) }, { "requestHash", Hash }, { "requestDecisionId", S.Number(1) }, { "requestDecisionHash", Hash }, { "skuId", S.Number(1) } };
        var checks = new BsonArray(); var dates = new List<string>();
        if (name == Commitments) {
            fields.AddRange(new BsonDocument { { "requestedPackages", Packages(1) }, { "committedPackages", Packages(1) }, { "version", S.Number(1, long.MaxValue - 1) },
                { "fence", S.Number(0, long.MaxValue - 1) }, { "createdAt", Date }, { "updatedAt", Date } });
            checks.Add(new BsonDocument("$lte", new BsonArray { "$committedPackages", "$requestedPackages" }));
            checks.Add(new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" })); dates.AddRange(["createdAt", "updatedAt"]);
        } else if (name == Allocations) {
            fields.AddRange(new BsonDocument { { "packages", Packages(1) }, { "orderId", S.Number(1) }, { "approvalId", S.Number(1) }, { "approvalHash", Hash },
                { "sequence", S.Number(1, long.MaxValue - 1) }, { "previousAllocationId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) }, { "recordedAt", Date }, { "snapshotHash", Hash } });
            foreach (var field in new[] { "previousAllocationId", "previousHash" }) checks.Add(new BsonDocument("$eq", new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$sequence", 1L }), new BsonDocument("$eq", new BsonArray { "$" + field, BsonNull.Value }) }));
            dates.Add("recordedAt");
        } else throw new ArgumentOutOfRangeException(nameof(name));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
}
