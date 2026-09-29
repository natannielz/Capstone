using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    [Fact]
    public async Task Policy_HTTP_every_route_requires_session_origin_csrf_and_original_actor()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var anonymous = await data.OpenAsync(null);
        var pool = await CreatePool(maker, reviewer); await PolicyOnlyGrants(data); var path = "/pools/" + pool;
        var preview = await Ok(await maker.Post(path + "/policy-preview/query", PolicyPreview));
        var proposed = await Ok(await maker.Post(path + "/policy-proposals", PolicyCommand(preview), "proposal", "\"1\""));
        var id = proposed.GetProperty("proposalId").GetString(); var effects = await PolicyHttpEffects(data);
        foreach (var (route, body) in new[] {
            (path + "/policy-preview/query", PolicyPreview), (path + "/policy-proposals", PolicyCommand(preview)),
            (path + "/policy-proposals/" + id + "/review/query", PolicyReview("withdraw")),
            (path + "/policy/query", PolicyHistory), (path + "/policy-proposals/query", PolicyHistory),
            (path + "/policy-proposals/" + id + "/query", PolicyHistory),
            ("/policy-proposals/" + id + "/approve", Review), ("/policy-proposals/" + id + "/reject", Review),
            ("/policy-proposals/" + id + "/withdraw", Review), ("/policy-operations/query", PolicyRecovery(pool, "propose", "proposal")) })
        {
            using var noSession = await anonymous.Post(route, body, "guard", "\"1\""); Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
            foreach (var fault in new[] { "csrf", "origin", "actor" })
            {
                using var request = maker.Request(route, body, "guard", "\"1\"");
                var header = fault == "csrf" ? "X-CSRF-Token" : fault == "origin" ? "Origin" : "X-Expected-Actor-Id";
                request.Headers.Remove(header);
                if (fault != "csrf") request.Headers.Add(header, fault == "origin" ? "https://foreign.example" : Reviewer.ToString());
                using var denied = await maker.Client.SendAsync(request);
                Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Conflict });
            }
        }
        Assert.Equal(effects, await PolicyHttpEffects(data));
    }
    [Fact]
    public async Task Policy_HTTP_rejects_ambiguous_inputs_missing_fields_unknown_nested_fields_and_implied_writer_facts()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        var pool = await CreatePool(maker, reviewer); await PolicyOnlyGrants(data); var path = "/pools/" + pool;
        var preview = await Ok(await maker.Post(path + "/policy-preview/query", PolicyPreview));
        foreach (var fault in new[] { "key", "version", "numeric-version", "leading-zero", "overflow", "negative", "null-version",
            "upper-hash", "short-hash", "hash-newline", "missing-hash", "reason", "active", "missing-input", "null-input",
            "missing-tenant", "nested-extra", "numeric-feed", "feed-zero-prefix", "feed-overflow", "feed-negative", "web-feed",
            "web-tenant", "writer", "external-writer", "missing-procedure", "blank-procedure", "null-procedure" })
        {
            var body = JsonSerializer.SerializeToNode(PolicyCommand(preview))!.AsObject();
            switch (fault)
            {
                case "numeric-version": body["expectedPolicyVersion"] = 0; break;
                case "leading-zero": body["expectedSuspensionVersion"] = "00"; break;
                case "overflow": body["expectedPolicyVersion"] = "9223372036854775808"; break;
                case "negative": body["expectedPolicyVersion"] = "-1"; break;
                case "null-version": body["expectedPolicyVersion"] = null; break;
                case "upper-hash": body["configurationHash"] = new string('A', 64); break;
                case "short-hash": body["configurationHash"] = "a"; break;
                case "hash-newline": body["configurationHash"] = new string('a', 64) + "\n"; break;
                case "missing-hash": body.Remove("configurationHash"); break;
                case "reason": body["reason"] = " "; break;
                case "active": body["active"] = true; break;
                case "missing-input": body.Remove("input"); break;
                case "null-input": body["input"] = null; break;
                case "missing-tenant": body["input"]!.AsObject().Remove("externalTenant"); break;
                case "nested-extra": body["input"]!["safetyBufferBaseUnits"] = "0"; break;
                case "numeric-feed": body["input"]!["feedIntervalMilliseconds"] = 0; break;
                case "feed-zero-prefix": body["input"]!["feedIntervalMilliseconds"] = "00"; break;
                case "feed-overflow": body["input"]!["feedIntervalMilliseconds"] = "9223372036854775808"; break;
                case "feed-negative": body["input"]!["feedIntervalMilliseconds"] = "-1"; break;
                case "web-feed": body["input"]!["feedIntervalMilliseconds"] = "1"; break;
                case "web-tenant": body["input"]!["externalTenant"] = "tenant"; break;
                case "writer": body["input"]!["writer"] = "unknown"; break;
                case "external-writer": body["input"]!["writer"] = "externalSystem"; break;
                case "missing-procedure": body["input"]!.AsObject().Remove("outageProcedureReference"); break;
                case "blank-procedure": body["input"]!["outageProcedureReference"] = " "; break;
                case "null-procedure": body["input"]!["reconciliationProcedureReference"] = null; break;
            }
            using var invalid = await maker.Post(path + "/policy-proposals", body, fault == "key" ? null : "invalid-" + fault, fault == "version" ? null : "\"1\"");
            Assert.True(invalid.StatusCode == HttpStatusCode.BadRequest, fault + ": " + invalid.StatusCode + " " + await invalid.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, await data.Rows("stockSourcePolicyProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Policy_HTTP_query_and_recovery_validation_cannot_resubmit_or_disclose_another_actors_receipt()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddActorAsync(48102, Location, Permissions.StockSourcePolicyPropose);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        using var reader = await data.OpenAsync(Reader); using var other = await data.OpenAsync(48102);
        var pool = await CreatePool(maker, reviewer); await PolicyOnlyGrants(data); var path = "/pools/" + pool;
        var preview = await Ok(await maker.Post(path + "/policy-preview/query", PolicyPreview));
        var proposal = await Ok(await maker.Post(path + "/policy-proposals", PolicyCommand(preview), "proposal", "\"1\""));
        var id = proposal.GetProperty("proposalId").GetString(); var effects = await PolicyHttpEffects(data);
        foreach (var fault in new[] { "purpose", "offset", "limit", "extra", "numeric-page", "offset-ceiling" })
        {
            var body = JsonSerializer.SerializeToNode(PolicyHistory)!.AsObject();
            if (fault == "purpose") body["purpose"] = "unknown";
            if (fault == "offset") body["offset"] = -1;
            if (fault == "limit") body["limit"] = 101;
            if (fault == "extra") body["actorId"] = Maker;
            if (fault == "numeric-page") body["offset"] = "0";
            if (fault == "offset-ceiling") body["offset"] = 10001;
            using var invalid = await reader.Post(path + "/policy-proposals/query", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var fault in new[] { "operation", "key", "pool", "extra", "missing-pool", "missing-operation" })
        {
            var body = JsonSerializer.SerializeToNode(PolicyRecovery(pool, "propose", "proposal"))!.AsObject();
            if (fault == "operation") body["operation"] = "stock.source.propose";
            if (fault == "key") body["key"] = "unsafe key";
            if (fault == "pool") body["physicalPoolId"] = 1;
            if (fault == "extra") body["input"] = new JsonObject();
            if (fault == "missing-pool") body.Remove("physicalPoolId");
            if (fault == "missing-operation") body.Remove("operation");
            using var invalid = await maker.Post("/policy-operations/query", body); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var action in new[] { "propose", "Approve", "", "read" })
        {
            using var invalid = await maker.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview(action));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var invalidId in new[] { "0", "01", "9223372036854775808", "1e2" })
        {
            using var invalid = await maker.Post("/pools/" + invalidId + "/policy-preview/query", PolicyPreview);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var foreign = await Ok(await other.Post("/policy-operations/query", PolicyRecovery(pool, "propose", "proposal")));
        Assert.Equal("not_confirmed", foreign.GetProperty("state").GetString());
        using var withdraw = await other.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview("withdraw"));
        Assert.Equal(HttpStatusCode.Forbidden, withdraw.StatusCode);
        Assert.Equal(effects, await PolicyHttpEffects(data));
    }
    [Fact]
    public async Task Policy_HTTP_checker_requires_independent_people_fresh_MFA_and_current_grants()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        var pool = await CreatePool(maker, reviewer); await PolicyOnlyGrants(data); var path = "/pools/" + pool;
        var preview = await Ok(await maker.Post(path + "/policy-preview/query", PolicyPreview));
        var proposal = await Ok(await maker.Post(path + "/policy-proposals", PolicyCommand(preview), "proposal", "\"1\""));
        var id = proposal.GetProperty("proposalId").GetString();
        using var wrongVersion = await reviewer.Post("/policy-proposals/" + id + "/approve", Review, "wrong", "\"2\"");
        Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
        using var noVersion = await reviewer.Post("/policy-proposals/" + id + "/approve", Review, "missing");
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        data.Base.Clock.UtcNow += TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1);
        using var stale = await reviewer.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview("approve"));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        using var freshReviewer = await data.OpenAsync(Reviewer);
        await Ok(await freshReviewer.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview("approve")));
        await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", Reviewer),
            Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        using var missingPerson = await freshReviewer.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview("approve"));
        Assert.Equal(HttpStatusCode.Forbidden, missingPerson.StatusCode);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", Maker),
            Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        using var revoked = await maker.Post("/policy-operations/query", PolicyRecovery(pool, "propose", "proposal"));
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(0, await data.Rows("stockSourcePolicyDecisions").CountDocumentsAsync(new BsonDocument()));
    }
}