using MongoDB.Bson;
using UnitToko.Platform.Authorization;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;
using R = UnitToko.Platform.Authorization.TemporaryDelegationRecords;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class TemporaryDelegationSchemas
{
    internal static readonly string[] Collections = [R.Heads, R.Events, UnitToko.Platform.Billing.PaymentReviewHandoverRecords.Collection];
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument value) { value["bsonType"] = new BsonArray { value["bsonType"], "null" }; return value; }
    private static BsonDocument Array(BsonDocument item, int min = 0) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", 256 }, { "uniqueItems", true }, { "items", item } };
    private static BsonDocument Optional(BsonDocument fields, params string[] optional) {
        var value = S.Object(fields); value["required"] = new BsonArray(fields.Names.Where(name => !optional.Contains(name))); return value;
    }
    private static BsonDocument Scope => Optional(new() {
        { "kind", S.Choice("system", "buyerOwned", "buyingOrganization", "buyingBranch", "buyingDivision", "fulfillmentShop", "fulfillmentWarehouse", "courierAssignment") },
        { "organizationId", S.Number(1) }, { "branchId", S.Number(1) }, { "divisionId", S.Number(1) },
        { "shopId", S.Number(1) }, { "warehouseId", S.Number(1) }, { "channelId", S.Number(1) } },
        "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId");
    private static BsonDocument Membership => Optional(new() {
        { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "userId", S.Number(1) }, { "scope", Scope }, { "status", S.Choice("active", "suspended", "revoked", "pending") },
        { "approvedBy", S.Number(1) }, { "effectiveAt", Date }, { "expiresAt", Date }, { "revokedAt", Date }, { "version", S.Number(1) } }, "expiresAt", "revokedAt");
    private static BsonDocument Grant {
        get {
            var value = Membership; var props = value["properties"].AsBsonDocument;
            props.Add("membershipId", S.Number(1)); props.Add("templateId", S.Text(64)); props.Add("templateVersion", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } });
            props.Add("permissions", Array(S.Text(128))); props.Add("requestedBy", S.Number(1)); props.Add("reason", S.Text(2000)); props.Add("delegablePermissions", Array(S.Text(128)));
            value["required"] = new BsonArray(props.Names.Where(name => name is not ("expiresAt" or "revokedAt"))); return value;
        }
    }
    private static BsonDocument Request => S.Object(new() {
        { "sourceGrantId", S.Number(1) }, { "sourceVersion", S.Number(1) }, { "beneficiaryUserId", S.Number(1) }, { "beneficiaryMembershipId", S.Number(1) },
        { "scope", Scope }, { "permissions", Array(S.Text(128), 1) }, { "effectiveAt", Date }, { "expiresAt", Date } });
    private static BsonDocument StateFields => new() {
        { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "eventId", S.Number(1) }, { "revision", S.Number(1, long.MaxValue - 1) }, { "sourceUserId", S.Number(1) }, { "sourceIdentityVersion", S.Number(1) },
        { "request", Request }, { "sourceGrant", Grant }, { "sourceMembership", Membership }, { "beneficiaryMembership", Membership },
        { "sourceNodes", Array(Optional(new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "kind", S.Choice("buyingOrganization", "fulfillmentOrganization", "branch", "division", "shop", "warehouse", "channel") }, { "status", S.Choice("active") }, { "version", S.Number(1) }, { "parentId", S.Number(1) } }, "parentId")) },
        { "proposalReason", S.Text(1000) }, { "status", S.Choice("pending", "active", "revoked") }, { "approvedBy", S.Number(0) },
        { "grantVersion", S.Number(0, long.MaxValue - 1) }, { "revokedAt", Nullable(Date) }, { "createdAt", Date }, { "updatedAt", Date } };
    internal static BsonDocument Validator(string collection) {
        if (collection == UnitToko.Platform.Billing.PaymentReviewHandoverRecords.Collection) return PaymentReviewHandoverSchemas.Validator();
        BsonDocument fields;
        if (collection == R.Heads) { fields = StateFields; fields.Add("fence", S.Number(0, long.MaxValue - 1)); }
        else if (collection == R.Events) fields = new() {
            { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "delegationId", S.Number(1) }, { "revision", S.Number(1, long.MaxValue - 1) }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
            { "action", S.Choice("propose", "approve", "revoke", "handover") }, { "requestKey", S.Text(128) },
            { "bodyHash", new BsonDocument { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}$" } } },
            { "reason", S.Text(1000) }, { "correlationId", S.Text(128) }, { "recordedAt", Date }, { "auditId", S.Number(1) },
            { "before", Nullable(S.Object(StateFields)) }, { "after", S.Object(StateFields) } };
        else throw new ArgumentOutOfRangeException(nameof(collection));
        return new("$jsonSchema", S.Object(fields));
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        UnitToko.Platform.Billing.PaymentReviewHandoverRecords.Collection => PaymentReviewHandoverSchemas.Keys(),
        R.Heads => [("ix_delegation_source", new() { { "sourceUserId", 1 }, { "status", 1 }, { "_id", -1 } }, false),
            ("ix_delegation_beneficiary", new() { { "request.beneficiaryUserId", 1 }, { "_id", -1 } }, false)],
        R.Events => [("ux_delegation_revision", new() { { "delegationId", 1 }, { "revision", 1 } }, true),
            ("ux_delegation_request", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
}
