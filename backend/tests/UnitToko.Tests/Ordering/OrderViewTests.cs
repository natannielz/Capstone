using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderViewTests(MongoFixture fixture)
{
    private static readonly OrderHistoryFilter Filter = new(new(new(10), new(11)), "oldest");
    private static OrderViewService Service(CatalogAuthoringTestData data) => new(data.Database, new(data.Client), new(data.Database), data.Clock);
    private static async Task<CatalogAuthoringTestData> Data(MongoFixture fixture) {
        Assert.True(CollectionMigrations.CurrentVersion >= 112, "Compose the real accepted 110/111 predecessors and register112 before exercising this candidate.");
        var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        await data.Base.AddGrantAsync(data.Buyer, 721120, CatalogAuthoringTestData.Audience, [Permissions.OrderRead]); return data;
    }
    [Fact]
    public void Query_cursors_are_exact_order_context_bound_and_never_carry_account_authority() {
        var descending = Filter with {Sort = "newest"}; var cursor = Filter.Cursor(9007199254740993);
        Assert.Equal(9007199254740993, Filter.ParseCursor(cursor)); Assert.Null(Filter.ParseCursor(null));
        foreach (var bad in new[] {cursor.Replace("oldest", "newest"), cursor.Replace(":10:11:", ":10:12:"), cursor + "\n", "v1:oldest:10:11:0:01", "v1:oldest:10:11:0:9223372036854775808"})
            Assert.Throws<DomainValidationException>(() => Filter.ParseCursor(bad));
        Assert.Throws<DomainValidationException>(() => descending.ParseCursor(cursor));
        Assert.Throws<DomainValidationException>(() => (Filter with {Sort = "price"}).Validate());
        Assert.Throws<DomainValidationException>(() => OrderHistoryFilter.Name(" Nama "));
    }
    [Fact]
    public async Task Save_rename_remove_and_original_recovery_preserve_exact_audited_versions_without_business_effects() {
        await using var data = await Data(fixture); var service = Service(data); var actor = data.Buyer;
        var create = await service.ChangeAsync(actor, "create", null, 0, "Pesanan cabang", Filter, CatalogPriceTestData.Meta("view-create")); var id = create["viewId"].AsInt64;
        var rename = await service.ChangeAsync(actor, "rename", id, 1, "Nama baru", null, CatalogPriceTestData.Meta("view-rename"));
        Assert.Equal("Nama baru", (await service.ReadAsync(actor, id)).Name); Assert.Equal(Filter, (await service.ReadAsync(actor, id)).Filter);
        var remove = await service.ChangeAsync(actor, "remove", id, 2, null, null, CatalogPriceTestData.Meta("view-remove"));
        Assert.Empty((await service.ListAsync(actor)).Items); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(actor, id));
        foreach (var (key, original) in new[] {("view-create", create), ("view-rename", rename), ("view-remove", remove)}) Assert.Equal(original, await service.RecoverAsync(actor, key));
        Assert.Equal(create, await service.ChangeAsync(actor, "create", null, 0, "Pesanan cabang", Filter, CatalogPriceTestData.Meta("view-create")));
        Assert.Equal(rename, await service.ChangeAsync(actor, "rename", id, 1, "Nama baru", null, CatalogPriceTestData.Meta("view-rename")));
        Assert.Equal(remove, await service.ChangeAsync(actor, "remove", id, 2, null, null, CatalogPriceTestData.Meta("view-remove")));
        Assert.Equal(3, await data.CountAsync(OrderViewBson.Versions));
        Assert.Equal(3, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", OrderViewBson.Operation)));
        Assert.Equal(3, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("operation", OrderViewBson.Operation)));
        foreach (var table in new[] {"orders", "orderDrafts", "reservations", "movements", "journalEntries", "paymentAllocations"}) Assert.Equal(0, await data.CountAsync(table));
    }
    [Fact]
    public async Task Concurrent_original_keys_and_stale_renames_preserve_one_winner_and_private_account_scope() {
        await using var data = await Data(fixture); var service = Service(data);
        var writes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.ChangeAsync(data.Buyer, "create", null, 0, "Asli", Filter, CatalogPriceTestData.Meta("same-view"))));
        Assert.Equal(writes[0], writes[1]); var id = writes[0]["viewId"].AsInt64;
        var attempts = await Task.WhenAll(new[] {"Satu", "Dua"}.Select(name => Record.ExceptionAsync(() => service.ChangeAsync(data.Buyer, "rename", id, 1, name, null, CatalogPriceTestData.Meta("rename-" + name)))));
        Assert.Single(attempts, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(attempts, error => error is not null));
        var other = await data.ActorAsync(721121, CatalogAuthoringTestData.Audience, Permissions.OrderRead);
        Assert.Empty((await service.ListAsync(other)).Items); Assert.Null(await service.RecoverAsync(other, "same-view"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(other, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ChangeAsync(other, "remove", id, 2, null, null, CatalogPriceTestData.Meta("other-remove")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ChangeAsync(data.Buyer, "create", null, 0, "Berbeda", Filter, CatalogPriceTestData.Meta("same-view")));
        Assert.Equal(2, await data.CountAsync(OrderViewBson.Versions));
    }
    [Theory]
    [InlineData("receipt")] [InlineData("audit")] [InlineData("owner")] [InlineData("filter")] [InlineData("head")]
    public async Task Missing_or_changed_original_provenance_never_becomes_a_retry_or_latest_result(string fault) {
        await using var data = await Data(fixture); var service = Service(data);
        var first = await service.ChangeAsync(data.Buyer, "create", null, 0, "Asli", Filter, CatalogPriceTestData.Meta("original")); var id = first["viewId"].AsInt64;
        await service.ChangeAsync(data.Buyer, "rename", id, 1, "Terbaru", null, CatalogPriceTestData.Meta("latest"));
        if (fault == "receipt") await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { {"operation", OrderViewBson.Operation}, {"key", "original"} });
        if (fault == "audit") await data.Rows("auditEvents").DeleteOneAsync(new BsonDocument { {"operation", OrderViewBson.Operation}, {"requestKey", "original"} });
        if (fault == "owner") await data.Rows(OrderViewBson.Versions).DeleteOneAsync(new BsonDocument("_id", first["versionId"]));
        if (fault == "filter") await data.Rows(OrderViewBson.Versions).UpdateOneAsync(new BsonDocument("_id", first["versionId"]), Builders<BsonDocument>.Update.Set("filter.sort", "newest"));
        if (fault == "head") await data.Rows(OrderViewBson.Heads).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("name", "Injected"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.Buyer, "original"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Buyer, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ChangeAsync(data.Buyer, "create", null, 0, "Asli", Filter, CatalogPriceTestData.Meta("original")));
    }
    [Fact]
    public async Task Revoked_context_cannot_be_loaded_replayed_recovered_or_widened_to_all_orders() {
        await using var data = await Data(fixture); var service = Service(data);
        var first = await service.ChangeAsync(data.Buyer, "create", null, 0, "Cabang", Filter, CatalogPriceTestData.Meta("revoked-view"));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 721120L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(data.Buyer));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(data.Buyer, first["viewId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(data.Buyer, "revoked-view"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ChangeAsync(data.Buyer, "create", null, 0, "Cabang", Filter, CatalogPriceTestData.Meta("revoked-view")));
    }
    [Fact]
    public async Task Saved_view_pages_cross_25_boundary_without_duplicates_or_account_leaks() {
        await using var data = await Data(fixture); var service = Service(data); var created = new List<long>();
        for (var i = 0; i < 26; i++) created.Add((await service.ChangeAsync(data.Buyer, "create", null, 0, "Filter " + i, Filter, CatalogPriceTestData.Meta("page-" + i)))["viewId"].AsInt64);
        var first = await service.ListAsync(data.Buyer); Assert.Equal(created.OrderDescending().Take(25), first.Items.Select(item => item.Id.Value));
        var second = await service.ListAsync(data.Buyer, first.NextId); Assert.Equal(created[0], Assert.Single(second.Items).Id.Value); Assert.Null(second.NextId);
    }
    [Fact]
    public async Task Both_order_directions_use_original_owner_graph_and_query_bound_exclusive_cursor() {
        // Existing isolated admission fixture exercises the actual order writer/reader, without a production-policy claim.
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, stock: 100); var ids = new List<long>();
        for (var i = 0; i < 26; i++) {var preview = await data.Service.PreviewAsync(data.Buyer, data.Input); ids.Add((await data.Service.SubmitAsync(data.Buyer, data.Input, preview.Review, CatalogPriceTestData.Meta("filter-order-" + i)))["orderId"].AsInt64);}
        foreach (var sort in new[] {"newest", "oldest"}) {
            var query = new OrderHistoryFilter(data.Input.Context, sort); var expected = sort == "newest" ? ids.OrderDescending().ToArray() : ids.Order().ToArray();
            var first = await data.Service.ListOwnFilteredAsync(data.Buyer, query); Assert.True(first.HasMore); Assert.Equal(expected.Take(25), first.Items.Select(item => item.Id.Value));
            var second = await data.Service.ListOwnFilteredAsync(data.Buyer, query, first.NextCursor); Assert.False(second.HasMore); Assert.Null(second.NextCursor); Assert.Equal(expected[25], Assert.Single(second.Items).Id.Value);
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ListOwnFilteredAsync(data.Buyer, query with {Context = null}, first.NextCursor));
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ListOwnFilteredAsync(data.Buyer, query, query.Cursor(long.MaxValue)));
        }
        var old = await data.Service.ListOwnAsync(data.Buyer); Assert.Equal(ids.OrderDescending().Take(25), old.Items.Select(item => item.Id.Value));
        var other = await data.Prices.Catalog.ActorAsync(721122, CatalogAuthoringTestData.Audience, Permissions.OrderRead);
        Assert.Empty((await data.Service.ListOwnFilteredAsync(other, new())).Items);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ListOwnFilteredAsync(other, Filter, Filter.Cursor(ids[0])));
    }
}
