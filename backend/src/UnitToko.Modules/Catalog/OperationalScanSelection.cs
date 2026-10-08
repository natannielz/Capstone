using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Catalog;

/// <summary>Explicit human-selected identity evidence. Exactly one branch is present. An approved
/// document code is not a catalog identifier and therefore never receives an invented registry ID.</summary>
public sealed record OperationalScanSelection(CatalogIdentifierSelection? Identifier, ApprovedSourceScanSelection? Source);
public sealed record ApprovedSourceScanSelection(LongId OrderId, long OrderVersion, LongId InspectionId,
    long InspectionRevision, int InspectionLineNumber, LongId SkuId, LongId DefinitionId, long DefinitionVersion,
    string SourceSystem, string SourceAccount, string SourceCode);
