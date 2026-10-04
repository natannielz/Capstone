using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Ordering;

// Same database, immutable approved book and independently confirmed credit as
// the supplier return. Existing local order/invoice policy inputs stay synthetic;
// catalog, prices, delivery, invoice, image review and verification use real owners.
[SupportedOSPlatform("windows")]
internal static class BankReceiptPurposeCustomerData
{
    internal static async Task<PaymentVerificationTestData> AttachAsync(MongoFixture fixture, ProcurementAdvanceReturnTestData supplier)
    {
        var receipt = await supplier.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", supplier.ReceiptId)).SingleAsync();
        Assert.Equal(4000m, receipt["amount"].AsDecimal);
        var chartBefore = await supplier.Rows("accountingChartVersions").Find(new BsonDocument("_id", supplier.Payables.Valuation.Book["chartId"])).SingleAsync();
        var catalog = await CatalogAuthoringTestData.AttachAsync(supplier.Base, fixture.Client);
        var prices = await CatalogPriceTestData.CreateAsync(fixture, attachedCatalog: catalog);
        var order = await OrderSubmissionTestData.CreateAsync(fixture, attachedPrices: prices);
        var dispatch = await ShipmentDispatchTestData.CreateAsync(fixture, attachedOrder: order);
        var invoice = await InvoiceData.CreateAsync(fixture, dispatch, preserveApprovedAccounting: true);
        var payment = await PaymentDraftData.CreateAsync(fixture, invoice);
        var read = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture, payment);
        var customer = await PaymentVerificationTestData.CreateAsync(fixture, attachedRead: read,
            attachedBank: supplier.Payables.Bank, attachedReceiptId: supplier.ReceiptId);
        Assert.Equal(chartBefore, await supplier.Rows("accountingChartVersions").Find(new BsonDocument("_id", chartBefore["_id"])).SingleAsync());
        Assert.False(await supplier.Rows(PaymentVerificationBson.Parties).Find(new BsonDocument("_id", supplier.ReceiptId)).AnyAsync());
        return customer;
    }
}
