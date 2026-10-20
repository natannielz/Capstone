using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Billing;

/// <summary>Synthetic approved input values; actual registered119 and every owning
/// order, finite inventory, commercial, book, shipment and recipient command. No
/// invoice policy fixture or seeded release/receipt substitutes for the producer.</summary>
[SupportedOSPlatform("windows")]
internal sealed class InvoiceReleaseTestData(GenuineFiniteDispatchTestData dispatch) : IAsyncDisposable
{
    internal GenuineFiniteDispatchTestData Dispatch => dispatch;
    internal IMongoDatabase Database => dispatch.Database;
    internal MongoUnitOfWork Unit => dispatch.Unit;
    internal MutableAuthorizationClock Clock => dispatch.Clock;
    internal ActorContext Maker = null!, Checker = null!, Finance = null!, Reader = null!, Outsider = null!;
    internal ShipmentReceiptService Receipts = null!;
    internal ShipmentReceiptView Receipt = null!;
    internal InvoiceReleaseService Releases = null!;
    internal InvoiceService Invoices = null!;
    internal ImmutableArray<InvoiceReceiptSelection> Selection => [new(Receipt.ShipmentId, Receipt.Id)];
    internal InvoiceReleaseInput Input = null!;
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independent invoice release review", "invoice-release-native");
    internal static async Task<InvoiceReleaseTestData> CreateAsync(MongoFixture fixture, bool systemClock = false, Func<GenuineFiniteDispatchTestData, Task>? beforeFirstOrder = null,
        bool includeCustomerCashMappings = false) {
        // includeCustomerMappings is the R02 fixture's explicit reviewed book-input
        // seam. It invokes AccountingBookSetupService before any stock journal.
        var dispatch = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted: false, createOrder: false,
            useSystemClock: systemClock, includeCustomerMappings: true, includeCustomerCashMappings: includeCustomerCashMappings,
            // Production order admission keeps the reviewed safety buffer; the single fixture package must sit outside it.
            reviewedSafetyBufferBaseUnits: 0);
        var data = new InvoiceReleaseTestData(dispatch);
        try {
            var migration = await data.Rows("schemaMigrations").Find(new BsonDocument("_id", 119)).SingleOrDefaultAsync();
            Assert.NotNull(migration); Assert.Equal("applied", migration["state"].AsString);
            if (beforeFirstOrder is not null) await beforeFirstOrder(dispatch);
            var organizations = dispatch.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
            var admission = new ApprovedOrderAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, organizations);
            dispatch.Orders = new(data.Database, data.Unit, new(data.Database), data.Clock, admission);
            dispatch.Buyer = await dispatch.IssueExistingSessionAsync(dispatch.Buyer);
            var preview = await dispatch.Orders.PreviewAsync(dispatch.Buyer, dispatch.Input);
            var order = await dispatch.Orders.SubmitAsync(dispatch.Buyer, dispatch.Input, preview.Review, Meta("invoice-real-order"));
            dispatch.OrderId = order["orderId"].AsInt64; dispatch.SuborderId = Assert.Single(order["suborderIds"].AsBsonArray).AsInt64;
            dispatch.Spender = await dispatch.IssueExistingSessionAsync(dispatch.Spender);
            var spending = await dispatch.Orders.ReviewOrderAsync(dispatch.Spender, dispatch.OrderId, null, OrderReviewStage.Spending);
            await dispatch.Orders.DecideOrderAsync(dispatch.Spender, dispatch.OrderId, null, OrderReviewStage.Spending, spending.Revision, spending.Review, Meta("invoice-real-spending"));
            dispatch.Dispatcher = await dispatch.IssueExistingSessionAsync(dispatch.Dispatcher);
            var store = await dispatch.Orders.ReviewOrderAsync(dispatch.Dispatcher, dispatch.OrderId, dispatch.SuborderId, OrderReviewStage.Store);
            await dispatch.Orders.DecideOrderAsync(dispatch.Dispatcher, dispatch.OrderId, dispatch.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, Meta("invoice-real-acceptance"));
            dispatch.Preparations = new(data.Database, data.Unit, new(data.Database), data.Clock, dispatch.Orders);
            var dispatchAdmission = new ApprovedShipmentDispatchAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, dispatch.Orders, organizations);
            dispatch.Dispatch = new(data.Database, data.Unit, new(data.Database), data.Clock, dispatch.Orders, dispatch.Preparations, dispatchAdmission);
            dispatchAdmission.AttachDispatchOwner(dispatch.Dispatch);
            var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
            await dispatch.Base.AddGrantAsync(dispatch.Dispatcher, 811601, scope, [Permissions.ShipmentRead]);
            await dispatch.Base.AddGrantAsync(dispatch.Courier, 811602, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), [Permissions.DeliveryEvidence]);
            await dispatch.Base.AddGrantAsync(dispatch.Buyer, 811603, CatalogAuthoringTestData.Audience, [Permissions.ReceiptAccept, Permissions.InvoiceRead]);
            var shipment = await dispatch.DispatchAsync(await dispatch.PrepareDispatchAsync(1, "invoice-real-pick"), "invoice-real-dispatch");
            var queries = new ShipmentQueryService(data.Database, data.Unit, data.Clock, dispatch.Orders, dispatch.Preparations);
            var documents = new ShipmentDocumentService(data.Database, data.Unit, new(data.Database), data.Clock, queries);
            data.Receipts = new(data.Database, data.Unit, new(data.Database), data.Clock, queries, documents);
            var document = await documents.IssueAsync(dispatch.Dispatcher, shipment["shipmentId"].AsInt64, 1, Meta("invoice-real-document"));
            if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", dispatch.Buyer.UserId)).AnyAsync())
                await data.Rows("accountProfiles").InsertOneAsync(Profile(dispatch.Buyer, data.Clock.UtcNow));
            dispatch.Courier = await dispatch.IssueExistingSessionAsync(dispatch.Courier);
            var challenge = await data.Receipts.IssueChallengeAsync(dispatch.Courier, document.ShipmentId.Value, document.Id.Value, dispatch.Buyer.UserId, Meta("invoice-real-consent-challenge"));
            dispatch.Buyer = await dispatch.IssueExistingSessionAsync(dispatch.Buyer);
            var reviewed = await data.Receipts.ReviewAsync(dispatch.Buyer, challenge.Id.Value, challenge.Token!);
            data.Receipt = await data.Receipts.ConfirmAsync(dispatch.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
                [.. reviewed.Lines.Select(line => new ReceiptLineDeclaration(line.SkuId, new(line.Packages, 0), new(0, 0), new(0, 0), null,
                    long.Parse(line.LineRevision, CultureInfo.InvariantCulture)))], Meta("invoice-real-recipient"));
            data.Maker = await dispatch.Base.AddActorAsync(811610, scope, Permissions.InvoiceRead, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleasePropose);
            data.Checker = await dispatch.Base.AddActorAsync(811611, scope, Permissions.InvoiceRead, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleaseApprove);
            data.Finance = await dispatch.Base.AddActorAsync(811612, scope, Permissions.InvoiceRead, Permissions.InvoiceIssue);
            data.Reader = await dispatch.Base.AddActorAsync(811613, scope, Permissions.InvoiceRead, Permissions.InvoiceReleaseRead);
            data.Outsider = await dispatch.Base.AddActorAsync(811614, new(GrantScopeKind.FulfillmentShop, 100,
                ShopId: dispatch.Stock.Valuation.Inspection.Receiving.Procurement.ForeignShopId, ChannelId: 103),
                Permissions.InvoiceRead, Permissions.InvoiceIssue, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleasePropose, Permissions.InvoiceReleaseApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Finance.UserId, data.Reader.UserId, data.Outsider.UserId);
            data.Maker = await dispatch.IssueExistingSessionAsync(data.Maker); data.Checker = await dispatch.IssueExistingSessionAsync(data.Checker);
            data.Finance = await dispatch.IssueExistingSessionAsync(data.Finance); data.Reader = await dispatch.IssueExistingSessionAsync(data.Reader);
            data.Outsider = await dispatch.IssueExistingSessionAsync(data.Outsider);
            foreach (var actor in new[] { data.Maker, data.Checker, data.Finance, data.Reader, data.Outsider }) await data.Rows("accountProfiles").InsertOneAsync(Profile(actor, data.Clock.UtcNow));
            data.Releases = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Receipts);
            data.Invoices = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Receipts, data.Releases);
            var facts = await data.Releases.EligibilityAsync(data.Maker, data.Selection);
            var line = Assert.Single(facts.Facts.Lines); var budget = Assert.Single(facts.Facts.Budgets); var date = AccountingCalendar.BusinessDate(data.Clock.UtcNow);
            // Values are human-entered synthetic rehearsal facts, never runtime defaults.
            var minute = DateTimeOffset.FromUnixTimeSeconds(data.Clock.UtcNow.ToUnixTimeSeconds() / 60 * 60);
            data.Input = new(data.Selection, 0, date, date, minute.AddMinutes(-1), minute.AddHours(1),
                "Synthetic existing HTML template acceptance", "Synthetic entity/channel/year numbering acceptance", "Synthetic no-hold source review",
                new("invoice_ar", "invoice_revenue", "invoice_tax", "invoice_charges"),
                [new(line.ReceiptId, line.SkuId, long.Parse(line.LineRevision, CultureInfo.InvariantCulture), budget.ApprovedTax, budget.ApprovedCharges)]);
            Assert.False(await data.Rows("authorizationGrants").Find(new BsonDocument { { "userId", new BsonDocument("$in", new BsonArray { data.Maker.UserId, data.Checker.UserId, data.Finance.UserId }) },
                { "permissions", Permissions.OrderSubmit } }).AnyAsync());
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    private static BsonDocument Profile(ActorContext actor, DateTimeOffset now) => new() { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
        { "displayName", "Synthetic invoice participant " + actor.UserId }, { "verifiedEmail", "invoice-" + actor.UserId + "@example.invalid" },
        { "verifiedAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime } };
    internal async Task<InvoiceReleaseCommandResult> ProposeAsync(string key = "release-propose", InvoiceReleaseInput? input = null) {
        input ??= Input; var preview = await Releases.PreviewAsync(Maker, input); return await Releases.ProposeAsync(Maker, input, preview.Binding, Meta(key));
    }
    internal async Task<InvoiceReleaseCommandResult> DecideAsync(InvoiceReleaseCommandResult proposal, string key = "release-approve", InvoiceReleaseDecision decision = InvoiceReleaseDecision.Approve) {
        var revision = long.Parse(proposal.Revision, CultureInfo.InvariantCulture);
        var preview = await Releases.PreviewDecisionAsync(Checker, proposal.ProposalId.Value, revision, decision);
        return await Releases.DecideAsync(Checker, proposal.ProposalId.Value, revision, decision, preview.Binding, Meta(key));
    }
    internal async Task<BsonDocument> ProtectedAsync() {
        var value = new BsonDocument();
        foreach (var name in new[] { "orders", "orderSuborders", "shipments", "shipmentReceipts", "stockBalances", "lots", "movements", "reservations", "bankReceipts", "paymentAllocations" }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); value.Add(name, new BsonArray(rows));
        }
        value.Add("cogsJournals", new BsonArray(await Rows("journalEntries").Find(new BsonDocument("source.namespace", "fulfillment.shipment")).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return value;
    }
    public ValueTask DisposeAsync() => dispatch.DisposeAsync();
}
