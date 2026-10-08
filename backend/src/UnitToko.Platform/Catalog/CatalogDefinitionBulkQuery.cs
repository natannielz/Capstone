using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogDefinitionQueryService
{
    /// <summary>Current entitled packages for explicit internal SKU codes; no substitution or commitment.</summary>
    public Task<ImmutableArray<CatalogPublishedVariant>> ReadExactSkuCodesAsync(ActorContext actor,
        ImmutableArray<string> codes, CancellationToken cancellationToken = default)
    {
        if (codes.IsDefaultOrEmpty || codes.Length > 100)
            throw new DomainValidationException("invalid_bulk_sku_query", "Choose up to 100 distinct internal SKU codes");
        var normalized = codes.Select(CatalogPolicy.NormalizeSkuCode).ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new DomainValidationException("invalid_bulk_sku_query", "Choose each internal SKU only once");
        return visibility.ReadCurrentAsync(actor, F.In("sku", normalized) & F.Eq("active", true), async (tx, filter, ct) => {
            var rows = await repository.Rows("skus").Find(tx, filter).Limit(101).ToListAsync(ct);
            // Missing, inactive and unauthorized codes have the same result. Never return a partial replacement.
            if (rows.Count != normalized.Length || rows.Any(row => !row.TryGetValue("sku", out var value) || !value.IsString) ||
                rows.Select(row => row["sku"].AsString).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                throw new AuthorizationDeniedException("order_package_unavailable");
            var byCode = rows.ToDictionary(row => row["sku"].AsString, StringComparer.Ordinal);
            var result = ImmutableArray.CreateBuilder<CatalogPublishedVariant>();
            foreach (var code in normalized)
            {
                if (!byCode.TryGetValue(code, out var row)) throw new AuthorizationDeniedException("order_package_unavailable");
                var item = await JoinAsync(tx, row, ct) ?? throw new AuthorizationDeniedException("order_package_unavailable");
                result.Add(item);
            }
            return result.ToImmutable();
        }, cancellationToken);
    }
}
