using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    private const long Stopper = 48100;
    private static object SuspensionPreview => new { purpose = "inventory-source-suspension-preview" };
    private static object SuspensionRead => new { purpose = "inventory-source-suspension-history" };
    private static object SuspensionCommand(JsonElement preview) => new
        { expectedSuspensionVersion = preview.GetProperty("suspensionVersion").GetString(), configurationHash = preview.GetProperty("configurationHash").GetString(), reason = "Synthetic stock-source stop" };
    private static object SuspensionRecovery(string pool, string key) => new
        { purpose = "operation-recovery", physicalPoolId = pool, operation = "stock.source.suspend", key };

    [Fact]
    public async Task Suspension_task_discovery_keeps_MFA_entry_without_history_or_administration_and_disappears_after_revocation()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend);
        using var actor = await data.OpenAsync(Stopper);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        var pool = await CreatePool(maker, reviewer);
        var fresh = await actor.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Equal(new[] { Permissions.StockSourceSuspend }, fresh.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray());
        using var catalog = await actor.Client.GetAsync("/api/v1/access/catalog");
        Assert.Equal(HttpStatusCode.Forbidden, catalog.StatusCode);
        using var history = await actor.Post("/pools/" + pool + "/suspensions/query", SuspensionRead);
        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        data.Base.Clock.UtcNow = data.Base.Clock.UtcNow.AddMinutes(5).AddSeconds(1);
        var stale = await actor.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Equal(new[] { Permissions.StockSourceSuspend }, stale.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray());
        using var preview = await actor.Post("/pools/" + pool + "/suspension-preview/query", SuspensionPreview);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Stopper),
            Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        var revoked = await actor.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Empty(revoked.GetProperty("allowedActions").EnumerateArray());
    }
    [Fact]
    public async Task Suspension_HTTP_command_only_preview_exact_stop_history_and_receipt_recovery()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        using var actor = await data.OpenAsync(Stopper); using var reader = await data.OpenAsync(Reader);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceSuspensions"), Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        var preview = await Ok(await actor.Post(path + "/suspension-preview/query", SuspensionPreview));
        Assert.Equal(pool, preview.GetProperty("physicalPoolId").GetString()); Assert.Equal("0", preview.GetProperty("suspensionVersion").GetString());
        Assert.Single(preview.GetProperty("affectedLocations").EnumerateArray());
        var body = SuspensionCommand(preview);
        var stopped = await Ok(await actor.Post(path + "/suspend", body, "stop", "\"1\""));
        Assert.Equal("9007199254740993", stopped.GetProperty("suspensionId").GetString()); Assert.Equal("suspended", stopped.GetProperty("status").GetString());
        Assert.Equal(stopped.GetRawText(), (await Ok(await actor.Post(path + "/suspend", body, "stop", "\"1\""))).GetRawText());
        var recovered = await Ok(await actor.Post("/suspension-operations/query", SuspensionRecovery(pool, "stop")));
        Assert.Equal("committed", recovered.GetProperty("state").GetString()); Assert.Equal(stopped.GetRawText(), recovered.GetProperty("result").GetRawText());
        var missing = await Ok(await actor.Post("/suspension-operations/query", SuspensionRecovery(pool, "unknown")));
        Assert.Equal("not_confirmed", missing.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, missing.GetProperty("result").ValueKind);
        using var noRead = await actor.Post(path + "/suspensions/query", SuspensionRead); Assert.Equal(HttpStatusCode.Forbidden, noRead.StatusCode);
        var latest = await Ok(await reader.Post(path + "/suspension/query", SuspensionRead));
        var detail = await Ok(await reader.Post(path + "/suspensions/9007199254740993/query", SuspensionRead));
        Assert.Equal(latest.GetRawText(), detail.GetRawText()); Assert.Equal("1", detail.GetProperty("suspensionVersion").GetString());
        var page = await Ok(await reader.Post(path + "/suspensions/query", SuspensionRead));
        Assert.Equal("1", page.GetProperty("total").GetString()); Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, (await Ok(await reader.Post(path + "/suspensions/999/query", SuspensionRead))).ValueKind);
        foreach (var field in new[] { "suspendedBy", "auditId", "record", "bodyHash", "correlationId", "input", "fence", "issuer", "subject", "handoffReference" })
            Assert.DoesNotContain("\"" + field + "\"", preview.GetRawText() + detail.GetRawText() + page.GetRawText());
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["active"].AsBoolean);
        Assert.Equal(1L, await data.Rows("stockSourceSuspensions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(3L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Suspension_HTTP_preview_requires_action_authority_while_history_remains_separate()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        using var denied = await reader.Post(path + "/suspension-preview/query", SuspensionPreview); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var absent = await Ok(await reader.Post(path + "/suspension/query", SuspensionRead)); Assert.Equal(JsonValueKind.Null, absent.ValueKind);
        var page = await Ok(await reader.Post(path + "/suspensions/query", SuspensionRead)); Assert.Equal("0", page.GetProperty("total").GetString());
    }
}