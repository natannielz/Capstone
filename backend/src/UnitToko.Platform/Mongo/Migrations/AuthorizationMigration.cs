using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v2 migration. Later authorization format changes belong in a new step.
internal sealed class AuthorizationMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 2;
    private const int DocumentSchemaVersion = 1;

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var schemas = new Dictionary<string, BsonDocument>
        {
            ["identities"] = Identity(),
            ["memberships"] = Membership(),
            ["authorizationGrants"] = Grant(),
            ["authorizationScopes"] = ScopeNode(),
            ["authorizationProposals"] = Proposal(),
            ["authorizationHistory"] = History()
        };

        var definitions = new List<(string Name, BsonDocument Validator, bool Exists)>();
        // Never manufacture approval, scope or status for older records. Operators must
        // reconcile incompatible records before explicitly retrying the deployment.
        foreach (var (name, schema) in schemas)
        {
            await lease.RenewAsync(token);
            var existing = await (await database.ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token))
                .SingleOrDefaultAsync(token);
            var previous = existing?.GetValue("options", new BsonDocument()).AsBsonDocument
                .GetValue("validator", new BsonDocument()).AsBsonDocument ?? new BsonDocument();
            var validator = PreserveExistingRules(previous, Validator(schema));
            definitions.Add((name, validator, existing is not null));
            var invalid = await database.GetCollection<BsonDocument>(name)
                .WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("$nor", new BsonArray { validator }))
                .Limit(1).AnyAsync(token);
            if (invalid)
                throw new InvalidOperationException($"Authorization migration found incompatible records in {name}; reconcile approved identities and scopes before retrying");
        }

        foreach (var (name, validator, exists) in definitions)
        {
            await lease.RenewAsync(token);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { exists ? "collMod" : "create", name }, { "validator", validator },
                { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: token);
        }

        // _id already uniquely identifies every Int64 aggregate. Provider binding uses
        // the v1 unique issuer+subject index; required fields now make it unconditional.
        await Index("memberships", ["userId", "status", "effectiveAt"], lease, token);
        await Index("authorizationGrants", ["userId", "status", "effectiveAt"], lease, token);
        await Index("authorizationGrants", ["membershipId", "status"], lease, token);
        await Index("authorizationScopes", ["kind", "parentId", "status"], lease, token);
        await Index("authorizationProposals", ["status", "scope.organizationId", "createdAt"], lease, token);
        await Index("authorizationProposals", ["subjectId", "status", "createdAt"], lease, token);
        await Index("authorizationHistory", ["subjectId", "createdAt", "_id"], lease, token);
        await Index("authorizationHistory", ["actorId", "createdAt", "_id"], lease, token);
    }

    private static BsonDocument PreserveExistingRules(BsonDocument previous, BsonDocument required)
    {
        if (previous.ElementCount == 0) return required;
        if (HasConjunct(previous, required)) return previous;
        return new BsonDocument("$and", new BsonArray { previous, required });
    }

    private static bool HasConjunct(BsonDocument validator, BsonDocument required) =>
        validator.Equals(required) || validator.TryGetValue("$and", out var conjunction) &&
        conjunction.IsBsonArray && conjunction.AsBsonArray.Any(value =>
            value.IsBsonDocument && HasConjunct(value.AsBsonDocument, required));

    private async Task Index(string collection, string[] fields, MigrationLease lease, CancellationToken token)
    {
        await lease.RenewAsync(token);
        await database.GetCollection<BsonDocument>(collection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument(fields.Select(field => new BsonElement(field, 1))),
                new CreateIndexOptions { Name = "ix_" + string.Join('_', fields) }), cancellationToken: token);
    }

    private static BsonDocument Identity() => Document(
        ["issuer", "subject", "status", "grantVersion", "fence"], new BsonDocument
        {
            { "issuer", Text(2048) }, { "subject", Text(512) }, { "status", Status() },
            { "grantVersion", Long(1) }, { "fence", Long(0) }
        });

    private static BsonDocument Membership() => Document(
        ["userId", "scope", "status", "approvedBy", "effectiveAt", "version"], new BsonDocument
        {
            { "userId", Long(1) }, { "scope", GrantScope() }, { "status", Status() },
            { "approvedBy", Long(1) }, { "effectiveAt", Date() },
            { "expiresAt", NullableDate() }, { "revokedAt", NullableDate() }, { "version", Long(1) }
        });

    private static BsonDocument Grant() => Document(
        ["userId", "membershipId", "templateId", "templateVersion", "permissions", "scope", "status",
            "requestedBy", "approvedBy", "reason", "effectiveAt", "version", "delegablePermissions"], new BsonDocument
        {
            { "userId", Long(1) }, { "membershipId", Long(1) }, { "templateId", Text(64) },
            { "templateVersion", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } } },
            { "permissions", Permissions() }, { "scope", GrantScope() }, { "status", Status() },
            { "requestedBy", Long(1) }, { "approvedBy", Long(1) }, { "reason", Text(2000) },
            { "effectiveAt", Date() }, { "expiresAt", NullableDate() }, { "revokedAt", NullableDate() },
            { "version", Long(1) }, { "delegablePermissions", Permissions() }
        });

    private static BsonDocument ScopeNode() => Document(
        ["kind", "status", "version", "fence"], new BsonDocument
        {
            { "kind", Choice("buyingOrganization", "fulfillmentOrganization", "branch", "division", "shop", "warehouse", "channel") },
            { "parentId", NullableLong() }, { "status", Status() },
            { "version", Long(1) }, { "fence", Long(0) }
        });

    private static BsonDocument Proposal() => Document(
        ["status", "kind", "subjectId", "requestedBy", "reason", "scope", "request", "createdAt", "version"], new BsonDocument
        {
            { "status", Choice("pending", "approved", "rejected") },
            { "kind", Choice("grant", "membership", "scope") }, { "subjectId", Long(1) },
            { "requestedBy", Long(1) }, { "reason", Text(2000) }, { "scope", GrantScope() },
            { "request", Object() }, { "createdAt", Date() }, { "version", Long(1) },
            { "approvedBy", Long(1) }, { "approvedAt", Date() }, { "resultId", Long(1) }
        });

    private static BsonDocument History() => Document(
        ["actorId", "subjectId", "operation", "reason", "correlationId", "createdAt", "before", "after"], new BsonDocument
        {
            { "actorId", Long(1) }, { "subjectId", Long(1) }, { "operation", Text(128) },
            { "reason", Text(2000) }, { "correlationId", Text(128) }, { "createdAt", Date() },
            { "before", Object() }, { "after", Object() }
        });

    private static BsonDocument GrantScope() => new()
    {
        { "bsonType", "object" }, { "required", new BsonArray { "kind" } },
        { "additionalProperties", false },
        { "properties", new BsonDocument
            {
                { "kind", Choice("buyerOwned", "buyingDivision", "buyingBranch", "buyingOrganization",
                    "fulfillmentShop", "fulfillmentWarehouse", "courierAssignment", "system") },
                { "organizationId", NullableLong() }, { "branchId", NullableLong() },
                { "divisionId", NullableLong() }, { "shopId", NullableLong() },
                { "warehouseId", NullableLong() }, { "channelId", NullableLong() }
            }
        }
    };

    private static BsonDocument Document(string[] required, BsonDocument properties)
    {
        properties.InsertAt(0, new BsonElement("schemaVersion", new BsonDocument("enum",
            new BsonArray { DocumentSchemaVersion })));
        properties.InsertAt(0, new BsonElement("_id", Long(1)));
        return new BsonDocument
        {
            { "bsonType", "object" },
            { "required", new BsonArray(new[] { "_id", "schemaVersion" }.Concat(required)) },
            { "properties", properties }
        };
    }

    private static BsonDocument Validator(BsonDocument schema) => new("$jsonSchema", schema);
    private static BsonDocument Long(long minimum) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument NullableLong() => new()
        { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } };
    private static BsonDocument Text(int maximum) => new()
        { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument NullableDate() => new("bsonType", new BsonArray { "date", "null" });
    private static BsonDocument Object() => new("bsonType", "object");
    private static BsonDocument Choice(params string[] values) => new("enum", new BsonArray(values));
    private static BsonDocument Status() => Choice("pending", "active", "suspended", "revoked");
    private static BsonDocument Permissions() => new()
    {
        { "bsonType", "array" }, { "maxItems", 256 }, { "uniqueItems", true }, { "items", Text(128) }
    };
}
