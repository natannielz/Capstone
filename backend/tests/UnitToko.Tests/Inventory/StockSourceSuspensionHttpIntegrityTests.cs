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
    public async Task Suspension_HTTP_all_routes_require_session_same_origin_csrf_and_exact_actor()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend, Permissions.StockSourceRead);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        using var actor = await data.OpenAsync(Stopper); using var anonymous = await data.OpenAsync(null);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        var preview = await Ok(await actor.Post(path + "/suspension-preview/query", SuspensionPreview));
        foreach (var (route, body) in new[] {
            (path + "/suspension-preview/query", SuspensionPreview), (path + "/suspend", SuspensionCommand(preview)),
            (path + "/suspension/query", SuspensionRead), (path + "/suspensions/query", SuspensionRead),
            (path + "/suspensions/999/query", SuspensionRead), ("/suspension-operations/query", SuspensionRecovery(pool, "unconfirmed")) })
        {
            using var unauthenticated = await anonymous.Post(route, body, "guard", "\"1\"");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            foreach (var fault in new[] { "csrf", "origin", "actor" })
            {
                using var request = actor.Request(route, body, "guard", "\"1\"");
                var name = fault == "csrf" ? "X-CSRF-Token" : fault == "origin" ? "Origin" : "X-Expected-Actor-Id";
                request.Headers.Remove(name);
                if (fault != "csrf") request.Headers.Add(name, fault == "origin" ? "https://foreign.example" : Reader.ToString());
                using var denied = await actor.Client.SendAsync(request);
                Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Conflict });
            }
        }
        Assert.Equal(0, await data.Rows("stockSourceSuspensions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Suspension_HTTP_rejects_missing_headers_malformed_versions_hashes_and_replacement_fields()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var actor = await data.OpenAsync(Stopper);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        var preview = await Ok(await actor.Post(path + "/suspension-preview/query", SuspensionPreview));
        foreach (var fault in new[] { "key", "if-match", "numeric", "leading-zero", "overflow", "negative", "upper-hash", "hash-length", "hash-newline", "reason", "missing", "null", "activate" })
        {
            var body = JsonSerializer.SerializeToNode(SuspensionCommand(preview))!.AsObject();
            switch (fault)
            {
                case "numeric": body["expectedSuspensionVersion"] = 0; break;
                case "leading-zero": body["expectedSuspensionVersion"] = "00"; break;
                case "overflow": body["expectedSuspensionVersion"] = "9223372036854775808"; break;
                case "negative": body["expectedSuspensionVersion"] = "-1"; break;
                case "upper-hash": body["configurationHash"] = new string('A', 64); break;
                case "hash-length": body["configurationHash"] = "a"; break;
                case "hash-newline": body["configurationHash"] = new string('a', 64) + "\n"; break;
                case "reason": body["reason"] = " "; break;
                case "missing": body.Remove("configurationHash"); break;
                case "null": body["expectedSuspensionVersion"] = null; break;
                case "activate": body["active"] = true; break;
            }
            using var invalid = await actor.Post(path + "/suspend", body, fault == "key" ? null : "invalid-" + fault, fault == "if-match" ? null : "\"1\"");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var id in new[] { "0", "01", "9223372036854775808", "1e2" })
        {
            using var invalid = await actor.Post("/pools/" + id + "/suspension-preview/query", SuspensionPreview);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Equal(0, await data.Rows("stockSourceSuspensions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Suspension_HTTP_changed_configuration_stale_versions_and_expired_MFA_require_new_review()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var actor = await data.OpenAsync(Stopper);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        var preview = await Ok(await actor.Post(path + "/suspension-preview/query", SuspensionPreview));
        using var wrongVersion = await actor.Post(path + "/suspend", SuspensionCommand(preview), "wrong-version", "\"2\"");
        Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
        var wrongHash = JsonSerializer.SerializeToNode(SuspensionCommand(preview))!.AsObject(); wrongHash["configurationHash"] = new string('a', 64);
        using var hash = await actor.Post(path + "/suspend", wrongHash, "wrong-hash", "\"1\""); Assert.Equal(HttpStatusCode.Conflict, hash.StatusCode);
        var wrongRevision = JsonSerializer.SerializeToNode(SuspensionCommand(preview))!.AsObject(); wrongRevision["expectedSuspensionVersion"] = "1";
        using var revision = await actor.Post(path + "/suspend", wrongRevision, "wrong-revision", "\"1\""); Assert.Equal(HttpStatusCode.Conflict, revision.StatusCode);
        var settings = await Ok(await maker.Post(path + "/revisions", RevisionCommand("New settings"), "settings", "\"1\""));
        await Ok(await reviewer.Post("/revisions/" + settings.GetProperty("proposalId").GetString() + "/approve", Review, "settings-approve", "\"1\""));
        using var stale = await actor.Post(path + "/suspend", SuspensionCommand(preview), "stale", "\"1\""); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var fresh = await Ok(await actor.Post(path + "/suspension-preview/query", SuspensionPreview));
        await Ok(await actor.Post(path + "/suspend", SuspensionCommand(fresh), "stop", "\"2\""));
        data.Base.Clock.UtcNow += TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1);
        foreach (var (route, body) in new[] { (path + "/suspension-preview/query", SuspensionPreview),
            (path + "/suspend", SuspensionCommand(fresh)), ("/suspension-operations/query", SuspensionRecovery(pool, "stop")) })
        {
            using var expired = await actor.Post(route, body, "stop", "\"2\""); Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        }
        Assert.Equal(1, await data.Rows("stockSourceSuspensions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(5, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Suspension_HTTP_all_historically_linked_scopes_remain_required_after_mapping_is_disabled()
    {
        await using var data = await HttpData.CreateAsync(fixture); await AliasScopes(data);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend, Permissions.StockSourceRead);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var actor = await data.OpenAsync(Stopper);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        var alias = await Ok(await maker.Post(path + "/aliases", AliasCommand(), "alias", "\"1\""));
        await Ok(await reviewer.Post("/alias-proposals/" + alias.GetProperty("proposalId").GetString() + "/approve", Review, "alias-approve", "\"1\""));
        var disable = await Ok(await maker.Post(path + "/aliases", AliasCommand("1", false), "disable", "\"1\""));
        await Ok(await reviewer.Post("/alias-proposals/" + disable.GetProperty("proposalId").GetString() + "/approve", Review, "disable-approve", "\"1\""));
        foreach (var (route, body) in new[] { (path + "/suspension-preview/query", SuspensionPreview), (path + "/suspension/query", SuspensionRead),
            (path + "/suspensions/query", SuspensionRead), (path + "/suspensions/999/query", SuspensionRead),
            ("/suspension-operations/query", SuspensionRecovery(pool, "unknown")) })
        {
            using var denied = await actor.Post(route, body); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await data.Base.AddGrantAsync(await data.ActorAsync(Stopper), Stopper + 100, (Location with { ChannelId = new(104) }).ToScope(), [Permissions.StockSourceSuspend, Permissions.StockSourceRead]);
        var preview = await Ok(await actor.Post(path + "/suspension-preview/query", SuspensionPreview)); Assert.Equal(2, preview.GetProperty("affectedLocations").GetArrayLength());
        await Ok(await actor.Post(path + "/suspend", SuspensionCommand(preview), "stop", "\"1\""));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Stopper + 100), Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        using var recovery = await actor.Post("/suspension-operations/query", SuspensionRecovery(pool, "stop")); Assert.Equal(HttpStatusCode.Forbidden, recovery.StatusCode);
        Assert.Equal(1, await data.Rows("stockSourceSuspensions").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Suspension_HTTP_malformed_queries_and_foreign_recovery_cannot_write_or_disclose()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(Stopper, Location, Permissions.StockSourceSuspend);
        await data.AddActorAsync(Stopper + 1, Location, Permissions.StockSourceSuspend);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        using var actor = await data.OpenAsync(Stopper); using var other = await data.OpenAsync(Stopper + 1);
        var pool = await CreatePool(maker, reviewer); var path = "/pools/" + pool;
        foreach (var fault in new[] { "purpose", "offset", "limit", "extra", "numeric-page" })
        {
            var body = JsonSerializer.SerializeToNode(SuspensionRead)!.AsObject();
            if (fault == "purpose") body["purpose"] = "unknown";
            if (fault == "offset") body["offset"] = -1;
            if (fault == "limit") body["limit"] = 101;
            if (fault == "extra") body["actorId"] = Maker;
            if (fault == "numeric-page") body["offset"] = "0";
            using var invalid = await reader.Post(path + "/suspensions/query", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var fault in new[] { "operation", "key", "pool", "extra" })
        {
            var body = JsonSerializer.SerializeToNode(SuspensionRecovery(pool, "unknown"))!.AsObject();
            if (fault == "operation") body["operation"] = "stock.source.approve";
            if (fault == "key") body["key"] = "unsafe key";
            if (fault == "pool") body["physicalPoolId"] = 1;
            if (fault == "extra") body["reason"] = "Replacement work";
            using var invalid = await actor.Post("/suspension-operations/query", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var preview = await Ok(await actor.Post(path + "/suspension-preview/query", SuspensionPreview));
        await Ok(await actor.Post(path + "/suspend", SuspensionCommand(preview), "stop", "\"1\""));
        var foreign = await Ok(await other.Post("/suspension-operations/query", SuspensionRecovery(pool, "stop")));
        Assert.Equal("not_confirmed", foreign.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, foreign.GetProperty("result").ValueKind);
        Assert.Equal(1, await data.Rows("stockSourceSuspensions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(3, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
}
