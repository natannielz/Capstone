using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using System.Security.Cryptography;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Catalog;
using C = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Tests.Ordering;

/// <summary>Two independently submitted, approved, dispatched and received orders.
/// Their shared invoice is issued only after an actual119 grouped release approval.
/// No historical row, stock value, receipt, invoice or admission result is fabricated.</summary>
[SupportedOSPlatform("windows")]
internal sealed class OrderTimelineGroupedTestData(InvoiceReleaseTestData release) : IAsyncDisposable
{
    internal InvoiceReleaseTestData Release => release;
    internal long OrderA { get; private set; }
    internal long OrderB { get; private set; }
    internal ShipmentReceiptView ReceiptA => release.Receipt;
    internal ShipmentReceiptView ReceiptB = null!;
    internal InvoiceView Invoice = null!;
    internal ShipmentQueryService Shipments = null!;
    internal PaymentDraftService Payments = null!;
    internal AfterSalesCaseService Cases = null!;
    internal static async Task<OrderTimelineGroupedTestData> CreateAsync(MongoFixture fixture, bool systemClock = false, bool includeCustomerCashMappings = false) {
        // Production writes with a provenance seal ring (IdentityKeyMaterial). Without one every command re-proves this
        // two-order graph in full and exceeds the 60 s transaction bound, so this fixture runs sealed like production.
        var ownsRing = ProvenanceSealRing.Current is null;
        if (ownsRing) ProvenanceSealRing.Configure(SealKeyRing.ForKeys("timeline", new Dictionary<string, byte[]> { ["timeline"] = RandomNumberGenerator.GetBytes(32) }));
        InvoiceReleaseTestData release;
        try {
            release = await InvoiceReleaseTestData.CreateAsync(fixture, systemClock: systemClock, beforeFirstOrder: GovernGroupingAsync,
                includeCustomerCashMappings: includeCustomerCashMappings);
        } catch { if (ownsRing) ProvenanceSealRing.Configure(null); throw; }
        var data = new OrderTimelineGroupedTestData(release) { ownsRing = ownsRing };
        try {
            var dispatch = release.Dispatch; data.OrderA = dispatch.OrderId;
            data.Shipments = new(release.Database, release.Unit, release.Clock, dispatch.Orders, dispatch.Preparations);
            await dispatch.Base.AddGrantAsync(dispatch.Buyer, 811720, CatalogAuthoringTestData.Audience,
                [Permissions.ShipmentRead, Permissions.PaymentEvidence, Permissions.CaseRead, Permissions.CaseRequest]);
            // The shared source fixture genuinely valued and quality-approved two cartons.
            // Its first order consumed one; release the remaining held carton through its owner.
            var released = await dispatch.Stock.ProposeAsync("timeline-second-carton-release");
            await dispatch.Stock.DecideAsync(released["proposalId"].AsInt64, "timeline-second-carton-approved");
            if (systemClock) dispatch.Buyer = await dispatch.IssueExistingSessionAsync(dispatch.Buyer);
            var review = await dispatch.Orders.PreviewAsync(dispatch.Buyer, dispatch.Input);
            var ordered = await dispatch.Orders.SubmitAsync(dispatch.Buyer, dispatch.Input, review.Review, InvoiceReleaseTestData.Meta("timeline-second-order"));
            data.OrderB = ordered["orderId"].AsInt64; var secondSuborder = Assert.Single(ordered["suborderIds"].AsBsonArray).AsInt64;
            Assert.NotEqual(data.OrderA, data.OrderB);
            if (systemClock) dispatch.Spender = await dispatch.IssueExistingSessionAsync(dispatch.Spender);
            var spending = await dispatch.Orders.ReviewOrderAsync(dispatch.Spender, data.OrderB, null, OrderReviewStage.Spending);
            await dispatch.Orders.DecideOrderAsync(dispatch.Spender, data.OrderB, null, OrderReviewStage.Spending, spending.Revision, spending.Review,
                InvoiceReleaseTestData.Meta("timeline-second-spending"));
            if (systemClock) dispatch.Dispatcher = await dispatch.IssueExistingSessionAsync(dispatch.Dispatcher);
            var store = await dispatch.Orders.ReviewOrderAsync(dispatch.Dispatcher, data.OrderB, secondSuborder, OrderReviewStage.Store);
            await dispatch.Orders.DecideOrderAsync(dispatch.Dispatcher, data.OrderB, secondSuborder, OrderReviewStage.Store, store.Revision, store.Review,
                InvoiceReleaseTestData.Meta("timeline-second-store"));
            var originalOrder = dispatch.OrderId; var originalSuborder = dispatch.SuborderId; BsonDocument shipment;
            try {
                dispatch.OrderId = data.OrderB; dispatch.SuborderId = secondSuborder;
                if (systemClock) dispatch.Dispatcher = await dispatch.IssueExistingSessionAsync(dispatch.Dispatcher);
                shipment = await dispatch.DispatchAsync(await dispatch.PrepareDispatchAsync(1, "timeline-second-pick"), "timeline-second-dispatch");
            } finally { dispatch.OrderId = originalOrder; dispatch.SuborderId = originalSuborder; }
            var documents = new ShipmentDocumentService(release.Database, release.Unit, new(release.Database), release.Clock, data.Shipments);
            if (systemClock) dispatch.Dispatcher = await dispatch.IssueExistingSessionAsync(dispatch.Dispatcher);
            var document = await documents.IssueAsync(dispatch.Dispatcher, shipment["shipmentId"].AsInt64, 1, InvoiceReleaseTestData.Meta("timeline-second-document"));
            if (systemClock) dispatch.Courier = await dispatch.IssueExistingSessionAsync(dispatch.Courier);
            var challenge = await release.Receipts.IssueChallengeAsync(dispatch.Courier, document.ShipmentId.Value, document.Id.Value, dispatch.Buyer.UserId,
                InvoiceReleaseTestData.Meta("timeline-second-challenge"));
            if (systemClock) dispatch.Buyer = await dispatch.IssueExistingSessionAsync(dispatch.Buyer);
            var received = await release.Receipts.ReviewAsync(dispatch.Buyer, challenge.Id.Value, challenge.Token!);
            data.ReceiptB = await release.Receipts.ConfirmAsync(dispatch.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
                [.. received.Lines.Select(line => new ReceiptLineDeclaration(line.SkuId, new(line.Packages, 0), new(0, 0), new(0, 0), null,
                    long.Parse(line.LineRevision, CultureInfo.InvariantCulture)))], InvoiceReleaseTestData.Meta("timeline-second-receipt"));
            ImmutableArray<InvoiceReceiptSelection> selection = [new(release.Receipt.ShipmentId, release.Receipt.Id), new(data.ReceiptB.ShipmentId, data.ReceiptB.Id)];
            if (systemClock) release.Maker = await dispatch.IssueExistingSessionAsync(release.Maker);
            var eligible = await release.Releases.EligibilityAsync(release.Maker, selection);
            Assert.Equal("buying_branch_period_channel", eligible.Facts.Grouping); Assert.Equal(2, eligible.Facts.Lines.Length); Assert.Equal(2, eligible.Facts.Budgets.Length);
            var input = release.Input with { Receipts = selection, Allocations = [.. eligible.Facts.Lines.Select(line => {
                var budget = Assert.Single(eligible.Facts.Budgets, item => item.OrderId == line.OrderId);
                Assert.Equal(new Money(0m), budget.PostedTax); Assert.Equal(new Money(0m), budget.PostedCharges);
                return new InvoiceReleaseAllocation(line.ReceiptId, line.SkuId, long.Parse(line.LineRevision, CultureInfo.InvariantCulture), budget.ApprovedTax, budget.ApprovedCharges);
            })] };
            if (systemClock) release.Maker = await dispatch.IssueExistingSessionAsync(release.Maker);
            var proposed = await release.ProposeAsync("timeline-grouped-release", input);
            if (systemClock) release.Checker = await dispatch.IssueExistingSessionAsync(release.Checker);
            await release.DecideAsync(proposed, "timeline-grouped-release-approved");
            if (systemClock) release.Finance = await dispatch.IssueExistingSessionAsync(release.Finance);
            var invoiceReview = await release.Invoices.PreviewAsync(release.Finance, selection);
            data.Invoice = await release.Invoices.IssueAsync(release.Finance, selection, invoiceReview.Review, InvoiceReleaseTestData.Meta("timeline-grouped-invoice"));
            var persisted = await release.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", data.Invoice.Id.Value)).SingleAsync();
            Assert.Equal(new[] { data.OrderA, data.OrderB }.Order().ToArray(), persisted["orderIds"].AsBsonArray.Select(value => value.AsInt64).Order().ToArray());
            data.Payments = new(release.Database, release.Unit, new(release.Database), release.Clock, release.Invoices);
            data.Cases = new(release.Database, release.Unit, new(release.Database), release.Clock, release.Invoices);
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static async Task GovernGroupingAsync(GenuineFiniteDispatchTestData dispatch) {
        var organizations = dispatch.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
        var maker = await dispatch.Base.AddActorAsync(811710, CatalogAuthoringTestData.Shop, C.ProposePermission);
        var checker = await dispatch.Base.AddActorAsync(811711, CatalogAuthoringTestData.Shop, C.ApprovePermission);
        await PersonTestData.SeedDistinctPeopleAsync(dispatch.Database, dispatch.Clock, maker.UserId, checker.UserId);
        var head = await dispatch.Rows(C.Heads).Find(new BsonDocument { { "target.kind", "branch" }, { "target.id", 11L } }).SingleAsync();
        var version = await dispatch.Rows(C.Versions).Find(new BsonDocument("_id", head["versionId"])).SingleAsync();
        var proposal = await dispatch.Rows(C.Proposals).Find(new BsonDocument("_id", version["proposalId"])).SingleAsync();
        var original = C.Input(proposal["input"]);
        var input = original with { ExpectedPolicyVersion = head["version"].AsInt64,
            Rule = original.Rule with { Terms = original.Rule.Terms with { Grouping = "buying_branch_period_channel" } } };
        var owner = new OrderCommercialPolicyService(dispatch.Database, dispatch.Unit, new(dispatch.Database), dispatch.Clock, organizations);
        var preview = await owner.PreviewAsync(maker, input);
        var proposed = await owner.ProposeAsync(maker, input, preview.Binding, InvoiceReleaseTestData.Meta("timeline-grouping-proposed"));
        var decision = await owner.PreviewDecisionAsync(checker, proposed["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve);
        await owner.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve, decision.Binding,
            InvoiceReleaseTestData.Meta("timeline-grouping-approved"));
    }
    internal async Task<BsonDocument> BusinessAsync() {
        var result = new BsonDocument();
        foreach (var name in new[] { "orders", "orderSuborders", "shipments", "shipmentReceipts", "stockBalances", "lots", "reservations", "movements",
            InvoiceBson.Invoices, InvoiceBson.Allocations, "journalEntries", "payments", "bankReceipts", "paymentAllocations" }) {
            var rows = await release.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result.Add(name, new BsonArray(rows));
        }
        return result;
    }
    private bool ownsRing;
    public async ValueTask DisposeAsync() {
        try { await release.DisposeAsync(); } finally { if (ownsRing) ProvenanceSealRing.Configure(null); }
    }
}
