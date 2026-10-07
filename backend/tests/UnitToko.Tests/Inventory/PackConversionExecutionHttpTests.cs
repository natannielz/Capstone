using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using UnitToko.Platform.Inventory;

namespace UnitToko.Tests.Inventory;

/// <summary>Real API/cookie/CSRF refusals. The approved local OTP session is synthetic;
/// this negative fixture supplies no conversion rule, stock or value acceptance.</summary>
[Collection("Mongo")]
public sealed class PackConversionExecutionHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual_session_strict_execution_JSON_purposes_and_required_headers_refuse_before_any_business_effect() {
        await using var data = await PackConversionRuleHttpTests.HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(PackConversionRuleHttpTests.Maker);
        using var anonymous = await data.OpenAsync(null);
        var input = new JsonObject { ["location"] = new JsonObject { ["kind"] = "fulfillmentWarehouse", ["organizationId"] = "100", ["shopId"] = "101",
            ["warehouseId"] = "102", ["channelId"] = "103", ["branchId"] = null, ["divisionId"] = null },
            ["ruleId"] = "1", ["ruleVersion"] = "1", ["sourceBalanceId"] = "1", ["steps"] = 1 };
        var expected = new JsonObject { ["sourceBalanceRevision"] = "1", ["poolRevision"] = "1", ["poolFence"] = "0", ["lotValueRevision"] = "1" };
        var execute = new JsonObject { ["input"] = input.DeepClone(), ["expected"] = expected.DeepClone(), ["binding"] = new string('a', 64), ["reason"] = "Synthetic refusal boundary" };
        var before = await data.PhysicalAsync();
        using (var response = await anonymous.Post("/executions/preview/query", new { purpose = "pack-conversion-preview", input }))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        foreach (var (key, version) in new (string?, string?)[] { (null, "\"1\""), ("conversion-boundary", null), ("conversion-boundary", "\"2\"") }) {
            using var response = await maker.Post("/executions", execute, key, version);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var fault in new[] { "numeric-id", "leading-zero", "quoted-steps", "oversized-steps", "unknown-input", "unknown-expected", "noncanonical-zero" }) {
            var body = execute.DeepClone().AsObject();
            if (fault == "numeric-id") body["input"]!["ruleId"] = 1;
            if (fault == "leading-zero") body["input"]!["sourceBalanceId"] = "01";
            if (fault == "quoted-steps") body["input"]!["steps"] = "1";
            if (fault == "oversized-steps") body["input"]!["steps"] = 1000001;
            if (fault == "unknown-input") body["input"]!["physicalPoolId"] = "1";
            if (fault == "unknown-expected") body["expected"]!["privatePerson"] = "1";
            if (fault == "noncanonical-zero") body["expected"]!["poolFence"] = "00";
            using var response = await maker.Post("/executions", body, "conversion-boundary-" + fault, "\"1\"");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var response = await maker.Post("/executions/query", new { purpose = "unrelated-history", location = input["location"], limit = 25 }))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var request = maker.Request("/executions/query", new { purpose = "pack-conversion-history", location = input["location"], limit = 25 })) {
            request.Headers.Remove("X-CSRF-Token");
            using var response = await maker.Client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(before, await data.PhysicalAsync());
        Assert.Equal(0L, await data.Count("packConversions"));
        Assert.Equal(0L, await data.Count(LotValueStore.AvailableHeads)); Assert.Equal(0L, await data.Count(LotValueStore.Transitions));
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            Assert.Equal(0L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, "inventory.pack-conversion.execute")));
    }
}
