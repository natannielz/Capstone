using System.Collections.Immutable;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentDispatchService
{
    internal async Task<ImmutableArray<ScanSourceCandidate>> ScanChoicesAsync(ActorContext actor, ScanRequest request, CancellationToken ct)
    {
        ScanRules.Validate(request); if (request.Purpose != "dispatch") throw ScanRules.Invalid();
        await StorageAsync(ct); await CatalogIdentifierRecords.RequireStorageAsync(database, ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var prepared = await preparations.ReadForDispatchAsync(actor, snapshot, tx, request.DocumentId!.Value, token);
            Expected(prepared, request.AuxiliaryId!.Value, request.AuxiliaryRevision!.Value); var view = View(prepared);
            if (view.Source.Revision != request.DocumentRevision || view.Source.Review != request.SourceReview)
                throw CatalogOperationalIdentifierProof.Changed();
            var expected = view.Source.Lines.Where(line => view.Lines.Any(selected => selected.SkuId == line.SkuId && selected.LineRevision == line.LineRevision) &&
                view.Lots.Any(lot => lot.Reservation.SkuId == line.SkuId && lot.Reservation.LineRevision == line.LineRevision && lot.Reservation.RemainingPackages > 0)).Select(ScanResolver.Choice).ToArray();
            var resolved = await new CatalogOperationalIdentifierProof(database).ResolveAsync(tx, prepared.Source.Order.Resource, request, expected, token);
            foreach (var capture in resolved.Captures) await capture.CompleteAsync(tx, token);
            await orders.FenceFulfillmentAsync(actor, snapshot, tx, [prepared.Source.Order], Permissions.ShipmentDispatch, token);
            return resolved.Choices.ToImmutableArray();
        }, ct);
    }
}
