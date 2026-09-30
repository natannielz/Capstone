using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationIntegrityTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData("head")] [InlineData("event")] [InlineData("journal-only")] [InlineData("audit")] [InlineData("receipt")]
    public async Task Damaged_history_cannot_recover_or_release_money_for_reallocation(string fault) {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var input = data.Input(0, 1000m);
        var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, PaymentAllocationTests.Data.Meta("original"));
        if (fault is "head" or "journal-only") await data.Rows(B.Receipts).DeleteManyAsync(new BsonDocument());
        if (fault is "event" or "journal-only") await data.Rows(B.Events).DeleteManyAsync(new BsonDocument());
        if (fault is "audit" or "journal-only") await data.Rows("auditEvents").DeleteManyAsync(new BsonDocument("kind", B.Operation));
        if (fault is "receipt" or "journal-only") await data.Rows("idempotencyRecords").DeleteManyAsync(new BsonDocument("operation", B.Operation));
        if (fault == "journal-only") {
            await data.Rows(B.Invoices).DeleteManyAsync(new BsonDocument());
            await data.Rows("outboxEvents").DeleteManyAsync(new BsonDocument("kind", B.Operation));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Actor, data.VerificationId, [data.InvoiceId]));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Actor, data.VerificationId, data.Input(0, 500m)));
        if (fault != "journal-only") await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Actor, "original"));
        // An entirely missing operation reference may be unknown, but surviving accounting always blocks new spending.
        Assert.Equal(3, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageTheory]
    [InlineData("period")] [InlineData("audit")] [InlineData("outbox")] [InlineData("policy")] [InlineData("suspense-remapped")] [InlineData("ar-remapped")]
    public async Task Failed_allocation_rolls_back_balances_event_accounting_and_operation_receipt(string fault) {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var input = data.Input(0, 1000m);
        var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        if (fault == "period") await data.Rows("accountingBooks").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("closedThrough", "2026-09"));
        if (fault == "policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false));
        if (fault is "suspense-remapped" or "ar-remapped") {
            var chart = await data.Rows("accountingChartVersions").Find(new BsonDocument("_id", 20L)).SingleAsync();
            chart["mappings"].AsBsonArray.Single(item => item["mappingKey"] == (fault == "suspense-remapped" ? "unidentified_cash" : "invoice_ar"))["accountId"] = 991L;
            await data.Rows("accountingChartVersions").ReplaceOneAsync(new BsonDocument("_id", 20L), chart);
        }
        if (fault is "audit" or "outbox") await data.Source.Read.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", fault == "audit" ? "auditEvents" : "outboxEvents" },
            { "validator", new BsonDocument("kind", new BsonDocument("$ne", B.Operation)) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var error = await Record.ExceptionAsync(() => data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, PaymentAllocationTests.Data.Meta("rollback")));
        Assert.NotNull(error);
        if (fault is "suspense-remapped" or "ar-remapped") Assert.Equal("payment_allocation_accounting_mapping_changed", Assert.IsType<UnitToko.Platform.Mongo.PersistenceConflictException>(error).Code);
        foreach (var name in new[] { B.Events, B.Receipts, B.Invoices }) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) Assert.False(await data.Rows(name).Find(new BsonDocument(field, B.Operation)).AnyAsync());
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
