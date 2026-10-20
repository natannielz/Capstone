using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Inventory;
using P = UnitToko.Platform.Billing.PaymentDraftBson;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderTimelineListTests(MongoFixture fixture)
{
    [WindowsStorageFact(Skip = Pending.TransactionLatency)]
    public async Task Genuine_grouped_invoice_keeps_shipments_payments_and_reported_cases_bound_to_the_exact_order_without_business_writes() {
        await using var data = await OrderTimelineGroupedTestData.CreateAsync(fixture);
        var release = data.Release; var buyer = release.Dispatch.Buyer;
        var other = await release.Dispatch.Catalog.ActorAsync(811721, CatalogAuthoringTestData.Audience,
            Permissions.ShipmentRead, Permissions.InvoiceRead, Permissions.PaymentEvidence, Permissions.CaseRead);
        var firstShipment = await data.Shipments.ListForOrderAsync(buyer, data.OrderA);
        var secondShipment = await data.Shipments.ListForOrderAsync(buyer, data.OrderB);
        Assert.Equal(data.ReceiptA.ShipmentId, Assert.Single(firstShipment.Items).Id);
        Assert.Equal(data.ReceiptB.ShipmentId, Assert.Single(secondShipment.Items).Id);
        Assert.False(firstShipment.HasMore); Assert.False(secondShipment.HasMore);
        Assert.Empty((await data.Shipments.ListForOrderAsync(buyer, data.OrderA, data.ReceiptA.ShipmentId.Value)).Items);
        Assert.Empty((await data.Shipments.ListForOrderAsync(buyer, long.MaxValue)).Items);
        Assert.Empty((await data.Shipments.ListForOrderAsync(other, data.OrderA)).Items);
        var actualShipment = await release.Rows("shipments").Find(new BsonDocument("_id", data.ReceiptA.ShipmentId.Value)).SingleAsync();
        var changedRoute = actualShipment.DeepClone().AsBsonDocument; changedRoute["orderId"] = data.OrderB;
        await release.Rows("shipments").ReplaceOneAsync(new BsonDocument("_id", actualShipment["_id"]), changedRoute,
            new ReplaceOptions { BypassDocumentValidation = true });
        try {
            var error = await Record.ExceptionAsync(() => data.Shipments.ListForOrderAsync(buyer, data.OrderB));
            Assert.True(error is PersistenceConflictException or AuthorizationDeniedException or UnitToko.Modules.Contracts.DomainValidationException, error?.ToString());
        } finally { await release.Rows("shipments").ReplaceOneAsync(new BsonDocument("_id", actualShipment["_id"]), actualShipment); }
        Assert.Equal(data.Invoice.Id, Assert.Single((await release.Invoices.ListForOrderAsync(buyer, data.OrderA)).Items).Id);
        Assert.Equal(data.Invoice.Id, Assert.Single((await release.Invoices.ListForOrderAsync(buyer, data.OrderB)).Items).Id);
        Assert.Empty((await release.Invoices.ListForOrderAsync(other, data.OrderA)).Items);

        var protectedBefore = await data.BusinessAsync();
        var paymentInput = new PaymentDraftInput([data.Invoice.Id], new Money(1000m), AccountingCalendar.BusinessDate(release.Clock.UtcNow),
            "Synthetic buyer declaration", "SYNTHETIC-TIMELINE-TRANSFER", null);
        var saved = await data.Payments.SaveAsync(buyer, null, 0, paymentInput, InvoiceReleaseTestData.Meta("timeline-private-payment-one"));
        var second = await data.Payments.SaveAsync(buyer, null, 0, paymentInput with { Note = "Separate explicit declaration" }, InvoiceReleaseTestData.Meta("timeline-private-payment-two"));
        var paymentIds = new[] { saved["declarationId"].AsInt64, second["declarationId"].AsInt64 }.OrderDescending().ToArray();
        foreach (var order in new[] { data.OrderA, data.OrderB }) {
            var page = await data.Payments.ListForOrderAsync(buyer, order);
            Assert.Equal(paymentIds, page.Items.Select(value => value.Id.Value).ToArray()); Assert.False(page.HasMore);
            Assert.All(page.Items, value => Assert.Equal("draft", value.State)); // A declaration is never paid cash.
            Assert.Equal(paymentIds[1], Assert.Single((await data.Payments.ListForOrderAsync(buyer, order, paymentIds[0])).Items).Id.Value);
            Assert.Empty((await data.Payments.ListForOrderAsync(buyer, order, paymentIds[1])).Items);
            Assert.Empty((await data.Payments.ListForOrderAsync(other, order)).Items);
        }
        Assert.Empty((await data.Payments.ListForOrderAsync(buyer, long.MaxValue)).Items);
        await RequirePaymentFilteringBeforePaginationAsync(data, saved, paymentIds);

        var source = await data.Cases.SourceAsync(buyer, data.Invoice.Id.Value);
        Assert.Equal(2, source.Lines.Length); Assert.Contains(source.Lines, value => value.OrderId.Value == data.OrderB);
        var reported = Assert.Single(source.Lines, value => value.OrderId.Value == data.OrderA);
        var complaint = await data.Cases.RequestAsync(buyer, new AfterSalesCaseInput(source.InvoiceId.Value, source.Binding, "damaged",
            [new(reported.ReceiptId.Value, reported.SkuId.Value, 1)]), InvoiceReleaseTestData.Meta("timeline-order-a-case"));
        var caseId = complaint["caseId"].AsInt64;
        var caseA = Assert.Single((await data.Cases.ListForOrderAsync(buyer, data.OrderA)).Items);
        Assert.Equal(caseId, caseA.Id.Value); Assert.Equal("requested", caseA.State);
        // Both orders are real constituents of the same immutable invoice source, but
        // the actual report selects only A's receipt. B must not inherit A's complaint.
        Assert.Contains(caseA.Source.Lines, value => value.OrderId.Value == data.OrderB);
        Assert.DoesNotContain(caseA.Lines, value => value.ReceiptId == data.ReceiptB.Id.Value);
        Assert.Empty((await data.Cases.ListForOrderAsync(buyer, data.OrderB)).Items);
        Assert.Empty((await data.Cases.ListForOrderAsync(buyer, data.OrderA, caseId)).Items);
        Assert.Empty((await data.Cases.ListForOrderAsync(buyer, long.MaxValue)).Items);
        Assert.Empty((await data.Cases.ListForOrderAsync(other, data.OrderA)).Items);
        Assert.Equal(protectedBefore, await data.BusinessAsync());

        // Matching route/order predicates never waive the retained owner proof. Each
        // deliberate deletion is restored before the next independent assertion.
        var paymentVersion = await release.Rows(P.Versions).Find(new BsonDocument("_id", saved["versionId"])).SingleAsync();
        var caseRow = await release.Rows(R.Collection).Find(new BsonDocument("_id", caseId)).SingleAsync();
        await MissingAsync(release, "auditEvents", new("_id", paymentVersion["auditId"]),
            () => data.Payments.ListForOrderAsync(buyer, data.OrderA));
        await MissingAsync(release, "auditEvents", new("_id", caseRow["auditId"]),
            () => data.Cases.ListForOrderAsync(buyer, data.OrderA));
        // A corrupt irrelevant case still must not appear as an order-B case solely
        // because its original grouped invoice contains B.
        var caseAudit = await release.Rows("auditEvents").Find(new BsonDocument("_id", caseRow["auditId"])).SingleAsync();
        await release.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", caseAudit["_id"]));
        try { Assert.Empty((await data.Cases.ListForOrderAsync(buyer, data.OrderB)).Items); }
        finally { await release.Rows("auditEvents").InsertOneAsync(caseAudit); }
        await MissingAsync(release, "shipments", new("_id", data.ReceiptA.ShipmentId.Value),
            () => release.Invoices.ListForOrderAsync(buyer, data.OrderA));
        Assert.Equal(protectedBefore, await data.BusinessAsync());
    }

    private static async Task MissingAsync(InvoiceReleaseTestData data, string collection, BsonDocument filter, Func<Task> read) {
        var original = await data.Rows(collection).Find(filter).SingleAsync();
        await data.Rows(collection).DeleteOneAsync(filter);
        try {
            var error = await Record.ExceptionAsync(read);
            Assert.True(error is AuthorizationDeniedException or PersistenceConflictException or UnitToko.Modules.Contracts.DomainValidationException, error?.ToString());
        } finally { await data.Rows(collection).InsertOneAsync(original); }
    }

    private static async Task RequirePaymentFilteringBeforePaginationAsync(OrderTimelineGroupedTestData data, BsonDocument saved, long[] expected) {
        var release = data.Release; var buyer = release.Dispatch.Buyer;
        var head = await release.Rows(P.Heads).Find(new BsonDocument("_id", saved["declarationId"])).SingleAsync();
        var version = await release.Rows(P.Versions).Find(new BsonDocument("_id", saved["versionId"])).SingleAsync();
        var heads = new List<BsonDocument>(); var versions = new List<BsonDocument>();
        // These are deliberately invalid, inadmissible corruption rows, not genuine
        // declarations. They prove exclusion before the 26-row paging witness is read.
        // Every positive business row above was created through its actual owner.
        for (var index = 0; index < 26; index++) {
            var denied = head.DeepClone().AsBsonDocument; denied["_id"] = long.MaxValue - 1000 - index;
            var wrongScope = denied["scopes"][0].DeepClone().AsBsonDocument; wrongScope["createdBy"] = 811721L;
            denied["scopes"].AsBsonArray.Add(wrongScope); heads.Add(denied); // One matching scope does not authorize every scope.
            var unrelated = head.DeepClone().AsBsonDocument; unrelated["_id"] = long.MaxValue - 2000 - index;
            var wrongVersion = version.DeepClone().AsBsonDocument; wrongVersion["_id"] = long.MaxValue - 3000 - index;
            wrongVersion["declarationId"] = unrelated["_id"]; wrongVersion["requestKey"] = "invalid-other-order-" + index;
            wrongVersion["input"]["invoiceIds"] = new BsonArray { long.MaxValue - 4000 };
            unrelated["versionId"] = wrongVersion["_id"]; heads.Add(unrelated); versions.Add(wrongVersion);
        }
        await release.Rows(P.Versions).InsertManyAsync(versions, new InsertManyOptions { BypassDocumentValidation = true });
        await release.Rows(P.Heads).InsertManyAsync(heads, new InsertManyOptions { BypassDocumentValidation = true });
        try {
            var page = await data.Payments.ListForOrderAsync(buyer, data.OrderA);
            Assert.False(page.HasMore); Assert.Equal(expected, page.Items.Select(value => value.Id.Value).ToArray());
            // Direct current-owner reads must still refuse these same corrupt rows.
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payments.ReadAsync(buyer, heads[0]["_id"].AsInt64));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payments.ReadAsync(buyer, heads[1]["_id"].AsInt64));
        } finally {
            await release.Rows(P.Heads).DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(heads.Select(row => row["_id"])))));
            await release.Rows(P.Versions).DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(versions.Select(row => row["_id"])))));
        }
    }
}
