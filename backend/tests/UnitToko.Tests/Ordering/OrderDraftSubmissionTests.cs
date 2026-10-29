using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;

/// <summary>F-030: a draft that has become an order is never the keranjang again. The next keranjang is a fresh draft and checks
/// out as a new order, while replaying the same checkout (a lost answer) still returns the original order.</summary>
[Collection("Mongo")]
public sealed class OrderDraftSubmissionTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Meta(string key) => CatalogPriceTestData.Meta(key);
    /// <summary>Records holding an ImmutableArray compare it by reference, so compare the draft's content field by field.</summary>
    private static void SameInput(OrderDraftInput expected, OrderDraftInput actual) {
        Assert.Equal(expected.Context, actual.Context); Assert.Equal(expected.Delivery, actual.Delivery);
        Assert.Equal(expected.PurchaseReference, actual.PurchaseReference); Assert.Equal(expected.Selections.AsEnumerable(), actual.Selections.AsEnumerable());
    }

    [Fact]
    public async Task A_draft_that_became_an_order_leaves_the_keranjang_and_the_next_keranjang_is_a_new_order()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var drafts = new OrderDraftService(data.Database, new(fixture.Client), new(data.Database), data.Prices.Catalog.Clock);
        var firstDraft = (await drafts.SaveAsync(data.Buyer, null, 0, data.Input, Meta("cart-first")))["draftId"].AsInt64;
        Assert.Equal(firstDraft, Assert.Single((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items).Id.Value);

        var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var first = await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, Meta("checkout-first"), firstDraft);

        // The submitted draft is no longer the keranjang (nor any listed draft), but the checkout that placed it can still read it.
        Assert.Empty((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        Assert.Equal(firstDraft, (await drafts.ReadAsync(data.Buyer, firstDraft)).Id.Value);
        // It cannot be edited back into a keranjang.
        Assert.Equal("order_draft_submitted", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            drafts.SaveAsync(data.Buyer, firstDraft, 1, data.Input with { PurchaseReference = "PO berikutnya" }, Meta("cart-edit-submitted")))).Code);
        // Replaying the same checkout (its answer was lost) returns the same order and writes nothing new.
        Assert.Equal(first, await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, Meta("checkout-first"), firstDraft));
        Assert.Equal(first, await data.Service.RecoverAsync(data.Buyer, "checkout-first"));
        // A different key for the same draft's packages never places them twice.
        Assert.Equal("order_draft_submitted", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, Meta("checkout-first-again"), firstDraft))).Code);
        Assert.Equal(1, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));

        // The next add-to-cart starts a fresh draft, which is the keranjang and checks out as a new order.
        var nextDraft = (await drafts.SaveAsync(data.Buyer, null, 0, data.Input, Meta("cart-next")))["draftId"].AsInt64;
        Assert.NotEqual(firstDraft, nextDraft);
        Assert.Equal(nextDraft, Assert.Single((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items).Id.Value);
        var nextReview = await data.Service.PreviewAsync(data.Buyer, data.Input);
        var second = await data.Service.SubmitAsync(data.Buyer, data.Input, nextReview.Review, Meta("checkout-next"), nextDraft);
        Assert.NotEqual(first["orderId"], second["orderId"]);
        Assert.Equal(2, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
        Assert.Empty((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        var links = await data.Rows("orderDraftSubmissions").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(new[] { firstDraft, nextDraft }, links.Select(row => row["draftId"].AsInt64));
        Assert.Equal(new[] { first["orderId"].AsInt64, second["orderId"].AsInt64 }, links.Select(row => row["_id"].AsInt64));
    }

    [Fact]
    public async Task A_draft_ordered_in_part_is_closed_and_its_unordered_packages_continue_in_a_successor_keranjang()
    {
        // Two channel groups of one keranjang are checked out one after the other (OrderCheckoutWorkspace).
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture, secondFulfillmentStock: 10);
        var drafts = new OrderDraftService(data.Database, new(fixture.Client), new(data.Database), data.Prices.Catalog.Clock);
        var draftId = (await drafts.SaveAsync(data.Buyer, null, 0, data.Input, Meta("cart-two")))["draftId"].AsInt64;
        var groupA = data.Input with { Selections = [data.Input.Selections[0]] }; var groupB = data.Input with { Selections = [data.Input.Selections[1]] };
        var reviewA = await data.Service.PreviewAsync(data.Buyer, groupA);
        var orderA = await data.Service.SubmitAsync(data.Buyer, groupA, reviewA.Review, Meta("checkout-a"), draftId);
        // The source draft is closed whatever is left in it: it is not listed and cannot be edited.
        Assert.Equal("order_draft_submitted", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            drafts.SaveAsync(data.Buyer, draftId, 1, data.Input, Meta("cart-edit-closed")))).Code);
        // What was not ordered continues as a new draft of the same owner, branch, delivery and reference, and is the keranjang.
        var successor = Assert.Single((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        Assert.NotEqual(draftId, successor.Id.Value); Assert.Equal(1, successor.Revision);
        SameInput(groupB, successor.Input);
        // Its receipt is an ordinary draft-save receipt: recovered by its key, and the same save again returns the same result.
        var successorKey = "order-successor:" + orderA["orderId"].AsInt64;
        var receipt = await drafts.RecoverAsync(data.Buyer, successorKey);
        Assert.Equal(successor.Id.Value, receipt!["draftId"].AsInt64);
        Assert.Equal(receipt, await drafts.SaveAsync(data.Buyer, null, 0, groupB,
            new(successorKey, "Sisa keranjang dilanjutkan setelah pesanan diajukan", "successor-replay")));
        Assert.Single((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        // The ordered group is not part of it, so it cannot be ordered a second time from the old draft (a new key does not help)...
        Assert.Equal("order_draft_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.SubmitAsync(data.Buyer, groupA, reviewA.Review, Meta("checkout-a-again"), draftId))).Code);
        // ...while replaying the same checkout still returns the same order.
        Assert.Equal(orderA, await data.Service.SubmitAsync(data.Buyer, groupA, reviewA.Review, Meta("checkout-a"), draftId));
        Assert.Equal(orderA, await data.Service.RecoverAsync(data.Buyer, "checkout-a"));
        // Editing the successor works (a new revision) and it stays the one keranjang.
        var edited = groupB with { Selections = [groupB.Selections[0] with { Packages = groupB.Selections[0].Packages + 1 }] };
        await drafts.SaveAsync(data.Buyer, successor.Id.Value, 1, edited, Meta("cart-edit-successor"));
        var current = Assert.Single((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        Assert.Equal((successor.Id, 2L), (current.Id, current.Revision)); SameInput(edited, current.Input);
        // The rest of the checkout that began on the old draft is ordered from the successor, which then closes too.
        var reviewB = await data.Service.PreviewAsync(data.Buyer, edited);
        await data.Service.SubmitAsync(data.Buyer, edited, reviewB.Review, Meta("checkout-b"), draftId);
        Assert.Empty((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        Assert.Equal("order_draft_submitted", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            drafts.SaveAsync(data.Buyer, successor.Id.Value, 2, edited, Meta("cart-edit-successor-closed")))).Code);
        Assert.Equal("order_draft_submitted", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.SubmitAsync(data.Buyer, edited, reviewB.Review, Meta("checkout-b-again"), draftId))).Code);
        Assert.Equal(2, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
        var links = await data.Rows("orderDraftSubmissions").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(new[] { draftId, successor.Id.Value }, links.Select(row => row["draftId"].AsInt64));
        Assert.Equal(successor.Id.Value, links[0]["successorId"].AsInt64); Assert.True(links[1]["successorId"].IsBsonNull);
    }

    [Fact]
    public async Task Two_checkouts_of_one_draft_with_different_keys_place_one_order()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var drafts = new OrderDraftService(data.Database, new(fixture.Client), new(data.Database), data.Prices.Catalog.Clock);
        var draftId = (await drafts.SaveAsync(data.Buyer, null, 0, data.Input, Meta("cart-race")))["draftId"].AsInt64;
        var review = await data.Service.PreviewAsync(data.Buyer, data.Input);
        async Task<object> Try(string key) {
            try { return await data.Service.SubmitAsync(data.Buyer, data.Input, review.Review, Meta(key), draftId); }
            catch (PersistenceConflictException conflict) { return conflict.Code; }
        }
        var outcomes = await Task.WhenAll(Task.Run(() => Try("race-one")), Task.Run(() => Try("race-two")));
        Assert.Equal(1, outcomes.Count(outcome => outcome is BsonDocument));
        Assert.Contains(outcomes.OfType<string>(), code => code is "order_draft_submitted" or "order_draft_changed");
        Assert.Equal(1, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("orderDraftSubmissions").CountDocumentsAsync(new BsonDocument()));
        // The database itself refuses a second link for one draft.
        var link = await data.Rows("orderDraftSubmissions").Find(new BsonDocument()).SingleAsync();
        var duplicate = link.DeepClone().AsBsonDocument; duplicate["_id"] = 9_000_001L;
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows("orderDraftSubmissions").InsertOneAsync(duplicate));
    }

    [Fact]
    public async Task An_order_is_closed_against_its_draft_only_when_it_is_the_drafts_saved_content()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture);
        var drafts = new OrderDraftService(data.Database, new(fixture.Client), new(data.Database), data.Prices.Catalog.Clock);
        var draftId = (await drafts.SaveAsync(data.Buyer, null, 0, data.Input, Meta("cart-saved")))["draftId"].AsInt64;
        // A reviewed input that is not what the draft holds (another reference here) is refused, with no order and no closing.
        var other = data.Input with { PurchaseReference = "PO lain" };
        var review = await data.Service.PreviewAsync(data.Buyer, other);
        Assert.Equal("order_draft_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.SubmitAsync(data.Buyer, other, review.Review, Meta("checkout-other"), draftId))).Code);
        Assert.Equal(0, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("orderDraftSubmissions").CountDocumentsAsync(new BsonDocument()));
        Assert.Single((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        // Another buyer's draft can never be named as the source.
        var stranger = await data.Prices.Catalog.ActorAsync(6001, CatalogAuthoringTestData.Audience, Permissions.CatalogRead, Permissions.OrderSubmit);
        // The other buyer holds the same branch grants, yet cannot close it, and the refusal is the same as for a draft that does not exist.
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.SubmitAsync(stranger, data.Input, review.Review, Meta("checkout-stranger"), draftId));
        var missing = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.SubmitAsync(stranger, data.Input, review.Review, Meta("checkout-nothing"), draftId + 900));
        Assert.Equal("order_draft_unavailable", denied.Code); Assert.Equal(denied.Code, missing.Code); Assert.Equal(denied.Message, missing.Message);
        Assert.Equal(0, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("orderDraftSubmissions").CountDocumentsAsync(new BsonDocument()));
        Assert.Single((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
    }

    [Fact]
    public async Task Http_checkout_names_its_draft_and_the_draft_leaves_the_keranjang()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var drafts = new OrderDraftService(data.Database, new(fixture.Client), new(data.Database), data.Prices.Catalog.Clock);
        var draftId = (await drafts.SaveAsync(data.Buyer, null, 0, data.Input, Meta("http-cart")))["draftId"].AsInt64;
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Prices.Catalog.Sessions);
            services.RemoveAll<OrderSubmissionService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Prices.Catalog.Issued[data.Buyer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var input = new { context = new { organizationId = "10", branchId = "11", divisionId = (string?)null },
            selections = data.Input.Selections.Select(line => new { skuId = line.SkuId.ToString(), definitionId = line.DefinitionId.ToString(), definitionVersion = line.DefinitionVersion.ToString(),
                priceId = line.PriceId.ToString(), priceVersion = line.PriceVersion.ToString(), packages = line.Packages }).ToArray(),
            delivery = data.Input.Delivery, purchaseReference = data.Input.PurchaseReference };
        async Task<HttpResponseMessage> Request(string path, object body, string? key = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ordering/orders" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", origin); request.Headers.Add("X-Expected-Actor-Id", data.Buyer.UserId.ToString()); request.Headers.Add("X-CSRF-Token", csrf);
            if (key is not null) { request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", "\"0\""); }
            return await client.SendAsync(request);
        }
        static async Task<JsonElement> Ok(HttpResponseMessage response) {
            var raw = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + raw);
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        var review = await Ok(await Request("/preview/query", new { purpose = "order-preparation", input }));
        var command = new { input, review = review.GetProperty("review").GetString(), reason = "Synthetic buyer confirms order", draftId = draftId.ToString() };
        var saved = await Ok(await Request("", command, "http-draft-checkout"));
        Assert.Equal(saved.GetRawText(), (await Ok(await Request("", command, "http-draft-checkout"))).GetRawText());
        Assert.Empty((await drafts.ListAsync(data.Buyer, data.Input.Context)).Items);
        var again = await Request("", command, "http-draft-checkout-again");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("order_draft_submitted", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(1, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Migration_123_creates_strict_empty_draft_submission_storage()
    {
        var database = fixture.Client.GetDatabase("order_draft_link_migration_" + Guid.NewGuid().ToString("N"));
        try {
            var migrations = new CollectionMigrations(database, new SystemClock());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.True(CollectionMigrations.CurrentVersion >= 123);
            var rows = database.GetCollection<BsonDocument>("orderDraftSubmissions");
            Assert.Equal(0, await rows.CountDocumentsAsync(new BsonDocument()));
            var at = DateTime.SpecifyKind(new DateTime(2026, 10, 22, 3, 0, 0), DateTimeKind.Utc);
            BsonDocument Row(long orderId) => new() { { "_id", orderId }, { "schemaVersion", 1 }, { "kind", "ordering.draft.submission" }, { "draftId", 7L },
                { "draftRevision", 2L }, { "successorId", BsonNull.Value }, { "createdBy", 41L }, { "skuIds", new BsonArray { 301L } }, { "recordedAt", at } };
            await rows.InsertOneAsync(Row(1));
            // An unknown field, a missing draft, no packages or a repeated package is refused.
            var extra = Row(2); extra["note"] = "x";
            var missing = Row(3); missing.Remove("draftId");
            var empty = Row(4); empty["skuIds"] = new BsonArray();
            var repeated = Row(5); repeated["skuIds"] = new BsonArray { 301L, 301L };
            var sameDraft = Row(6); // a draft is consumed by one order only
            foreach (var row in new[] { extra, missing, empty, repeated, sameDraft })
                await Assert.ThrowsAsync<MongoWriteException>(() => rows.InsertOneAsync(row));
            var indexes = await (await rows.Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, index => index["key"].AsBsonDocument == new BsonDocument("draftId", 1) && index.GetValue("unique", false).AsBoolean);
            await migrations.ApplyAsync();
            Assert.Equal(1, await rows.CountDocumentsAsync(new BsonDocument()));
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }
}
