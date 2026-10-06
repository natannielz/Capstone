using System.Collections.Immutable;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    public Task<ImmutableArray<StockWarehouseOption>> PackConversionWarehouseOptionsAsync(ActorContext actor,
        AuthorizationReadMetadata audit, CancellationToken ct = default) {
        ValidateConversionChoiceRead(audit, "pack-conversion-rule-warehouses");
        return StockWarehouseOptionsCoreAsync(actor, Permissions.StockConvert, audit, "inventory.pack-conversion.rule.warehouses", ct);
    }
    public Task<CatalogPage<StockCountPackageOption>> PackConversionPackagesAsync(ActorContext actor, GrantScope location,
        PackConversionChoiceRole role, int offset, int limit, string? search, AuthorizationReadMetadata audit, CancellationToken ct = default) {
        if (!Enum.IsDefined(role)) throw PackConversionRulePolicy.Invalid();
        ValidateConversionChoiceRead(audit, role == PackConversionChoiceRole.Source ? "pack-conversion-rule-source" : "pack-conversion-rule-destination");
        return StockPackagesCoreAsync(actor, location, offset, limit, search, audit, Permissions.StockConvert,
            "inventory.pack-conversion.rule.packages", role == PackConversionChoiceRole.Source ? "sealed" : "loose", ct);
    }
    private static void ValidateConversionChoiceRead(AuthorizationReadMetadata audit, string purpose) {
        if (audit is null || audit.Purpose != purpose || CatalogAuthoringPolicy.Text(audit.CorrelationId, 128) != audit.CorrelationId)
            throw new DomainValidationException("invalid_stock_choice_purpose", "Use the exact rule-preparation selection purpose");
    }
}
