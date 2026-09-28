using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    [Theory]
    [InlineData("reject")] [InlineData("withdraw")]
    public async Task Revision_HTTP_terminal_decisions_and_unknown_receipts_never_apply_settings(string action)
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        var pool = await CreatePool(maker, reviewer);
        var proposal = await Ok(await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("Candidate"), "revision", "\"1\""));
        var actor = action == "withdraw" ? maker : reviewer;
        var result = await Ok(await actor.Post("/revisions/" + proposal.GetProperty("proposalId").GetString() + "/" + action, Review, "decision", "\"1\""));
        Assert.Equal(action == "withdraw" ? "withdrawn" : "rejected", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("appliedPoolVersion").ValueKind);
        var receipt = await Ok(await actor.Post("/revision-operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.revision." + action, key = "decision" }));
        Assert.Equal(result.GetRawText(), receipt.GetProperty("result").GetRawText());
        var unknown = await Ok(await maker.Post("/revision-operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.revision.propose", key = "missing" }));
        Assert.Equal("not_confirmed", unknown.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("result").ValueKind);
        Assert.Equal(0, await data.Rows("stockSourceRevisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, (await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["revision"].AsInt64);
    }

    [Fact]
    public async Task Revision_HTTP_stale_proposals_self_review_and_expired_MFA_are_denied_without_partial_changes()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Maker), Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.StockSourceApprove));
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        var pool = await CreatePool(maker, reviewer);
        var first = await Ok(await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("First"), "first", "\"1\""));
        var second = await Ok(await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("Second"), "second", "\"1\""));
        var firstId = first.GetProperty("proposalId").GetString(); var secondId = second.GetProperty("proposalId").GetString();
        using var self = await maker.Post("/revisions/" + firstId + "/approve", Review, "self", "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var wrongVersion = await reviewer.Post("/revisions/" + firstId + "/approve", Review, "wrong-version", "\"2\"");
        Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
        await Ok(await reviewer.Post("/revisions/" + firstId + "/approve", Review, "approval", "\"1\""));
        using var stale = await reviewer.Post("/revisions/" + secondId + "/approve", Review, "stale", "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleNew = await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("Third"), "third", "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, staleNew.StatusCode);
        data.Base.Clock.UtcNow = data.Base.Clock.UtcNow.AddMinutes(6);
        using var expired = await reviewer.Post("/revision-operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.revision.approve", key = "approval" });
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.Equal(1, await data.Rows("stockSourceRevisionDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("stockSourceRevisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(5, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Revision_HTTP_scope_isolation_precedes_private_reads_and_null_or_unknown_input_fails_closed()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        var foreign = Location with { ChannelId = new(104) };
        await data.AddActorAsync(48000, foreign, Permissions.StockSourceRead, Permissions.StockSourceApprove, Permissions.StockSourcePropose);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader); using var other = await data.OpenAsync(48000);
        var pool = await CreatePool(maker, reviewer);
        var proposed = await Ok(await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("Candidate"), "revision", "\"1\""));
        var id = proposed.GetProperty("proposalId").GetString();
        var foreignBody = JsonSerializer.SerializeToNode(LocationBody)!.AsObject(); foreignBody["channelId"] = "104";
        var history = await Ok(await other.Post("/revisions/query", new { purpose = "inventory-source-revisions", location = foreignBody }));
        Assert.Equal("0", history.GetProperty("total").GetString());
        var absent = await Ok(await other.Post("/revisions/" + id + "/query", new { purpose = "inventory-source-revisions", location = foreignBody }));
        Assert.Equal(JsonValueKind.Null, absent.ValueKind);
        using var denied = await other.Post("/revisions/" + id + "/approve", Review, "foreign", "\"1\"");
        using var missing = await other.Post("/revisions/999999/query", RevisionsQuery);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        using var foreignPool = await other.Post("/pools/" + pool + "/query", new { purpose = "inventory-source-current", location = foreignBody });
        using var missingPool = await other.Post("/pools/999999/query", new { purpose = "inventory-source-current", location = foreignBody });
        Assert.Equal(HttpStatusCode.Forbidden, foreignPool.StatusCode); Assert.Equal(await Code(foreignPool), await Code(missingPool));
        foreach (var body in new object[] { new { purpose = "inventory-source-revisions", location = (object?)null },
            new { purpose = "wrong", location = LocationBody }, new { purpose = "inventory-source-revisions", location = LocationBody, limit = 101 },
            new { purpose = "inventory-source-revisions", location = LocationBody, physicalPoolId = 1 } })
        {
            using var invalid = await reader.Post("/revisions/query", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(0, await data.Rows("stockSourceRevisionDecisions").CountDocumentsAsync(new BsonDocument()));
    }
}
