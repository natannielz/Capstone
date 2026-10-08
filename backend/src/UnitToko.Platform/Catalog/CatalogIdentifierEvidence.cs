using MongoDB.Bson;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

internal static class CatalogIdentifierEvidence
{
    internal static readonly string[] Operations = ["catalog.identifier.assign", "catalog.identifier.retire", "catalog.identifier.correct-sku"];
    internal static BsonDocument Result(CatalogIdentifierResult value) => new()
    {
        { "skuId", value.SkuId.Value }, { "skuRevision", value.SkuRevision }, { "identifierId", value.IdentifierId.Value },
        { "identifierRevision", value.IdentifierRevision }, { "recordedAt", value.RecordedAt.UtcDateTime }
    };
    internal static CatalogIdentifierResult Result(BsonDocument value)
    {
        if (!CatalogRepository.Fields(value, "skuId", "skuRevision", "identifierId", "identifierRevision", "recordedAt") ||
            new[] { "skuId", "skuRevision", "identifierId", "identifierRevision" }.Any(field => !CatalogRepository.Positive(value, field)) ||
            !CatalogSchemas.ValidDate(value["recordedAt"])) throw CatalogIdentifierRecords.Broken();
        return new(new(value["skuId"].AsInt64), value["skuRevision"].AsInt64, new(value["identifierId"].AsInt64),
            value["identifierRevision"].AsInt64, CatalogRepository.Date(value["recordedAt"]));
    }
    internal static GrantScope RequireAudit(BsonDocument audit)
    {
        if (!CatalogRepository.Fields(audit, "_id", "schemaVersion", "kind", "actorId", "actorGrantVersion", "operation", "bodyHash", "reason", "correlationId",
            "createdAt", "owner", "skuBefore", "skuAfter", "definition", "identifiersBefore", "identifiersAfter", "result") ||
            !Schema(audit) || new[] { "_id", "actorId", "actorGrantVersion" }.Any(field => !CatalogRepository.Positive(audit, field)) ||
            audit["kind"] != "catalog.identifier.command" || !audit["operation"].IsString || !Operations.Contains(audit["operation"].AsString) ||
            !CatalogReceiptRules.Hash(audit["bodyHash"]) || !Text(audit["reason"], 1000) || !Text(audit["correlationId"], 128) ||
            !CatalogSchemas.ValidDate(audit["createdAt"]) || !audit["owner"].IsBsonDocument ||
            new[] { "skuBefore", "skuAfter", "definition", "result" }.Any(field => !audit[field].IsBsonDocument) ||
            new[] { "identifiersBefore", "identifiersAfter" }.Any(field => !audit[field].IsBsonArray || audit[field].AsBsonArray.Count > 2)) throw CatalogIdentifierRecords.Broken();
        var before = CatalogRepository.Require("skus", audit["skuBefore"].AsBsonDocument);
        var after = CatalogRepository.Require("skus", audit["skuAfter"].AsBsonDocument);
        var definition = CatalogRepository.Require("variantDefinitions", audit["definition"].AsBsonDocument);
        CatalogAuthoringService.RequirePair(before, definition); CatalogAuthoringService.RequirePair(after, definition);
        var owner = CatalogRepository.Owner(audit["owner"].AsBsonDocument);
        if (owner != CatalogRepository.Owner(before["authoringOwner"].AsBsonDocument) ||
            before["revision"].AsInt64 == long.MaxValue || after["revision"].AsInt64 != before["revision"].AsInt64 + 1 ||
            !before["active"].AsBoolean || !after["active"].AsBoolean) throw CatalogIdentifierRecords.Broken();
        var operation = audit["operation"].AsString;
        string[] mutable = ["revision", "fence"];
        if (before.Where(field => !mutable.Contains(field.Name)).Any(field => !CatalogSchemas.SemanticEquals(field.Value, after[field.Name]))) throw CatalogIdentifierRecords.Broken();
        var oldIds = Parse(audit["identifiersBefore"].AsBsonArray); var newIds = Parse(audit["identifiersAfter"].AsBsonArray);
        if (oldIds.Concat(newIds).Any(item => item.SkuId.Value != before["_id"].AsInt64 || item.OrganizationId.Value != before["organizationId"].AsInt64) ||
            audit["identifiersAfter"].AsBsonArray.Any(row => row["auditId"] != audit["_id"])) throw CatalogIdentifierRecords.Broken();
        var at = CatalogRepository.Date(audit["createdAt"]); var result = Result(audit["result"].AsBsonDocument);
        if (result.SkuId.Value != after["_id"].AsInt64 || result.SkuRevision != after["revision"].AsInt64 || result.RecordedAt != at ||
            newIds.Count(item => item.Id == result.IdentifierId && item.Revision == result.IdentifierRevision) != 1) throw CatalogIdentifierRecords.Broken();
        if (operation == "catalog.identifier.assign")
        {
            if (oldIds.Length != 0 || newIds.Length != 1 || newIds[0].Revision != 1 || newIds[0].IsPrimary || newIds[0].RetiredAt is not null || newIds[0].EffectiveAt != at ||
                newIds[0].DefinitionId?.Value != definition["_id"].AsInt64)
                throw CatalogIdentifierRecords.Broken();
        }
        else if (operation == "catalog.identifier.retire")
        {
            if (oldIds.Length != 1 || newIds.Length != 1 || oldIds[0].IsPrimary || oldIds[0].RetiredAt is not null ||
                newIds[0].RetiredAt != at || !Transition(oldIds[0], newIds[0], false)) throw CatalogIdentifierRecords.Broken();
        }
        else
        {
            if (oldIds.Length != 1 || newIds.Length != 2 || !oldIds[0].IsPrimary || oldIds[0].RetiredAt is not null ||
                newIds.Count(item => item.IsPrimary) != 1) throw CatalogIdentifierRecords.Broken();
            var alias = newIds.SingleOrDefault(item => item.Id == oldIds[0].Id); var primary = newIds.SingleOrDefault(item => item.IsPrimary);
            if (alias is null || primary is null || alias.IsPrimary || alias.RetiredAt is not null || !Transition(oldIds[0], alias, true) ||
                primary.Id == alias.Id || primary.Revision != 1 || primary.Kind != "skuCode" || primary.EffectiveAt != at ||
                primary.RetiredAt is not null || primary.CanonicalValue == oldIds[0].CanonicalValue || result.IdentifierId != primary.Id ||
                primary.DefinitionId?.Value != definition["_id"].AsInt64 || alias.DefinitionId?.Value != (oldIds[0].DefinitionId?.Value ?? definition["_id"].AsInt64)) throw CatalogIdentifierRecords.Broken();
        }
        return owner;
    }
    internal static bool Transition(CatalogIdentifier before, CatalogIdentifier after, bool alias)
    {
        return before.Revision < long.MaxValue && after.Revision == before.Revision + 1 && before.Id == after.Id &&
            before.OrganizationId == after.OrganizationId && before.SkuId == after.SkuId && before.Kind == after.Kind &&
            before.Namespace == after.Namespace && before.RawValue == after.RawValue && before.CanonicalValue == after.CanonicalValue &&
            before.EffectiveAt == after.EffectiveAt && (before.DefinitionId is null || before.DefinitionId == after.DefinitionId) &&
            (alias ? before.IsPrimary && !after.IsPrimary : before.IsPrimary == after.IsPrimary);
    }
    internal static void RequireReceipt(BsonDocument receipt, BsonDocument audit)
    {
        _ = RequireAudit(audit);
        if (!CatalogRepository.Fields(receipt, "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result", "catalogIdentifier") ||
            !Schema(receipt) || !receipt["_id"].IsObjectId || !receipt["key"].IsString || !CatalogReceiptRules.Key(receipt["key"].AsString) ||
            !receipt["catalogIdentifier"].IsBsonDocument || !CatalogRepository.Fields(receipt["catalogIdentifier"].AsBsonDocument, "schemaVersion", "auditId") ||
            !Schema(receipt["catalogIdentifier"].AsBsonDocument) || receipt["catalogIdentifier"]["auditId"] != audit["_id"] ||
            new[] { "actorId", "operation", "bodyHash", "createdAt", "result" }.Any(field => !CatalogSchemas.SemanticEquals(receipt[field], audit[field]))) throw CatalogIdentifierRecords.Broken();
    }
    private static CatalogIdentifier[] Parse(BsonArray rows)
    {
        if (rows.Any(row => !row.IsBsonDocument)) throw CatalogIdentifierRecords.Broken();
        var result = rows.Select(row => CatalogIdentifierRecords.Read(row.AsBsonDocument)).ToArray();
        if (result.Select(item => item.Id).Distinct().Count() != result.Length) throw CatalogIdentifierRecords.Broken();
        return result;
    }
    private static bool Schema(BsonDocument row) => row.TryGetValue("schemaVersion", out var value) && value.IsInt32 && value == 1;
    private static bool Text(BsonValue value, int maximum) => value.IsString && CatalogSchemas.CanonicalText(value.AsString, maximum);
}
