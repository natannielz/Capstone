using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    private static object CurrentQuery => new { purpose = "inventory-source-current", location = LocationBody };
    private static object RevisionsQuery => new { purpose = "inventory-source-revisions", location = LocationBody };
    private static JsonObject RevisionCommand(string label)
    {
        var body = JsonSerializer.SerializeToNode(Command)!.AsObject(); body["label"] = label; return body;
    }
    private static async Task<string> CreatePool(Browser maker, Browser reviewer)
    {
        var original = await Ok(await maker.Post("", Command, "original"));
        var approval = await Ok(await reviewer.Post("/" + original.GetProperty("proposalId").GetString() + "/approve", Review, "original-approve", "\"1\""));
        return approval.GetProperty("physicalPoolId").GetString()!;
    }

    [Fact]
    public async Task Revision_HTTP_current_proposal_independent_review_history_and_recovery_preserve_exact_ids()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceRevisionProposals"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var pool = await CreatePool(maker, reviewer);
        var current = await Ok(await reader.Post("/pools/" + pool + "/query", CurrentQuery));
        Assert.Equal("1", current.GetProperty("poolVersion").GetString()); Assert.Equal("inactive", current.GetProperty("poolState").GetString());
        var list = await Ok(await reader.Post("/pools/query", CurrentQuery));
        Assert.Equal("1", list.GetProperty("total").GetString()); Assert.Single(list.GetProperty("items").EnumerateArray());
        var proposal = await Ok(await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("First reviewed change"), "revision", "\"1\""));
        Assert.Equal("9007199254740993", proposal.GetProperty("proposalId").GetString());
        Assert.Equal("1", proposal.GetProperty("basePoolVersion").GetString());
        var id = proposal.GetProperty("proposalId").GetString();
        var approval = await Ok(await reviewer.Post("/revisions/" + id + "/approve", Review, "revision-approval", "\"1\""));
        Assert.Equal("approved", approval.GetProperty("status").GetString()); Assert.Equal("2", approval.GetProperty("appliedPoolVersion").GetString());
        var detail = await Ok(await reader.Post("/revisions/" + id + "/query", RevisionsQuery));
        Assert.Equal("Synthetic warehouse source", detail.GetProperty("baseSettings").GetProperty("label").GetString());
        Assert.Equal("First reviewed change", detail.GetProperty("proposedSettings").GetProperty("label").GetString());
        var second = await Ok(await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("Second reviewed change"), "second", "\"2\""));
        await Ok(await reviewer.Post("/revisions/" + second.GetProperty("proposalId").GetString() + "/approve", Review, "second-approval", "\"1\""));
        var recovered = await Ok(await reviewer.Post("/revision-operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.revision.approve", key = "revision-approval" }));
        Assert.Equal("committed", recovered.GetProperty("state").GetString());
        Assert.Equal(approval.GetRawText(), recovered.GetProperty("result").GetRawText());
        using var noHistory = await maker.Post("/revisions/query", RevisionsQuery); Assert.Equal(HttpStatusCode.Forbidden, noHistory.StatusCode);
        var proposedRecovery = await Ok(await maker.Post("/revision-operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.revision.propose", key = "revision" }));
        Assert.Equal(proposal.GetRawText(), proposedRecovery.GetProperty("result").GetRawText());
        var history = await Ok(await reader.Post("/revisions/query", RevisionsQuery));
        Assert.Equal("2", history.GetProperty("total").GetString());
        Assert.Equal(3, (await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["revision"].AsInt64);
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["active"].AsBoolean);
        Assert.Equal(2, await data.Rows("stockSourceRevisions").CountDocumentsAsync(new BsonDocument()));
        foreach (var hidden in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "issuer", "subject", "record", "fence" })
            Assert.DoesNotContain("\"" + hidden + "\"", current.GetRawText() + detail.GetRawText() + history.GetRawText());
    }

    [Fact]
    public async Task Revision_HTTP_requires_current_actor_csrf_versions_and_strict_explicit_settings()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var anonymous = await data.OpenAsync(null);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool + "/revisions";
        using var noSession = await anonymous.Post(path, RevisionCommand("Candidate"), "anonymous", "\"1\"");
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        foreach (var fault in new[] { "csrf", "origin", "actor", "key", "version", "extra", "numeric", "scope" })
        {
            var body = RevisionCommand("Candidate");
            if (fault == "extra") body["approved"] = true;
            if (fault == "numeric") body["safetyBufferBaseUnits"] = 2;
            if (fault == "scope") body["location"] = null;
            using var request = maker.Request(path, body, "invalid-" + fault, "\"1\"");
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            if (fault == "origin") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://foreign.example"); }
            if (fault == "actor") { request.Headers.Remove("X-Expected-Actor-Id"); request.Headers.Add("X-Expected-Actor-Id", Reviewer.ToString()); }
            if (fault == "key") request.Headers.Remove("Idempotency-Key");
            if (fault == "version") request.Headers.Remove("If-Match");
            using var response = await maker.Client.SendAsync(request);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.Conflict });
        }
        Assert.Equal(0, await data.Rows("stockSourceRevisionProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
}
