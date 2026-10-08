using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentReviewHandoverSchemas
{
    internal static BsonDocument Validator() {
        var fields = new BsonDocument { { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "operation", S.Choice(PaymentReviewActionRecords.Operation) }, { "requestKey", S.Text(128) },
            { "delegationState", S.Choice("active", "expired", "revoked", "not_yet_effective") }, { "recordedAt", new BsonDocument("bsonType", "date") } };
        foreach (var field in new[] { "_id", "declarationId", "declarationRevision", "submissionId", "reviewRevision", "previousActionId",
            "delegationId", "delegationRevision", "delegationEventId", "originalAssigneeId", "actorId", "auditId" }) fields.Add(field, S.Number(1));
        foreach (var field in new[] { "sourceHash", "previousActionHash", "bodyHash", "actionHash", "snapshotHash" })
            fields.Add(field, new BsonDocument { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}$" } });
        return new("$jsonSchema", S.Object(fields));
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys() => [
        ("ux_payment_handover_request", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
        ("ix_payment_handover_declaration", new() { { "declarationId", 1 }, { "reviewRevision", 1 } }, true),
        ("ix_payment_handover_delegation", new() { { "delegationId", 1 }, { "_id", -1 } }, false)];
}
