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
    private static JsonObject AliasLocationBody(string channel = "104")
    {
        var value = JsonSerializer.SerializeToNode(LocationBody)!.AsObject(); value["channelId"] = channel; return value;
    }
    private static object AliasRead => new { purpose = "inventory-source-alias", requestedLocation = AliasLocationBody() };
    private static object AliasHistory => new { purpose = "inventory-source-alias-history", requestedLocation = AliasLocationBody(), offset = 0, limit = 25 };
    private static object AliasCommand(string version = "0", bool enabled = true) => new
        { requestedLocation = AliasLocationBody(), expectedAliasVersion = version, enabled, reason = "Synthetic channel mapping" };
    private static object AliasRecovery(string pool, string operation, string key) => new
        { purpose = "operation-recovery", physicalPoolId = pool, requestedLocation = AliasLocationBody(), operation = "stock.source.alias." + operation, key };
    private static async Task AliasScopes(HttpData data)
    {
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1)));
        foreach (var (id, permission) in new[] { (Maker, Permissions.StockSourcePropose), (Reviewer, Permissions.StockSourceApprove), (Reader, Permissions.StockSourceRead) })
            await data.Base.AddGrantAsync(await data.ActorAsync(id), id + 100, (Location with { ChannelId = new(104) }).ToScope(), [permission]);
    }

    [Fact]
    public async Task Alias_HTTP_review_history_exact_ids_and_recovery_preserve_settings_and_private_records()
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceAliasProposals"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        var absent = await Ok(await reader.Post(path + "/aliases/query", AliasRead));
        Assert.Equal("absent", absent.GetProperty("bindingState").GetString());
        Assert.Equal(JsonValueKind.Null, absent.GetProperty("mapping").ValueKind);
        var proposed = await Ok(await maker.Post(path + "/aliases", AliasCommand(), "mapping", "\"1\""));
        Assert.Equal("9007199254740993", proposed.GetProperty("proposalId").GetString());
        Assert.Equal("0", proposed.GetProperty("baseAliasVersion").GetString());
        var id = proposed.GetProperty("proposalId").GetString();
        var approved = await Ok(await reviewer.Post("/alias-proposals/" + id + "/approve", Review, "mapping-approve", "\"1\""));
        Assert.Equal("approved", approved.GetProperty("status").GetString()); Assert.Equal("1", approved.GetProperty("appliedAliasVersion").GetString());
        var detail = await Ok(await reader.Post(path + "/alias-proposals/" + id + "/query", AliasHistory));
        Assert.Equal("103", detail.GetProperty("ownerLocation").GetProperty("channelId").GetString());
        Assert.Equal("104", detail.GetProperty("requestedLocation").GetProperty("channelId").GetString());
        Assert.Equal("1", detail.GetProperty("poolVersion").GetString());
        var current = await Ok(await reader.Post(path + "/aliases/query", AliasRead));
        Assert.Equal("current", current.GetProperty("bindingState").GetString());
        Assert.Equal("inactive", current.GetProperty("source").GetProperty("poolState").GetString());
        Assert.Equal("1", current.GetProperty("source").GetProperty("poolVersion").GetString());
        var disabled = await Ok(await maker.Post(path + "/aliases", AliasCommand("1", false), "disable", "\"1\""));
        await Ok(await reviewer.Post("/alias-proposals/" + disabled.GetProperty("proposalId").GetString() + "/approve", Review, "disable-approve", "\"1\""));
        var next = await Ok(await reader.Post(path + "/aliases/query", AliasRead)); Assert.Equal("disabled", next.GetProperty("bindingState").GetString());
        var recovered = await Ok(await reviewer.Post("/alias-operations/query", AliasRecovery(pool, "approve", "mapping-approve")));
        Assert.Equal("committed", recovered.GetProperty("state").GetString()); Assert.Equal(approved.GetRawText(), recovered.GetProperty("result").GetRawText());
        var proposedRecovery = await Ok(await maker.Post("/alias-operations/query", AliasRecovery(pool, "propose", "mapping")));
        Assert.Equal(proposed.GetRawText(), proposedRecovery.GetProperty("result").GetRawText());
        using var noRead = await maker.Post(path + "/alias-proposals/query", AliasHistory); Assert.Equal(HttpStatusCode.Forbidden, noRead.StatusCode);
        var history = await Ok(await reader.Post(path + "/alias-proposals/query", AliasHistory));
        Assert.Equal("2", history.GetProperty("total").GetString());
        Assert.Equal(1, (await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["revision"].AsInt64);
        Assert.Equal(2, await data.Rows("stockSourceAliases").CountDocumentsAsync(new BsonDocument()));
        foreach (var hidden in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "issuer", "subject", "record", "fence" })
            Assert.DoesNotContain("\"" + hidden + "\"", current.GetRawText() + detail.GetRawText() + history.GetRawText());
    }

    [Fact]
    public async Task Alias_HTTP_requires_actor_csrf_explicit_boolean_and_exact_mapping_versions()
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var anonymous = await data.OpenAsync(null);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool + "/aliases";
        using var noSession = await anonymous.Post(path, AliasCommand(), "anonymous", "\"1\"");
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        foreach (var fault in new[] { "csrf", "origin", "actor", "key", "version", "extra", "numeric", "scope", "boolean", "missing-boolean", "leading-zero" })
        {
            var body = JsonSerializer.SerializeToNode(AliasCommand())!.AsObject();
            if (fault == "extra") body["approved"] = true;
            if (fault == "numeric") body["expectedAliasVersion"] = 0;
            if (fault == "leading-zero") body["expectedAliasVersion"] = "00";
            if (fault == "scope") body["requestedLocation"] = null;
            if (fault == "boolean") body["enabled"] = "true";
            if (fault == "missing-boolean") body.Remove("enabled");
            using var request = maker.Request(path, body, "invalid-" + fault, "\"1\"");
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            if (fault == "origin") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://foreign.example"); }
            if (fault == "actor") { request.Headers.Remove("X-Expected-Actor-Id"); request.Headers.Add("X-Expected-Actor-Id", Reviewer.ToString()); }
            if (fault == "key") request.Headers.Remove("Idempotency-Key");
            if (fault == "version") request.Headers.Remove("If-Match");
            using var response = await maker.Client.SendAsync(request);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.Conflict });
        }
        Assert.Equal(0, await data.Rows("stockSourceAliasProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
}
