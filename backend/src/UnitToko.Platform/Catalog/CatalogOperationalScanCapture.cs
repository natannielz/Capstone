using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Catalog;

internal sealed class CatalogOperationalScanCapture
{
    private readonly IReadOnlyList<CatalogOperationalIdentifierProof.Capture> captures;
    internal IReadOnlyList<OperationalScanEvidence> Evidence { get; }
    private CatalogOperationalScanCapture(IReadOnlyList<CatalogOperationalIdentifierProof.Capture> captures, IReadOnlyList<OperationalScanEvidence> evidence)
    { this.captures = captures; Evidence = evidence; }
    internal static async Task<CatalogOperationalScanCapture> ReadAsync(IMongoDatabase database, IClientSessionHandle tx, ResourceScope resource,
        IReadOnlyList<ScanCandidate> exactOwnerPackages, IReadOnlyList<OperationalScanSelection>? selected, CancellationToken ct)
    {
        if (selected is null || selected.Count == 0) return new([], []);
        var captures = new List<CatalogOperationalIdentifierProof.Capture>(); var evidence = new List<OperationalScanEvidence>();
        foreach (var value in selected)
        {
            if (value.Identifier is not { } identifier || value.Source is not null) throw ScanRules.Invalid();
            var expected = exactOwnerPackages.SingleOrDefault(item => item.SkuId == identifier.SkuId && item.DefinitionId == identifier.DefinitionId &&
                item.DefinitionVersion == identifier.DefinitionVersion) ?? throw CatalogOperationalIdentifierProof.Changed();
            var capture = await new CatalogOperationalIdentifierProof(database).ReadAsync(tx, resource, expected, ct);
            captures.Add(capture); evidence.Add(capture.Evidence(identifier));
        }
        return new(captures, evidence);
    }
    internal async Task CompleteAsync(IClientSessionHandle tx, CancellationToken ct)
    { foreach (var capture in captures) await capture.CompleteAsync(tx, ct); }
}
