using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderReadTests(MongoFixture fixture)
{
    [Fact]
    public async Task Original_order_remains_readable_with_read_grant_after_submission_and_catalog_access_end() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var id = await OrderDecisionTests.Submit(data);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        Assert.True((await data.Rows("catalogEntitlements").UpdateManyAsync(new BsonDocument("userId", data.Buyer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"))).ModifiedCount > 0);
        var order = await data.Service.ReadOwnAsync(data.Buyer, id);
        Assert.Equal(25500.50m, order.Total.Value); Assert.Equal(2, Assert.Single(order.Catalog.Lines).Packages);
        Assert.Equal(id, Assert.Single((await data.Service.ListOwnAsync(data.Buyer)).Items).Id.Value);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Buyer, data.Input));
    }
    [Fact]
    public async Task Read_permission_is_separate_and_other_buyers_cannot_find_or_read_the_order() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var id = await OrderDecisionTests.Submit(data);
        var other = await data.Prices.Catalog.ActorAsync(8700, CatalogAuthoringTestData.Audience, Permissions.OrderRead);
        Assert.Empty((await data.Service.ListOwnAsync(other)).Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadOwnAsync(other, id));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6250L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadOwnAsync(data.Buyer, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ListOwnAsync(data.Buyer));
        // Still permitted to prepare a new order; read access was not inferred from submission permission.
        Assert.NotNull(await data.Service.PreviewAsync(data.Buyer, data.Input));
    }
    [Fact]
    public async Task Order_pages_use_descending_ids_and_keep_branch_context_and_current_status() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var first = await OrderDecisionTests.Submit(data);
        var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var second = (await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("another-order")))["orderId"].AsInt64;
        var page = await data.Service.ListOwnAsync(data.Buyer);
        Assert.Equal(new[] { second, first }, page.Items.Select(order => order.Id.Value)); Assert.False(page.HasMore);
        Assert.Equal(first, Assert.Single((await data.Service.ListOwnAsync(data.Buyer, beforeId: second)).Items).Id.Value);
        Assert.Empty((await data.Service.ListOwnAsync(data.Buyer, beforeId: first)).Items);
        Assert.All(page.Items, order => Assert.Equal(data.Input.Context, order.Input.Context));
    }
    [Theory] [InlineData("branch")] [InlineData("membership")] [InlineData("session")]
    public async Task Inactive_read_authority_cannot_expose_saved_commercial_or_delivery_details(string fault) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var id = await OrderDecisionTests.Submit(data);
        if (fault == "branch") await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 11L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        if (fault == "membership") await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", 6250L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        if (fault == "session") data.Prices.Catalog.Clock.UtcNow = data.Prices.Catalog.Clock.UtcNow.AddHours(9);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadOwnAsync(data.Buyer, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ListOwnAsync(data.Buyer));
    }
    [Theory]
    [InlineData(false, "grant")] [InlineData(true, "grant")]
    [InlineData(false, "membership")] [InlineData(true, "membership")]
    [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "deadline")] [InlineData(true, "deadline")]
    public async Task Revocation_or_session_expiry_during_read_cannot_escape_the_final_fence(bool list, string change) {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); var id = await OrderDecisionTests.Submit(data);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var reads = 0; var observed = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.Command.GetValue("find", BsonNull.Value) == "orders" && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Order read race did not complete");
                }
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        // The public production constructor can read accepted orders without any admission adapter.
        var reader = new OrderSubmissionService(database, new(client), new(database), data.Prices.Catalog.Clock);
        var pending = Task.Run(async () => { if (list) await reader.ListOwnAsync(data.Buyer); else await reader.ReadOwnAsync(data.Buyer, id); });
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6250L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "membership") await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", 6250L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await data.Prices.Catalog.Sessions.RevokeAsync(data.Prices.Catalog.Issued[data.Buyer.UserId].OpaqueToken, "Synthetic read revocation");
            if (change == "deadline") data.Prices.Catalog.Clock.UtcNow = data.Prices.Catalog.Clock.UtcNow.AddHours(9);
        } finally { release.Set(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, observed);
    }
    [Fact]
    public async Task Accepted_orders_page_at_the_server_limit_without_duplicate_or_missing_orders() {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, stock: 100);
        var ids = new List<long>();
        for (var index = 0; index < 26; index++) {
            var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
            ids.Add((await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, CatalogPriceTestData.Meta("page-order-" + index)))["orderId"].AsInt64);
        }
        var first = await data.Service.ListOwnAsync(data.Buyer);
        Assert.True(first.HasMore); Assert.Equal(25, first.Items.Length);
        Assert.Equal(ids.AsEnumerable().Reverse().Take(25), first.Items.Select(order => order.Id.Value));
        var last = await data.Service.ListOwnAsync(data.Buyer, beforeId: first.Items[^1].Id.Value);
        Assert.False(last.HasMore); Assert.Equal(ids[0], Assert.Single(last.Items).Id.Value);
    }
}
