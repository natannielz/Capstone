using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using UnitToko.Tests.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

/// <summary>Real order, reservation, shipment, recipient, invoice and journal owners.
/// Invoice policy admission is explicitly synthetic like InvoiceData; this is no proof
/// of a production invoice-policy producer or its operational approval.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionInvoiceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Partial_invoiced_shipment_keeps_original_price_and_blocks_amendment_below_posted_tax_while_new_line_rounds_independently() {
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock: false, includeCustomerMappings: true);
        await using var data = prepared.Data;
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        await data.Base.AddGrantAsync(data.Dispatcher, 790201, scope, [Permissions.ShipmentRead]);
        await data.Base.AddGrantAsync(data.Courier, 790202, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), [Permissions.DeliveryEvidence]);
        await data.Base.AddGrantAsync(data.Buyer, 790203, CatalogAuthoringTestData.Audience, [Permissions.ReceiptAccept, Permissions.InvoiceRead]);
        if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", data.Buyer.UserId)).AnyAsync())
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", data.Buyer.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic R02 recipient" }, { "verifiedEmail", "r02-recipient@example.invalid" },
                { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        var finance = await data.Base.AddActorAsync(790204, scope, Permissions.InvoiceIssue, Permissions.InvoiceRead);
        var queries = new ShipmentQueryService(data.Database, data.Unit, data.Clock, data.Orders, data.Preparations);
        var documents = new ShipmentDocumentService(data.Database, data.Unit, new(data.Database), data.Clock, queries);
        var receipts = new ShipmentReceiptService(data.Database, data.Unit, new(data.Database), data.Clock, queries, documents);
        var admission = new SyntheticInvoicePolicy(data, finance);
        await admission.SeedAsync(new Money(24000m));
        var invoices = new InvoiceService(data.Database, data.Unit, new(data.Database), data.Clock, receipts, admission);
        async Task<(ShipmentReceiptView Receipt, InvoiceView Invoice)> AcceptAndInvoice(long shipmentId, string key) {
            var document = await documents.IssueAsync(data.Dispatcher, shipmentId, 1, GenuineFiniteDispatchTestData.Meta(key + "-document"));
            var challenge = await receipts.IssueChallengeAsync(data.Courier, shipmentId, document.Id.Value, data.Buyer.UserId,
                GenuineFiniteDispatchTestData.Meta(key + "-challenge"));
            var review = await receipts.ReviewAsync(data.Buyer, challenge.Id.Value, challenge.Token!);
            var declarations = review.Lines.Select(line => new ReceiptLineDeclaration(line.SkuId, new(line.Packages, 0), new(0, 0), new(0, 0), null,
                long.Parse(line.LineRevision, CultureInfo.InvariantCulture))).ToImmutableArray();
            var receipt = await receipts.ConfirmAsync(data.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion,
                true, declarations, GenuineFiniteDispatchTestData.Meta(key + "-receipt"));
            var selection = new[] { new InvoiceReceiptSelection(receipt.ShipmentId, receipt.Id) };
            var preview = await invoices.PreviewAsync(finance, selection);
            var invoice = await invoices.IssueAsync(finance, selection, preview.Review, GenuineFiniteDispatchTestData.Meta(key + "-invoice"));
            Assert.Equal(invoice.Id, (await invoices.RecoverAsync(finance, key + "-invoice"))!.Id);
            return (receipt, invoice);
        }
        var firstShipment = await data.DispatchAsync(await data.PrepareDispatchAsync(8, "r02-invoice-old-preparation"), "r02-invoice-old-dispatch");
        var first = await AcceptAndInvoice(firstShipment["shipmentId"].AsInt64, "r02-invoice-old");
        Assert.Equal(new Money(160000m), first.Invoice.Detail.Net); Assert.Equal(new Money(24000m), first.Invoice.Detail.Tax);
        var originalInvoice = await data.Rows("invoices").Find(new BsonDocument("_id", first.Invoice.Id.Value)).SingleAsync();
        var originalReceipt = await data.Rows("shipmentReceipts").Find(new BsonDocument("_id", first.Receipt.Id.Value)).SingleAsync();
        var originalJournal = await data.Rows("journalEntries").Find(new BsonDocument("_id", originalInvoice["journalId"])).SingleAsync();
        var originalDocument = await invoices.DownloadAsync(finance, first.Invoice.Id.Value);
        var originalOrder = await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync(); originalOrder.Remove("fence");
        async Task<BsonDocument> ProposePrice(decimal amount, string key) {
            var prices = new CatalogPriceService(data.Database, data.Unit, new(data.Database), data.Clock);
            var prior = await prices.PreviewAsync(prepared.PriceMaker, data.SkuId, CatalogPriceAccess.Prepare);
            var proposal = await prices.ProposeAsync(prepared.PriceMaker, data.SkuId, prior.Expected, new("IDR", new(amount)), GenuineFiniteDispatchTestData.Meta(key));
            return await prices.DecideAsync(prepared.PriceChecker, proposal["proposalId"].AsInt64, 1, UnitToko.Modules.Catalog.CatalogPriceDecision.Approve,
                "Explicit new price for buyer consent", GenuineFiniteDispatchTestData.Meta(key + "-approved"));
        }
        async Task<BsonDocument> ProposeReplacement(string key) {
            var target = Assert.Single((await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items);
            var replacement = Assert.Single((await data.Orders.SubstitutionPackagesAsync(data.Dispatcher, data.OrderId, data.SuborderId)).Items,
                line => line.Definition.SkuId.Value == data.SkuId);
            var input = new OrderSubstitutionInput(new(data.OrderId), new(data.SuborderId), target.Revision, target.DispatchSequence,
                new(new(data.SkuId), 4, new(replacement.Definition.SkuId, replacement.Definition.Id, replacement.Definition.Version, replacement.Price.Id, replacement.Price.Version, 4)));
            var review = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
            return await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, review.Review, GenuineFiniteDispatchTestData.Meta(key));
        }
        await ProposePrice(10000m, "r02-invoice-lower-price");
        var lower = await ProposeReplacement("r02-invoice-lower-proposal"); var unchanged = await data.BusinessAsync();
        var blocked = await Assert.ThrowsAsync<DomainValidationException>(() => data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer,
            lower["proposalId"].AsInt64, OrderSubstitutionDecision.Accept));
        Assert.Equal("invoice_commercial_allocation_exceeded", blocked.Code); Assert.Equal(unchanged, await data.BusinessAsync());
        await ProposePrice(21000m, "r02-invoice-renewed-price");
        var proposed = await ProposeReplacement("r02-invoice-renewed-proposal");
        var consent = await data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposed["proposalId"].AsInt64, OrderSubstitutionDecision.Accept);
        var accepted = await data.Orders.DecideSubstitutionAsync(data.Buyer, consent.ProposalId.Value, consent.Revision, OrderSubstitutionDecision.Accept,
            consent.Review, GenuineFiniteDispatchTestData.Meta("r02-invoice-consent"));
        var spend = await data.Orders.ReviewOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending);
        await data.Orders.DecideOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending, spend.Revision, spend.Review, GenuineFiniteDispatchTestData.Meta("r02-invoice-spend"));
        var store = await data.Orders.ReviewOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store);
        await data.Orders.DecideOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, GenuineFiniteDispatchTestData.Meta("r02-invoice-store"));
        var source = await data.Orders.ReadFulfillmentOrderAsync(data.Dispatcher, data.SuborderId);
        var previous = (await data.Preparations.ReadAsync(data.Dispatcher, data.SuborderId))!;
        await data.Preparations.SaveAsync(data.Dispatcher, data.SuborderId, previous.Revision, source.Review,
            [new(new(data.SkuId), 4, accepted["revision"].AsInt64)], GenuineFiniteDispatchTestData.Meta("r02-invoice-new-preparation"));
        var ready = await data.Dispatch.ReadPreparationAsync(data.Dispatcher, data.SuborderId);
        var lot = Assert.Single(ready.Lots);
        Assert.Equal(accepted["revision"].AsInt64, lot.Reservation.LineRevision);
        var dispatch = await data.Dispatch.PreviewAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value, ready.PreparationRevision,
            data.Courier.UserId, [new(lot.Reservation.ReservationId, 4)]);
        var secondShipment = await data.DispatchAsync(dispatch, "r02-invoice-new-dispatch");
        await admission.SetTaxAsync(new Money(400m));
        var second = await AcceptAndInvoice(secondShipment["shipmentId"].AsInt64, "r02-invoice-new");
        Assert.Equal(new Money(84000m), second.Invoice.Detail.Net); Assert.Equal(new Money(400m), second.Invoice.Detail.Tax);
        var allocation = await data.Rows("invoiceSourceAllocations").Find(new BsonDocument("invoiceId", second.Invoice.Id.Value)).SingleAsync();
        Assert.Equal(accepted["revision"], allocation["lineRevision"]); Assert.Equal(0L, allocation["previousBaseUnits"].AsInt64);
        Assert.Equal(new Money(0m), InvoiceBson.Money(allocation["previousNet"]));
        var historical = await data.Unit.ExecuteAsync((tx, ct) => new InvoiceRecords(data.Database, receipts).OrderAllocationsAsync(finance, tx, data.OrderId, ct));
        Assert.Equal(2, historical.Count); Assert.Equal(24400m, historical.Sum(row => InvoiceBson.Money(row["tax"]).Value));
        Assert.Equal(originalInvoice, await data.Rows("invoices").Find(new BsonDocument("_id", first.Invoice.Id.Value)).SingleAsync());
        Assert.Equal(originalReceipt, await data.Rows("shipmentReceipts").Find(new BsonDocument("_id", first.Receipt.Id.Value)).SingleAsync());
        Assert.Equal(originalJournal, await data.Rows("journalEntries").Find(new BsonDocument("_id", originalInvoice["journalId"])).SingleAsync());
        Assert.Equal(originalDocument, await invoices.DownloadAsync(finance, first.Invoice.Id.Value));
        var orderAfter = await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync(); orderAfter.Remove("fence");
        Assert.Equal(originalOrder, orderAfter);
    }

    private sealed class SyntheticInvoicePolicy(GenuineFiniteDispatchTestData data, ActorContext finance) : IInvoiceAdmissionReader, IInvoiceReleaseWriter
    {
        public Task BindPostedAsync(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms, BsonDocument invoice, CancellationToken ct) =>
            LegacyInvoiceRelease.AnchorAsync(data.Database, tx, invoice, ct);
        public void RequireCurrent(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms) { }
        private readonly ConcurrentDictionary<IClientSessionHandle, (long Transaction, BsonDocument Row)> captured = new();
        public IMongoDatabase Database => data.Database;
        private IMongoCollection<BsonDocument> Rows => data.Rows("fixtureR02InvoiceApproval");
        internal Task SeedAsync(Money tax) => Rows.InsertOneAsync(new BsonDocument { { "_id", 1L }, { "revision", 1L }, { "fence", 0L },
            { "approved", true }, { "expiresAt", data.Clock.UtcNow.AddMinutes(30).UtcDateTime }, { "tax", InvoiceBson.Money(tax) } });
        internal async Task SetTaxAsync(Money tax) {
            Assert.Equal(1L, (await Rows.UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("tax", InvoiceBson.Money(tax)).Inc("revision", 1L))).ModifiedCount);
        }
        public async Task<InvoiceTerms> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, ImmutableArray<InvoiceReceiptSource> sources, DateOnly date, CancellationToken ct) {
            if (actor != finance || !tx.IsInTransaction) throw InvoicePolicy.Invalid();
            var row = await Rows.Find(tx, new BsonDocument("_id", 1L)).SingleAsync(ct);
            if (!row["approved"].AsBoolean || ShipmentReceiptRecords.Time(row["expiresAt"]) <= data.Clock.UtcNow) throw InvoicePolicy.Invalid();
            Assert.Single(sources); Assert.Single(sources[0].Snapshot["receipt"]["lines"].AsBsonArray);
            captured[tx] = (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber, row.DeepClone().AsBsonDocument);
            return new(new(1, row["revision"].AsInt64), new(2, 1), new(3, 1), new(4, 1), new(5, 1), new(6, 1), "receipt",
                date, date, "issue_date", 14, "calendar_days", [], [], "half_even", "invoice_ar", "invoice_revenue", "invoice_tax", "invoice_charges",
                "Synthetic R02 invoice admission only; not operational policy approval", ShipmentReceiptRecords.Time(row["expiresAt"]),
                [.. sources.SelectMany(source => source.Snapshot["receipt"]["lines"].AsBsonArray.Select(line => new InvoiceApprovedLine(
                    source.Snapshot["receipt"]["id"].AsInt64, line["skuId"].AsInt64, InvoiceBson.Money(row["tax"]), new(0m), OrderLineBson.Revision(line))))]);
        }
        public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, InvoiceTerms terms, CancellationToken ct) {
            if (actor != finance || !tx.IsInTransaction || !captured.TryGetValue(tx, out var held) ||
                held.Transaction != tx.WrappedCoreSession.CurrentTransaction.TransactionNumber || terms.Policy.Revision != held.Row["revision"].AsInt64 ||
                terms.ValidUntil <= data.Clock.UtcNow) throw InvoicePolicy.Invalid();
            if ((await Rows.UpdateOneAsync(tx, held.Row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw InvoicePolicy.Invalid();
            held.Row["fence"] = checked(held.Row["fence"].AsInt64 + 1); captured[tx] = held;
        }
    }
}
