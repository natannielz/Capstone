using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogAuthoringHttpTests
{
    private const long PriceReviewerId = 45003;
    private static object PriceRead(string access = "prepare") => new { purpose = "catalog-pricing", access };
    private static object PriceCommand(JsonElement preview, string amount = "12000.25") => new {
        expectedDefinitionId = preview.GetProperty("expected").GetProperty("definitionId").GetString(),
        expectedDefinitionVersion = preview.GetProperty("expected").GetProperty("definitionVersion").GetString(),
        expectedPriceVersion = preview.GetProperty("expected").GetProperty("priceVersion").GetString(),
        input = new { currency = "IDR", unitPrice = amount }, reason = "Harga kemasan untuk peninjauan independen"
    };
    private static async Task<string> PriceSku(Browser maker)
    {
        var (_, family) = await CreateFamily(maker);
        var sku = await Ok(await maker.Post("/families/" + family.GetProperty("id").GetString() + "/skus",
            new { code = "PENCIL-12", physicalItemKey = "PENCIL", definition = Definition(12, "Dus"), reason = "Dua belas unit" }, "price-sku", "1"));
        return sku.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Price_HTTP_persists_reviewed_package_price_and_recovers_exact_action_with_private_history()
    {
        await using var data = await HttpData.CreateAsync(fixture, pricing: true);
        using var maker = await data.OpenAsync(ManagerId); using var checker = await data.OpenAsync(PriceReviewerId);
        using var buyer = await data.OpenAsync(BuyerId);
        var makerTasks = await Ok(await maker.Client.GetAsync("/api/v1/access/current"));
        var checkerTasks = await Ok(await checker.Client.GetAsync("/api/v1/access/current"));
        Assert.Contains(makerTasks.GetProperty("allowedActions").EnumerateArray(), item => item.GetString() == Permissions.PricingManage);
        Assert.Contains(checkerTasks.GetProperty("allowedActions").EnumerateArray(), item => item.GetString() == Permissions.PricingApprove);
        var sku = await PriceSku(maker); var path = "/skus/" + sku;
        var choices = await Ok(await checker.Price("/skus/query", new { purpose = "catalog-pricing", access = "review", search = "PENCIL", limit = 25 }));
        Assert.Equal(sku, Assert.Single(choices.GetProperty("items").EnumerateArray()).GetProperty("sku").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.Price("/skus/query", new { purpose = "catalog-pricing", access = "review" })).StatusCode);
        foreach (var collection in new[] { "catalogPriceProposals", "catalogPriceDecisions", "priceVersions" })
            await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", collection), Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        var preview = await Ok(await maker.Price(path + "/preview/query", PriceRead()));
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("currentPrice").ValueKind);
        Assert.Equal(12, preview.GetProperty("definition").GetProperty("baseUnitsPerPackage").GetInt32());
        Assert.Equal("0", preview.GetProperty("expected").GetProperty("priceVersion").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.Price(path + "/preview/query", PriceRead())).StatusCode);
        var proposed = await Ok(await maker.Price(path + "/proposals", PriceCommand(preview), "price-propose", "1"));
        var proposal = proposed.GetProperty("proposalId").GetString(); Assert.Equal("9007199254740993", proposal);
        var detailPath = path + "/proposals/" + proposal + "/query";
        var detail = await Ok(await checker.Price(detailPath, PriceRead("review")));
        var proposals = await Ok(await checker.Price(path + "/proposals/query", new { purpose = "catalog-pricing", access = "review", limit = 25 }));
        Assert.Equal(proposal, Assert.Single(proposals.GetProperty("items").EnumerateArray()).GetProperty("proposalId").GetString());
        Assert.Equal("12000.25", detail.GetProperty("input").GetProperty("unitPrice").GetString());
        // The page learns only whether the viewer proposed it ("oleh Anda"); who proposed stays private, as asserted below.
        Assert.False(detail.GetProperty("ownProposal").GetBoolean());
        Assert.True((await Ok(await maker.Price(detailPath, PriceRead()))).GetProperty("ownProposal").GetBoolean());
        Assert.Equal("Dus", detail.GetProperty("definition").GetProperty("packageLabel").GetString());
        await data.Base.AddGrantAsync(new(ManagerId, "https://approved.example/", "subject-" + ManagerId, 1, data.Base.Clock.UtcNow, FactorAssurance.MultiFactor),
            45011, Owner, [Permissions.PricingApprove]);
        var review = new { reviewReference = "Tinjauan harga independen", reason = "Harga telah diperiksa" };
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.Price("/proposals/" + proposal + "/approve", review, "own-price", "1")).StatusCode);
        var approved = await Ok(await checker.Price("/proposals/" + proposal + "/approve", review, "price-approve", "1"));
        // Allocation is monotonic, not gapless: the denied self-approval reserved an unused ID.
        Assert.Equal("approve", approved.GetProperty("status").GetString()); Assert.Equal("9007199254740994", approved.GetProperty("priceId").GetString());
        var after = await Ok(await maker.Price(path + "/preview/query", PriceRead()));
        Assert.Equal("12000.25", after.GetProperty("currentPrice").GetProperty("unitPrice").GetString());
        var history = await Ok(await checker.Price(path + "/history/query", new { purpose = "catalog-pricing", access = "review", limit = 25 }));
        Assert.Single(history.GetProperty("items").EnumerateArray());
        var recovered = await Ok(await checker.Price(path + "/operations/query", new { purpose = "operation-recovery", action = "approve", key = "price-approve" }));
        Assert.Equal("committed", recovered.GetProperty("state").GetString()); Assert.Equal(approved.GetRawText(), recovered.GetProperty("result").GetRawText());
        var missing = await Ok(await maker.Price(path + "/operations/query", new { purpose = "operation-recovery", action = "approve", key = "price-approve" }));
        Assert.Equal("not_confirmed", missing.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.Price(detailPath, PriceRead("review"))).StatusCode);
        foreach (var hidden in new[] { "proposedBy", "decidedBy", "bodyHash", "personApproval", "auditId", "requestKey", "schemaVersion" })
            Assert.DoesNotContain("\"" + hidden + "\"", preview.GetRawText() + detail.GetRawText() + history.GetRawText());
        Assert.Equal(1, await data.Count("priceVersions"));
    }

    [Fact]
    public async Task Price_HTTP_rejects_unsafe_wire_values_missing_headers_stale_bindings_and_revoked_access()
    {
        await using var data = await HttpData.CreateAsync(fixture, pricing: true);
        using var maker = await data.OpenAsync(ManagerId); using var checker = await data.OpenAsync(PriceReviewerId);
        var sku = await PriceSku(maker); var path = "/skus/" + sku;
        var preview = await Ok(await maker.Price(path + "/preview/query", PriceRead()));
        foreach (var fault in new[] { "numeric-money", "null-money", "missing-money", "unknown-field", "numeric-version" }) {
            var body = JsonSerializer.SerializeToNode(PriceCommand(preview))!.AsObject();
            if (fault == "numeric-money") body["input"]!["unitPrice"] = 12.34;
            if (fault == "null-money") body["input"]!["unitPrice"] = null;
            if (fault == "missing-money") body["input"]!.AsObject().Remove("unitPrice");
            if (fault == "unknown-field") body["approve"] = true;
            if (fault == "numeric-version") body["expectedPriceVersion"] = 0;
            Assert.Equal(HttpStatusCode.BadRequest, (await maker.Price(path + "/proposals", body, fault, "1")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.Price(path + "/proposals", PriceCommand(preview), version: "1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.Price(path + "/proposals", PriceCommand(preview), "missing-version")).StatusCode);
        using (var noActor = maker.Request("/owners/query", PriceRead())) {
            noActor.RequestUri = new Uri(Origin + "/api/v1/catalog/prices" + path + "/preview/query");
            noActor.Headers.Remove("X-Expected-Actor-Id");
            Assert.Equal(HttpStatusCode.BadRequest, (await maker.Client.SendAsync(noActor)).StatusCode);
        }
        var proposed = await Ok(await maker.Price(path + "/proposals", PriceCommand(preview, "0.00"), "explicit-zero", "1"));
        await Ok(await maker.Post(path + "/definition", new { definition = Definition(12, "Dus dua belas"), reason = "Koreksi label" }, "rename", "1"));
        var review = new { reviewReference = "Tinjauan", reason = "Harga kemasan" };
        var stale = await checker.Price("/proposals/" + proposed.GetProperty("proposalId").GetString() + "/approve", review, "stale-price", "1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", ManagerId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.Price(path + "/preview/query", PriceRead())).StatusCode);
        Assert.Equal(0, await data.Count("priceVersions"));
        data.Base.Clock.UtcNow = data.Base.Clock.UtcNow.AddMinutes(6);
        var expiredTasks = await Ok(await checker.Client.GetAsync("/api/v1/access/current"));
        Assert.Contains(expiredTasks.GetProperty("allowedActions").EnumerateArray(), item => item.GetString() == Permissions.PricingApprove);
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.Price(path + "/preview/query", PriceRead("review"))).StatusCode);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", PriceReviewerId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        var revokedTasks = await Ok(await checker.Client.GetAsync("/api/v1/access/current"));
        Assert.DoesNotContain(revokedTasks.GetProperty("allowedActions").EnumerateArray(), item => item.GetString() == Permissions.PricingApprove);
    }
}
