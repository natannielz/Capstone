using System.Collections.Immutable;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

/// <summary>Private persistence mechanics. Callers must authorize server-loaded ownership in the same transaction.</summary>
internal sealed class CatalogRepository(IMongoDatabase database)
{
    internal IMongoCollection<BsonDocument> Rows(string collection) => database.GetCollection<BsonDocument>(collection);
    internal async Task<BsonDocument> GetAsync(IClientSessionHandle tx, string collection, long id, CancellationToken ct)
    {
        if (id <= 0) throw Invalid();
        return Require(collection, await Rows(collection).Find(tx, new BsonDocument("_id", id)).FirstOrDefaultAsync(ct));
    }
    internal static BsonDocument Require(string collection, BsonDocument? row)
    {
        if (row is null || !CatalogSchemas.IsValid(collection, row)) throw Invalid();
        return row;
    }
    internal static AuthorizationDeniedException Invalid(string code = "catalog_resource_unavailable") => new(code);
    internal static BsonDocument Owner(GrantScope scope)
    {
        CatalogAuthoringPolicy.Owner(scope);
        return new() { { "kind", AuthorizationBson.Name(scope.Kind) }, { "organizationId", scope.OrganizationId!.Value },
            { "shopId", scope.ShopId!.Value }, { "channelId", scope.ChannelId!.Value },
            { "warehouseId", scope.WarehouseId.HasValue ? new BsonInt64(scope.WarehouseId.Value) : BsonNull.Value } };
    }
    internal static GrantScope Owner(BsonDocument row)
    {
        var allowed = new[] { "kind", "organizationId", "shopId", "channelId", "warehouseId", "branchId", "divisionId" };
        if (row.Names.Distinct(StringComparer.Ordinal).Count() != row.ElementCount || row.Names.Any(name => !allowed.Contains(name)) ||
            !row.TryGetValue("kind", out var kind) || !kind.IsString || kind.AsString is not ("fulfillmentShop" or "fulfillmentWarehouse") ||
            !Positive(row, "organizationId") || !Positive(row, "shopId") || !Positive(row, "channelId") ||
            !row.GetValue("branchId", BsonNull.Value).IsBsonNull || !row.GetValue("divisionId", BsonNull.Value).IsBsonNull) throw Invalid();
        var warehouse = row.GetValue("warehouseId", BsonNull.Value);
        if (kind == "fulfillmentShop" && !warehouse.IsBsonNull || kind == "fulfillmentWarehouse" && (!warehouse.IsInt64 || warehouse.AsInt64 <= 0)) throw Invalid();
        return new(kind == "fulfillmentShop" ? GrantScopeKind.FulfillmentShop : GrantScopeKind.FulfillmentWarehouse,
            row["organizationId"].AsInt64, ShopId: row["shopId"].AsInt64,
            WarehouseId: warehouse.IsBsonNull ? null : warehouse.AsInt64, ChannelId: row["channelId"].AsInt64);
    }
    internal static FilterDefinition<BsonDocument> OwnerFilter(string path, GrantScope owner)
    {
        var f = Builders<BsonDocument>.Filter;
        return f.And(Owner(owner).Select(element => f.Eq(path + "." + element.Name, element.Value))
            .Append(f.Eq(path + ".branchId", BsonNull.Value)).Append(f.Eq(path + ".divisionId", BsonNull.Value)));
    }
    internal static ResourceScope Resource(GrantScope owner) => new(FulfillmentOrganizationId: owner.OrganizationId,
        ShopId: owner.ShopId, WarehouseId: owner.WarehouseId, ChannelId: owner.ChannelId);
    internal static bool Positive(BsonDocument row, string field) =>
        row.TryGetValue(field, out var value) && value.IsInt64 && value.AsInt64 > 0;
    internal static bool Fields(BsonDocument row, params string[] fields) =>
        row.ElementCount == fields.Length && fields.All(row.Contains);
    internal static DateTimeOffset Date(BsonValue value) => CatalogSchemas.ValidDate(value)
        ? DateTimeOffset.FromUnixTimeMilliseconds(value.AsBsonDateTime.MillisecondsSinceEpoch) : throw Invalid();
    internal static long Next(long value)
    {
        if (value is <= 0 or long.MaxValue) throw new PersistenceConflictException("catalog_revision_exhausted", "Catalog revision is exhausted");
        return value + 1;
    }
    internal static void Expected(BsonDocument row, long expected)
    {
        if (expected <= 0) throw new DomainValidationException("invalid_catalog_request", "A positive expected revision is required");
        if (row["revision"].AsInt64 != expected) throw new PersistenceConflictException("catalog_revision_conflict", "Catalog data changed; reload and review");
    }
    internal static BsonDocument Category(CatalogCategory value, long fence = 0) => new()
    {
        { "_id", value.Id.Value }, { "schemaVersion", 1 }, { "organizationId", value.OrganizationId.Value },
        { "authoringOwner", Owner(value.AuthoringOwner) }, { "code", value.Code }, { "name", value.Name },
        { "active", value.Active }, { "revision", value.Revision }, { "fence", fence }
    };
    internal static CatalogCategory Category(BsonDocument row)
    {
        Require("catalogCategories", row);
        return new(new(row["_id"].AsInt64), new(row["organizationId"].AsInt64), Owner(row["authoringOwner"].AsBsonDocument),
            row["code"].AsString, row["name"].AsString, row["active"].AsBoolean, row["revision"].AsInt64);
    }
    internal static BsonDocument Family(ProductFamily value, long fence = 0) => new()
    {
        { "_id", value.Id.Value }, { "schemaVersion", 1 }, { "organizationId", value.OrganizationId.Value },
        { "authoringOwner", Owner(value.AuthoringOwner) }, { "publicId", value.PublicId.ToString("D") },
        { "publicUuid", new BsonBinaryData(value.PublicId, GuidRepresentation.Standard) },
        { "categoryId", value.CategoryId.Value }, { "categoryCode", value.CategoryCode }, { "name", value.Name },
        { "description", value.Description }, { "imageAssetIds", new BsonArray() },
        { "active", value.Active }, { "revision", value.Revision }, { "fence", fence }
    };
    internal static ProductFamily Family(BsonDocument row)
    {
        Require("productFamilies", row);
        return new(new(row["_id"].AsInt64), new(row["organizationId"].AsInt64), Owner(row["authoringOwner"].AsBsonDocument),
            Guid.Parse(row["publicId"].AsString), new(row["categoryId"].AsInt64), row["categoryCode"].AsString,
            row["name"].AsString, row["description"].AsString, [], row["active"].AsBoolean, row["revision"].AsInt64);
    }
    internal static BsonDocument Sku(Sku value, GrantScope owner, VariantDefinitionVersion definition,
        BsonArray? listings = null, long fence = 0) => new()
    {
        { "_id", value.Id.Value }, { "schemaVersion", 1 }, { "organizationId", value.OrganizationId.Value },
        { "authoringOwner", Owner(owner) }, { "familyId", value.FamilyId.Value },
        { "publicId", value.PublicId.ToString("D") }, { "publicUuid", new BsonBinaryData(value.PublicId, GuidRepresentation.Standard) },
        { "sku", value.Code }, { "physicalItemKey", value.PhysicalItemKey }, { "active", value.Active },
        { "currentDefinitionId", value.CurrentDefinitionId.Value }, { "revision", value.Revision },
        { "predecessorSkuId", value.PredecessorSkuId is { } predecessor ? new BsonInt64(predecessor.Value) : BsonNull.Value },
        { "displayName", value.Code + " · " + definition.PackageLabel },
        { "catalogListings", listings?.DeepClone() ?? new BsonArray() }, { "fence", fence }
    };
    internal static Sku Sku(BsonDocument row)
    {
        Require("skus", row);
        return new(new(row["_id"].AsInt64), new(row["organizationId"].AsInt64), new(row["familyId"].AsInt64),
            Guid.Parse(row["publicId"].AsString), row["sku"].AsString, row["physicalItemKey"].AsString,
            row["active"].AsBoolean, new(row["currentDefinitionId"].AsInt64), row["revision"].AsInt64,
            row["predecessorSkuId"].IsBsonNull ? null : new LongId(row["predecessorSkuId"].AsInt64));
    }
    internal static BsonDocument Definition(VariantDefinitionVersion value) => new()
    {
        { "_id", value.Id.Value }, { "schemaVersion", 1 }, { "skuId", value.SkuId.Value }, { "version", value.Version },
        { "packageLabel", value.PackageLabel }, { "packageKind", value.PackageKind }, { "baseUnitCode", value.BaseUnitCode },
        { "baseUnitsPerPackage", value.BaseUnitsPerPackage },
        { "attributes", new BsonArray(value.Attributes.Select(pair => new BsonDocument { { "key", pair.Key }, { "value", pair.Value } })) },
        { "imageAssetIds", new BsonArray() }, { "minimumOrderPackages", value.MinimumOrderPackages },
        { "orderIncrementPackages", value.OrderIncrementPackages }, { "effectiveAt", value.EffectiveAt.UtcDateTime }
    };
    internal static VariantDefinitionVersion Definition(BsonDocument row)
    {
        Require("variantDefinitions", row);
        var attributes = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in row["attributes"].AsBsonArray) attributes.Add(pair["key"].AsString, pair["value"].AsString);
        return new(new(row["_id"].AsInt64), new(row["skuId"].AsInt64), row["version"].AsInt64, row["packageLabel"].AsString,
            row["packageKind"].AsString, row["baseUnitCode"].AsString, row["baseUnitsPerPackage"].AsInt32,
            attributes.ToImmutable(), [], row["minimumOrderPackages"].AsInt32, row["orderIncrementPackages"].AsInt32, Date(row["effectiveAt"]));
    }
    internal static BsonDocument Assortment(BsonDocument? row)
    {
        if (row is null || !Fields(row, "_id", "schemaVersion", "ownerScope", "status", "version", "fence") ||
            !Positive(row, "_id") || !Positive(row, "version") || !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 ||
            !row["fence"].IsInt64 || row["fence"].AsInt64 < 0 || !row["status"].IsString ||
            row["status"].AsString is not ("active" or "suspended" or "revoked") || !row["ownerScope"].IsBsonDocument) throw Invalid();
        _ = Owner(row["ownerScope"].AsBsonDocument);
        return row;
    }
    internal static BsonDocument Snapshots(BsonDocument? category = null, BsonDocument? family = null, BsonDocument? sku = null,
        BsonDocument? definition = null, IEnumerable<BsonDocument>? assortments = null) => new()
    {
        { "category", category?.DeepClone() ?? BsonNull.Value }, { "family", family?.DeepClone() ?? BsonNull.Value },
        { "sku", sku?.DeepClone() ?? BsonNull.Value }, { "definition", definition?.DeepClone() ?? BsonNull.Value },
        { "assortments", new BsonArray((assortments ?? []).OrderBy(row => row["_id"].AsInt64).Select(row => row.DeepClone())) }
    };
    internal async Task InsertAsync(IClientSessionHandle tx, string collection, BsonDocument row, CancellationToken ct)
    {
        Require(collection, row); await Rows(collection).InsertOneAsync(tx, row, cancellationToken: ct);
    }
    internal async Task ReplaceAsync(IClientSessionHandle tx, string collection, BsonDocument before, BsonDocument after, CancellationToken ct)
    {
        if (before["fence"].AsInt64 == long.MaxValue) throw new PersistenceConflictException("catalog_revision_exhausted", "Catalog fence is exhausted");
        after["fence"] = before["fence"].AsInt64 + 1;
        Require(collection, after);
        var result = await Rows(collection).ReplaceOneAsync(tx, Exact(collection, before), after, cancellationToken: ct);
        if (result.ModifiedCount != 1) throw new PersistenceConflictException("catalog_revision_conflict", "Catalog data changed; reload and review");
    }
    internal async Task TouchAsync(IClientSessionHandle tx, string collection, BsonDocument expected, CancellationToken ct)
    {
        if (expected["fence"].AsInt64 == long.MaxValue) throw Invalid("catalog_authority_changed");
        var result = await Rows(collection).UpdateOneAsync(tx, Exact(collection, expected),
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
        if (result.ModifiedCount != 1) throw Invalid("catalog_authority_changed");
    }
    internal static FilterDefinition<BsonDocument> Exact(string collection, BsonDocument expected)
    {
        var f = Builders<BsonDocument>.Filter;
        var filters = expected.Where(element => element.Name != "fence").Select(element => f.Eq(element.Name, element.Value)).ToList();
        if (collection != "catalogAssortments") filters.Add(CatalogSchemas.Validator(collection));
        else
        {
            var types = new BsonDocument(expected.Select(element => new BsonElement(element.Name,
                new BsonDocument("bsonType", element.Value.BsonType switch
                { BsonType.Int64 => "long", BsonType.Int32 => "int", BsonType.String => "string", BsonType.Document => "object", _ => throw Invalid() }))));
            filters.Add(new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" },
                { "required", new BsonArray(expected.Names) }, { "additionalProperties", false }, { "properties", types } }));
        }
        filters.Add(new BsonDocument("fence", new BsonDocument { { "$type", "long" }, { "$gte", 0L }, { "$lt", long.MaxValue } }));
        return f.And(filters);
    }
    internal static string Digest(BsonValue value) => Convert.ToHexString(SHA256.HashData(
        new BsonDocument("value", Canonical(value)).ToBson())).ToLowerInvariant();
    private static BsonValue Canonical(BsonValue value) => value switch
    {
        BsonDocument document => new BsonDocument(document.Elements.OrderBy(element => element.Name, StringComparer.Ordinal)
            .Select(element => new BsonElement(element.Name, Canonical(element.Value)))),
        BsonArray array => new BsonArray(array.Select(Canonical)),
        _ => value.DeepClone()
    };
}
