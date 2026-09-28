using System.Text.RegularExpressions;
using MongoDB.Bson;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

/// <summary>Private original-result validation. Historical snapshots never acquire current authority.</summary>
internal static class CatalogReceiptRules
{
    internal static readonly string[] Operations = ["catalog.category.save", "catalog.family.create", "catalog.family.edit", "catalog.sku.create", "catalog.sku.revise", "catalog.sku.replace", "catalog.sku.retire", "catalog.sku.publish"];
    internal static bool Hash(BsonValue value) => value.IsString && Regex.IsMatch(value.AsString, "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    internal static bool Key(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9._:-]{1,128}\\z", RegexOptions.CultureInvariant);
    internal static BsonDocument Result(CatalogMutationResult value) => new()
    {
        { "kind", value.Kind }, { "id", value.Id.Value }, { "version", value.Version },
        { "familyId", value.FamilyId is { } family ? new BsonInt64(family.Value) : BsonNull.Value },
        { "currentDefinitionId", value.CurrentDefinitionId is { } definition ? new BsonInt64(definition.Value) : BsonNull.Value },
        { "definitionVersion", value.DefinitionVersion is { } version ? new BsonInt64(version) : BsonNull.Value },
        { "predecessorSkuId", value.PredecessorSkuId is { } predecessor ? new BsonInt64(predecessor.Value) : BsonNull.Value },
        { "recordedAt", value.RecordedAt.UtcDateTime }
    };
    internal static CatalogMutationResult Result(BsonDocument value)
    {
        if (!CatalogRepository.Fields(value, "kind", "id", "version", "familyId", "currentDefinitionId", "definitionVersion", "predecessorSkuId", "recordedAt") ||
            !value["kind"].IsString || value["kind"].AsString is not ("category" or "family" or "sku") || !CatalogRepository.Positive(value, "id") || !CatalogRepository.Positive(value, "version") ||
            !CatalogSchemas.ValidDate(value["recordedAt"]) || new[] { "familyId", "currentDefinitionId", "definitionVersion", "predecessorSkuId" }.Any(field => !value[field].IsBsonNull && !CatalogRepository.Positive(value, field))) throw Invalid();
        LongId? Id(string field) => value[field].IsBsonNull ? null : new(value[field].AsInt64);
        var result = new CatalogMutationResult(value["kind"].AsString, new(value["id"].AsInt64), value["version"].AsInt64, Id("familyId"), Id("currentDefinitionId"),
            value["definitionVersion"].IsBsonNull ? null : value["definitionVersion"].AsInt64, Id("predecessorSkuId"), CatalogRepository.Date(value["recordedAt"]));
        if (result.Kind == "category" && (result.FamilyId is not null || result.CurrentDefinitionId is not null || result.DefinitionVersion is not null || result.PredecessorSkuId is not null) ||
            result.Kind == "family" && (result.FamilyId != result.Id || result.CurrentDefinitionId is not null || result.DefinitionVersion is not null || result.PredecessorSkuId is not null) ||
            result.Kind == "sku" && (result.FamilyId is null || result.CurrentDefinitionId is null || result.DefinitionVersion is null)) throw Invalid();
        return result;
    }
    internal static void RequireReceipt(BsonDocument receipt, long actorId, string operation, string key)
    {
        if (!CatalogRepository.Fields(receipt, "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result", "catalogAuthoring") ||
            !receipt["_id"].IsObjectId || !Schema(receipt) || !CatalogRepository.Positive(receipt, "actorId") || receipt["actorId"].AsInt64 != actorId ||
            !receipt["operation"].IsString || receipt["operation"].AsString != operation || !Operations.Contains(operation) ||
            !receipt["key"].IsString || receipt["key"].AsString != key || !Key(key) || !Hash(receipt["bodyHash"]) ||
            !CatalogSchemas.ValidDate(receipt["createdAt"]) || !receipt["result"].IsBsonDocument || !receipt["catalogAuthoring"].IsBsonDocument) throw Invalid();
        var reference = receipt["catalogAuthoring"].AsBsonDocument;
        if (!CatalogRepository.Fields(reference, "schemaVersion", "auditId") || !Schema(reference) || !CatalogRepository.Positive(reference, "auditId")) throw Invalid();
        _ = Result(receipt["result"].AsBsonDocument);
    }
    internal static GrantScope[] RequireAudit(BsonDocument audit)
    {
        try { return Audit(audit); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidCastException or ArgumentException or DomainValidationException or AuthorizationDeniedException)
        { throw Invalid(); }
    }
    private static GrantScope[] Audit(BsonDocument audit)
    {
        if (!CatalogRepository.Fields(audit, "_id", "schemaVersion", "kind", "actorId", "actorGrantVersion", "operation", "bodyHash", "reason", "correlationId", "createdAt", "owners", "result", "before", "after") ||
            !Schema(audit) || !CatalogRepository.Positive(audit, "_id") || !CatalogRepository.Positive(audit, "actorId") ||
            !audit["actorGrantVersion"].IsInt64 || audit["actorGrantVersion"].AsInt64 <= 0 || !audit["kind"].IsString || audit["kind"].AsString != "catalog.command" ||
            !audit["operation"].IsString || !Operations.Contains(audit["operation"].AsString) || !Hash(audit["bodyHash"]) ||
            !Text(audit["reason"], 1000) || !Text(audit["correlationId"], 128) || !CatalogSchemas.ValidDate(audit["createdAt"]) ||
            !audit["result"].IsBsonDocument || !audit["before"].IsBsonDocument || !audit["after"].IsBsonDocument || !audit["owners"].IsBsonArray) throw Invalid();
        var result = Result(audit["result"].AsBsonDocument); if (result.RecordedAt != CatalogRepository.Date(audit["createdAt"])) throw Invalid();
        var before = audit["before"].AsBsonDocument; var after = audit["after"].AsBsonDocument; Snapshot(before); Snapshot(after);
        var operation = audit["operation"].AsString; var masterName = result.Kind == "category" ? "category" : result.Kind == "family" ? "family" : "sku";
        if (!after[masterName].IsBsonDocument) throw Invalid(); var master = after[masterName].AsBsonDocument;
        if (master["_id"].AsInt64 != result.Id.Value || master["revision"].AsInt64 != result.Version || !operation.StartsWith("catalog." + result.Kind + ".", StringComparison.Ordinal)) throw Invalid();
        if (result.Kind == "sku")
        {
            var definition = after["definition"].AsBsonDocument;
            if (master["familyId"].AsInt64 != result.FamilyId!.Value.Value || master["currentDefinitionId"].AsInt64 != result.CurrentDefinitionId!.Value.Value ||
                definition["version"].AsInt64 != result.DefinitionVersion || master["predecessorSkuId"] != audit["result"]["predecessorSkuId"]) throw Invalid();
        }
        if (operation is "catalog.sku.create" or "catalog.sku.replace")
        {
            if (master["revision"].AsInt64 != 1 || !master["active"].AsBoolean || master["catalogListings"].AsBsonArray.Count != 0 || after["definition"]["version"].AsInt64 != 1 ||
                after["definition"]["effectiveAt"] != audit["createdAt"] || !Same(before["family"], after["family"]) || !Same(before["category"], after["category"])) throw Invalid();
            if (operation == "catalog.sku.create")
            {
                if (!before["sku"].IsBsonNull || !before["definition"].IsBsonNull || !master["predecessorSkuId"].IsBsonNull) throw Invalid();
            }
            else
            {
                if (!before["sku"].IsBsonDocument || !before["sku"]["active"].AsBoolean || master["predecessorSkuId"] != before["sku"]["_id"] ||
                    master["_id"] == before["sku"]["_id"] || master["publicId"] == before["sku"]["publicId"] || master["sku"] == before["sku"]["sku"] ||
                    master["familyId"] != before["sku"]["familyId"] || Owner(master) != Owner(before["sku"].AsBsonDocument)) throw Invalid();
            }
        }
        else if (operation == "catalog.family.create")
        {
            if (!before["family"].IsBsonNull || result.Version != 1 || !master["active"].AsBoolean || !Same(before["category"], after["category"])) throw Invalid();
        }
        else if (operation == "catalog.category.save" && before["category"].IsBsonNull)
        { if (result.Version != 1 || !master["active"].AsBoolean) throw Invalid(); }
        else
        {
            if (!before[masterName].IsBsonDocument) throw Invalid(); var original = before[masterName].AsBsonDocument;
            if (original["_id"] != master["_id"] || original["revision"].AsInt64 == long.MaxValue || original["revision"].AsInt64 + 1 != master["revision"].AsInt64 || Owner(original) != Owner(master)) throw Invalid();
            var immutable = result.Kind switch
            {
                "category" => new[] { "organizationId", "code", "active" },
                "family" => ["organizationId", "publicId", "publicUuid", "imageAssetIds", "active"],
                _ => ["organizationId", "familyId", "publicId", "publicUuid", "sku", "physicalItemKey", "predecessorSkuId"]
            };
            if (immutable.Any(field => original[field] != master[field])) throw Invalid();
            if (operation == "catalog.family.edit" && !after["category"]["active"].AsBoolean) throw Invalid();
            if (result.Kind == "sku")
            {
                if (!Same(before["family"], after["family"]) || !Same(before["category"], after["category"])) throw Invalid();
                if (operation == "catalog.sku.revise")
                {
                    var oldDefinition = before["definition"].AsBsonDocument; var nextDefinition = after["definition"].AsBsonDocument;
                    if (oldDefinition["_id"] == nextDefinition["_id"] || oldDefinition["version"].AsInt64 == long.MaxValue ||
                        oldDefinition["version"].AsInt64 + 1 != nextDefinition["version"].AsInt64 || original["active"] != master["active"] ||
                        original["catalogListings"] != master["catalogListings"] || nextDefinition["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch < oldDefinition["effectiveAt"].AsBsonDateTime.MillisecondsSinceEpoch ||
                        new[] { "skuId", "packageKind", "baseUnitCode", "baseUnitsPerPackage", "attributes" }.Any(field => oldDefinition[field] != nextDefinition[field])) throw Invalid();
                }
                else
                {
                    if (!Same(before["definition"], after["definition"]) || !original["active"].AsBoolean ||
                        (operation == "catalog.sku.publish" && !master["active"].AsBoolean) ||
                        (operation == "catalog.sku.retire" && (master["active"].AsBoolean || master["catalogListings"].AsBsonArray.Count != 0))) throw Invalid();
                    PublicationTransition(before, after);
                }
            }
        }
        var required = new HashSet<GrantScope> { Owner(master) };
        if (operation is "catalog.sku.publish" or "catalog.sku.retire")
            foreach (var snapshot in new[] { before, after })
                foreach (var listing in snapshot["sku"]["catalogListings"].AsBsonArray) required.Add(CatalogRepository.Owner(listing["ownerScope"].AsBsonDocument));
        else if (before["assortments"].AsBsonArray.Count != 0 || after["assortments"].AsBsonArray.Count != 0) throw Invalid();
        if (audit["owners"].AsBsonArray.Count is 0 or > 256 || audit["owners"] != CatalogAuthoringService.OwnerArray(required)) throw Invalid();
        return required.ToArray();
    }
    private static void Snapshot(BsonDocument value)
    {
        if (!CatalogRepository.Fields(value, "category", "family", "sku", "definition", "assortments")) throw Invalid();
        foreach (var (field, collection) in new[] { ("category", "catalogCategories"), ("family", "productFamilies"), ("sku", "skus"), ("definition", "variantDefinitions") })
            if (!value[field].IsBsonNull && (!value[field].IsBsonDocument || !CatalogSchemas.IsValid(collection, value[field].AsBsonDocument))) throw Invalid();
        if (value["family"].IsBsonDocument)
        {
            if (!value["category"].IsBsonDocument || value["family"]["categoryId"] != value["category"]["_id"] || value["family"]["categoryCode"] != value["category"]["code"] || Owner(value["family"].AsBsonDocument) != Owner(value["category"].AsBsonDocument)) throw Invalid();
        }
        if (value["sku"].IsBsonDocument)
        {
            if (!value["family"].IsBsonDocument || value["sku"]["familyId"] != value["family"]["_id"] || Owner(value["sku"].AsBsonDocument) != Owner(value["family"].AsBsonDocument) || !value["definition"].IsBsonDocument) throw Invalid();
            try { CatalogAuthoringService.RequirePair(value["sku"].AsBsonDocument, value["definition"].AsBsonDocument); }
            catch (AuthorizationDeniedException) { throw Invalid(); }
        }
        else if (!value["definition"].IsBsonNull) throw Invalid();
        if (!value["assortments"].IsBsonArray || value["assortments"].AsBsonArray.Count > 256) throw Invalid();
        long previous = 0;
        foreach (var item in value["assortments"].AsBsonArray)
        {
            if (!item.IsBsonDocument) throw Invalid();
            try { CatalogRepository.Assortment(item.AsBsonDocument); } catch (AuthorizationDeniedException) { throw Invalid(); }
            if (item["_id"].AsInt64 <= previous) throw Invalid(); previous = item["_id"].AsInt64;
        }
    }
    private static void PublicationTransition(BsonDocument before, BsonDocument after)
    {
        var oldIds = before["sku"]["catalogListings"].AsBsonArray.Select(item => item["assortmentId"].AsInt64).ToHashSet();
        var newIds = after["sku"]["catalogListings"].AsBsonArray.Select(item => item["assortmentId"].AsInt64).ToHashSet();
        var union = oldIds.Union(newIds).Order().ToArray();
        if (!before["assortments"].AsBsonArray.Select(item => item["_id"].AsInt64).SequenceEqual(union) || !after["assortments"].AsBsonArray.Select(item => item["_id"].AsInt64).SequenceEqual(union)) throw Invalid();
        foreach (var id in union)
        {
            var old = before["assortments"].AsBsonArray.Single(item => item["_id"].AsInt64 == id).AsBsonDocument;
            var next = after["assortments"].AsBsonArray.Single(item => item["_id"].AsInt64 == id).AsBsonDocument;
            var changed = oldIds.Contains(id) != newIds.Contains(id);
            if (old["ownerScope"] != next["ownerScope"] || old["status"] != next["status"] || changed && old["version"].AsInt64 == long.MaxValue ||
                next["version"].AsInt64 != old["version"].AsInt64 + (changed ? 1 : 0)) throw Invalid();
            foreach (var snapshot in new[] { before, after })
                foreach (var item in snapshot["sku"]["catalogListings"].AsBsonArray.Where(item => item["assortmentId"].AsInt64 == id))
                    if (CatalogRepository.Owner(item["ownerScope"].AsBsonDocument) != CatalogRepository.Owner(old["ownerScope"].AsBsonDocument)) throw Invalid();
        }
    }
    private static bool Same(BsonValue a, BsonValue b)
    {
        if (!a.IsBsonDocument || !b.IsBsonDocument) return a == b;
        var left = a.DeepClone().AsBsonDocument; var right = b.DeepClone().AsBsonDocument; left.Remove("fence"); right.Remove("fence"); return left == right;
    }
    private static GrantScope Owner(BsonDocument row) => CatalogRepository.Owner(row["authoringOwner"].AsBsonDocument);
    private static bool Schema(BsonDocument row) => row.TryGetValue("schemaVersion", out var value) && value.IsInt32 && value.AsInt32 == 1;
    private static bool Text(BsonValue value, int maximum) => value.IsString && CatalogSchemas.CanonicalText(value.AsString, maximum);
    internal static AuthorizationDeniedException Invalid() => new("catalog_receipt_invalid");
}
