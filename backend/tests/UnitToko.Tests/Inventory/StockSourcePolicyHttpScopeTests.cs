using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    [Fact]
    public async Task Policy_HTTP_real_stop_invalidates_approval_but_preserves_rejection_and_all_linked_scope_checks()
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend);
        await data.Base.AddGrantAsync(await data.ActorAsync(Stopper), Stopper + 100, (Location with { ChannelId = new(104) }).ToScope(), [Permissions.StockSourceSuspend]);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        using var reader = await data.OpenAsync(Reader); using var stopper = await data.OpenAsync(Stopper);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        var alias = await Ok(await maker.Post(path + "/aliases", AliasCommand(), "alias", "\"1\""));
        await Ok(await reviewer.Post("/alias-proposals/" + alias.GetProperty("proposalId").GetString() + "/approve", Review, "alias-approve", "\"1\""));
        await PolicyOnlyGrants(data);
        var preview = await Ok(await maker.Post(path + "/policy-preview/query", PolicyPreview));
        var mapping = preview.GetProperty("configuration").GetProperty("mappings")[0];
        Assert.Equal("104", mapping.GetProperty("location").GetProperty("channelId").GetString()); Assert.True(mapping.GetProperty("enabled").GetBoolean());
        Assert.Equal("1", mapping.GetProperty("aliasVersion").GetString());
        var proposed = await Ok(await maker.Post(path + "/policy-proposals", PolicyCommand(preview), "policy", "\"1\""));
        var id = proposed.GetProperty("proposalId").GetString();
        var stop = await Ok(await stopper.Post(path + "/suspension-preview/query", SuspensionPreview));
        await Ok(await stopper.Post(path + "/suspend", SuspensionCommand(stop), "stop", "\"1\""));
        using var stale = await reviewer.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview("approve"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        foreach (var (actor, action) in new[] { (reviewer, "reject"), (maker, "withdraw") })
        {
            var review = await Ok(await actor.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview(action)));
            Assert.False(review.GetProperty("bindingCurrent").GetBoolean());
            Assert.Equal("1", review.GetProperty("current").GetProperty("suspensionVersion").GetString());
            Assert.Equal("0", review.GetProperty("proposal").GetProperty("suspensionVersion").GetString());
        }
        using var staleCommand = await reviewer.Post("/policy-proposals/" + id + "/approve", Review, "stale", "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, staleCommand.StatusCode);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Reviewer + 100),
            Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        using var revoked = await reviewer.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview("reject"));
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        var withdrawn = await Ok(await maker.Post("/policy-proposals/" + id + "/withdraw", Review, "withdraw", "\"1\""));
        Assert.Equal("withdrawn", withdrawn.GetProperty("status").GetString());
        var history = await Ok(await reader.Post(path + "/policy-proposals/query", PolicyHistory));
        Assert.Equal("withdrawn", history.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(0, await data.Rows("stockSourcePolicies").CountDocumentsAsync(new BsonDocument()));
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["active"].AsBoolean);
    }
    [Fact]
    public async Task Policy_HTTP_external_writer_requires_explicit_tenant_and_feed_and_preserves_reviewed_facts()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var source = JsonSerializer.SerializeToNode(Command)!.AsObject();
        source["mode"] = "externalLedger"; source["control"] = "externalHold"; source["externalNamespace"] = "synthetic-provider";
        source["reservationProtocolReference"] = "Synthetic reservation protocol";
        var created = await Ok(await maker.Post("", source, "source"));
        var accepted = await Ok(await reviewer.Post("/" + created.GetProperty("proposalId").GetString() + "/approve", Review, "source-approve", "\"1\""));
        var pool = accepted.GetProperty("physicalPoolId").GetString()!; var path = "/pools/" + pool;
        await PolicyOnlyGrants(data);
        var preview = await Ok(await maker.Post(path + "/policy-preview/query", PolicyPreview));
        var valid = JsonSerializer.SerializeToNode(PolicyCommand(preview))!.AsObject();
        valid["input"]!["writer"] = "externalSystem"; valid["input"]!["externalTenant"] = "Tenant-A";
        valid["input"]!["feedIntervalMilliseconds"] = "922337203685477";
        foreach (var fault in new[] { "tenant", "space", "zero", "range", "writer" })
        {
            var wrong = valid.DeepClone();
            if (fault == "tenant") wrong["input"]!["externalTenant"] = null;
            if (fault == "space") wrong["input"]!["externalTenant"] = " Tenant-A";
            if (fault == "zero") wrong["input"]!["feedIntervalMilliseconds"] = "0";
            if (fault == "range") wrong["input"]!["feedIntervalMilliseconds"] = "922337203685478";
            if (fault == "writer") wrong["input"]!["writer"] = "unitToko";
            using var invalid = await maker.Post(path + "/policy-proposals", wrong, fault, "\"1\"");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var proposed = await Ok(await maker.Post(path + "/policy-proposals", valid, "policy", "\"1\""));
        var id = proposed.GetProperty("proposalId").GetString();
        await Ok(await reviewer.Post("/policy-proposals/" + id + "/approve", Review, "policy-approve", "\"1\""));
        var latest = await Ok(await reader.Post(path + "/policy/query", PolicyHistory));
        Assert.Equal("Tenant-A", latest.GetProperty("policy").GetProperty("input").GetProperty("externalTenant").GetString());
        Assert.Equal("922337203685477", latest.GetProperty("policy").GetProperty("input").GetProperty("feedIntervalMilliseconds").GetString());
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["active"].AsBoolean);
    }
}