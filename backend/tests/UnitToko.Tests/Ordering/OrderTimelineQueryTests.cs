using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Billing;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderTimelineQueryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Order_invoice_history_keeps_its_real_release_proof_scope_and_exclusive_cursor() {
        await using var data = await InvoiceReleaseTestData.CreateAsync(fixture);
        await data.DecideAsync(await data.ProposeAsync());
        var preview = await data.Invoices.PreviewAsync(data.Finance, data.Selection);
        var issued = await data.Invoices.IssueAsync(data.Finance, data.Selection, preview.Review, InvoiceReleaseTestData.Meta("timeline-issued"));
        var before = await data.ProtectedAsync();
        var page = await data.Invoices.ListForOrderAsync(data.Reader, data.Dispatch.OrderId);
        Assert.Equal(issued.Id, Assert.Single(page.Items).Id); Assert.False(page.HasMore);
        Assert.Empty((await data.Invoices.ListForOrderAsync(data.Reader, long.MaxValue)).Items);
        Assert.Empty((await data.Invoices.ListForOrderAsync(data.Reader, data.Dispatch.OrderId, issued.Id.Value)).Items);
        Assert.Empty((await data.Invoices.ListForOrderAsync(data.Outsider, data.Dispatch.OrderId)).Items);
        Assert.Equal(before, await data.ProtectedAsync());
        Assert.Equal(1, await data.Rows(InvoiceBson.Invoices).CountDocumentsAsync(new BsonDocument()));
        // The filtered path must still verify the owning release; matching an orderId is insufficient.
        await data.Rows(InvoiceReleaseBson.Bindings).DeleteOneAsync(new BsonDocument("_id", issued.Id.Value));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Invoices.ListForOrderAsync(data.Reader, data.Dispatch.OrderId));
    }
}
