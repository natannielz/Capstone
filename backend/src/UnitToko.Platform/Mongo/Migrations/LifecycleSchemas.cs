using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Kept independent of mutable domain catalogs so an applied migration stays reproducible.
internal static class LifecycleSchemas
{
    public static IReadOnlyDictionary<string, BsonDocument> Collections() => new Dictionary<string, BsonDocument>
    {
        ["authorizationProposals"] = Json(ProposalResolution()),
        ["authorizationHistory"] = Json(new BsonDocument
        {
            { "bsonType", "object" }, { "properties", new BsonDocument("scope", GrantScope()) }
        }),
        ["organizationProfiles"] = Json(OrganizationProfile()),
        ["organizationProposals"] = Json(OrganizationProposal()),
        ["authorizationReadEvents"] = Json(ReadEvent())
    };

    private static BsonDocument ProposalResolution()
    {
        string[] fields = ["resolutionKind", "resolvedBy", "resolvedAt", "resolutionReason"];
        return new BsonDocument
        {
            { "bsonType", "object" },
            { "properties", new BsonDocument
                {
                    { "resolutionKind", Choice("rejected", "withdrawn") }, { "resolvedBy", Long(1) },
                    { "resolvedAt", Date() }, { "resolutionReason", Text(2000) }
                }
            },
            { "oneOf", new BsonArray
                {
                    Without(fields),
                    new BsonDocument
                    {
                        { "required", new BsonArray(fields) },
                        { "properties", new BsonDocument("status", Choice("rejected")) }
                    }
                }
            }
        };
    }

    private static BsonDocument OrganizationProfile()
    {
        var schema = Document(["kind", "parentId", "code", "normalizedCode", "name", "version",
            "createdBy", "updatedBy", "createdAt", "updatedAt", "approvedProposalId"], new BsonDocument
        {
            { "kind", NodeKind() }, { "parentId", NullableId() }, { "code", Code(false) },
            { "normalizedCode", Code(true) }, { "name", Text(160) }, { "version", Long(1) },
            { "createdBy", Long(1) }, { "updatedBy", Long(1) }, { "createdAt", Date() },
            { "updatedAt", Date() }, { "approvedProposalId", Long(1) }
        });
        schema.Add("allOf", new BsonArray { NodeParentShape() });
        return schema;
    }

    private static BsonDocument OrganizationProposal()
    {
        var schema = Document(["changeKind", "scopeId", "kind", "parentId", "code", "normalizedCode", "name",
            "expectedScopeVersion", "expectedProfileVersion", "requestedBy", "reason", "status", "version",
            "createdAt", "before", "impact"], new BsonDocument
        {
            { "changeKind", Choice("create", "updateProfile", "reactivate") }, { "scopeId", NullableId() },
            { "kind", NodeKind() }, { "parentId", NullableId() }, { "code", Code(false) },
            { "normalizedCode", Code(true) }, { "name", Text(160) },
            { "expectedScopeVersion", Long(0) }, { "expectedProfileVersion", Long(0) },
            { "requestedBy", Long(1) }, { "reason", Text(2000) },
            { "status", Choice("pending", "approved", "rejected", "withdrawn") }, { "version", Long(1) },
            { "createdAt", Date() }, { "before", new BsonDocument("bsonType", "object") }, { "impact", Impact() },
            { "approvedBy", Long(1) }, { "approvedAt", Date() }, { "resultScopeId", Long(1) },
            { "decisionBy", Long(1) }, { "decisionAt", Date() }, { "decisionReason", Text(2000) }
        });
        string[] approvalFields = ["approvedBy", "approvedAt", "resultScopeId"];
        string[] decisionFields = ["decisionBy", "decisionAt", "decisionReason"];
        schema.Add("allOf", new BsonArray
        {
            NodeParentShape(),
            new BsonDocument("oneOf", new BsonArray
            {
                new BsonDocument("properties", new BsonDocument
                {
                    { "changeKind", Choice("create") }, { "scopeId", new BsonDocument("bsonType", "null") },
                    { "expectedScopeVersion", new BsonDocument("enum", new BsonArray { 0L }) },
                    { "expectedProfileVersion", new BsonDocument("enum", new BsonArray { 0L }) }
                }),
                new BsonDocument("properties", new BsonDocument
                {
                    { "changeKind", Choice("updateProfile", "reactivate") }, { "scopeId", Long(1) },
                    { "expectedScopeVersion", Long(1) }
                })
            }),
            new BsonDocument("oneOf", new BsonArray
            {
                State("pending", [], [.. approvalFields, .. decisionFields]),
                State("approved", approvalFields, decisionFields),
                State("rejected", decisionFields, approvalFields),
                State("withdrawn", decisionFields, approvalFields)
            })
        });
        return schema;
    }

    private static BsonDocument ReadEvent() => Document(["actorId", "actorGrantVersion", "operation", "purpose",
        "correlationId", "createdAt", "selection", "resultCount", "scopes"], new BsonDocument
    {
        { "actorId", Long(1) }, { "actorGrantVersion", Long(0) },
        { "operation", Choice("access.subject.read", "access.proposals.read", "access.grant.preview",
            "access.history.read", "access.receipt.read", "organization.workbench.read") },
        { "purpose", Choice("access-review", "account-support", "security-investigation", "operation-recovery") },
        { "correlationId", Text(128) }, { "createdAt", Date() },
        { "selection", new BsonDocument
            {
                { "bsonType", "object" }, { "additionalProperties", false },
                { "properties", new BsonDocument
                    {
                        { "subjectId", Long(1) }, { "offset", Integer(0) }, { "limit", Integer(1, 100) },
                        { "operation", Text(128) }, { "keyHash", Hash() }
                    }
                }
            }
        },
        { "resultCount", Integer(0, 100) },
        { "scopes", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 256 }, { "items", GrantScope() } } }
    });

    private static BsonDocument Impact() => new()
    {
        { "bsonType", "object" }, { "additionalProperties", false },
        { "required", new BsonArray { "affectedScopes", "activeMemberships", "activeGrants", "affectedAccounts", "revision" } },
        { "properties", new BsonDocument
            {
                { "affectedScopes", Long(0) }, { "activeMemberships", Long(0) }, { "activeGrants", Long(0) },
                { "affectedAccounts", Long(0) }, { "revision", Hash() }
            }
        }
    };

    private static BsonDocument NodeParentShape() => new("oneOf", new BsonArray
    {
        new BsonDocument("properties", new BsonDocument
        {
            { "kind", Choice("buyingOrganization", "fulfillmentOrganization") },
            { "parentId", new BsonDocument("bsonType", "null") }
        }),
        new BsonDocument("properties", new BsonDocument
        {
            { "kind", Choice("branch", "division", "shop", "warehouse", "channel") }, { "parentId", Long(1) }
        })
    });

    private static BsonDocument State(string state, string[] required, string[] forbidden)
    {
        var schema = new BsonDocument("properties", new BsonDocument("status", Choice(state)));
        if (required.Length > 0) schema.Add("required", new BsonArray(required));
        schema.Add("allOf", new BsonArray { Without(forbidden) });
        return schema;
    }

    private static BsonDocument Without(string[] fields) => new("not", new BsonDocument("anyOf",
        new BsonArray(fields.Select(field => new BsonDocument("required", new BsonArray { field })))));

    private static BsonDocument Document(string[] required, BsonDocument properties)
    {
        properties.InsertAt(0, new BsonElement("schemaVersion", new BsonDocument
            { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } }));
        properties.InsertAt(0, new BsonElement("_id", Long(1)));
        return new BsonDocument
        {
            { "bsonType", "object" }, { "additionalProperties", false },
            { "required", new BsonArray(new[] { "_id", "schemaVersion" }.Concat(required)) }, { "properties", properties }
        };
    }

    private static BsonDocument GrantScope() => new()
    {
        { "bsonType", "object" }, { "required", new BsonArray { "kind" } }, { "additionalProperties", false },
        { "properties", new BsonDocument
            {
                { "kind", Choice("buyerOwned", "buyingDivision", "buyingBranch", "buyingOrganization",
                    "fulfillmentShop", "fulfillmentWarehouse", "courierAssignment", "system") },
                { "organizationId", NullableId() }, { "branchId", NullableId() }, { "divisionId", NullableId() },
                { "shopId", NullableId() }, { "warehouseId", NullableId() }, { "channelId", NullableId() }
            }
        }
    };

    public static BsonDocument Long(long minimum) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument Integer(int minimum, int? maximum = null)
    {
        var value = new BsonDocument { { "bsonType", "int" }, { "minimum", minimum } };
        if (maximum is { } max) value.Add("maximum", max);
        return value;
    }
    private static BsonDocument NullableId() => new()
        { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } };
    private static BsonDocument Text(int maximum) => new()
        { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum } };
    private static BsonDocument Code(bool normalized)
    {
        var value = Text(64);
        value.Add("pattern", normalized ? "^[A-Z0-9][A-Z0-9._-]*\\z" : "^[A-Za-z0-9][A-Za-z0-9._-]*\\z");
        return value;
    }
    private static BsonDocument Hash() => new()
        { { "bsonType", "string" }, { "minLength", 64 }, { "maxLength", 64 }, { "pattern", "^[a-f0-9]{64}$" } };
    private static BsonDocument NodeKind() => Choice("buyingOrganization", "fulfillmentOrganization", "branch", "division", "shop", "warehouse", "channel");
    private static BsonDocument Choice(params string[] values) => new("enum", new BsonArray(values));
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Json(BsonDocument schema) => new("$jsonSchema", schema);
}
