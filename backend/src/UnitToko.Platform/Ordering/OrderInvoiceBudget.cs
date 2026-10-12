using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    // Internal financial conservation after the caller authorized the order/receipt. The
    // accepted shipment still uses its historical terms; only the aggregate ceiling is current.
    internal async Task<BsonDocument> CurrentInvoiceBudgetAsync(IClientSessionHandle tx, ActorContext actor, long orderId, CancellationToken ct) {
        var (order, progress) = await GraphWithProgressAsync(tx, actor, orderId, ct, requireOwner: false);
        var commercial = await CurrentSubstitutionCommercialAsync(tx, order, progress.Revision, ct);
        await TouchAsync(tx, order, ct);
        return commercial.Terms.DeepClone().AsBsonDocument;
    }
    private sealed record SubstitutionInvoiceBudget(IReadOnlyList<BsonDocument> Allocated, BsonArray Invoices, BsonArray Allocations);
    private async Task<(BsonArray Invoices, BsonArray Allocations)> SubstitutionInvoiceRowsAsync(SubstitutionContext context, CancellationToken ct) {
        var orderId = context.Order["_id"].AsInt64; var byId = new BsonDocument("_id", 1);
        var invoiceRows = await Rows(InvoiceBson.Invoices).Find(context.Tx, new BsonDocument("orderIds", orderId)).Sort(byId).ToListAsync(ct);
        var allocationRows = await Rows(InvoiceBson.Allocations).Find(context.Tx, new BsonDocument("orderId", orderId)).Sort(byId).ToListAsync(ct);
        return (new BsonArray(invoiceRows), new BsonArray(allocationRows));
    }
    private async Task<SubstitutionInvoiceBudget> RequireSubstitutionInvoiceBudgetAsync(SubstitutionContext context, BsonDocument proposedTerms,
        CancellationToken ct, SubstitutionInvoiceBudget? verified = null) {
        if (verified is not null) {
            // Same transaction attempt: a substitution never writes Billing rows. Exactly unchanged invoice and
            // allocation rows keep the full owner verification already performed for this command.
            var (currentInvoices, currentAllocations) = await SubstitutionInvoiceRowsAsync(context, ct);
            if (currentInvoices != verified.Invoices || currentAllocations != verified.Allocations) throw S.Unavailable();
            InvoiceRecords.RequireBudget(verified.Allocated, proposedTerms);
            SubstitutionCurrent(context); return verified;
        }
        // Reuse actual invoice, issued document, receipt and shipment owners. This gate is
        // called by the command, never by Graph, so history replay cannot recurse through Billing.
        var preparations = new ShipmentPreparationService(database, unit, ids, clock, this);
        var shipments = new ShipmentQueryService(database, unit, clock, this, preparations);
        var documents = new ShipmentDocumentService(database, unit, ids, clock, shipments);
        var receipts = new ShipmentReceiptService(database, unit, ids, clock, shipments, documents);
        var invoices = new InvoiceRecords(database, receipts);
        await InvoiceStorage.RequireAsync(database, ct);
        var allocated = await invoices.OrderAllocationsAsync(context.Actor, context.Tx, context.Order["_id"].AsInt64, ct);
        InvoiceRecords.RequireBudget(allocated, proposedTerms);
        var (invoiceRows, allocationRows) = await SubstitutionInvoiceRowsAsync(context, ct);
        SubstitutionCurrent(context); return new(allocated, invoiceRows, allocationRows);
    }
}
