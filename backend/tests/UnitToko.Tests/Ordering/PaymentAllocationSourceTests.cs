using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationSourceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Owning_consumer_returns_the_actual_matched_cash_and_invoice_without_creating_an_allocation() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var saved = await data.VerifyAsync(await data.PreviewAsync());
        var store = new AuthorizationStore(data.Read.Database, new(data.Read.Catalog.Client), data.Read.Catalog.Clock);
        await store.ReadAsync(data.Reviewer, async (authority, tx, token) => {
            var source = await data.Service.AllocationSourceAsync(data.Reviewer, authority, tx, saved["verificationId"].AsInt64, true, token);
            Assert.Equal(data.ReceiptId, source.Bank.Receipt["_id"].AsInt64); Assert.Equal(4000m, source.Bank.Receipt["amount"].AsDecimal);
            var invoice = await data.Read.Payment.Invoice.Service.AllocationSourceAsync(data.Reviewer, authority, tx, data.Read.Payment.Issued.Id.Value, token);
            Assert.Equal("invoice_ar", invoice["arMapping"].AsString); Assert.Equal(data.Read.Payment.Issued.Detail.Total.Value, invoice["total"].AsDecimal);
            source.RequireCurrent(); return true;
        }, CancellationToken.None);
        Assert.False(await data.Rows("paymentAllocations").Find(new BsonDocument()).AnyAsync()); Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task A_new_financial_consumer_requires_current_original_bank_approval_and_its_final_deadline() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var saved = await data.VerifyAsync(await data.PreviewAsync());
        var start = data.Read.Catalog.Clock.UtcNow;
        await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Bank.Reviewer.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", start.AddSeconds(1).UtcDateTime).Inc("version", 1L));
        var store = new AuthorizationStore(data.Read.Database, new(data.Read.Catalog.Client), data.Read.Catalog.Clock);
        var reached = false;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => store.ReadAsync(data.Reviewer, async (authority, tx, token) => {
            var source = await data.Service.AllocationSourceAsync(data.Reviewer, authority, tx, saved["verificationId"].AsInt64, true, token);
            reached = true; data.Read.Catalog.Clock.UtcNow = start.AddSeconds(2); source.RequireCurrent(); return true;
        }, CancellationToken.None));
        Assert.True(reached);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => store.ReadAsync(data.Reviewer, (authority, tx, token) =>
            data.Service.AllocationSourceAsync(data.Reviewer, authority, tx, saved["verificationId"].AsInt64, true, token), CancellationToken.None));
        var history = await store.ReadAsync(data.Reader, (authority, tx, token) =>
            data.Service.AllocationSourceAsync(data.Reader, authority, tx, saved["verificationId"].AsInt64, false, token), CancellationToken.None);
        Assert.Equal(saved["verificationId"], history.Decision["_id"]);
        Assert.False(await data.Rows("paymentAllocations").Find(new BsonDocument()).AnyAsync()); Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
