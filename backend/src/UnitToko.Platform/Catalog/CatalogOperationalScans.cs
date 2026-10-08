using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    internal async Task<ImmutableArray<ScanSourceCandidate>> StocktakeScanChoicesAsync(ActorContext actor, ScanRequest request,
        string correlationId, CancellationToken cancellationToken)
    {
        ScanRules.Validate(request);
        if (request.Purpose != "stocktake" || CatalogAuthoringPolicy.Text(correlationId, 128) != correlationId) throw ScanRules.Invalid();
        var location = request.Location!; var auditId = await ids.NextAsync("auditEvents", cancellationToken);
        await CatalogIdentifierRecords.RequireStorageAsync(database, cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) => {
            var context = await BeginAsync(actor, tx, ct, Permissions.StockCount);
            _ = StockChoiceQuery(context, Permissions.StockCount, [location]);
            var shop = location with {Kind = GrantScopeKind.FulfillmentShop, WarehouseId = null};
            string? code = request.CodeKind == "sku" ? ScanRules.ExactSku(request.RawCode) : null;
            if (request.CodeKind == "gtin") { try { code = Gtin.Parse(request.RawCode).Canonical14; } catch (DomainValidationException) { } }
            var matchingIds = code is null ? [] : await repository.Rows(CatalogIdentifierRecords.Collection).Find(tx,
                F.Eq("organizationId", location.OrganizationId!.Value) & F.Eq("kind", request.CodeKind == "sku" ? "skuCode" : "gtin") &
                F.Eq("canonicalValue", code) & F.Eq("retiredAt", BsonNull.Value) &
                F.In("namespace", request.CodeKind == "sku" ? new[] { "internal" } :
                    new[] { CatalogIdentifierPolicy.Namespace("gtin", location, null), CatalogIdentifierPolicy.Namespace("gtin", shop, null) }))
                .Limit(3).ToListAsync(ct);
            if (matchingIds.Count > 2) throw CatalogIdentifierRecords.Broken();
            // The exact match predicate precedes limits; an unrelated page cannot hide the unique code.
            var filter = F.Or(CatalogRepository.OwnerFilter("authoringOwner", location), CatalogRepository.OwnerFilter("authoringOwner", shop)) &
                F.Eq("active", true) & F.In("_id", matchingIds.Select(row => row["skuId"]));
            var pipeline = StockChoicePipeline(filter); pipeline.Add(new("$limit", 101));
            var selected = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).ToListAsync(ct);
            if (selected.Count > 100) throw CatalogRepository.Invalid("scan_resolution_limit");
            var result = ImmutableArray.CreateBuilder<ScanSourceCandidate>();
            foreach (var selectedRow in selected) {
                var sku = await repository.GetAsync(tx, "skus", selectedRow["_id"].AsInt64, ct); var owner = RowOwner(sku);
                var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
                var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
                var definition = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
                if ((owner != location && owner != shop) || RowOwner(family) != owner || RowOwner(category) != owner || family["categoryCode"] != category["code"]) throw CatalogRepository.Invalid();
                RequirePair(sku, definition); RequireActive(sku); RequireActive(family); RequireActive(category);
                context.Head("skus", sku); context.Head("productFamilies", family); context.Head("catalogCategories", category); context.Owners.Add(owner);
                result.Add(new(new(sku["_id"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture), new(sku["_id"].AsInt64),
                    new(definition["_id"].AsInt64), definition["version"].AsInt64, sku["sku"].AsString, definition["packageLabel"].AsString,
                    definition["packageKind"].AsString, definition["baseUnitCode"].AsString, definition["baseUnitsPerPackage"].AsInt32)));
            }
            var resolved = await new CatalogOperationalIdentifierProof(database).ResolveAsync(tx, CatalogRepository.Resource(location), request, result.ToImmutable(), ct);
            foreach (var capture in resolved.Captures) await capture.CompleteAsync(tx, ct);
            await AuditStockChoiceAsync(context, Permissions.StockCount, [location], auditId.Value, "inventory.stock-count.scan",
                new("operational-scan", correlationId), new() {{"kind", request.CodeKind}, {"code", request.RawCode}}, resolved.Choices.Length, ct);
            return resolved.Choices.ToImmutableArray();
        }, cancellationToken);
    }
}
