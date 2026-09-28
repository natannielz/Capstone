using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

public enum StockCountChoicePurpose { Count, History }
public sealed record StockWarehouseOption(GrantScope Location, string OrganizationName, string ShopName,
    string WarehouseName, string ChannelName);
public sealed record StockCountPackageOption(LongId SkuId, long SkuRevision, LongId DefinitionId, long DefinitionVersion,
    string SkuCode, string FamilyName, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage);
