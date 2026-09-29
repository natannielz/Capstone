using System.Collections.Immutable;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Billing;

internal sealed record InvoiceApprovedLine(long ReceiptId, long SkuId, Money Tax, Money Charges);
internal sealed record InvoiceTerms(OrderPolicyReference Policy, OrderPolicyReference TaxPolicy, OrderPolicyReference TermsPolicy,
    OrderPolicyReference Calendar, OrderPolicyReference Numbering, OrderPolicyReference Template,
    string Grouping, DateOnly PeriodFrom, DateOnly PeriodTo, string TermOrigin, int TermDays, string DayRule,
    ImmutableArray<DayOfWeek> WorkingDays, ImmutableArray<DateOnly> Holidays, string Rounding,
    string ReceivableMapping, string RevenueMapping, string TaxMapping, string ChargesMapping,
    string TaxDocumentProcess, DateTimeOffset ValidUntil, ImmutableArray<InvoiceApprovedLine> Lines);

/// <summary>Owner integration, never HTTP input. The producer must load and conditionally WRITE
/// actual independently approved invoice/tax/terms/calendar/template/numbering rules, original
/// order commercial allocation decisions and invoice eligibility/holds in the caller's transaction.
/// Tax and charges are approved source allocations, not rates or amounts supplied by the browser.
/// Missing approval or producer is unavailable. Local fixtures never register a production policy.</summary>
internal interface IInvoiceAdmissionReader
{
    IMongoDatabase Database { get; }
    Task<InvoiceTerms> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, ImmutableArray<InvoiceReceiptSource> sources, DateOnly issueDate, CancellationToken ct);
    Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, InvoiceTerms approved, CancellationToken ct);
}

internal static class InvoiceAdmissionPolicy
{
    internal static void Require(ImmutableArray<InvoiceReceiptSource> sources, InvoiceTerms value, DateTimeOffset now) {
        if (value is null || sources.IsDefaultOrEmpty || sources.Length > InvoicePolicy.MaximumReceipts || value.Lines.IsDefaultOrEmpty || value.Lines.Length > InvoicePolicy.MaximumLines ||
            value.Grouping is not ("buying_org_period_channel" or "buying_branch_period_channel" or "receipt") ||
            value.TermOrigin is not ("issue_date" or "earliest_receipt" or "latest_receipt") || value.TermDays is < 0 or > 365 ||
            value.DayRule is not ("calendar_days" or "working_days") || value.WorkingDays.IsDefault || value.WorkingDays.Any(day => !Enum.IsDefined(day)) ||
            value.WorkingDays.Distinct().Count() != value.WorkingDays.Length || value.DayRule == "working_days" && value.WorkingDays.Length == 0 ||
            value.Holidays.IsDefault || value.Holidays.Length > 730 || value.Holidays.Any(date => date == default) || value.Holidays.Distinct().Count() != value.Holidays.Length ||
            value.Rounding is not ("half_even" or "half_up") || value.PeriodFrom == default || value.PeriodTo < value.PeriodFrom ||
            value.PeriodTo.DayNumber - value.PeriodFrom.DayNumber > 366) throw InvoicePolicy.Invalid();
        foreach (var reference in new[] { value.Policy, value.TaxPolicy, value.TermsPolicy, value.Calendar, value.Numbering, value.Template }) OrderAdmissionPolicy.Reference(reference);
        foreach (var mapping in new[] { value.ReceivableMapping, value.RevenueMapping, value.TaxMapping, value.ChargesMapping }) AccountingValidation.Code(mapping);
        if (new[] { value.ReceivableMapping, value.RevenueMapping, value.TaxMapping, value.ChargesMapping }.Distinct().Count() != 4) throw InvoicePolicy.Invalid();
        InventoryLedger.RequireText(value.TaxDocumentProcess, 1000);
        Current(value, now);
        var first = sources[0]; var scope = first.Resource;
        if (value.Grouping == "receipt" && sources.Length != 1) throw InvoicePolicy.Invalid("invoice_grouping_changed");
        var keys = sources.SelectMany(source => source.Snapshot["receipt"]["lines"].AsBsonArray.Select(line => (source.Snapshot["receipt"]["id"].AsInt64, line["skuId"].AsInt64))).ToArray();
        if (keys.Length != value.Lines.Length || value.Lines.Any(line => line is null) || value.Lines.Select(line => (line.ReceiptId, line.SkuId)).Distinct().Count() != keys.Length ||
            value.Lines.Any(line => !keys.Contains((line.ReceiptId, line.SkuId)))) throw InvoicePolicy.Invalid();
        foreach (var line in value.Lines) { _ = new Money(line.Tax.Value); _ = new Money(line.Charges.Value); }
        foreach (var source in sources) {
            var date = AccountingCalendar.BusinessDate(ShipmentReceiptRecords.Time(source.Snapshot["receipt"]["recordedAt"]));
            if (date < value.PeriodFrom || date > value.PeriodTo || date > AccountingCalendar.BusinessDate(now) ||
                source.Resource.BuyingOrganizationId != scope.BuyingOrganizationId || source.Resource.FulfillmentOrganizationId != scope.FulfillmentOrganizationId ||
                source.Resource.ShopId != scope.ShopId || source.Resource.ChannelId != scope.ChannelId ||
                value.Grouping == "buying_branch_period_channel" && source.Resource.BranchId != scope.BranchId ||
                !CatalogSchemas.SemanticEquals(source.Snapshot["shipment"]["seller"], first.Snapshot["shipment"]["seller"]) ||
                !CatalogSchemas.SemanticEquals(source.Snapshot["commercial"]["terms"]["buyer"], first.Snapshot["commercial"]["terms"]["buyer"])) throw InvoicePolicy.Invalid("invoice_grouping_changed");
        }
        _ = DueDate(sources, value, AccountingCalendar.BusinessDate(now));
    }
    internal static void Current(InvoiceTerms value, DateTimeOffset now) {
        if (value.ValidUntil <= now) throw InvoicePolicy.Invalid("invoice_policy_expired");
    }
    internal static DateOnly DueDate(ImmutableArray<InvoiceReceiptSource> sources, InvoiceTerms value, DateOnly issueDate) {
        var dates = sources.Select(source => AccountingCalendar.BusinessDate(ShipmentReceiptRecords.Time(source.Snapshot["receipt"]["recordedAt"])));
        var origin = value.TermOrigin == "issue_date" ? issueDate : value.TermOrigin == "earliest_receipt" ? dates.Min() : dates.Max();
        try {
            if (value.DayRule == "calendar_days") return origin.AddDays(value.TermDays);
            var result = origin; var remaining = value.TermDays;
            for (var examined = 0; remaining > 0 && examined < 3650; examined++) {
                result = result.AddDays(1); if (value.WorkingDays.Contains(result.DayOfWeek) && !value.Holidays.Contains(result)) remaining--;
            }
            if (remaining > 0) throw InvoicePolicy.Invalid(); return result;
        } catch (ArgumentOutOfRangeException) { throw InvoicePolicy.Invalid(); }
    }
}
