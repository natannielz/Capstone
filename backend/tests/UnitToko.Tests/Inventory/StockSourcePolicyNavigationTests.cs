using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    [Fact]
    public async Task Policy_task_discovery_preserves_scoped_maker_checker_and_stale_MFA_entry_without_history()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var checker = await data.OpenAsync(Reviewer);
        var pool = await CreatePool(maker, checker); await PolicyOnlyGrants(data);
        foreach (var (actor, permission) in new[] { (maker, Permissions.StockSourcePolicyPropose), (checker, Permissions.StockSourcePolicyApprove) })
        {
            var current = await actor.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
            Assert.Equal(new[] { permission }, current.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray());
            using var catalog = await actor.Client.GetAsync("/api/v1/access/catalog"); Assert.Equal(HttpStatusCode.Forbidden, catalog.StatusCode);
            using var history = await actor.Post("/pools/" + pool + "/policy-proposals/query", PolicyHistory); Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        }
        var preview = await Ok(await maker.Post("/pools/" + pool + "/policy-preview/query", PolicyPreview));
        var proposal = await Ok(await maker.Post("/pools/" + pool + "/policy-proposals", PolicyCommand(preview), "task-policy", "\"1\""));
        var id = proposal.GetProperty("proposalId").GetString();
        data.Base.Clock.UtcNow = data.Base.Clock.UtcNow.AddMinutes(5).AddSeconds(1);
        var stale = await checker.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Equal(new[] { Permissions.StockSourcePolicyApprove }, stale.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray());
        using var denied = await checker.Post("/pools/" + pool + "/policy-proposals/" + id + "/review/query", PolicyReview("approve"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await Ok(await maker.Post("/pools/" + pool + "/policy-proposals/" + id + "/review/query", PolicyReview("withdraw")));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Reviewer),
            Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        var revoked = await checker.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Empty(revoked.GetProperty("allowedActions").EnumerateArray());
    }

    [Fact]
    public void Policy_identity_return_path_is_explicit_and_rejects_modified_destinations()
    {
        const string path = "/inventory/sources/policies";
        var options = new Auth0Options();
        Assert.Equal(path, ReturnPathPolicy.Require(path, options));
        foreach (var suffix in new[] { "/", "/other", "?returnPath=https://outside.test", "#other", "\n", "/../aliases" })
            Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require(path + suffix, options));
    }
}