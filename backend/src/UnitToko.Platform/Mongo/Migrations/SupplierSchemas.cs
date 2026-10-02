using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.SupplierBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class SupplierSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Heads, B.Proposals, B.Observations, B.Decisions, B.Versions, B.Namespaces];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Boolean => new("bsonType", "bool");
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument item, int minimum, int maximum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", item } };
    internal static BsonDocument Owner() => S.Object(new() { { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "channelId", S.Number(1) } });
    internal static BsonDocument Details() => S.Object(new() { { "subjectReference", S.Text(128) }, { "legalName", S.Text(160) }, { "displayName", S.Text(160) },
        { "registrationNamespace", Nullable(S.Text(64)) }, { "registrationValue", Nullable(S.Text(128)) }, { "address", S.Text(500) },
        { "contactName", S.Text(160) }, { "contactMethod", S.Choice("email", "phone", "other") }, { "contactValue", S.Text(256) } });
    internal static BsonDocument Mapping() => S.Object(new() { { "location", ProcurementRequestSchemas.Location() }, { "sourceSystem", S.Choice("omi", "s_mart", "supplier") },
        { "sourceAccountReference", S.Text(128) }, { "externalItemCode", S.Text(128) }, { "skuId", S.Number(1) }, { "expectedSkuRevision", S.Number(1) },
        { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) }, { "validFrom", Date }, { "validUntil", Date } });
    internal static BsonDocument Input() => S.Object(new() { { "owner", Owner() }, { "supplierId", Nullable(S.Number(1)) }, { "baseVersion", S.Number(0, long.MaxValue - 2) },
        { "policyId", S.Number(1) }, { "policyRevision", S.Number(1) }, { "details", Details() }, { "mappings", Array(Mapping(), 1, 100) } });
    internal static BsonDocument Sources() => S.Object(new(B.SourceNames.Select(name => new BsonElement(name, OrganizationAddressSchemas.Source()))));
    internal static BsonDocument MappingSource() => S.Object(new() { { "location", ProcurementRequestSchemas.Location() }, { "sources", ProcurementRequestSchemas.Sources() }, { "catalog", ProcurementRequestSchemas.Catalog() } });
    private static BsonDocument Header() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    private static BsonDocument PolicyFields() {
        var fields = Header(); fields.AddRange(new BsonDocument { { "scope", Owner() }, { "revision", S.Number(1, long.MaxValue - 1) },
            { "subjectReference", S.Text(128) }, { "legalName", S.Text(160) }, { "registrationNamespace", Nullable(S.Text(64)) }, { "registrationValue", Nullable(S.Text(128)) },
            { "method", S.Choice("established-contact", "authenticated-record") }, { "trustedChannelReference", S.Text(256) }, { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) },
            { "approvedAt", Date }, { "expiresAt", Date }, { "maxObservationAgeSeconds", S.Number(1, 31536000) }, { "allowReferenceOnly", Boolean }, { "active", Boolean } }); return fields;
    }
    internal static BsonDocument Policy() => S.Object(PolicyFields());
    private static BsonDocument Person() => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)],
        B.Heads => [new() { { "owner", 1 }, { "subjectReference", 1 } }],
        B.Proposals => [new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Observations or B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Versions => [new() { { "supplierId", 1 }, { "version", 1 } }, new("decisionId", 1)],
        B.Namespaces => [new() { { "owner", 1 }, { "sourceSystem", 1 }, { "sourceAccountReference", 1 }, { "externalItemCode", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = Header(); var checks = new BsonArray(); var dates = new List<string>();
        if (name == B.Policies) {
            fields = PolicyFields(); fields.Add("fence", S.Number(0, long.MaxValue - 1)); dates.AddRange(["approvedAt", "expiresAt"]);
            checks.Add(Gt("$expiresAt", "$approvedAt")); checks.Add(Eq(Eq("$registrationNamespace", BsonNull.Value), Eq("$registrationValue", BsonNull.Value)));
        } else if (name == B.Heads) {
            fields.AddRange(new BsonDocument { { "owner", Owner() }, { "subjectReference", S.Text(128) }, { "rootProposalId", S.Number(1) },
                { "version", S.Number(0, long.MaxValue - 1) }, { "versionId", Nullable(S.Number(1)) }, { "fence", S.Number(0, long.MaxValue - 1) }, { "createdAt", Date }, { "updatedAt", Date } });
            checks.Add(Eq(Eq("$version", 0L), Eq("$versionId", BsonNull.Value))); checks.Add(new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" })); dates.AddRange(["createdAt", "updatedAt"]);
        } else if (name == B.Namespaces) {
            fields.AddRange(new BsonDocument { { "owner", Owner() }, { "sourceSystem", S.Choice("omi", "s_mart", "supplier") }, { "sourceAccountReference", S.Text(128) },
                { "externalItemCode", S.Text(128) }, { "fence", S.Number(0, long.MaxValue - 1) }, { "createdAt", Date } }); dates.Add("createdAt");
        } else if (name == B.Versions) {
            fields.AddRange(new BsonDocument { { "supplierId", S.Number(1) }, { "owner", Owner() }, { "version", S.Number(1, long.MaxValue - 1) }, { "proposalId", S.Number(1) },
                { "observationId", S.Number(1) }, { "decisionId", S.Number(1) }, { "previousVersionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) },
                { "details", Details() }, { "mappings", Array(Mapping(), 1, 100) }, { "mappingSources", Array(MappingSource(), 1, 100) },
                { "namespaceIds", Array(S.Number(1), 1, 100) }, { "effectiveAt", Date }, { "snapshotHash", Hash } });
            checks.Add(Eq(Eq("$version", 1L), Eq("$previousVersionId", BsonNull.Value))); checks.Add(Eq(Eq("$version", 1L), Eq("$previousHash", BsonNull.Value)));
            checks.Add(Eq(Eq("$details.registrationNamespace", BsonNull.Value), Eq("$details.registrationValue", BsonNull.Value))); dates.Add("effectiveAt");
            MappingChecks(checks, "$mappings", "$mappingSources", "$owner");
        } else {
            fields.AddRange(new BsonDocument { { "proposalId", S.Number(1) }, { "supplierId", S.Number(1) }, { "owner", Owner() }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
                { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash }, { "people", Array(Person(), name == B.Proposals ? 1 : 2, name == B.Decisions ? 3 : name == B.Proposals ? 1 : 2) } });
            S.Metadata(fields); dates.Add("recordedAt");
            checks.Add(Unique(Map(SafeArray("$people"), "person", "$$person.userId")));
            checks.Add(Eq(Map(SafeArray("$people"), "person", "$$person.userId"), new BsonDocument("$sortArray", new BsonDocument { { "input", Map(SafeArray("$people"), "person", "$$person.userId") }, { "sortBy", 1 } })));
            checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", Map(SafeArray("$people"), "person", "$$person.userId") }));
            if (name == B.Proposals) {
                fields.AddRange(new BsonDocument { { "version", S.Number(1, 1) }, { "operation", S.Choice(B.Prefix + "propose") }, { "input", Input() }, { "sources", Sources() },
                    { "mappingSources", Array(MappingSource(), 1, 100) }, { "policy", Policy() }, { "binding", Hash } });
                checks.Add(Eq("$_id", "$proposalId")); checks.Add(Eq("$owner", "$input.owner")); checks.Add(Eq("$policy.scope", "$owner"));
                checks.Add(Eq("$policy._id", "$input.policyId")); checks.Add(Eq("$policy.revision", "$input.policyRevision"));
                foreach (var part in new[] { "subjectReference", "legalName", "registrationNamespace", "registrationValue" }) checks.Add(Eq("$policy." + part, "$input.details." + part));
                checks.Add(Eq(Eq("$input.details.registrationNamespace", BsonNull.Value), Eq("$input.details.registrationValue", BsonNull.Value)));
                checks.Add(new BsonDocument("$cond", new BsonArray { Eq("$input.supplierId", BsonNull.Value), Eq("$input.baseVersion", 0L), Eq("$input.supplierId", "$supplierId") }));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$policy.approvedAt", "$recordedAt" })); checks.Add(Gt("$policy.expiresAt", "$recordedAt")); checks.Add(Eq("$policy.active", true));
                dates.AddRange(["policy.approvedAt", "policy.expiresAt"]); SourceChecks(checks, dates, "sources"); MappingChecks(checks, "$input.mappings", "$mappingSources", "$owner");
            } else if (name == B.Observations) {
                fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "observe") }, { "proposalHash", Hash }, { "observed", Details() }, { "policyId", S.Number(1) }, { "policyRevision", S.Number(1) },
                    { "policyHash", Hash }, { "trustedChannelReference", S.Text(256) }, { "sourceReference", S.Text(256) }, { "attested", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } }, { "checkedAt", Date } });
                checks.Add(Eq("$checkedAt", "$recordedAt")); checks.Add(Eq(Eq("$observed.registrationNamespace", BsonNull.Value), Eq("$observed.registrationValue", BsonNull.Value))); dates.Add("checkedAt");
            } else {
                fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "proposalHash", Hash }, { "decision", S.Choice("approve", "reject") },
                    { "observationId", Nullable(S.Number(1)) }, { "versionId", Nullable(S.Number(1)) }, { "supplierVersion", Nullable(S.Number(1)) }, { "currentVersion", S.Number(0, long.MaxValue - 2) },
                    { "reviewSources", Sources() }, { "reviewMappingSources", Array(MappingSource(), 0, 100) }, { "reviewPolicy", Nullable(Policy()) }, { "namespaceIds", Array(S.Number(1), 0, 100) }, { "binding", Hash } });
                checks.Add(Eq("$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$decision" })));
                foreach (var field in new[] { "observationId", "versionId", "supplierVersion", "reviewPolicy" }) checks.Add(Eq(Eq("$decision", "reject"), Eq("$" + field, BsonNull.Value)));
                foreach (var field in new[] { "namespaceIds", "reviewMappingSources" }) checks.Add(Eq(Eq("$decision", "reject"), Eq(new BsonDocument("$size", SafeArray("$" + field)), 0)));
                checks.Add(new BsonDocument("$or", new BsonArray { Eq("$decision", "reject"), Eq("$supplierVersion", new BsonDocument("$add", new BsonArray { "$currentVersion", 1L })) }));
                SourceChecks(checks, dates, "reviewSources");
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (checks.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        foreach (var path in dates) terms.Add(S.DateRange(path)); return new("$and", terms);
    }
    private static void SourceChecks(BsonArray checks, List<string> dates, string prefix) {
        foreach (var (part, kind, id) in new[] { ("organization", "fulfillmentOrganization", "organizationId"), ("shop", "shop", "shopId"), ("channel", "channel", "channelId") }) {
            checks.Add(Eq("$" + prefix + "." + part + ".target.id", "$owner." + id)); checks.Add(Eq("$" + prefix + "." + part + ".target.kind", kind));
            checks.Add(new BsonDocument("$lte", new BsonArray { "$" + prefix + "." + part + ".profile.approvedAt", "$recordedAt" })); dates.Add(prefix + "." + part + ".profile.approvedAt");
        }
    }
    private static void MappingChecks(BsonArray checks, string mappings, string sources, string owner) {
        checks.Add(All(Map(SafeArray(mappings), "mapping", new BsonDocument("$and", new BsonArray { Gt("$$mapping.validUntil", "$$mapping.validFrom"),
            Gt("$$mapping.validFrom", new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc))),
            new BsonDocument("$lte", new BsonArray { "$$mapping.validUntil", new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)) }),
            Eq("$$mapping.location.organizationId", owner + ".organizationId"), Eq("$$mapping.location.shopId", owner + ".shopId"), Eq("$$mapping.location.channelId", owner + ".channelId") }))));
        checks.Add(Eq(Map(SafeArray(mappings), "mapping", new BsonDocument { { "location", "$$mapping.location" }, { "skuId", "$$mapping.skuId" }, { "revision", "$$mapping.expectedSkuRevision" },
            { "definitionId", "$$mapping.definitionId" }, { "definitionVersion", "$$mapping.definitionVersion" } }), Map(SafeArray(sources), "source", new BsonDocument {
                { "location", "$$source.location" }, { "skuId", "$$source.catalog.sku._id" }, { "revision", "$$source.catalog.sku.revision" },
                { "definitionId", "$$source.catalog.definition._id" }, { "definitionVersion", "$$source.catalog.definition.version" } })));
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument Gt(BsonValue left, BsonValue right) => new("$gt", new BsonArray { left, right });
    private static BsonDocument SafeArray(string path) => new("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument All(BsonValue value) => new("$allElementsTrue", new BsonArray { value });
    private static BsonDocument Unique(BsonValue value) => Eq(new BsonDocument("$size", value), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { value, new BsonArray() })));
}
