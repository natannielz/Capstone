using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Billing;

internal static class InvoiceBson
{
    internal const string Invoices = "invoices", Allocations = "invoiceSourceAllocations", Operation = "billing.invoice.issue";
    internal static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static DateOnly Date(BsonValue value) => DateOnly.ParseExact(value.AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static BsonDecimal128 Money(Money value) => new(value.Value);
    internal static Money Money(BsonValue value) => new(Decimal128.ToDecimal(value.AsDecimal128));
    internal static BsonDocument Terms(InvoiceTerms value) => new() {
        { "policy", OrderSubmissionBson.Reference(value.Policy) }, { "taxPolicy", OrderSubmissionBson.Reference(value.TaxPolicy) }, { "termsPolicy", OrderSubmissionBson.Reference(value.TermsPolicy) },
        { "calendar", OrderSubmissionBson.Reference(value.Calendar) }, { "numbering", OrderSubmissionBson.Reference(value.Numbering) }, { "template", OrderSubmissionBson.Reference(value.Template) },
        { "grouping", value.Grouping }, { "periodFrom", Date(value.PeriodFrom) }, { "periodTo", Date(value.PeriodTo) }, { "termOrigin", value.TermOrigin }, { "termDays", value.TermDays },
        { "dayRule", value.DayRule }, { "workingDays", new BsonArray(value.WorkingDays.Select(day => new BsonInt32((int)day))) }, { "holidays", new BsonArray(value.Holidays.Order().Select(day => new BsonString(Date(day)))) },
        { "rounding", value.Rounding }, { "receivableMapping", value.ReceivableMapping }, { "revenueMapping", value.RevenueMapping }, { "taxMapping", value.TaxMapping }, { "chargesMapping", value.ChargesMapping },
        { "taxDocumentProcess", value.TaxDocumentProcess }, { "validUntil", value.ValidUntil.UtcDateTime },
        { "lines", new BsonArray(value.Lines.OrderBy(line => line.ReceiptId).ThenBy(line => line.SkuId).Select(line => new BsonDocument {
            { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "tax", Money(line.Tax) }, { "charges", Money(line.Charges) } })) } };
    internal static InvoiceTerms Terms(BsonDocument value) => new(OrderSubmissionBson.Reference(value["policy"]), OrderSubmissionBson.Reference(value["taxPolicy"]), OrderSubmissionBson.Reference(value["termsPolicy"]),
        OrderSubmissionBson.Reference(value["calendar"]), OrderSubmissionBson.Reference(value["numbering"]), OrderSubmissionBson.Reference(value["template"]),
        value["grouping"].AsString, Date(value["periodFrom"]), Date(value["periodTo"]), value["termOrigin"].AsString, value["termDays"].AsInt32, value["dayRule"].AsString,
        [.. value["workingDays"].AsBsonArray.Select(day => (DayOfWeek)day.AsInt32)], [.. value["holidays"].AsBsonArray.Select(Date)], value["rounding"].AsString,
        value["receivableMapping"].AsString, value["revenueMapping"].AsString, value["taxMapping"].AsString, value["chargesMapping"].AsString, value["taxDocumentProcess"].AsString,
        ShipmentReceiptRecords.Time(value["validUntil"]), [.. value["lines"].AsBsonArray.Select(line => new InvoiceApprovedLine(line["receiptId"].AsInt64, line["skuId"].AsInt64, Money(line["tax"]), Money(line["charges"])))]);
    internal static BsonArray Sources(ImmutableArray<InvoiceReceiptSource> sources) => new(sources.Select(source => new BsonDocument {
        { "scope", ShipmentPreparationBson.Scope(source.Resource) }, { "snapshot", source.Snapshot.DeepClone() } }));
    internal static ImmutableArray<InvoiceReceiptSource> Sources(BsonArray values) => [.. values.Select(value => new InvoiceReceiptSource(ShipmentPreparationBson.Scope(value["scope"].AsBsonDocument), value["snapshot"].AsBsonDocument))];
    internal static object Body(ImmutableArray<InvoiceReceiptSelection> selected, string review) => new { selected, review };
    internal static ImmutableArray<InvoiceReceiptSelection> Selected(BsonDocument row) => InvoicePolicy.Copy(row["sources"].AsBsonArray.Select(source =>
        new InvoiceReceiptSelection(new(source["snapshot"]["shipment"]["shipmentId"].AsInt64), new(source["snapshot"]["receipt"]["id"].AsInt64))));
    internal static string Review(BsonArray sources, BsonDocument terms, BsonArray lines, DateOnly issueDate, DateOnly dueDate) => CatalogRepository.Digest(new BsonDocument {
        { "sources", sources }, { "terms", terms }, { "lines", new BsonArray(lines.Select(line => new BsonDocument(line.AsBsonDocument.Where(field => field.Name != "allocationId")))) }, { "issueDate", Date(issueDate) }, { "dueDate", Date(dueDate) } });
    internal static BsonDocument Totals(BsonArray lines) {
        var net = new Money(0m); var tax = new Money(0m); var charges = new Money(0m);
        foreach (var line in lines) { net = Modules.Accounting.Money.Add(net, Money(line["net"])); tax = Modules.Accounting.Money.Add(tax, Money(line["tax"])); charges = Modules.Accounting.Money.Add(charges, Money(line["charges"])); }
        var total = Modules.Accounting.Money.Add(Modules.Accounting.Money.Add(net, tax), charges);
        if (total.Value <= 0) throw InvoicePolicy.Invalid("invoice_total_invalid");
        return new() { { "net", Money(net) }, { "tax", Money(tax) }, { "charges", Money(charges) }, { "total", Money(total) } };
    }
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not ("snapshotHash" or "postingFence"))));
    internal static BsonDocument Result(BsonDocument row) => new() { { "invoiceId", row["_id"] }, { "revision", 1L }, { "issuedAt", row["issuedAt"] }, { "numberEntity", row["numberEntity"] },
        { "numberChannel", row["numberChannel"] }, { "numberYear", row["numberYear"] }, { "number", row["number"] }, { "total", row["totals"]["total"] }, { "orderIds", row["orderIds"].DeepClone() } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["issuedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["issuedAt"] }, { "orderIds", row["orderIds"].DeepClone() }, { "result", Result(row) } };
    internal static BsonDocument Allocation(BsonDocument invoice, BsonDocument line) => new() { { "_id", line["allocationId"] }, { "schemaVersion", 1 }, { "invoiceId", invoice["_id"] },
        { "orderId", line["orderId"] }, { "shipmentId", line["shipmentId"] }, { "receiptId", line["receiptId"] }, { "skuId", line["skuId"] }, { "definitionId", line["definition"]["_id"] },
        { "acceptedBaseUnits", line["acceptedBaseUnits"] }, { "previousBaseUnits", line["previousBaseUnits"] }, { "previousNet", line["previousNet"] },
        { "net", line["net"] }, { "tax", line["tax"] }, { "charges", line["charges"] }, { "rounding", invoice["terms"]["rounding"] }, { "issuedAt", invoice["issuedAt"] } };
}
