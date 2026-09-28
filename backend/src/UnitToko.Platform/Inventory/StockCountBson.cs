using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

/// <summary>Canonical immutable records; historical facts never carry current authority.</summary>
internal static class StockCountBson
{
    internal const string Operation = "inventory.stock-count.record";
    internal const string AuditKind = "inventory.stock-count.recorded";
    internal const string Collection = "stockCountObservations";
    internal static bool Key(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9._:-]{1,128}\z", RegexOptions.CultureInvariant);
    internal static string Hash(BsonDocument value) => Convert.ToHexString(SHA256.HashData(value.ToBson())).ToLowerInvariant();
    internal static bool IsHash(BsonValue value) => value.IsString && Regex.IsMatch(value.AsString, @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    internal static BsonDocument Scope(GrantScope scope) => CatalogRepository.Owner(scope);
    internal static GrantScope Scope(BsonDocument row)
    {
        Fields(row, "kind", "organizationId", "shopId", "channelId", "warehouseId");
        return CatalogRepository.Owner(row);
    }
    internal static string BodyHash(StockCountInput input, string reason) => Hash(new()
    {
        { "location", Scope(input.Location) }, { "skuId", input.SkuId }, { "expectedSkuRevision", input.ExpectedSkuRevision },
        { "expectedDefinitionId", input.ExpectedDefinitionId }, { "packages", input.Packages },
        { "observedLotLabel", input.ObservedLotLabel is { } label ? label : BsonNull.Value }, { "reason", reason }
    });
    internal static StockCountDefinitionSnapshot Definition(StockDefinitionSnapshot value) =>
        new(value.Owner, value.SkuId, value.SkuRevision, value.FamilyId, value.FamilyRevision, value.CategoryId,
            value.CategoryRevision, value.DefinitionId, value.DefinitionVersion, value.SkuCode, value.PhysicalItemKey,
            value.PackageLabel, value.PackageKind, value.BaseUnitCode, value.BaseUnitsPerPackage);
    internal static BsonDocument Definition(StockCountDefinitionSnapshot value) => new()
    {
        { "owner", Scope(value.Owner) }, { "skuId", value.SkuId }, { "skuRevision", value.SkuRevision },
        { "familyId", value.FamilyId }, { "familyRevision", value.FamilyRevision },
        { "categoryId", value.CategoryId }, { "categoryRevision", value.CategoryRevision },
        { "definitionId", value.DefinitionId }, { "definitionVersion", value.DefinitionVersion },
        { "skuCode", value.SkuCode }, { "physicalItemKey", value.PhysicalItemKey }, { "packageLabel", value.PackageLabel },
        { "packageKind", value.PackageKind }, { "baseUnitCode", value.BaseUnitCode }, { "baseUnitsPerPackage", value.BaseUnitsPerPackage }
    };
    internal static BsonDocument Create(long id, long auditId, ActorContext actor, StockCountInput input,
        StockCountDefinitionSnapshot definition, AuthorizationCommandMetadata metadata, DateTimeOffset now) =>
        Create(id, auditId, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, input,
            definition, metadata.IdempotencyKey, metadata.Reason, metadata.CorrelationId,
            DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
    private static BsonDocument Create(long id, long auditId, long actorId, string issuer, string subject,
        long grantVersion, StockCountInput input, StockCountDefinitionSnapshot definition, string key,
        string reason, string correlationId, DateTimeOffset now)
    {
        var row = new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "revision", 1L }, { "location", Scope(input.Location) },
            { "actorId", actorId }, { "issuer", issuer }, { "subject", subject }, { "actorGrantVersion", grantVersion },
            { "recordedAt", now.UtcDateTime }, { "requestKey", key }, { "bodyHash", BodyHash(input, reason) },
            { "definition", Definition(definition) }, { "packages", input.Packages },
            { "baseUnits", StockCountPolicy.BaseUnits(input.Packages, definition.BaseUnitsPerPackage) },
            { "observedLotLabel", input.ObservedLotLabel is { } label ? label : BsonNull.Value },
            { "baseline", "unavailable" }, { "coverage", "single-observation" }, { "auditId", auditId },
            { "reason", reason }, { "correlationId", correlationId }
        };
        row.Add("fingerprint", Hash(row));
        return row;
    }
    internal static StockCountObservation Read(BsonDocument row)
    {
        try { return ReadCore(row); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidCastException or ArgumentException or DomainValidationException or OverflowException)
        { throw Invalid(); }
    }
    private static StockCountObservation ReadCore(BsonDocument row)
    {
        Fields(row, "_id", "schemaVersion", "revision", "location", "actorId", "issuer", "subject", "actorGrantVersion",
            "recordedAt", "requestKey", "bodyHash", "definition", "packages", "baseUnits", "observedLotLabel",
            "baseline", "coverage", "auditId", "reason", "correlationId", "fingerprint");
        if (!Schema(row) || Long(row, "revision") != 1 || !CatalogSchemas.ValidDate(row["recordedAt"]) ||
            !IsHash(row["bodyHash"]) || !IsHash(row["fingerprint"]) || !Key(Text(row, "requestKey", 128)) ||
            row["baseline"] != "unavailable" || row["coverage"] != "single-observation") throw Invalid();
        var location = Scope(Document(row, "location"));
        StockCountPolicy.Location(location);
        var definition = ReadDefinition(Document(row, "definition"));
        if (!ScopePolicy.ContainsScope(definition.Owner, location)) throw Invalid();
        var packages = Integer(row, "packages");
        var label = row["observedLotLabel"].IsBsonNull ? null : Text(row, "observedLotLabel", 160);
        var input = new StockCountInput(location, definition.SkuId, definition.SkuRevision, definition.DefinitionId, packages, label);
        StockCountPolicy.Validate(input);
        var id = Long(row, "_id");
        var audit = Long(row, "auditId");
        var actor = Long(row, "actorId");
        var issuer = Text(row, "issuer", 2048);
        var subject = Text(row, "subject", 256);
        var grantVersion = Long(row, "actorGrantVersion");
        var reason = Text(row, "reason", 1000);
        var correlation = Text(row, "correlationId", 128);
        var time = DateTimeOffset.FromUnixTimeMilliseconds(row["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch);
        var expected = Create(id, audit, actor, issuer, subject, grantVersion, input, definition, row["requestKey"].AsString, reason, correlation, time);
        if (!Same(row, expected)) throw Invalid();
        return new(id, location, actor, issuer, subject, time, definition, packages,
            StockCountPolicy.BaseUnits(packages, definition.BaseUnitsPerPackage), label, "unavailable", "single-observation", audit, reason);
    }
    private static StockCountDefinitionSnapshot ReadDefinition(BsonDocument row)
    {
        Fields(row, "owner", "skuId", "skuRevision", "familyId", "familyRevision", "categoryId", "categoryRevision",
            "definitionId", "definitionVersion", "skuCode", "physicalItemKey", "packageLabel", "packageKind", "baseUnitCode", "baseUnitsPerPackage");
        var owner = Scope(Document(row, "owner"));
        CatalogAuthoringPolicy.Owner(owner);
        var code = Text(row, "skuCode", 64);
        if (CatalogPolicy.NormalizeSkuCode(code) != code) throw Invalid();
        var kind = Text(row, "packageKind", 16);
        if (kind is not ("loose" or "sealed" or "other")) throw Invalid();
        var contents = Integer(row, "baseUnitsPerPackage");
        if (contents <= 0) throw Invalid();
        return new(owner, Long(row, "skuId"), Long(row, "skuRevision"), Long(row, "familyId"), Long(row, "familyRevision"),
            Long(row, "categoryId"), Long(row, "categoryRevision"), Long(row, "definitionId"), Long(row, "definitionVersion"),
            code, Text(row, "physicalItemKey", 128), Text(row, "packageLabel", 160), kind, Text(row, "baseUnitCode", 64), contents);
    }
    internal static async Task RequireHistoricalCatalogAsync(StockCountObservation observation,
        Func<string, long, CancellationToken, Task<BsonDocument?>> load, CancellationToken token)
    {
        var snapshot = observation.Definition;
        var sku = RequireHistoricalMaster("skus", await load("skus", snapshot.SkuId, token),
            snapshot.SkuId, snapshot.SkuRevision, snapshot.Owner);
        if (sku["familyId"].AsInt64 != snapshot.FamilyId || sku["sku"] != snapshot.SkuCode ||
            sku["physicalItemKey"] != snapshot.PhysicalItemKey)
            throw Invalid();
        _ = RequireHistoricalMaster("productFamilies", await load("productFamilies", snapshot.FamilyId, token),
            snapshot.FamilyId, snapshot.FamilyRevision, snapshot.Owner);
        // Family category is reclassifiable. Anchor the original category's identity, not today's family.categoryId.
        // Exact historical family/category linkage would require a separate immutable history contract.
        _ = RequireHistoricalMaster("catalogCategories", await load("catalogCategories", snapshot.CategoryId, token),
            snapshot.CategoryId, snapshot.CategoryRevision, snapshot.Owner);
        RequireDefinition(observation, await load("variantDefinitions", snapshot.DefinitionId, token));
    }

    private static BsonDocument RequireHistoricalMaster(string collection, BsonDocument? row, long id,
        long recordedRevision, GrantScope owner)
    {
        if (row is null || !CatalogSchemas.IsValid(collection, row) || row["_id"].AsInt64 != id ||
            row["revision"].AsInt64 < recordedRevision || Scope(Document(row, "authoringOwner")) != owner)
            throw Invalid();
        return row;
    }

    private static void RequireDefinition(StockCountObservation observation, BsonDocument? definition)
    {
        if (definition is null || !CatalogSchemas.IsValid("variantDefinitions", definition) ||
            definition["_id"].AsInt64 != observation.Definition.DefinitionId ||
            definition["skuId"].AsInt64 != observation.Definition.SkuId || definition["version"].AsInt64 != observation.Definition.DefinitionVersion ||
            definition["packageLabel"] != observation.Definition.PackageLabel || definition["packageKind"] != observation.Definition.PackageKind ||
            definition["baseUnitCode"] != observation.Definition.BaseUnitCode || definition["baseUnitsPerPackage"].AsInt32 != observation.Definition.BaseUnitsPerPackage)
            throw Invalid();
    }
    internal static BsonDocument Audit(BsonDocument row) => new()
    {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", AuditKind }, { "operation", Operation },
        { "observationId", row["_id"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "issuer", row["issuer"] }, { "subject", row["subject"] }, { "location", row["location"].DeepClone() },
        { "createdAt", row["recordedAt"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "fingerprint", row["fingerprint"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }
    };
    internal static BsonDocument Receipt(BsonDocument row, ObjectId id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "actorId", row["actorId"] }, { "operation", Operation },
        { "key", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "createdAt", row["recordedAt"] },
        { "result", row.DeepClone() }, { "stockCount", new BsonDocument
            { { "schemaVersion", 1 }, { "observationId", row["_id"] }, { "auditId", row["auditId"] }, { "issuer", row["issuer"] }, { "subject", row["subject"] } } }
    };
    internal static void Graph(BsonDocument row, BsonDocument? audit, BsonDocument? receipt)
    {
        _ = Read(row);
        if (audit is null || receipt is null || !receipt.TryGetValue("_id", out var id) || !id.IsObjectId ||
            !Same(audit, Audit(row)) || !Same(receipt, Receipt(row, id.AsObjectId))) throw Invalid();
    }
    internal static long ReceiptObservationId(BsonDocument receipt)
    {
        if (!receipt.TryGetValue("stockCount", out var marker) || !marker.IsBsonDocument) throw Invalid();
        return Long(marker.AsBsonDocument, "observationId");
    }
    internal static BsonDocument Document(BsonDocument row, string field) =>
        row.TryGetValue(field, out var value) && value.IsBsonDocument ? value.AsBsonDocument : throw Invalid();
    internal static long Long(BsonDocument row, string field) =>
        row.TryGetValue(field, out var value) && value.IsInt64 && value.AsInt64 > 0 ? value.AsInt64 : throw Invalid();
    private static int Integer(BsonDocument row, string field) =>
        row.TryGetValue(field, out var value) && value.IsInt32 ? value.AsInt32 : throw Invalid();
    private static string Text(BsonDocument row, string field, int maximum) =>
        row.TryGetValue(field, out var value) && value.IsString && StockCountPolicy.CanonicalText(value.AsString, maximum) ? value.AsString : throw Invalid();
    private static bool Schema(BsonDocument row) => row.TryGetValue("schemaVersion", out var value) && value.IsInt32 && value.AsInt32 == 1;
    private static void Fields(BsonDocument row, params string[] fields)
    {
        if (row.ElementCount != fields.Length || row.Names.Distinct(StringComparer.Ordinal).Count() != row.ElementCount || fields.Any(field => !row.Contains(field))) throw Invalid();
    }
    internal static bool Same(BsonValue left, BsonValue right) =>
        UniqueFields(left) && UniqueFields(right) && CatalogSchemas.SemanticEquals(left, right);
    private static bool UniqueFields(BsonValue value) => value.IsBsonDocument
        ? value.AsBsonDocument.Names.Distinct(StringComparer.Ordinal).Count() == value.AsBsonDocument.ElementCount &&
            value.AsBsonDocument.All(field => UniqueFields(field.Value))
        : !value.IsBsonArray || value.AsBsonArray.All(UniqueFields);

    internal static AuthorizationDeniedException Invalid() => new("stock_count_record_invalid");
}
