using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentVerificationRaceTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData("policy")] [InlineData("mfa")] [InlineData("session")] [InlineData("evidence")]
    public async Task Expiry_after_receipt_insertion_rolls_back_the_entire_verification(string kind) {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var start = data.Read.Catalog.Clock.UtcNow;
        if (kind == "policy") await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("expiresAt", start.AddSeconds(1).UtcDateTime));
        if (kind == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Reviewer.Session!.Id), Builders<BsonDocument>.Update.Set("idleExpiresAt", start.AddSeconds(1).UtcDateTime));
        var preview = await data.PreviewAsync(); var selected = 0; var advanced = false;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI"));
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.TryGetValue("insert", out var insert) && insert == "idempotencyRecords" &&
                    command.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("operation", "") == B.Operation)) selected = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (!advanced && command.RequestId == selected) { advanced = true; data.Read.Catalog.Clock.UtcNow = start.AddSeconds(kind == "mfa" ? 301 : kind == "evidence" ? 61 : 2); }
            });
        };
        var service = data.NewService(new MongoClient(settings));
        var failure = await Record.ExceptionAsync(() => service.VerifyAsync(data.Reviewer, data.Read.DeclarationId, data.ReceiptId,
            PaymentVerificationTestData.Input(preview), PaymentVerificationTestData.Meta("expire-final")));
        Assert.True(advanced); Assert.NotNull(failure);
        Assert.False(await data.Rows(B.Decisions).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(B.Parties).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", B.Operation)).AnyAsync());
        Assert.False(await data.Rows("auditEvents").Find(new BsonDocument("kind", B.Operation)).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", B.Operation)).AnyAsync());
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Concurrent_correction_and_verification_cannot_both_commit_the_old_review() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var preview = await data.PreviewAsync();
        var fields = data.Corrections.Fields.Select(field => field.Field == "recipient" ? field with { Value = "Concurrent reviewed recipient" } : field).ToImmutableArray();
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.VerifyAsync(preview)),
            Record.ExceptionAsync(() => data.Read.Service.CorrectReviewAsync(data.Reviewer, data.Read.DeclarationId, 1,
                data.Corrections.Binding, fields, PaymentVerificationTestData.Meta("concurrent-correction"))));
        Assert.Single(results, error => error is null);
        var failure = Assert.IsType<UnitToko.Platform.Mongo.PersistenceConflictException>(Assert.Single(results, error => error is not null));
        Assert.Contains(failure.Code, new[] { "version_conflict", "payment_verification_changed", "payment_already_verified" });
        if (results[0] is null) {
            Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(1, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument()));
            Assert.NotNull(await data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
        } else {
            Assert.False(await data.Rows(B.Decisions).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(B.Parties).Find(new BsonDocument()).AnyAsync());
            Assert.Equal(2, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument()));
            Assert.NotNull(await data.Read.Service.RecoverReviewCorrectionAsync(data.Reviewer, data.Read.DeclarationId, "concurrent-correction"));
        }
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
