using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonApprovalStoreTests(MongoFixture fixture)
{
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static AuthorizationCommandMetadata Metadata => new("person-payment", "Review synthetic payment", "person-store-test");

    [Fact]
    public async Task Different_accounts_without_verified_people_cannot_approve()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => VerifyAsync(data, actor));
        Assert.Equal("independent_person_required", denied.Code);
        await UnchangedAsync(data);
    }

    [Fact]
    public async Task Reviewed_equivalent_accounts_cannot_approve_their_own_record()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, true);
        await MergeAsync(data);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => VerifyAsync(data, actor));
        Assert.Equal("independent_person_required", denied.Code);
        await UnchangedAsync(data);
    }

    [Fact]
    public async Task Retry_uses_original_participants_and_current_person_authority_without_repeating_write()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, true);
        var first = await VerifyAsync(data, actor);
        Assert.Equal(first, await VerifyAsync(data, actor));
        Assert.Equal(2L, (await PaymentAsync(data))["version"].AsInt64);
        var record = await data.Database.GetCollection<BsonDocument>("idempotencyRecords")
            .Find(new BsonDocument("key", Metadata.IdempotencyKey)).SingleAsync();
        Assert.True(record.Contains("personApproval"));
        var safe = record["personApproval"].ToJson();
        Assert.DoesNotContain("sourceSubjectKey", safe);
        Assert.DoesNotContain("sourceReference", safe);
        Assert.DoesNotContain("sourceDigest", safe);
        Assert.False(first.Contains("personApproval"));

        await MergeAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => VerifyAsync(data, actor));
        Assert.Equal(2L, (await PaymentAsync(data))["version"].AsInt64);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("extra-field")]
    [InlineData("unsupported-schema")]
    [InlineData("int32-participant")]
    [InlineData("missing-participant")]
    public async Task Receipt_without_exact_server_owned_person_evidence_fails_closed(string invalid)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, true);
        await VerifyAsync(data, actor);
        var records = data.Database.GetCollection<BsonDocument>("idempotencyRecords");
        var filter = new BsonDocument("key", Metadata.IdempotencyKey);
        var row = await records.Find(filter).SingleAsync();
        if (invalid == "missing") row.Remove("personApproval");
        else if (invalid == "wrong-type") row["personApproval"] = "untrusted";
        else if (invalid == "unsupported-schema") row["personApproval"]["schemaVersion"] = 2;
        else if (invalid == "int32-participant") row["personApproval"]["approval"]["recordedBy"] = 2000;
        else if (invalid == "missing-participant") row["personApproval"]["approval"].AsBsonDocument.Remove("recordedBy");
        else row["personApproval"].AsBsonDocument["clientOverride"] = true;
        await records.ReplaceOneAsync(filter, row);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => VerifyAsync(data, actor));
        Assert.Equal("person_receipt_invalid", denied.Code);
        Assert.Equal(2L, (await PaymentAsync(data))["version"].AsInt64);
    }

    [Fact]
    public async Task Person_expiry_after_business_write_rolls_back_write_and_receipt()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, true);
        await data.Database.GetCollection<BsonDocument>("personAccountBindings").UpdateOneAsync(
            new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => VerifyAsync(data, actor,
            afterWrite: () => data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(2)));
        await UnchangedAsync(data);
    }

    [Fact]
    public async Task Grant_expiry_after_business_write_rolls_back_write_and_receipt()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, true);
        await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(
            new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => VerifyAsync(data, actor,
            afterWrite: () => data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(2)));
        await UnchangedAsync(data);
    }

    [Fact]
    public async Task Late_equivalence_change_retries_stale_snapshot_then_denies_before_business_write()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var pending = VerifyAsync(data, actor, afterLoad: async () =>
        {
            if (Interlocked.Increment(ref reads) == 1) { entered.SetResult(); await resume.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await MergeAsync(data); }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.True(reads >= 2, "Changed group authority must conflict and force a fresh resolution.");
        await UnchangedAsync(data);
    }

    [Theory]
    [InlineData(Permissions.PaymentRecord)]
    [InlineData(Permissions.RefundApprove)]
    public async Task Receipt_cannot_be_replayed_using_a_different_permission(string permission)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data, true);
        await VerifyAsync(data, actor);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => VerifyAsync(data, actor, permission: permission));
        Assert.Equal("person_receipt_invalid", denied.Code);
    }

    [Fact]
    public async Task Ordinary_permission_requires_no_person_backfill()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await PrepareAsync(data);
        Assert.Equal(2L, (await VerifyAsync(data, actor, permission: Permissions.PaymentRecord))["version"].AsInt64);
    }

    private static async Task<ActorContext> PrepareAsync(StoreTestData data, bool withPeople = false)
    {
        var actor = await data.AddActorAsync(1000, Shop, Permissions.PaymentVerify, Permissions.PaymentRecord, Permissions.RefundApprove);
        await data.AddActorAsync(2000, Shop, Permissions.PaymentRecord);
        await data.Database.GetCollection<BsonDocument>("payments").InsertOneAsync(new BsonDocument
        {
            { "_id", 1L }, { "schemaVersion", 1 }, { "version", 1L }, { "recordedBy", 2000L },
            { "fulfillmentOrganizationId", 100L }, { "shopId", 101L }, { "channelId", 103L }
        });
        if (withPeople) await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, actor.UserId, 2000);
        return actor;
    }

    private static Task<BsonDocument> VerifyAsync(StoreTestData data, ActorContext actor,
        Action? afterWrite = null, Func<Task>? afterLoad = null, string permission = Permissions.PaymentVerify)
    {
        var payments = data.Database.GetCollection<BsonDocument>("payments");
        return data.Store.ExecuteAuthorizedAsync(actor, "payment.verify", permission, Metadata, new { id = 1L, expectedVersion = 1L },
            async (session, token) =>
            {
                var row = await payments.Find(session, new BsonDocument("_id", 1L)).SingleAsync(token);
                if (afterLoad is not null) await afterLoad();
                return new(AuthorizationBson.Resource(row), new(row["version"].AsInt64, 1, RecordedBy: row["recordedBy"].AsInt64));
            }, async (session, token) =>
            {
                await MongoUnitOfWork.RequireVersionAsync(payments, session, new BsonDocument("_id", 1L), 1,
                    Builders<BsonDocument>.Update.Set("verified", true), token);
                afterWrite?.Invoke();
                return new BsonDocument("version", 2L);
            });
    }

    private async Task MergeAsync(StoreTestData data) => await new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (session, token) =>
    {
        // Synthetic implementation of the documented shared-head protocol; no governed production writer is implied.
        await data.Database.GetCollection<BsonDocument>("personRecords").UpdateOneAsync(session,
            new BsonDocument("_id", PersonTestData.PersonId(2000)),
            Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(1000)).Inc("version", 1L), cancellationToken: token);
        await data.Database.GetCollection<BsonDocument>("personEquivalenceGroups").UpdateOneAsync(session,
            new BsonDocument("_id", PersonTestData.GroupId(1000)), Builders<BsonDocument>.Update
                .Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(1000), PersonTestData.PersonId(2000) })
                .Set("memberUserIds", new BsonArray { 1000L, 2000L }).Inc("version", 1L), cancellationToken: token);
        await data.Database.GetCollection<BsonDocument>("personEquivalenceGroups").UpdateOneAsync(session,
            new BsonDocument("_id", PersonTestData.GroupId(2000)),
            Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: token);
        return true;
    });

    private static Task<BsonDocument> PaymentAsync(StoreTestData data) =>
        data.Database.GetCollection<BsonDocument>("payments").Find(new BsonDocument("_id", 1L)).SingleAsync();

    private static async Task UnchangedAsync(StoreTestData data)
    {
        Assert.Equal(1L, (await PaymentAsync(data))["version"].AsInt64);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("idempotencyRecords").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
