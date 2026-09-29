using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    [Theory]
    [InlineData("reject")] [InlineData("withdraw")]
    public async Task Alias_HTTP_terminal_decisions_and_unknown_receipts_do_not_create_mappings(string action)
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        var pool = await CreatePool(maker, reviewer);
        var proposal = await Ok(await maker.Post("/pools/" + pool + "/aliases", AliasCommand(), "mapping", "\"1\""));
        var actor = action == "withdraw" ? maker : reviewer;
        var result = await Ok(await actor.Post("/alias-proposals/" + proposal.GetProperty("proposalId").GetString() + "/" + action, Review, "decision", "\"1\""));
        Assert.Equal(action == "withdraw" ? "withdrawn" : "rejected", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("appliedAliasVersion").ValueKind);
        var recovered = await Ok(await actor.Post("/alias-operations/query", AliasRecovery(pool, action, "decision")));
        Assert.Equal(result.GetRawText(), recovered.GetProperty("result").GetRawText());
        var absent = await Ok(await maker.Post("/alias-operations/query", AliasRecovery(pool, "propose", "missing")));
        Assert.Equal("not_confirmed", absent.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, absent.GetProperty("result").ValueKind);
        using var repeated = await reviewer.Post("/alias-proposals/" + proposal.GetProperty("proposalId").GetString() + "/approve", Review, "repeat", "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal(0, await data.Rows("stockSourceAliases").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(4, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Alias_HTTP_stale_settings_competing_mapping_self_review_and_expired_MFA_cannot_apply()
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", Maker), Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.StockSourceApprove));
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool + "/aliases";
        var first = await Ok(await maker.Post(path, AliasCommand(), "first", "\"1\""));
        var second = await Ok(await maker.Post(path, AliasCommand(), "second", "\"1\""));
        var id = first.GetProperty("proposalId").GetString();
        using var self = await maker.Post("/alias-proposals/" + id + "/approve", Review, "self", "\"1\""); Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var version = await reviewer.Post("/alias-proposals/" + id + "/approve", Review, "version", "\"2\""); Assert.Equal(HttpStatusCode.Conflict, version.StatusCode);
        await Ok(await reviewer.Post("/alias-proposals/" + id + "/approve", Review, "approval", "\"1\""));
        using var competing = await reviewer.Post("/alias-proposals/" + second.GetProperty("proposalId").GetString() + "/approve", Review, "competing", "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, competing.StatusCode);
        var revision = await Ok(await maker.Post("/pools/" + pool + "/revisions", RevisionCommand("New source settings"), "settings", "\"1\""));
        await Ok(await reviewer.Post("/revisions/" + revision.GetProperty("proposalId").GetString() + "/approve", Review, "settings-approve", "\"1\""));
        var current = await Ok(await reader.Post("/pools/" + pool + "/aliases/query", AliasRead));
        Assert.Equal("stale", current.GetProperty("bindingState").GetString());
        Assert.Equal("2", current.GetProperty("source").GetProperty("poolVersion").GetString());
        Assert.Equal("1", current.GetProperty("mapping").GetProperty("poolVersion").GetString());
        using var stale = await maker.Post(path, AliasCommand("1", false), "stale", "\"1\""); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var historical = await Ok(await reader.Post("/pools/" + pool + "/alias-proposals/" + id + "/query", AliasHistory));
        Assert.Equal("approved", historical.GetProperty("status").GetString());
        data.Base.Clock.UtcNow = data.Base.Clock.UtcNow.AddMinutes(6);
        using var expired = await reviewer.Post("/alias-operations/query", AliasRecovery(pool, "approve", "approval"));
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.Equal(1, await data.Rows("stockSourceAliases").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("stockSourceAliasDecisions").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Alias_HTTP_requires_both_scopes_and_filters_exact_channel_before_paging_or_detail()
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(105, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        await data.Base.AddGrantAsync(await data.ActorAsync(Reader), 48003, (Location with { ChannelId = new(105) }).ToScope(), [Permissions.StockSourceRead]);
        await data.AddActorAsync(48000, Location with { ChannelId = new(104) }, Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourceApprove);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader); using var requestedOnly = await data.OpenAsync(48000);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        var first = await Ok(await maker.Post(path + "/aliases", AliasCommand(), "first", "\"1\""));
        var second = await Ok(await maker.Post(path + "/aliases", AliasCommand(), "second", "\"1\""));
        var id = first.GetProperty("proposalId").GetString();
        foreach (var (suffix, body) in new[] {
            (path + "/aliases/query", AliasRead), (path + "/alias-proposals/query", AliasHistory),
            (path + "/alias-proposals/" + id + "/query", AliasHistory), (path + "/aliases", AliasCommand()),
            ("/alias-proposals/" + id + "/approve", Review), ("/alias-operations/query", AliasRecovery(pool, "propose", "first")) })
        {
            using var denied = await requestedOnly.Post(suffix, body, "foreign", "\"1\""); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var page = await Ok(await reader.Post(path + "/alias-proposals/query", new { purpose = "inventory-source-alias-history", requestedLocation = AliasLocationBody(), offset = 1, limit = 1 }));
        Assert.Equal("2", page.GetProperty("total").GetString()); Assert.Equal(second.GetProperty("proposalId").GetString(), Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        var foreignQuery = new { purpose = "inventory-source-alias-history", requestedLocation = AliasLocationBody("105") };
        var empty = await Ok(await reader.Post(path + "/alias-proposals/query", foreignQuery)); Assert.Equal("0", empty.GetProperty("total").GetString());
        var foreign = await Ok(await reader.Post(path + "/alias-proposals/" + id + "/query", foreignQuery));
        var missing = await Ok(await reader.Post(path + "/alias-proposals/999999/query", foreignQuery));
        Assert.Equal(JsonValueKind.Null, foreign.ValueKind); Assert.Equal(foreign.ValueKind, missing.ValueKind);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Reader + 100), Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        using var lost = await reader.Post(path + "/aliases/query", AliasRead); Assert.Equal(HttpStatusCode.Forbidden, lost.StatusCode);
        Assert.Equal(0, await data.Rows("stockSourceAliases").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Alias_HTTP_rejects_unknown_recovery_operations_and_bad_query_shapes_without_writes()
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var pool = await CreatePool(maker, reviewer);
        foreach (var fault in new[] { "purpose", "scope", "offset", "limit", "extra", "numeric-page" })
        {
            var body = JsonSerializer.SerializeToNode(AliasHistory)!.AsObject();
            if (fault == "purpose") body["purpose"] = "unknown";
            if (fault == "scope") body["requestedLocation"] = null;
            if (fault == "offset") body["offset"] = -1;
            if (fault == "limit") body["limit"] = 101;
            if (fault == "extra") body["actorId"] = Maker;
            if (fault == "numeric-page") body["offset"] = "0";
            using var invalid = await reader.Post("/pools/" + pool + "/alias-proposals/query", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var fault in new[] { "operation", "key", "pool", "extra" })
        {
            var body = JsonSerializer.SerializeToNode(AliasRecovery(pool, "propose", "unknown"))!.AsObject();
            if (fault == "operation") body["operation"] = "stock.source.propose";
            if (fault == "key") body["key"] = "unsafe key";
            if (fault == "pool") body["physicalPoolId"] = 1;
            if (fault == "extra") body["reason"] = "Replacement work";
            using var invalid = await maker.Post("/alias-operations/query", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(0, await data.Rows("stockSourceAliasProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
}
