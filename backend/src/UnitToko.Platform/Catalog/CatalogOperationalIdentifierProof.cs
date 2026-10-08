using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

/// <summary>The operational owner supplies its already-authorized exact document/package facts.
/// This reader grants no permission and must be followed by that owner's final authority fence.</summary>
internal sealed class CatalogOperationalIdentifierProof(IMongoDatabase database)
{
    private readonly CatalogRepository repository = new(database);
    internal sealed class Capture(CatalogRepository repository, CatalogIdentifierGraph.Capture graph,
        BsonDocument sku, BsonDocument family, BsonDocument category, BsonDocument definition, BsonDocument currentDefinition,
        IReadOnlyList<CatalogIdentifier> identifiers)
    {
        internal IReadOnlyList<CatalogIdentifier> Identifiers { get; } = identifiers;
        internal CatalogIdentifierSelection Selection(CatalogIdentifier identifier) => new(identifier.Id, identifier.Revision,
            identifier.SkuId, new(definition["_id"].AsInt64), definition["version"].AsInt64, identifier.Kind, identifier.Namespace, identifier.CanonicalValue);
        internal OperationalScanEvidence Evidence(CatalogIdentifierSelection supplied)
        {
            var exact = Identifiers.SingleOrDefault(item => item.Id == supplied.IdentifierId && item.RetiredAt is null);
            if (exact is null || Selection(exact) != supplied) throw Changed();
            var assignment = graph.Identifiers.Single(row => row["_id"] == exact.Id.Value).DeepClone().AsBsonDocument;
            return new(new(supplied, null), assignment, sku.DeepClone().AsBsonDocument, definition.DeepClone().AsBsonDocument, null);
        }
        internal async Task CompleteAsync(IClientSessionHandle tx, CancellationToken ct)
        {
            await graph.CompleteAsync(tx, ct);
            await CatalogIdentifierRecords.FenceAsync(repository.Rows("variantDefinitions"), tx, definition, ct);
            if (currentDefinition["_id"] != definition["_id"])
                await CatalogIdentifierRecords.FenceAsync(repository.Rows("variantDefinitions"), tx, currentDefinition, ct);
            // The SKU fence also protects membership: a concurrently assigned or retired mapping
            // necessarily revises this exact SKU. These writes occur in the caller transaction.
            await repository.TouchAsync(tx, "skus", sku, ct);
            await repository.TouchAsync(tx, "productFamilies", family, ct);
            await repository.TouchAsync(tx, "catalogCategories", category, ct);
        }
    }
    internal async Task<Capture> ReadAsync(IClientSessionHandle tx, ResourceScope resource, ScanCandidate expected, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw Changed();
        var sku = await repository.GetAsync(tx, "skus", expected.SkuId.Value, ct);
        var owner = CatalogRepository.Owner(sku["authoringOwner"].AsBsonDocument);
        if (owner.OrganizationId != resource.FulfillmentOrganizationId || owner.ShopId != resource.ShopId || owner.ChannelId != resource.ChannelId ||
            owner.WarehouseId is { } warehouse && resource.WarehouseId != warehouse) throw Changed();
        var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
        var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
        var current = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
        var definition = await repository.GetAsync(tx, "variantDefinitions", expected.DefinitionId.Value, ct);
        CatalogAuthoringService.RequirePair(sku, current);
        if (!sku["active"].AsBoolean || !family["active"].AsBoolean || !category["active"].AsBoolean ||
            CatalogRepository.Owner(family["authoringOwner"].AsBsonDocument) != owner || CatalogRepository.Owner(category["authoringOwner"].AsBsonDocument) != owner ||
            family["categoryCode"] != category["code"] || definition["skuId"] != sku["_id"] || definition["version"] != expected.DefinitionVersion ||
            definition["packageLabel"] != expected.PackageLabel || definition["packageKind"] != expected.PackageKind ||
            definition["baseUnitCode"] != expected.BaseUnitCode || definition["baseUnitsPerPackage"] != expected.BaseUnitsPerPackage ||
            sku["sku"] != expected.SkuCode ||
            new[] { "packageKind", "baseUnitCode", "baseUnitsPerPackage", "attributes" }.Any(field => !CatalogSchemas.SemanticEquals(definition[field], current[field])))
            throw Changed();
        var graph = await new CatalogIdentifierGraph(database).ReadSkuAsync(tx, sku, current, ct);
        return new(repository, graph, sku, family, category, definition, current, graph.Identifiers.Select(CatalogIdentifierRecords.Read).ToArray());
    }
    internal async Task<(ScanSourceCandidate[] Choices, Capture[] Captures)> ResolveAsync(IClientSessionHandle tx, ResourceScope resource,
        ScanRequest request, IReadOnlyList<ScanSourceCandidate> expected, CancellationToken ct)
    {
        if (expected.Count > 100) throw ScanRules.Invalid();
        var result = new List<ScanSourceCandidate>(); var captures = new Dictionary<(long Sku, long Definition), Capture>();
        var code = request.CodeKind == "sku" ? ScanRules.ExactSku(request.RawCode) : request.CodeKind == "gtin" ? CanonicalGtin(request.RawCode) : null;
        if (request.CodeKind == "supplier") return (expected.ToArray(), []);
        if (code is null) return ([], []);
        foreach (var source in expected)
        {
            var identity = (source.Candidate.SkuId.Value, source.Candidate.DefinitionId.Value);
            if (!captures.TryGetValue(identity, out var capture))
            {
                capture = await ReadAsync(tx, resource, source.Candidate, ct); captures.Add(identity, capture);
            }
            foreach (var identifier in capture.Identifiers.Where(value => value.RetiredAt is null &&
                value.Kind == (request.CodeKind == "sku" ? "skuCode" : "gtin") && value.CanonicalValue == code))
                result.Add(source with { Candidate = source.Candidate with { Selection = new(capture.Selection(identifier), null),
                    CurrentPrimaryCode = capture.Identifiers.Single(value => value.IsPrimary).CanonicalValue } });
        }
        return (result.ToArray(), captures.Values.ToArray());
    }
    private static string? CanonicalGtin(string raw) { try { return Gtin.Parse(raw).Canonical14; } catch (DomainValidationException) { return null; } }
    internal static PersistenceConflictException Changed() => new("operational_scan_mapping_changed", "Reload the exact document, scan again and confirm the current package");
}
