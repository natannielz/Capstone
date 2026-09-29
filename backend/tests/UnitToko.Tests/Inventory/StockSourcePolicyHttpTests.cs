using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;

namespace UnitToko.Tests.Inventory;

public sealed partial class StockSourceSetupHttpTests
{
    private static object PolicyPreview => new { purpose = "inventory-source-policy-preview" };
    private static object PolicyHistory => new { purpose = "inventory-source-policy-history" };
    private static object PolicyReview(string action) => new { purpose = "inventory-source-policy-review", action };
    private static object PolicyCommand(JsonElement preview) => new
    {
        expectedSuspensionVersion = preview.GetProperty("suspensionVersion").GetString(),
        expectedPolicyVersion = preview.GetProperty("policyVersion").GetString(),
        configurationHash = preview.GetProperty("configurationHash").GetString(),
        input = new { writer = "unitToko", externalTenant = (string?)null, feedIntervalMilliseconds = "0",
            reconciliationProcedureReference = "synthetic-reconciliation", outageProcedureReference = "synthetic-outage" },
        reason = "Synthetic operating-policy proposal"
    };
    private static object PolicyRecovery(string pool, string operation, string key) =>
        new { purpose = "operation-recovery", physicalPoolId = pool, operation = "stock.source.policy." + operation, key };
    private static async Task PolicyOnlyGrants(HttpData data)
    {
        foreach (var (actor, permission) in new[] { (Maker, Permissions.StockSourcePolicyPropose), (Reviewer, Permissions.StockSourcePolicyApprove) })
            await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", actor),
                Builders<BsonDocument>.Update.Set("permissions", new BsonArray { permission }));
    }
    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("reject", "rejected")]
    [InlineData("withdraw", "withdrawn")]
    public async Task Policy_HTTP_action_only_review_large_ids_private_history_and_four_operation_recovery(string action, string status)
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        foreach (var collection in new[] { "inventoryPools", "stockSourcePolicyProposals", "stockSourcePolicyDecisions", "stockSourcePolicies" })
            await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", collection),
                Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        var pool = await CreatePool(maker, reviewer); await PolicyOnlyGrants(data); var path = "/pools/" + pool;
        var empty = await Ok(await reader.Post(path + "/policy/query", PolicyHistory));
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("policy").ValueKind); Assert.False(empty.GetProperty("bindingCurrent").GetBoolean());
        var preview = await Ok(await maker.Post(path + "/policy-preview/query", PolicyPreview));
        Assert.Equal(pool, preview.GetProperty("physicalPoolId").GetString()); Assert.Equal("0", preview.GetProperty("policyVersion").GetString());
        Assert.Equal("0", preview.GetProperty("configuration").GetProperty("settings").GetProperty("safetyBufferBaseUnits").GetString());
        var proposed = await Ok(await maker.Post(path + "/policy-proposals", PolicyCommand(preview), "policy-propose", "\"1\""));
        Assert.Equal("9007199254740993", proposed.GetProperty("proposalId").GetString());
        Assert.Equal("proposed", proposed.GetProperty("status").GetString());
        var id = proposed.GetProperty("proposalId").GetString(); var actor = action == "withdraw" ? maker : reviewer;
        var review = await Ok(await actor.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview(action)));
        Assert.Equal(action, review.GetProperty("action").GetString()); Assert.True(review.GetProperty("bindingCurrent").GetBoolean());
        Assert.Equal(pool, review.GetProperty("proposal").GetProperty("physicalPoolId").GetString());
        foreach (var user in new[] { maker, reviewer })
        {
            using var noHistory = await user.Post(path + "/policy-proposals/query", PolicyHistory);
            Assert.Equal(HttpStatusCode.Forbidden, noHistory.StatusCode);
        }
        using var readOnly = await reader.Post(path + "/policy-proposals/" + id + "/review/query", PolicyReview(action));
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        var decided = await Ok(await actor.Post("/policy-proposals/" + id + "/" + action, Review, "policy-" + action, "\"1\""));
        Assert.Equal(status, decided.GetProperty("status").GetString()); Assert.Equal("9007199254740993", decided.GetProperty("decisionId").GetString());
        Assert.Equal(action == "approve" ? JsonValueKind.String : JsonValueKind.Null, decided.GetProperty("policyId").ValueKind);
        var effects = await PolicyHttpEffects(data);
        foreach (var (user, operation, expected) in new[] { (maker, "propose", proposed), (actor, action, decided) })
        {
            var recovered = await Ok(await user.Post("/policy-operations/query", PolicyRecovery(pool, operation, "policy-" + operation)));
            Assert.Equal("committed", recovered.GetProperty("state").GetString()); Assert.Equal(expected.GetRawText(), recovered.GetProperty("result").GetRawText());
            var missing = await Ok(await user.Post("/policy-operations/query", PolicyRecovery(pool, operation, "unknown")));
            Assert.Equal("not_confirmed", missing.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, missing.GetProperty("result").ValueKind);
        }
        var page = await Ok(await reader.Post(path + "/policy-proposals/query", PolicyHistory));
        Assert.Equal("1", page.GetProperty("total").GetString()); Assert.Single(page.GetProperty("items").EnumerateArray());
        var detail = await Ok(await reader.Post(path + "/policy-proposals/" + id + "/query", PolicyHistory));
        Assert.Equal(status, detail.GetProperty("status").GetString()); Assert.Equal(detail.GetRawText(), page.GetProperty("items")[0].GetRawText());
        var latest = await Ok(await reader.Post(path + "/policy/query", PolicyHistory));
        if (action == "approve") { Assert.True(latest.GetProperty("bindingCurrent").GetBoolean()); Assert.Equal(detail.GetRawText(), latest.GetProperty("policy").GetRawText()); }
        else Assert.Equal(JsonValueKind.Null, latest.GetProperty("policy").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Ok(await reader.Post(path + "/policy-proposals/999/query", PolicyHistory))).ValueKind);
        foreach (var hidden in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "correlationId", "schemaVersion", "issuer", "subject", "fence" })
            Assert.DoesNotContain("\"" + hidden + "\"", preview.GetRawText() + review.GetRawText() + detail.GetRawText() + page.GetRawText() + latest.GetRawText() + decided.GetRawText());
        Assert.Equal(effects, await PolicyHttpEffects(data));
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["active"].AsBoolean);
        Assert.Equal(4, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument()));
    }
    private static async Task<string[]> PolicyHttpEffects(HttpData data)
    {
        var values = new List<string>();
        foreach (var name in new[] { "stockSourcePolicyProposals", "stockSourcePolicyDecisions", "stockSourcePolicies",
            "idCounters", "auditEvents", "idempotencyRecords", "inventoryPools" })
            values.Add(name + ":" + string.Join("|", (await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(row => row.ToJson())));
        return values.ToArray();
    }
}