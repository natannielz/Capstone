using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;
using C = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Platform.Billing;

internal sealed record InvoiceReleaseCapture(ImmutableArray<InvoiceReceiptSource> Receipts, BsonDocument Snapshot,
    DateTimeOffset CapturedAt, InvoiceReleaseInput Input);

/// <summary>All economic facts come from existing owners. Only explicitly reviewed tax
/// and charge allocations enter from the release request, within the actual remaining
/// approved order budget. No reservation, stock value or COGS posting is created here.</summary>
internal sealed class InvoiceReleaseSources(IMongoDatabase database, IClock clock, ShipmentReceiptService receipts)
{
    internal async Task<ImmutableArray<InvoiceReceiptSource>> ReadAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, ImmutableArray<InvoiceReceiptSelection> selected, string permission, CancellationToken ct)
    {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
        var found = ImmutableArray.CreateBuilder<InvoiceReceiptSource>();
        foreach (var item in selected) {
            var source = await receipts.InvoiceSourceAsync(actor, authority, tx, item.ShipmentId.Value, permission, ct) ?? throw B.Changed();
            if (!query.Matches(source.Resource) || source.Snapshot["receipt"]["id"] != item.ReceiptId.Value ||
                source.Snapshot["receipt"]["lines"].AsBsonArray.Count == 0) throw B.Changed();
            found.Add(source);
        }
        if (found.Sum(source => source.Snapshot["receipt"]["lines"].AsBsonArray.Count) > InvoicePolicy.MaximumLines) throw InvoiceReleaseRules.Invalid();
        return found.ToImmutable();
    }

    internal async Task<InvoiceReleaseCapture> CaptureAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        InvoiceReleaseInput input, string sourcePermission, CancellationToken ct, BsonDocument? ownInvoice = null)
    {
        input = InvoiceReleaseRules.Copy(input); var began = clock.UtcNow;
        if (began < input.ValidFrom || began >= input.ValidUntil) throw B.Changed();
        if (ownInvoice is not null) {
            if (ownInvoice["issuedBy"] != actor.UserId || !InvoiceRecords.Equal(B.Selected(InvoiceBson.Selected(ownInvoice)), B.Selected(input.Receipts))) throw B.Broken();
            var approvedHistory = await new InvoiceReleaseRecords(database).VerifyInvoiceBindingAsync(tx, ownInvoice, ct) ?? throw B.Broken();
            var decision = approvedHistory.Events.Single(row => row["revision"] == ownInvoice["terms"]["policy"]["revision"]);
            InvoiceReleaseRecords.Equal(decision["input"], B.Input(input));
        }
        var source = await ReadAsync(actor, authority, tx, input.Receipts, sourcePermission, ct);
        var commercial = await new InvoiceReleaseCommercial(database, receipts).ReadAndFenceAsync(actor, tx, source, ct);
        var rule = commercial.Common; RequireGrouping(source, input, rule, began);
        var acceptedKeys = source.SelectMany(value => value.Snapshot["receipt"]["lines"].AsBsonArray.Select(line =>
            (value.Snapshot["receipt"]["id"].AsInt64, line["skuId"].AsInt64, OrderLineBson.Revision(line)))).ToArray();
        if (acceptedKeys.Length != input.Allocations.Length || input.Allocations.Any(line => !acceptedKeys.Contains((line.ReceiptId.Value, line.SkuId.Value, line.LineRevision)))) throw B.Changed();
        var budgets = new BsonArray(); var lines = new BsonArray(); var records = new InvoiceRecords(database, receipts);
        foreach (var order in source.GroupBy(value => value.Snapshot["commercial"]["orderId"].AsInt64).OrderBy(value => value.Key)) {
            var previous = (await records.OrderAllocationsAsync(actor, tx, order.Key, ct))
                .Where(row => ownInvoice is null || row["invoiceId"] != ownInvoice["_id"]).OrderBy(row => row["_id"].AsInt64).ToArray();
            var budget = await receipts.CurrentInvoiceBudgetAsync(actor, tx, order.Key, ct);
            budgets.Add(new BsonDocument { { "orderId", order.Key }, { "terms", budget }, { "allocations", new BsonArray(previous) } });
            var accumulated = previous.Select(row => row.DeepClone().AsBsonDocument).ToList();
            foreach (var receipt in order) foreach (var accepted in receipt.Snapshot["receipt"]["lines"].AsBsonArray) {
                if (accumulated.Any(row => row["receiptId"] == receipt.Snapshot["receipt"]["id"] && row["skuId"] == accepted["skuId"]))
                    throw new Mongo.PersistenceConflictException("receipt_already_invoiced", "The selected accepted quantity already has an invoice");
                var prior = accumulated.Where(row => OrderLineBson.Same(row, accepted)).ToArray();
                if (prior.Any(row => row["rounding"] != rule.Rounding)) throw B.Changed();
                var previousUnits = prior.Aggregate(0L, (sum, row) => checked(sum + row["acceptedBaseUnits"].AsInt64));
                var previousNet = prior.Aggregate(new Money(0m), (sum, row) => Money.Add(sum, InvoiceBson.Money(row["net"])));
                var allocation = input.Allocations.Single(line => line.ReceiptId.Value == receipt.Snapshot["receipt"]["id"].AsInt64 &&
                    line.SkuId.Value == accepted["skuId"].AsInt64 && line.LineRevision == OrderLineBson.Revision(accepted));
                var quoted = receipt.Snapshot["commercial"]["quote"]["lines"].AsBsonArray.Single(line => OrderLineBson.Identity(line) == OrderLineBson.Identity(accepted));
                var definition = quoted["definition"].AsBsonDocument;
                var net = InvoicePolicy.AcceptedDelta(InvoiceBson.Money(quoted["price"]["unitPrice"]), definition["baseUnitsPerPackage"].AsInt32,
                    quoted["baseUnits"].AsInt64, previousUnits, previousNet, accepted["acceptedBaseUnits"].AsInt64, rule.Rounding);
                var line = new BsonDocument { { "orderId", order.Key }, { "shipmentId", receipt.Snapshot["shipment"]["shipmentId"] },
                    { "receiptId", receipt.Snapshot["receipt"]["id"] }, { "skuId", accepted["skuId"] },
                    { "skuCode", quoted["skuCode"] }, { "productName", quoted["familyName"] }, { "definition", definition.DeepClone() },
                    { "price", quoted["price"].DeepClone() }, { "acceptedBaseUnits", accepted["acceptedBaseUnits"] },
                    { "previousBaseUnits", previousUnits }, { "previousNet", InvoiceBson.Money(previousNet) },
                    { "net", InvoiceBson.Money(net) }, { "tax", InvoiceBson.Money(allocation.Tax) }, { "charges", InvoiceBson.Money(allocation.Charges) }, { "rounding", rule.Rounding } };
                OrderLineBson.With(line, OrderLineBson.Revision(accepted)); lines.Add(line); accumulated.Add(line);
            }
            InvoiceRecords.RequireBudget(accumulated, budget);
        }
        var first = source[0].Resource;
        var accounting = await new InvoiceReleaseAccounting(database, clock).ReadAndFenceAsync(tx,
            new(first.FulfillmentOrganizationId!.Value, first.ShopId!.Value, first.ChannelId!.Value, "IDR"), input.Mappings, AccountingCalendar.BusinessDate(began), ct);
        var snapshot = new BsonDocument { { "receipts", InvoiceBson.Sources(source) }, { "commercial", commercial.Sources }, { "rule", C.Rule(rule) },
            { "budgets", budgets }, { "accounting", accounting }, { "lines", lines },
            { "templateComponent", B.TemplateComponent }, { "numberingComponent", B.NumberingComponent } };
        if (clock.UtcNow < began || clock.UtcNow >= input.ValidUntil) throw B.Changed();
        return new(source, snapshot, began, input);
    }

    internal async Task HistoricalAsync(ActorContext actor, IClientSessionHandle tx, BsonDocument source, CancellationToken ct)
    {
        var historical = InvoiceBson.Sources(source["receipts"].AsBsonArray);
        foreach (var receipt in historical) await receipts.RequireInvoiceHistoryAsync(actor, tx, receipt, ct);
        var commercial = await new InvoiceReleaseCommercial(database, receipts).ReadAndFenceAsync(actor, tx, historical, ct);
        InvoiceReleaseRecords.Equal(source["commercial"], commercial.Sources); InvoiceReleaseRecords.Equal(source["rule"], C.Rule(commercial.Common));
        var accounting = source["accounting"]; var setup = new AccountingBookSetupRecords(database);
        var proposal = await setup.ProposalAsync(tx, accounting["setupProposalId"].AsInt64, ct);
        var decision = await setup.DecisionAsync(tx, accounting["setupDecisionId"].AsInt64, proposal, ct);
        InvoiceReleaseRecords.Equal(proposal["snapshotHash"], accounting["setupProposalHash"]);
        InvoiceReleaseRecords.Equal(decision["snapshotHash"], accounting["setupDecisionHash"]);
        InvoiceReleaseRecords.Equal(decision["bookId"], accounting["book"]["_id"]); InvoiceReleaseRecords.Equal(decision["chartId"], accounting["chart"]["_id"]);
        InvoiceReleaseRecords.Equal(accounting["key"], decision["key"]);
        await InvoiceReleaseAccounting.FenceApprovedAsync(database, tx, proposal, decision, ct);
        InvoiceReleaseRecords.Equal(source["templateComponent"], B.TemplateComponent); InvoiceReleaseRecords.Equal(source["numberingComponent"], B.NumberingComponent);
        // Historical reads do not reassert that an old accounting period or release
        // window is open today. New issuance uses CaptureAsync again in its transaction.
    }

    internal static InvoiceTerms Terms(long releaseId, long approvedRevision, InvoiceReleaseInput input, BsonDocument source)
    {
        if (releaseId <= 0 || approvedRevision < 2) throw B.Broken();
        var rule = C.Rule(source["rule"]); var terms = rule.Terms; var reference = new OrderPolicyReference(releaseId, approvedRevision);
        return new(reference, reference, reference, reference, reference, reference, terms.Grouping, input.PeriodFrom, input.PeriodTo,
            terms.TermOrigin, terms.TermDays, terms.DayRule, terms.WorkingDays, terms.Holidays, rule.Rounding,
            input.Mappings.Receivable, input.Mappings.Revenue, input.Mappings.Tax, input.Mappings.Charges, terms.TaxDocumentProcess, input.ValidUntil,
            [.. input.Allocations.Select(line => new InvoiceApprovedLine(line.ReceiptId.Value, line.SkuId.Value, line.Tax, line.Charges, line.LineRevision))]);
    }

    private static void RequireGrouping(ImmutableArray<InvoiceReceiptSource> source, InvoiceReleaseInput input, OrderCommercialRule rule, DateTimeOffset now)
    {
        var first = source[0]; var scope = first.Resource;
        if (rule.Terms.Grouping == "receipt" && source.Length != 1) throw B.Changed();
        foreach (var receipt in source) {
            var date = AccountingCalendar.BusinessDate(B.Time(receipt.Snapshot["receipt"]["recordedAt"]));
            if (date < input.PeriodFrom || date > input.PeriodTo || date > AccountingCalendar.BusinessDate(now) ||
                receipt.Resource.BuyingOrganizationId != scope.BuyingOrganizationId || receipt.Resource.FulfillmentOrganizationId != scope.FulfillmentOrganizationId ||
                receipt.Resource.ShopId != scope.ShopId || receipt.Resource.ChannelId != scope.ChannelId ||
                rule.Terms.Grouping == "buying_branch_period_channel" && receipt.Resource.BranchId != scope.BranchId ||
                !InvoiceRecords.Equal(receipt.Snapshot["shipment"]["seller"], first.Snapshot["shipment"]["seller"]) ||
                !InvoiceRecords.Equal(receipt.Snapshot["commercial"]["terms"]["buyer"], first.Snapshot["commercial"]["terms"]["buyer"])) throw B.Changed();
        }
    }
}
