using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

/// <summary>Current identifier extension; frozen catalog v11 validators remain unchanged.</summary>
internal static class CatalogIdentifierRecords
{
    internal const string Collection = "catalogIdentifiers", History = "catalogIdentifierHistory";
    internal static readonly BsonDocument PrimaryPredicate = new() { { "kind", "skuCode" }, { "isPrimary", true } };
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] IdentifierKeys =
    [
        ("ux_reserved_identifier", new() { { "organizationId", 1 }, { "kind", 1 }, { "namespace", 1 }, { "canonicalValue", 1 } }, true),
        ("ux_sku_primary", new("skuId", 1), true),
        ("ix_identifier_sku", new() { { "skuId", 1 }, { "_id", 1 } }, false)
    ];
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] HistoryKeys =
        [("ux_identifier_assignment", new() { { "identifierId", 1 }, { "assignmentVersion", 1 } }, true)];

    internal static CatalogIdentifier Read(BsonDocument row)
    {
        if (row.GetValue("schemaVersion", BsonNull.Value) == 1)
        {
            if (!CatalogSchemas.IsValid(Collection, row)) throw Broken();
        }
        else if (!CatalogRepository.Fields(row, "_id", "schemaVersion", "organizationId", "skuId", "kind", "namespace", "rawValue", "canonicalValue",
            "isPrimary", "effectiveAt", "retiredAt", "revision", "provenanceReference", "auditId", "definitionId") ||
            !row["schemaVersion"].IsInt32 || row["schemaVersion"] != 2 ||
            new[] { "_id", "organizationId", "skuId", "revision", "auditId", "definitionId" }.Any(key => !CatalogRepository.Positive(row, key)) ||
            new[] { "kind", "namespace", "rawValue", "canonicalValue", "provenanceReference" }.Any(key => !row[key].IsString) ||
            !row["isPrimary"].IsBoolean || !CatalogSchemas.ValidDate(row["effectiveAt"]) ||
            !row["retiredAt"].IsBsonNull && !CatalogSchemas.ValidDate(row["retiredAt"])) throw Broken();
        try
        {
            var value = new CatalogIdentifier(new(row["_id"].AsInt64), new(row["organizationId"].AsInt64), new(row["skuId"].AsInt64),
                row["kind"].AsString, row["namespace"].AsString, row["rawValue"].AsString, row["canonicalValue"].AsString,
                row["isPrimary"].AsBoolean, CatalogRepository.Date(row["effectiveAt"]), row["retiredAt"].IsBsonNull ? null : CatalogRepository.Date(row["retiredAt"]),
                row["revision"].AsInt64, row["provenanceReference"].AsString, row.TryGetValue("definitionId", out var definition) ? new LongId(definition.AsInt64) : null);
            CatalogIdentifierPolicy.Validate(value); return value;
        }
        catch (Exception error) when (error is DomainValidationException or InvalidCastException or KeyNotFoundException or ArgumentException) { throw Broken(); }
    }
    internal static BsonDocument Write(CatalogIdentifier value, long auditId)
    {
        CatalogIdentifierPolicy.Validate(value);
        if (auditId <= 0 || value.DefinitionId is null) throw Broken();
        return new()
        {
            { "_id", value.Id.Value }, { "schemaVersion", 2 }, { "organizationId", value.OrganizationId.Value }, { "skuId", value.SkuId.Value },
            { "kind", value.Kind }, { "namespace", value.Namespace }, { "rawValue", value.RawValue }, { "canonicalValue", value.CanonicalValue },
            { "isPrimary", value.IsPrimary }, { "effectiveAt", value.EffectiveAt.UtcDateTime }, { "retiredAt", value.RetiredAt is { } at ? new BsonDateTime(at.UtcDateTime) : BsonNull.Value },
            { "revision", value.Revision }, { "provenanceReference", value.ProvenanceReference }, { "auditId", auditId }, { "definitionId", value.DefinitionId.Value.Value }
        };
    }
    internal static BsonDocument AssignmentHistory(long historyId, BsonDocument assignment, string operation, long actorId, DateTimeOffset at) => new()
    {
        { "_id", historyId }, { "schemaVersion", 2 }, { "identifierId", assignment["_id"] }, { "organizationId", assignment["organizationId"] },
        { "skuId", assignment["skuId"] }, { "assignmentVersion", assignment["revision"] }, { "event", operation }, { "actorId", actorId },
        { "createdAt", at.UtcDateTime }, { "auditId", assignment["auditId"] }, { "assignment", assignment.DeepClone() }
    };
    internal static void RequireHistory(BsonDocument row)
    {
        if (row.GetValue("schemaVersion", BsonNull.Value) == 1)
        { if (!CatalogSchemas.IsValid(History, row)) throw Broken(); return; }
        if (!CatalogRepository.Fields(row, "_id", "schemaVersion", "identifierId", "organizationId", "skuId", "assignmentVersion", "event", "actorId", "createdAt", "auditId", "assignment") ||
            !row["schemaVersion"].IsInt32 || row["schemaVersion"] != 2 ||
            new[] { "_id", "identifierId", "organizationId", "skuId", "assignmentVersion", "actorId", "auditId" }.Any(key => !CatalogRepository.Positive(row, key)) ||
            !row["event"].IsString || row["event"].AsString is not ("assigned" or "retired" or "aliased") ||
            !CatalogSchemas.ValidDate(row["createdAt"]) || !row["assignment"].IsBsonDocument) throw Broken();
        var assignment = row["assignment"].AsBsonDocument; var parsed = Read(assignment);
        if (assignment.GetValue("schemaVersion", 0) != 2 || row["identifierId"] != assignment["_id"] || row["organizationId"] != assignment["organizationId"] ||
            row["skuId"] != assignment["skuId"] || row["assignmentVersion"] != assignment["revision"] || row["auditId"] != assignment["auditId"] ||
            CatalogRepository.Date(row["createdAt"]) < parsed.EffectiveAt ||
            row["event"] == "retired" && parsed.RetiredAt != CatalogRepository.Date(row["createdAt"]) ||
            row["event"] == "assigned" && (parsed.Revision != 1 || parsed.EffectiveAt != CatalogRepository.Date(row["createdAt"]) || parsed.RetiredAt is not null) ||
            row["event"] == "aliased" && (parsed.IsPrimary || parsed.Kind != "skuCode" || parsed.Revision <= 1 || parsed.RetiredAt is not null)) throw Broken();
    }
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in new[] { Collection, History })
        {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var expected = CatalogIdentifierSchemas.Validator(name);
            if (definition is null || !AccountContactStorage.SafeDefinition(definition, expected) ||
                !CatalogSchemas.SemanticEquals(definition["options"]["validator"], expected)) throw Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!SafeIndexes(name, indexes)) throw Broken();
        }
    }
    internal static bool SafeIndexes(string collection, IReadOnlyList<BsonDocument> indexes)
    {
        var required = collection == Collection ? IdentifierKeys : HistoryKeys;
        return indexes.Count == required.Length + 1 && indexes.Any(row => row.GetValue("name", "") == "_id_" &&
            row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1) && !Unsafe(row) && !row.Contains("partialFilterExpression")) &&
            required.All(key => indexes.Count(row => Matches(collection, row, key)) == 1);
    }
    internal static bool Matches(string collection, BsonDocument index, (string Name, BsonDocument Key, bool Unique) key) =>
        index.GetValue("name", "") == key.Name && index.GetValue("key", BsonNull.Value) == key.Key &&
        index.GetValue("unique", false).IsBoolean && index.GetValue("unique", false).AsBoolean == key.Unique && !Unsafe(index) &&
        (collection == Collection && key.Name == "ux_sku_primary"
            ? index.GetValue("partialFilterExpression", BsonNull.Value) == PrimaryPredicate
            : !index.Contains("partialFilterExpression"));
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false) != false || index.GetValue("hidden", false) != false ||
        index.Contains("expireAfterSeconds") || index.Contains("collation") && index["collation"] != new BsonDocument("locale", "simple");
    internal static FilterDefinition<BsonDocument> Exact(BsonDocument row) => Builders<BsonDocument>.Filter.And(row.Select(field => Builders<BsonDocument>.Filter.Eq(field.Name, field.Value))) &
        new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { new BsonDocument("$size", new BsonDocument("$objectToArray", "$$ROOT")), row.ElementCount }));
    internal static async Task FenceAsync(IMongoCollection<BsonDocument> rows, IClientSessionHandle tx, BsonDocument observed, CancellationToken ct)
    {
        // The byte-equivalent replacement must be a real write even for immutable histories.
        if ((await rows.DeleteOneAsync(tx, Exact(observed), cancellationToken: ct)).DeletedCount != 1) throw new PersistenceConflictException("catalog_identifier_changed", "Reload the identifier and confirm its current package");
        await rows.InsertOneAsync(tx, observed.DeepClone().AsBsonDocument, cancellationToken: ct);
    }
    internal static DomainValidationException Broken() => new("catalog_identifier_integrity_invalid", "The permanent identifier reservation or its evidence is unavailable");
}
