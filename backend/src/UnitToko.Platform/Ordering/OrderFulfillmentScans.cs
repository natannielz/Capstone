using System.Collections.Immutable;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    internal async Task<ImmutableArray<ScanSourceCandidate>> ScanChoicesAsync(ActorContext actor, ScanRequest request, CancellationToken ct)
    {
        ScanRules.Validate(request); if (request.Purpose != "picking") throw ScanRules.Invalid();
        await StorageAsync(ct); await CatalogIdentifierRecords.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var source = await ReadForShipmentPreparationAsync(actor, snapshot, tx, request.DocumentId!.Value, token);
            if (source.View.Revision != request.DocumentRevision || source.View.Review != request.SourceReview || !source.View.CanPrepare)
                throw CatalogOperationalIdentifierProof.Changed();
            var expected = source.View.Lines.Where(line => line.RemainingPackages > 0).Select(line => ScanResolver.Choice(line)).ToArray();
            var resolved = await new CatalogOperationalIdentifierProof(database).ResolveAsync(tx, source.Resource, request, expected, token);
            foreach (var capture in resolved.Captures) await capture.CompleteAsync(tx, token);
            await FenceShipmentPreparationAsync(actor, snapshot, tx, [source], token);
            return resolved.Choices.ToImmutableArray();
        }, ct);
    }
}
