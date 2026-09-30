using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationDeadlineTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData("policy")] [InlineData("mfa")] [InlineData("session")] [InlineData("bank-person")]
    public async Task Expiry_after_financial_result_persistence_aborts_all_effects(string kind) {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var clock = data.Source.Read.Catalog.Clock; var start = clock.UtcNow;
        if (kind == "policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("expiresAt", start.AddSeconds(1).UtcDateTime));
        if (kind == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Actor.Session!.Id), Builders<BsonDocument>.Update.Set("idleExpiresAt", start.AddSeconds(1).UtcDateTime));
        if (kind == "bank-person") await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Source.Bank.Reviewer.UserId), Builders<BsonDocument>.Update.Set("expiresAt", start.AddSeconds(1).UtcDateTime).Inc("version", 1L));
        var input = data.Input(0, 1000m); var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        var selected = 0; var advanced = false; var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.TryGetValue("update", out var update) && update == "idempotencyRecords" && command.Command["updates"].AsBsonArray.Any(row =>
                    row["q"].AsBsonDocument.GetValue("operation", "") == B.Operation && row["u"].AsBsonDocument.TryGetValue("$set", out var set) && set.AsBsonDocument.Contains("result"))) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => { if (!advanced && command.RequestId == selected) { advanced = true; clock.UtcNow = start.AddSeconds(kind == "mfa" ? 301 : 2); } });
        };
        var error = await Record.ExceptionAsync(() => data.NewService(new MongoClient(settings)).AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, PaymentAllocationTests.Data.Meta("deadline")));
        Assert.True(advanced); Assert.NotNull(error);
        foreach (var name in new[] { B.Events, B.Receipts, B.Invoices }) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) Assert.False(await data.Rows(name).Find(new BsonDocument(field, B.Operation)).AnyAsync());
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
