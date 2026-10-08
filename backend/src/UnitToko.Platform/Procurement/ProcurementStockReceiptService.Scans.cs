using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using I = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementStockReceiptService
{
    private async Task<(CatalogOperationalScanCapture? Capture, IReadOnlyList<OperationalScanEvidence> Evidence)> CaptureReceiptScanAsync(
        Context context, ProcurementStockReceiptInput input, IReadOnlyList<OperationalScanSelection>? selections, CancellationToken ct)
    {
        if (selections is null || selections.Count == 0) return (null, []);
        if (selections.Count != 1 || context.Order is null || context.Inspection is null) throw ScanRules.Invalid();
        var physical = context.Inspection["input"]["lines"][input.InspectionLineNumber - 1];
        var product = PurchaseOrderService.FundingOrderView(context.Order.Source)["lines"].AsBsonArray.Single(row => row["skuId"] == physical["requestSkuId"]);
        var selection = selections[0];
        var candidate = new ScanCandidate(input.InspectionId + ":" + input.InspectionLineNumber, new(product["skuId"].AsInt64),
            new(product["definitionId"].AsInt64), product["definitionVersion"].AsInt64, product["skuCode"].AsString, product["packageLabel"].AsString,
            product["packageKind"].AsString, product["baseUnitCode"].AsString, product["baseUnitsPerPackage"].AsInt32);
        if (selection.Source is { } source)
        {
            var expected = new ApprovedSourceScanSelection(new(input.OrderId), input.OrderVersion, new(input.InspectionId), 1,
                input.InspectionLineNumber, candidate.SkuId, candidate.DefinitionId, candidate.DefinitionVersion, product["sourceSystem"].AsString,
                product["sourceAccountReference"].AsString, product["externalItemCode"].AsString);
            if (selection.Identifier is not null || source != expected) throw CatalogOperationalIdentifierProof.Changed();
            return (null, [new(selection, null, null, null, new BsonDocument {
                { "orderSource", context.Order.Source.DeepClone() }, { "inspection", context.Inspection.DeepClone() } })]);
        }
        var capture = await CatalogOperationalScanCapture.ReadAsync(database, context.Tx, Resource(input.Location), [candidate], selections, ct);
        return (capture, capture.Evidence);
    }
    internal async Task<ImmutableArray<ScanSourceCandidate>> ScanChoicesAsync(ActorContext actor, ScanRequest request, CancellationToken ct)
    {
        ScanRules.Validate(request); if (request.Purpose != "receiving") throw ScanRules.Invalid(); await StorageAsync(ct);
        await CatalogIdentifierRecords.RequireStorageAsync(database, ct);
        var location = request.Location!;
        var key = new ProcurementLocation(location.OrganizationId!.Value, location.ShopId!.Value, location.WarehouseId!.Value, location.ChannelId!.Value);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SelectedOrderAsync(actor, authority, tx, key, request.DocumentId!.Value, token);
            if (context.Order!.Source["order"]["version"].AsInt64 != request.DocumentRevision || request.AuxiliaryRevision != 1) throw Changed();
            var history = await records.InspectionAsync(tx, request.AuxiliaryId!.Value, token);
            ProcurementStockReceiptRecords.Equal(history.Source["notice"]["source"], context.Order.Source);
            var inspection = history.Submissions.SingleOrDefault(row => row["_id"] == request.AuxiliaryId.Value) ?? throw Changed();
            AddFence(context, ProcurementReceivingBson.Heads, history.CustodyHead); AddFence(context, I.Heads, history.Head ?? throw Changed());
            foreach (var draft in history.Drafts) AddFence(context, I.Drafts, draft);
            var approved = PurchaseOrderService.FundingOrderView(context.Order.Source)["lines"].AsBsonArray;
            var result = ImmutableArray.CreateBuilder<ScanSourceCandidate>();
            for (var index = 0; index < inspection["input"]["lines"].AsBsonArray.Count; index++) {
                var physical = inspection["input"]["lines"][index];
                if (physical["receivedPackages"].AsInt32 <= 0) continue;
                var product = approved.SingleOrDefault(row => row["skuId"] == physical["requestSkuId"]) ?? throw Changed();
                var sourceSelection = new ApprovedSourceScanSelection(new(request.DocumentId.Value), request.DocumentRevision!.Value,
                    new(request.AuxiliaryId.Value), request.AuxiliaryRevision!.Value, index + 1, new(product["skuId"].AsInt64),
                    new(product["definitionId"].AsInt64), product["definitionVersion"].AsInt64, product["sourceSystem"].AsString,
                    product["sourceAccountReference"].AsString, product["externalItemCode"].AsString);
                result.Add(new(new(request.AuxiliaryId.Value + ":" + (index + 1), new(product["skuId"].AsInt64), new(product["definitionId"].AsInt64),
                    product["definitionVersion"].AsInt64, product["skuCode"].AsString, product["packageLabel"].AsString, product["packageKind"].AsString,
                    product["baseUnitCode"].AsString, product["baseUnitsPerPackage"].AsInt32, request.CodeKind == "supplier" ? new(null, sourceSelection) : null), product["sourceSystem"].AsString,
                    product["sourceAccountReference"].AsString, product["externalItemCode"].AsString));
            }
            var resolved = await new CatalogOperationalIdentifierProof(database).ResolveAsync(tx, CatalogRepository.Resource(location), request, result.ToImmutable(), token);
            foreach (var capture in resolved.Captures) await capture.CompleteAsync(tx, token);
            await dispositions.OrphansAsync(tx, token); await records.OrphansAsync(tx, token); await FenceAsync(context, token);
            return resolved.Choices.ToImmutableArray();
        }, ct);
    }
    private static PersistenceConflictException Changed() => new("operational_scan_context_changed", "Reload the current document and inspect the package again");
}
