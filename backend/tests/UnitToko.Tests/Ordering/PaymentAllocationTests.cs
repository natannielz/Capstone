using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Partial_allocation_and_original_key_recovery_move_suspense_to_receivable_without_minting_cash() {
        await using var data = await Data.CreateAsync(fixture);
        var beforeCash = new BsonArray((await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument()).ToListAsync()).Select(BankReceiptBson.Snapshot));
        var input = data.Input(0, 1000m); var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        Assert.Equal(4000m, preview.View.Receipt["amount"].AsDecimal); Assert.Equal(3000m, preview.Result.RemainingReceipt.Value);
        Assert.False(await data.Rows(B.Events).Find(new BsonDocument()).AnyAsync());
        var saved = await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, Data.Meta("allocate-first"));
        Assert.Equal(saved, await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, Data.Meta("allocate-first")));
        Assert.Equal(saved, await data.Service.RecoverAsync(data.Actor, "allocate-first"));
        var read = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]);
        Assert.Equal(1000m, read.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(1L, read.ReceiptBalance["version"].AsInt64);
        Assert.Equal(1000m, read.Invoices[0]["balance"]["paid"].AsDecimal);
        Assert.Equal(3, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", "billing.payment-allocation")).SingleAsync();
        Assert.Equal("unidentified_cash", journal["lines"][0]["mappingKey"].AsString); Assert.Equal(1000m, journal["lines"][0]["debit"].AsDecimal);
        Assert.Equal("invoice_ar", journal["lines"][1]["mappingKey"].AsString); Assert.Equal(1000m, journal["lines"][1]["credit"].AsDecimal);
        Assert.Equal(beforeCash, new BsonArray((await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument()).ToListAsync()).Select(BankReceiptBson.Snapshot)));
        var secondInput = data.Input(1, 500m); var second = await data.Service.PreviewAsync(data.Actor, data.VerificationId, secondInput);
        await data.Service.AllocateAsync(data.Actor, data.VerificationId, second.Binding, secondInput, Data.Meta("allocate-second"));
        var after = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]);
        Assert.Equal(1500m, after.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(1500m, after.Invoices[0]["balance"]["paid"].AsDecimal);
        Assert.Equal(saved, await data.Service.RecoverAsync(data.Actor, "allocate-first"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.AllocateAsync(data.Source.Reader, data.VerificationId, second.Binding, secondInput, Data.Meta("reader-denied")));
    }
    [WindowsStorageFact]
    public async Task Allocation_requires_current_independent_manual_reviewer_after_a_different_person_verifies_the_bank_receipt() {
        await using var data = await Data.CreateAsync(fixture, manual: true);
        var input = data.Input(0, 1000m); var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        var saved = await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, Data.Meta("manual-allocation"));
        Assert.Equal(saved, await data.Service.RecoverAsync(data.Actor, "manual-allocation"));
        Assert.Equal("manual", preview.View.Verification["review"]["kind"]);
        Assert.Equal(1, await data.Rows("paymentManualReviews").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(B.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(BankReceiptBson.Receipts).CountDocumentsAsync(new BsonDocument()));
        await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", 7480L), Builders<BsonDocument>.Update.Set("expiresAt", data.Source.Read.Catalog.Clock.UtcNow.UtcDateTime));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Actor, data.VerificationId, data.Input(1, 500m)));
        Assert.Equal(1, await data.Rows(B.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(3, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Concurrent_allocations_cannot_spend_the_same_balance_twice() {
        await using var data = await Data.CreateAsync(fixture); var input = data.Input(0, 1000m);
        var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async n => {
            try { await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, Data.Meta("race-" + n)); return true; }
            catch (Exception error) when (error is UnitToko.Platform.Mongo.PersistenceConflictException or UnitToko.Modules.Contracts.DomainValidationException) { return false; }
        }));
        Assert.Single(outcomes, success => success);
        Assert.Equal(1, await data.Rows(B.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(3, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var read = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]);
        Assert.Equal(1000m, read.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(1000m, read.Invoices[0]["balance"]["paid"].AsDecimal);
    }
    internal sealed class Data(PaymentVerificationTestData source) : IAsyncDisposable {
        internal PaymentVerificationTestData Source { get; } = source;
        internal ActorContext Actor = null!;
        internal long VerificationId;
        internal long InvoiceId => Source.Read.Payment.Issued.Id.Value;
        internal PaymentAllocationService Service => new(Source.Read.Database, new(Source.Read.Catalog.Client), new(Source.Read.Database), Source.Read.Catalog.Clock, Source.Service, Source.Read.Payment.Invoice.Service);
        internal PaymentAllocationService NewService(MongoClient client) {
            var verify = Source.NewService(client, out var invoices); var db = client.GetDatabase(Source.Read.Database.DatabaseNamespace.DatabaseName);
            return new(db, new(client), new(db), Source.Read.Catalog.Clock, verify, invoices);
        }
        internal IMongoCollection<BsonDocument> Rows(string name) => Source.Rows(name);
        internal PaymentAllocationInput Input(long version, decimal amount) => new(version, [new(new(InvoiceId), version, new(amount))]);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic explicitly reviewed invoice allocation", "synthetic-payment-allocation");
        internal static async Task<Data> CreateAsync(MongoFixture fixture, bool manual = false) {
            var data = new Data(await PaymentVerificationTestData.CreateAsync(fixture, manual ? "ocr_unavailable" : null));
            try {
                if (manual) {
                    await PaymentManualReviewTests.Policy(data.Source);
                    var reviewer = await data.Source.Read.Catalog.ActorAsync(7480, CatalogAuthoringTestData.Shop, Permissions.PaymentReviewManual, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
                    await PersonTestData.SeedDistinctPeopleAsync(data.Source.Read.Database, data.Source.Read.Catalog.Clock, reviewer.UserId);
                    var preview = await data.Source.Service.PreviewManualReviewAsync(reviewer, data.Source.Read.DeclarationId);
                    await data.Source.Service.RecordManualReviewAsync(reviewer, data.Source.Read.DeclarationId, 0, new(preview.Binding, true, PaymentManualReviewTests.Fields()), Meta("allocation-manual-review"));
                }
                data.VerificationId = (await data.Source.VerifyAsync(await data.Source.PreviewAsync()))["verificationId"].AsInt64;
                data.Actor = await data.Source.Read.Catalog.ActorAsync(7510, CatalogAuthoringTestData.Shop, Permissions.PaymentAllocate, Permissions.PaymentReviewRead,
                    Permissions.InvoiceRead, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead);
                await PersonTestData.SeedDistinctPeopleAsync(data.Source.Read.Database, data.Source.Read.Catalog.Clock, data.Actor.UserId);
                var receipt = await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", data.Source.ReceiptId)).SingleAsync();
                await data.Rows(B.Policies).InsertOneAsync(new BsonDocument { { "_id", 950L }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "scope", receipt["scope"] },
                    { "suspenseMapping", "unidentified_cash" }, { "arMapping", "invoice_ar" }, { "procedureReference", "synthetic-human-invoice-allocation" },
                    { "operatingOwnerReference", "synthetic-finance-owner" }, { "approvalReference", "synthetic-allocation-approval" }, { "active", true },
                    { "approvedAt", data.Source.Read.Catalog.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Source.Read.Catalog.Clock.UtcNow.AddDays(1).UtcDateTime } });
                return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
