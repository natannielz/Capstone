using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentNotificationSchemas
{
    internal static BsonDocument Receipt() => S.Object(new() {
        { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "actionId", S.Number(1) }, { "actionHash", PaymentSubmissionSchemas.Hash() }, { "recipientId", S.Number(1) },
        { "disposition", S.Choice("available", "not_applicable") }, { "consumerId", S.Choice(PaymentReviewNotifications.ConsumerId) },
        { "claimToken", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{32}\z" } } },
        { "claimAttempt", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000 } } },
        { "claimedAt", new BsonDocument("bsonType", "date") }, { "leaseUntil", new BsonDocument("bsonType", "date") },
        { "claimDeadline", new BsonDocument("bsonType", "date") }, { "createdAt", new BsonDocument("bsonType", "date") }, { "snapshotHash", PaymentSubmissionSchemas.Hash() } });
    internal static BsonDocument OutboxValidator() => new("$or", new BsonArray {
        new BsonDocument { { "kind", new BsonDocument("$ne", PaymentReviewActionRecords.Operation) }, { "notificationReceipt", new BsonDocument("$exists", false) } },
        new BsonDocument("$and", new BsonArray {
            new BsonDocument("kind", PaymentReviewActionRecords.Operation),
            new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "properties", new BsonDocument("notificationReceipt", Receipt()) } }),
            new BsonDocument("$or", new BsonArray {
                new BsonDocument { { "status", new BsonDocument("$ne", "completed") }, { "notificationReceipt", new BsonDocument("$exists", false) } },
                new BsonDocument { { "status", "completed" }, { "notificationReceipt", new BsonDocument("$exists", true) },
                    { "$expr", new BsonDocument("$and", new BsonArray {
                        new BsonDocument("$eq", new BsonArray { "$completedAt", "$notificationReceipt.createdAt" }),
                        new BsonDocument("$eq", new BsonArray { "$attempts", "$notificationReceipt.claimAttempt" }),
                        new BsonDocument("$gte", new BsonArray { "$notificationReceipt.createdAt", "$notificationReceipt.claimedAt" }),
                        new BsonDocument("$gte", new BsonArray { "$notificationReceipt.claimedAt", "$createdAt" }),
                        new BsonDocument("$lt", new BsonArray { "$notificationReceipt.createdAt", "$notificationReceipt.leaseUntil" }),
                        new BsonDocument("$lte", new BsonArray { "$notificationReceipt.leaseUntil", "$notificationReceipt.claimDeadline" }) }) } }
            }) }) });
}
