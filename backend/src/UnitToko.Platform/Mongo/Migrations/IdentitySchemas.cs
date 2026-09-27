using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v4 shape. Keep this independent of mutable domain catalogs and provider SDKs.
internal static class IdentitySchemas
{
    public static IReadOnlyDictionary<string, BsonDocument> Collections() => new Dictionary<string, BsonDocument>
    {
        ["identities"] = Json(new BsonDocument
        {
            { "bsonType", "object" }, { "properties", new BsonDocument("securityVersion", Long(1)) }
        }),
        ["identitySessions"] = Json(Session()),
        ["oidcTransactions"] = Json(OidcTransaction()),
        ["accountInvitations"] = Json(Invitation()),
        ["accountProfiles"] = Json(Document(["displayName", "verifiedEmail", "verifiedAt", "updatedAt", "version"], new()
        {
            { "displayName", Nullable(Text(160)) }, { "verifiedEmail", Text(254) }, { "verifiedAt", Date() },
            { "updatedAt", Date() }, { "version", Long(1) }
        })),
        ["identityProviderIntents"] = Json(Document(["kind", "userId", "invitationId", "requestedBy", "approvedBy",
            "securityVersion", "status", "reason", "createdAt", "protectedPayload", "version"], new()
        {
            { "kind", Choice("invitation", "passwordRecovery", "factorRecovery") }, { "userId", Long(1) },
            { "invitationId", Nullable(Long(1)) }, { "requestedBy", Long(1) }, { "approvedBy", Nullable(Long(1)) },
            { "securityVersion", Long(1) }, { "status", Choice("blocked") }, { "reason", Text(2000) },
            { "createdAt", Date() }, { "protectedPayload", Text(262144) }, { "version", Long(1) }
        })),
        ["identityReadEvents"] = Json(Document(["actorId", "actorGrantVersion", "operation", "purpose", "correlationId",
            "createdAt", "selection", "resultCount", "scopes"], new()
        {
            { "actorId", Long(1) }, { "actorGrantVersion", Long(1) },
            { "operation", Choice("identity.invitations.read", "identity.directory.read") },
            { "purpose", Choice("access-review", "account-support", "security-investigation") },
            { "correlationId", Text(128) }, { "createdAt", Date() },
            { "selection", Object(["offset", "limit"], new()
                { { "offset", Integer(0, 10000) }, { "limit", Integer(1, 100) }, { "subjectId", Long(1) } }) },
            { "resultCount", Integer(0, 100) }, { "scopes", Array(Scope(), 1, 256) }
        }))
    };

    private static BsonDocument Session()
    {
        var schema = Document(["familyId", "userId", "issuer", "subject", "connectionId", "grantVersion", "securityVersion",
            "status", "version", "fence", "createdAt", "lastUsedAt", "idleExpiresAt", "absoluteExpiresAt",
            "providerAuthenticatedAt", "factorCompletedAt", "factorType", "assurancePolicyVersion", "protectedPayload"], new()
        {
            { "familyId", Hash() }, { "userId", Long(1) }, { "issuer", Text(2048) }, { "subject", Text(256) },
            { "connectionId", Text(256) }, { "grantVersion", Long(1) }, { "securityVersion", Long(1) },
            { "status", Choice("active", "revoked", "rotated") }, { "version", Long(1) }, { "fence", Long(0) },
            { "createdAt", Date() }, { "lastUsedAt", Date() }, { "idleExpiresAt", Date() }, { "absoluteExpiresAt", Date() },
            { "providerAuthenticatedAt", Date() }, { "factorCompletedAt", Date() }, { "factorType", Choice("otp") },
            { "assurancePolicyVersion", Text(64) }, { "protectedPayload", Text(262144) }, { "providerSid", Text(256) },
            { "revokedAt", Date() }, { "revocationReason", Text(256) }
        }, Hash());
        schema.Add("oneOf", new BsonArray
        {
            State("active", [], ["revokedAt", "revocationReason"]),
            State("revoked", ["revokedAt", "revocationReason"], []),
            State("rotated", ["revokedAt", "revocationReason"], [])
        });
        return schema;
    }

    private static BsonDocument OidcTransaction()
    {
        var schema = Document(["purpose", "status", "issuer", "clientId", "challengeHash", "returnPath", "createdAt",
            "expiresAt", "version", "expectedUserId", "expectedSessionHash", "expectedSessionVersion", "expectedSecurityVersion",
            "expectedGrantVersion", "expectedSubject", "operation", "invitationId", "invitationSecretHash"], new()
        {
            { "purpose", Choice("login", "stepUp") }, { "status", Choice("pending", "consumed") },
            { "issuer", Text(2048) }, { "clientId", Text(256) }, { "challengeHash", Hash() }, { "returnPath", Text(200) },
            { "createdAt", Date() }, { "expiresAt", Date() }, { "version", Long(1) },
            { "expectedUserId", Nullable(Long(1)) }, { "expectedSessionHash", Nullable(Hash()) },
            { "expectedSessionVersion", Nullable(Long(1)) }, { "expectedSecurityVersion", Nullable(Long(1)) },
            { "expectedGrantVersion", Nullable(Long(1)) }, { "expectedSubject", Nullable(Text(256)) },
            { "operation", Nullable(Text(128)) }, { "invitationId", Nullable(Long(1)) },
            { "invitationSecretHash", Nullable(Hash()) }, { "consumedAt", Date() }
        }, Hash());
        schema.Add("allOf", new BsonArray
        {
            new BsonDocument("oneOf", new BsonArray
            {
                State("pending", [], ["consumedAt"]), State("consumed", ["consumedAt"], [])
            }),
            new BsonDocument("oneOf", new BsonArray
            {
                new BsonDocument("properties", new BsonDocument
                {
                    { "purpose", Choice("login") }, { "expectedUserId", Null() }, { "expectedSessionHash", Null() },
                    { "expectedSessionVersion", Null() }, { "expectedSecurityVersion", Null() },
                    { "expectedGrantVersion", Null() }, { "expectedSubject", Null() }, { "operation", Null() }
                }),
                new BsonDocument("properties", new BsonDocument
                {
                    { "purpose", Choice("stepUp") }, { "expectedUserId", Long(1) }, { "expectedSessionHash", Hash() },
                    { "expectedSessionVersion", Long(1) }, { "expectedSecurityVersion", Long(1) },
                    { "expectedGrantVersion", Long(1) }, { "expectedSubject", Text(256) }, { "operation", Text(128) },
                    { "invitationId", Null() }, { "invitationSecretHash", Null() }
                })
            }),
            new BsonDocument("oneOf", new BsonArray
            {
                new BsonDocument("properties", new BsonDocument { { "invitationId", Null() }, { "invitationSecretHash", Null() } }),
                new BsonDocument("properties", new BsonDocument { { "invitationId", Long(1) }, { "invitationSecretHash", Hash() } })
            })
        });
        return schema;
    }

    private static BsonDocument Invitation()
    {
        var schema = Document(["userId", "membershipId", "grantId", "intendedEmail", "scope", "role", "requestedBy",
            "requesterGrantVersion", "requesterSecurityVersion", "providerIssuer", "providerConnectionId", "reason", "createdAt", "expiresAt", "accessExpiresAt", "status", "version"], new()
        {
            { "userId", Long(1) }, { "membershipId", Long(1) }, { "grantId", Nullable(Long(1)) }, { "intendedEmail", Text(254) },
            { "scope", Scope() }, { "role", Nullable(Object(["templateId", "templateVersion", "permissions", "delegablePermissions"], new()
                { { "templateId", Text(64) }, { "templateVersion", Integer(1) },
                  { "permissions", Array(Text(128), 0, 256, true) }, { "delegablePermissions", Array(Text(128), 0, 256, true) } })) },
            { "requestedBy", Long(1) }, { "requesterGrantVersion", Long(1) }, { "requesterSecurityVersion", Long(1) },
            { "providerIssuer", Text(2048) }, { "providerConnectionId", Text(256) },
            { "reason", Text(2000) }, { "createdAt", Date() }, { "expiresAt", Date() }, { "accessExpiresAt", Nullable(Date()) },
            { "status", Choice("pending", "approved", "redeemed", "rejected", "withdrawn", "revoked") }, { "version", Long(1) },
            { "approvedBy", Long(1) }, { "approvedAt", Date() }, { "approvedFactorAt", Date() },
            { "approverGrantVersion", Long(1) }, { "approverSecurityVersion", Long(1) },
            { "authorityGrantIds", Array(Long(1), 1, 256, true) }, { "secretHash", Hash() }, { "providerIntentId", Long(1) },
            { "resolvedBy", Long(1) }, { "resolvedAt", Date() }, { "resolutionReason", Text(2000) }, { "redeemedAt", Date() }
        });
        schema.Add("allOf", new BsonArray { new BsonDocument("oneOf", new BsonArray
        {
            new BsonDocument("properties", new BsonDocument { { "role", Null() }, { "grantId", Null() } }),
            new BsonDocument("properties", new BsonDocument { { "role", new BsonDocument("bsonType", "object") }, { "grantId", Long(1) } })
        }) });
        string[] approved = ["approvedBy", "approvedAt", "approvedFactorAt", "approverGrantVersion",
            "approverSecurityVersion", "authorityGrantIds", "secretHash", "providerIntentId"];
        string[] resolved = ["resolvedBy", "resolvedAt", "resolutionReason"];
        schema.Add("oneOf", new BsonArray
        {
            State("pending", [], [.. approved, .. resolved, "redeemedAt"]),
            State("approved", approved, [.. resolved, "redeemedAt"]),
            State("redeemed", [.. approved, "redeemedAt"], resolved),
            State("rejected", resolved, [.. approved, "redeemedAt"]),
            State("withdrawn", resolved, [.. approved, "redeemedAt"]),
            State("revoked", [.. approved, .. resolved], ["redeemedAt"])
        });
        return schema;
    }

    private static BsonDocument Scope() => Object(["kind"], new()
    {
        { "kind", Choice("buyerOwned", "buyingDivision", "buyingBranch", "buyingOrganization",
            "fulfillmentShop", "fulfillmentWarehouse", "courierAssignment", "system") },
        { "organizationId", Nullable(Long(1)) }, { "branchId", Nullable(Long(1)) },
        { "divisionId", Nullable(Long(1)) }, { "shopId", Nullable(Long(1)) },
        { "warehouseId", Nullable(Long(1)) }, { "channelId", Nullable(Long(1)) }
    });
    private static BsonDocument Document(string[] required, BsonDocument properties, BsonDocument? id = null)
    {
        properties.InsertAt(0, new("schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } }));
        properties.InsertAt(0, new("_id", id ?? Long(1)));
        return Object(["_id", "schemaVersion", .. required], properties);
    }
    private static BsonDocument Object(string[] required, BsonDocument properties) => new()
    {
        { "bsonType", "object" }, { "additionalProperties", false }, { "required", new BsonArray(required) }, { "properties", properties }
    };
    private static BsonDocument State(string status, string[] required, string[] forbidden)
    {
        var result = new BsonDocument("properties", new BsonDocument("status", Choice(status)));
        if (required.Length > 0) result.Add("required", new BsonArray(required));
        if (forbidden.Length > 0) result.Add("not", new BsonDocument("anyOf",
            new BsonArray(forbidden.Select(field => new BsonDocument("required", new BsonArray { field })))));
        return result;
    }
    private static BsonDocument Array(BsonDocument items, int minimum, int maximum, bool unique = false) => new()
    {
        { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "uniqueItems", unique }, { "items", items }
    };
    private static BsonDocument Nullable(BsonDocument type) => new("anyOf", new BsonArray { type, Null() });
    private static BsonDocument Null() => new("bsonType", "null");
    private static BsonDocument Long(long minimum) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument Integer(int minimum, int maximum = int.MaxValue) => new()
        { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum } };
    private static BsonDocument Hash() => new()
        { { "bsonType", "string" }, { "minLength", 64 }, { "maxLength", 64 }, { "pattern", "^[a-f0-9]{64}\\z" } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Json(BsonDocument schema) => new("$jsonSchema", schema);
}
