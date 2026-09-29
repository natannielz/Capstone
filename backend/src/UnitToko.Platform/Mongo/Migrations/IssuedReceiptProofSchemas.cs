using MongoDB.Bson;
using UnitToko.Platform.Documents;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class IssuedReceiptProofSchemas
{
    internal static BsonDocument Key() => new() { { "kind", 1 }, { "sourceId", 1 }, { "sourceRevision", 1 } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    internal static BsonDocument Validator() {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(IssuedDocumentStore.ProofKind) }, { "sourceId", S.Number(1) }, { "sourceRevision", S.Number(1, 1) }, { "version", S.Number(1, 1) },
            { "renderer", S.Choice("receipt-proof-html-v1") }, { "mediaType", S.Choice("text/html; charset=utf-8") },
            { "authority", S.Choice("staff", "recipient") }, { "snapshot", new BsonDocument("bsonType", "object") },
            { "content", new BsonDocument { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", IssuedDocumentStore.MaximumProofBytes } } },
            { "contentHash", Hash() }, { "byteLength", S.Number(1, IssuedDocumentStore.MaximumProofBytes) }, { "bodyHash", Hash() },
            { "issuedBy", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "requestKey", S.Text(128) }, { "issuedAt", new BsonDocument("bsonType", "date") } };
        S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("issuedAt"),
            new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$sourceId", "$snapshot.shipmentId" }),
                new BsonDocument("$eq", new BsonArray { "$sourceRevision", "$snapshot.shipmentRevision" }),
                new BsonDocument("$gte", new BsonArray { "$issuedAt", "$snapshot.receipt.recordedAt" }),
                new BsonDocument("$eq", new BsonArray { "$byteLength", new BsonDocument("$strLenBytes", "$content") }) })) });
    }
}
