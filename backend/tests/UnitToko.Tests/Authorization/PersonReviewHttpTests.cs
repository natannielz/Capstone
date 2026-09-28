using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Authorization;

public sealed partial class PersonWithdrawalServiceTests
{
    private const string PersonApi = "/api/v1/identity/administration/person-reviews";
    private const string PersonOrigin = "https://portal.unit-toko.co.id";

    [Fact]
    public async Task Http_person_review_routes_default_to_unavailable_after_verified_sign_in()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        await WorkflowProfilesAsync(data);
        using var host = new PersonHttpHost(data);
        using var browser = await host.OpenAsync(MakerId);
        var response = await browser.PostAsync("/targets/search", new { purpose = "access-review", term = "Petugas" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("person_governance_unavailable", (await PersonBody(response)).GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, await data.CountAsync("personReviewPreviews"));
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
    }

    [Fact]
    public async Task Http_person_review_origin_csrf_and_expected_actor_fail_before_private_access_or_writes()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        using var host = new PersonHttpHost(data, service);
        using var browser = await host.OpenAsync(MakerId);
        var beforeReads = await data.CountAsync("authorizationReadEvents");
        foreach (var fault in new[] { "origin-missing", "origin-foreign", "csrf-missing", "csrf-wrong", "actor-missing", "actor-wrong", "actor-leading-zero" })
        {
            using var request = browser.Request("/targets/search", new { purpose = "access-review", term = "Petugas" });
            var expected = HttpStatusCode.Forbidden;
            if (fault == "origin-missing") request.Headers.Remove("Origin");
            if (fault == "origin-foreign") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://foreign.example"); }
            if (fault == "csrf-missing") request.Headers.Remove("X-CSRF-Token");
            if (fault == "csrf-wrong") { request.Headers.Remove("X-CSRF-Token"); request.Headers.Add("X-CSRF-Token", "forged"); }
            if (fault.StartsWith("actor", StringComparison.Ordinal))
            {
                request.Headers.Remove("X-Expected-Actor-Id");
                expected = fault == "actor-wrong" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest;
                if (fault == "actor-wrong") request.Headers.Add("X-Expected-Actor-Id", CheckerId.ToString(CultureInfo.InvariantCulture));
                if (fault == "actor-leading-zero") request.Headers.Add("X-Expected-Actor-Id", "03000");
            }
            var response = await browser.Client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using (var caseVariant = browser.Request("/targets/search", new { purpose = "access-review", term = "Petugas" }))
        {
            caseVariant.RequestUri = new Uri(PersonOrigin + PersonApi.ToUpperInvariant() + "/targets/search");
            caseVariant.Headers.Remove("X-Expected-Actor-Id");
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Client.SendAsync(caseVariant)).StatusCode);
        }
        Assert.Equal(beforeReads, await data.CountAsync("authorizationReadEvents"));
        Assert.Equal(0, await data.CountAsync("personReviewPreviews"));
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
    }

    [Fact]
    public async Task Http_person_review_anonymous_cookie_and_stale_actor_cannot_authenticate_by_submitted_ids()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        using var host = new PersonHttpHost(data, await PreviewServiceAsync(data));
        using var anonymous = await host.OpenAsync(null);
        var response = await anonymous.PostAsync("/proposals/query", new { purpose = "access-review" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var checker = await host.OpenAsync(CheckerId);
        using var stale = checker.Request("/operations/query", new { purpose = "operation-recovery", operation = "person.review.propose", key = "old-tab" });
        stale.Headers.Remove("X-Expected-Actor-Id"); stale.Headers.Add("X-Expected-Actor-Id", MakerId.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.Conflict, (await checker.Client.SendAsync(stale)).StatusCode);
        Assert.Equal(0, await data.CountAsync("authorizationReadEvents"));
    }

    [Fact]
    public async Task Http_person_review_rejects_unknown_missing_and_wrongly_typed_request_fields_and_noncanonical_ids()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        using var host = new PersonHttpHost(data, await PreviewServiceAsync(data));
        using var browser = await host.OpenAsync(MakerId);
        foreach (var payload in new[]
        {
            "{}", "{\"purpose\":null}", "{\"purpose\":\"access-review\",\"role\":\"admin\"}",
            "{\"purpose\":\"access-review\",\"limit\":\"25\"}", "{\"purpose\":\"access-review\",\"offset\":1.5}",
            "{\"purpose\":\"access-review\",\"limit\":101}", "{\"purpose\":\"access-review\",\"offset\":-1}"
        })
        {
            using var request = browser.Request("/proposals/query", new { purpose = "access-review" });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await browser.Client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"Payload {payload} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        foreach (var suffix in new[] { "/proposals/query", "/targets/search" })
        foreach (var field in new[] { "offset", "limit" })
        {
            var payload = new Dictionary<string, object> { ["purpose"] = "access-review", [field] = "25" };
            if (suffix == "/targets/search") payload["term"] = "Petugas";
            var response = await browser.PostAsync(suffix, payload);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"Route {suffix} accepted quoted {field}: {response.StatusCode}, {await response.Content.ReadAsStringAsync()}");
        }
        foreach (var id in new[] { "0", "01", "9223372036854775808", "1.5", "-1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/proposals/" + id + "/query", new { purpose = "access-review" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/proposals", new { previewReference = 123, reason = "Reviewed request" }, "numeric-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/proposals", new { previewReference = Guid.NewGuid().ToString("D"), reason = "Reviewed request", groupId = "3005000" }, "injected-group")).StatusCode);
        Assert.Equal(0, await data.CountAsync("personReviewProposals"));
    }

    [Fact]
    public async Task Http_person_review_versions_and_idempotency_keys_require_exact_header_contracts()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        using var host = new PersonHttpHost(data, await PreviewServiceAsync(data));
        using var browser = await host.OpenAsync(MakerId);
        foreach (var version in new string?[] { null, "1", "W/\"1\"", "\"01\"", "\"1\",\"2\"", "\"9223372036854775808\"", "\"-1\"" })
        {
            using var request = browser.Request("/targets/" + data.GroupId + "/preview", new { purpose = "access-review" });
            if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Client.SendAsync(request)).StatusCode);
        }
        foreach (var key in new string?[] { null, "wrong key", new string('a', 129) })
        {
            using var request = browser.Request("/proposals", new { previewReference = Guid.NewGuid().ToString("D"), reason = "Reviewed request" });
            if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Client.SendAsync(request)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/targets/" + data.GroupId + "/preview", new { purpose = "operation-recovery" }, version: "1")).StatusCode);
        Assert.Equal(0, await data.CountAsync("personReviewPreviews"));
        Assert.Equal(0, await data.CountAsync("idempotencyRecords"));
    }

    [Fact]
    public async Task Http_person_review_complete_preview_proposal_independent_approval_and_receipt_flow_preserves_string_ids_and_exact_replays()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data);
        const long highWater = 9007199254740992;
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", "personReviewProposals" }, { "value", highWater } });
        using var host = new PersonHttpHost(data, service);
        using var maker = await host.OpenAsync(MakerId); using var checker = await host.OpenAsync(CheckerId);
        var found = await OkPersonBody(await maker.PostAsync("/targets/search", new { purpose = "access-review", term = "Petugas" }));
        Assert.Equal("1", found.GetProperty("total").GetString());
        var target = Assert.Single(found.GetProperty("items").EnumerateArray());
        Assert.Equal(data.GroupId.ToString(CultureInfo.InvariantCulture), target.GetProperty("groupId").GetString());
        Assert.Equal(2, target.GetProperty("accounts").GetArrayLength());
        Assert.All(target.GetProperty("accounts").EnumerateArray(), account => Assert.Equal(JsonValueKind.String, account.GetProperty("userId").ValueKind));
        var previewResponse = await maker.PostAsync("/targets/" + data.GroupId + "/preview", new { purpose = "access-review" }, version: "1");
        var preview = await OkPersonBody(previewResponse); Assert.Equal("\"1\"", previewResponse.Headers.ETag?.Tag);
        var submission = new { previewReference = preview.GetProperty("reference").GetString(), reason = "Reviewed person withdrawal from HTTP" };
        var submittedResponse = await maker.PostAsync("/proposals", submission, "http-propose");
        var submitted = await OkPersonBody(submittedResponse);
        Assert.Equal("9007199254740993", submitted.GetProperty("proposalId").GetString());
        Assert.Equal("1", submitted.GetProperty("version").GetString()); Assert.Equal("\"1\"", submittedResponse.Headers.ETag?.Tag);
        Assert.Equal(submitted.GetRawText(), (await OkPersonBody(await maker.PostAsync("/proposals", submission, "http-propose"))).GetRawText());
        var id = submitted.GetProperty("proposalId").GetString();
        var queue = await OkPersonBody(await checker.PostAsync("/proposals/query", new { purpose = "access-review", status = "pending" }));
        Assert.Equal("1", queue.GetProperty("total").GetString());
        var detail = await OkPersonBody(await checker.PostAsync("/proposals/" + id + "/query", new { purpose = "access-review" }));
        Assert.Equal("2", detail.GetProperty("summary").GetProperty("affectedAccountCount").GetString());
        Assert.Contains(detail.GetProperty("summary").GetProperty("allowedActions").EnumerateArray(), item => item.GetString() == "approve");
        foreach (var forbidden in new[] { "sourceSubjectKey", "sourceReference", "manifestDigest", "selectionDigest", "requestDigest", "protectedPayload", "providerSubject" })
            Assert.DoesNotContain(forbidden, detail.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.PostAsync("/proposals/" + id + "/preview", new { purpose = "access-review" }, version: "1")).StatusCode);
        var approvalPreview = await OkPersonBody(await checker.PostAsync("/proposals/" + id + "/preview", new { purpose = "access-review" }, version: "1"));
        var approvalBody = new { previewReference = approvalPreview.GetProperty("reference").GetString(), reason = "Independent HTTP approval" };
        var approvalResponse = await checker.PostAsync("/proposals/" + id + "/approve", approvalBody, "http-approve", "1");
        var approved = await OkPersonBody(approvalResponse);
        Assert.Equal("approved", approved.GetProperty("status").GetString()); Assert.Equal("2", approved.GetProperty("groupVersion").GetString());
        Assert.Equal("\"2\"", approvalResponse.Headers.ETag?.Tag);
        Assert.Equal(approved.GetRawText(), (await OkPersonBody(await checker.PostAsync("/proposals/" + id + "/approve", approvalBody, "http-approve", "1"))).GetRawText());
        var receiptRequest = new { purpose = "operation-recovery", operation = "person.review.approve", key = "http-approve" };
        var receipt = await OkPersonBody(await checker.PostAsync("/operations/query", receiptRequest));
        Assert.True(receipt.GetProperty("confirmed").GetBoolean()); Assert.Equal(approved.GetRawText(), receipt.GetProperty("result").GetRawText());
        var anotherActor = await OkPersonBody(await maker.PostAsync("/operations/query", receiptRequest));
        Assert.False(anotherActor.GetProperty("confirmed").GetBoolean()); Assert.Equal(JsonValueKind.Null, anotherActor.GetProperty("result").ValueKind);
        var missing = await OkPersonBody(await checker.PostAsync("/operations/query", new { purpose = "operation-recovery", operation = "person.review.approve", key = "never-received" }));
        Assert.False(missing.GetProperty("confirmed").GetBoolean()); Assert.Equal(JsonValueKind.Null, missing.GetProperty("result").ValueKind);
        Assert.Equal(2, await data.CountAsync("personReviewHistory")); Assert.Equal(2, await data.CountAsync("idempotencyRecords"));
        foreach (var user in new[] { TargetId, AliasId }) Assert.Equal(2L, (await data.RowAsync("identities", user))["securityVersion"].AsInt64);
    }

    [Fact]
    public async Task Http_person_review_stale_profile_preview_is_rejected_without_consumption_or_proposal()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        using var host = new PersonHttpHost(data, await PreviewServiceAsync(data));
        using var browser = await host.OpenAsync(MakerId);
        var preview = await OkPersonBody(await browser.PostAsync("/targets/" + data.GroupId + "/preview", new { purpose = "access-review" }, version: "1"));
        await data.Rows("accountProfiles").UpdateOneAsync(Id(AliasId), U.Set("displayName", "Changed after preview").Inc("version", 1L));
        var reference = preview.GetProperty("reference").GetString();
        var denied = await browser.PostAsync("/proposals", new { previewReference = reference, reason = "Stale reviewed names" }, "stale-http-preview");
        Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Conflict });
        Assert.Equal(0, await data.CountAsync("personReviewProposals")); Assert.Equal(0, await data.CountAsync("idempotencyRecords"));
        Assert.Equal("ready", (await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", reference)).SingleAsync())["status"].AsString);
    }

    [Fact]
    public async Task Http_person_review_visibility_precedes_search_totals_and_hidden_proposal_reads()
    {
        await using var data = await Data.CreateAsync(workflowFixture); await WorkflowProfilesAsync(data);
        var proposal = await data.ProposeAsync();
        var disclosed = new HashSet<long> { MakerId, CheckerId, TargetId };
        var service = data.NewService(selection: db => new WorkflowSelection(db, data.GroupId, disclosed));
        using var host = new PersonHttpHost(data, service); using var browser = await host.OpenAsync(CheckerId);
        var page = await OkPersonBody(await browser.PostAsync("/targets/search", new { purpose = "access-review", term = "Petugas" }));
        Assert.Equal("0", page.GetProperty("total").GetString()); Assert.Empty(page.GetProperty("items").EnumerateArray());
        var queue = await OkPersonBody(await browser.PostAsync("/proposals/query", new { purpose = "access-review" }));
        Assert.Equal("0", queue.GetProperty("total").GetString()); Assert.Empty(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync("/proposals/" + proposal.ProposalId + "/query", new { purpose = "access-review" })).StatusCode);
    }

    [Fact]
    public async Task Http_person_review_rejection_and_withdrawal_retain_independence_versions_and_exact_retries()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        var service = await PreviewServiceAsync(data); var first = await data.ProposeAsync();
        using var host = new PersonHttpHost(data, service);
        using var maker = await host.OpenAsync(MakerId); using var checker = await host.OpenAsync(CheckerId);
        var decision = new { reason = "Independent request decision" };
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.PostAsync("/proposals/" + first.ProposalId + "/reject", decision, "self-reject-http", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await checker.PostAsync("/proposals/" + first.ProposalId + "/reject", decision, "stale-reject-http", "2")).StatusCode);
        var rejected = await OkPersonBody(await checker.PostAsync("/proposals/" + first.ProposalId + "/reject", decision, "reject-http", "1"));
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Equal(rejected.GetRawText(), (await OkPersonBody(await checker.PostAsync("/proposals/" + first.ProposalId + "/reject", decision, "reject-http", "1"))).GetRawText());
        var second = await service.ProposeAsync(data.Maker, data.GroupId, 1, Metadata("second-http-proposal"));
        var withdrawn = await OkPersonBody(await maker.PostAsync("/proposals/" + second.ProposalId + "/withdraw", decision, "withdraw-http", "1"));
        Assert.Equal("withdrawn", withdrawn.GetProperty("status").GetString());
        Assert.Equal(1L, (await data.RowAsync("identities", TargetId))["securityVersion"].AsInt64);
        Assert.Equal("active", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
    }

    [Fact]
    public async Task Http_person_review_contract_documents_headers_projects_only_explicit_permissions_and_allows_exact_return_path()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        using var host = new PersonHttpHost(data, await PreviewServiceAsync(data)); using var browser = await host.OpenAsync(MakerId);
        using var schema = JsonDocument.Parse(await browser.Client.GetStringAsync("/openapi/v1.json"));
        foreach (var suffix in new[] { "/proposals/query", "/targets/search", "/proposals/{id}/query", "/targets/{id}/preview", "/proposals/{id}/preview", "/proposals", "/proposals/{id}/approve", "/proposals/{id}/reject", "/proposals/{id}/withdraw", "/operations/query" })
        {
            var operation = schema.RootElement.GetProperty("paths").GetProperty(PersonApi + suffix).GetProperty("post");
            foreach (var header in new[] { "X-Expected-Actor-Id", "X-CSRF-Token" })
                Assert.True(Assert.Single(operation.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == header).GetProperty("required").GetBoolean());
            if (suffix.EndsWith("/preview", StringComparison.Ordinal) || suffix.EndsWith("/approve", StringComparison.Ordinal) || suffix.EndsWith("/reject", StringComparison.Ordinal) || suffix.EndsWith("/withdraw", StringComparison.Ordinal))
                Assert.True(Assert.Single(operation.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == "If-Match").GetProperty("required").GetBoolean());
        }
        var current = await browser.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        var actions = current.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("person.review.propose", actions); Assert.Contains("person.review.approve", actions); Assert.Contains("person.review.read", actions);
        Assert.Equal("/administration/person-reviews", ReturnPathPolicy.Require("/administration/person-reviews", new Auth0Options()));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("session")]
    public async Task Http_person_review_expired_policy_or_session_does_not_consume_preview(string expiry)
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        using var host = new PersonHttpHost(data, await PreviewServiceAsync(data)); using var browser = await host.OpenAsync(MakerId);
        var deadline = data.Clock.UtcNow.AddSeconds(1);
        if (expiry == "policy") await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(PolicyId), U.Set("expiresAt", deadline.UtcDateTime));
        var preview = await OkPersonBody(await browser.PostAsync("/targets/" + data.GroupId + "/preview", new { purpose = "access-review" }, version: "1"));
        if (expiry == "session")
        {
            // Set the idle deadline after the authenticated preview request: reading a live
            // cookie slides this deadline. The protected absolute lifetime remains intact.
            Assert.NotNull(await data.Sessions.ReadAsync(data.Issued[MakerId].OpaqueToken));
            await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id),
                U.Set("idleExpiresAt", deadline.UtcDateTime));
        }
        data.Clock.UtcNow = deadline.AddSeconds(1);
        if (expiry == "session") Assert.Null(await data.Sessions.ReadAsync(data.Issued[MakerId].OpaqueToken));
        var reference = preview.GetProperty("reference").GetString();
        var response = await browser.PostAsync("/proposals", new { previewReference = reference, reason = "Expired current authority" }, "expired-http-preview");
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(0, await data.CountAsync("personReviewProposals")); Assert.Equal(0, await data.CountAsync("idempotencyRecords"));
        Assert.Equal("ready", (await data.Rows("personReviewPreviews").Find(new BsonDocument("reference", reference)).SingleAsync())["status"].AsString);
    }

    [Fact]
    public async Task Http_person_review_requires_fresh_factor_for_new_consequence_preview()
    {
        await using var data = await Data.CreateAsync(workflowFixture);
        using var host = new PersonHttpHost(data, await PreviewServiceAsync(data)); using var browser = await host.OpenAsync(MakerId);
        data.Clock.UtcNow += TimeSpan.FromMinutes(6);
        var response = await browser.PostAsync("/targets/" + data.GroupId + "/preview", new { purpose = "access-review" }, version: "1");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await data.CountAsync("personReviewPreviews"));
    }

    private static async Task<JsonElement> PersonBody(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<JsonElement> OkPersonBody(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, response.StatusCode + ": " + body);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(body); return document.RootElement.Clone();
    }
    private sealed class PersonHttpHost : IDisposable
    {
        private readonly Data data;
        private readonly IdentityRuntimeTests.RuntimeKeys keys = new();
        private readonly WebApplicationFactory<Program> factory;
        internal PersonHttpHost(Data data, PersonWithdrawalService? service = null)
        {
            this.data = data;
            var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
            factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                // Keep the real cookie handler/resolver and middleware; only use fixture-owned
                // encrypted sessions and explicitly synthetic governance adapters.
                services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Sessions);
                if (service is not null) { services.RemoveAll<PersonWithdrawalService>(); services.AddSingleton(service); }
            }));
        }
        internal async Task<PersonBrowser> OpenAsync(long? userId)
        {
            var client = factory.CreateClient(new() { BaseAddress = new(PersonOrigin), AllowAutoRedirect = false });
            if (userId.HasValue) client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Issued[userId.Value].OpaqueToken);
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
            return new(client, userId ?? MakerId, csrf.GetProperty("requestToken").GetString()!);
        }
        public void Dispose() { factory.Dispose(); keys.Dispose(); }
    }
    private sealed class PersonBrowser(HttpClient client, long actorId, string csrf) : IDisposable
    {
        internal HttpClient Client => client;
        internal HttpRequestMessage Request(string suffix, object body, string? key = null, string? version = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, PersonApi + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", PersonOrigin); request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", "\"" + version + "\"");
            return request;
        }
        internal async Task<HttpResponseMessage> PostAsync(string suffix, object body, string? key = null, string? version = null)
        {
            using var request = Request(suffix, body, key, version); return await client.SendAsync(request);
        }
        public void Dispose() => client.Dispose();
    }
}
