using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationCorrectionHttpTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Correction_routes_require_current_actor_and_private_metadata_and_recover_original_partial_correction() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        var allocationInput = data.Input(0, 1000m); var allocationPreview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, allocationInput);
        var original = await data.Service.AllocateAsync(data.Actor, data.VerificationId, allocationPreview.Binding, allocationInput, PaymentAllocationTests.Data.Meta("http-correction-original"));
        var originalId = original["allocationId"].AsInt64; var checker = await PaymentAllocationCorrectionTests.Checker(data);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Source.Read.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Source.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Source.Read.Catalog.Sessions);
            services.RemoveAll<PaymentAllocationService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Source.Read.Catalog.Issued[checker.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var input = new { expectedReceiptVersion = "1", lines = new[] { new { invoiceId = data.InvoiceId.ToString(), expectedVersion = "1", amount = "400.00" } } };
        var shownInput = new UnitToko.Modules.Billing.PaymentAllocationInput(1, [new(new(data.InvoiceId), 1, new(400m))]);
        var preview = await data.Service.PreviewCorrectionAsync(checker, originalId, shownInput);
        async Task<HttpResponseMessage> Send(string kind, string? fault = null, object? overrideBody = null, long? routeId = null) {
            var suffix = kind switch { "query" or "candidates" => "/query", "preview" => "/preview", "recover" => "/operations/query", _ => "" };
            var route = kind == "candidates" ? $"/api/v1/billing/payment-verifications/{routeId ?? data.VerificationId}/allocations/corrections" : $"/api/v1/billing/payment-allocations/{routeId ?? originalId}/corrections";
            object body = overrideBody ?? (kind switch { "query" => new { purpose = "payment-allocation-correction" }, "candidates" => new { purpose = "payment-allocation-correction", before = (string?)null }, "preview" => input,
                "recover" => new { purpose = "operation-recovery", key = "http-allocation-correction" }, _ => new { binding = preview.Binding, input, reason = "Synthetic independent HTTP partial correction" } });
            using var request = new HttpRequestMessage(HttpMethod.Post, route + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "wrong-actor" ? data.Actor.UserId.ToString() : checker.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", "http-allocation-correction");
            if (fault != "revision") request.Headers.Add("If-Match", fault == "stale" ? "\"2\"" : "\"1\"");
            return await client.SendAsync(request);
        }
        foreach (var kind in new[] { "candidates", "query", "preview", "save", "recover" }) foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest), ("wrong-actor", HttpStatusCode.Conflict) }) {
            using var response = await Send(kind, fault); Assert.Equal(status, response.StatusCode);
        }
        foreach (var fault in new[] { "key", "revision", "stale" }) { using var response = await Send("save", fault); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        using (var allocatorClient = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false })) {
            allocatorClient.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Source.Read.Catalog.Issued[data.Actor.UserId].OpaqueToken);
            var allocatorCsrf = (await allocatorClient.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
            using var denied = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/payment-allocations/{originalId}/corrections/preview") { Content = JsonContent.Create(input) };
            denied.Headers.Add("Origin", origin); denied.Headers.Add("X-Expected-Actor-Id", data.Actor.UserId.ToString()); denied.Headers.Add("X-CSRF-Token", allocatorCsrf);
            using var response = await allocatorClient.SendAsync(denied); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        foreach (var kind in new[] { "candidates", "query" }) {
            using var wrongPurpose = await Send(kind, overrideBody: new { purpose = "payment-allocation" }); Assert.Equal(HttpStatusCode.BadRequest, wrongPurpose.StatusCode);
            object extra = kind == "candidates" ? new { purpose = "payment-allocation-correction", before = (string?)null, invoiceIds = new[] { data.InvoiceId.ToString() } } : new { purpose = "payment-allocation-correction", invoiceIds = new[] { data.InvoiceId.ToString() } };
            using var extraMember = await Send(kind, overrideBody: extra); Assert.Equal(HttpStatusCode.BadRequest, extraMember.StatusCode);
        }
        using (var missingCursor = await Send("candidates", overrideBody: new { purpose = "payment-allocation-correction" })) Assert.Equal(HttpStatusCode.BadRequest, missingCursor.StatusCode);
        using (var invalidCursor = await Send("candidates", overrideBody: new { purpose = "payment-allocation-correction", before = "0" })) Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        using (var extraMember = await Send("preview", overrideBody: new { input.expectedReceiptVersion, input.lines, originallyAllocated = "4000.00" })) Assert.Equal(HttpStatusCode.BadRequest, extraMember.StatusCode);
        using (var malformedVersion = await Send("preview", overrideBody: new { expectedReceiptVersion = "01", input.lines })) Assert.Equal(HttpStatusCode.BadRequest, malformedVersion.StatusCode);
        using (var pending = await Send("recover")) { Assert.True(pending.IsSuccessStatusCode, await pending.Content.ReadAsStringAsync()); var value = await pending.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("not_confirmed", value.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, value.GetProperty("result").ValueKind); }
        using (var candidates = await Send("candidates")) {
            Assert.True(candidates.IsSuccessStatusCode, await candidates.Content.ReadAsStringAsync()); Assert.True(candidates.Headers.CacheControl!.NoStore);
            var value = await candidates.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(value.GetProperty("hasMore").GetBoolean());
            var item = Assert.Single(value.GetProperty("items").EnumerateArray()); Assert.Equal(originalId.ToString(), item.GetProperty("originalAllocationId").GetString());
            Assert.Equal("1000.00", item.GetProperty("capacities")[0].GetProperty("originallyAllocated").GetString()); Assert.Equal("0.00", item.GetProperty("capacities")[0].GetProperty("previouslyReversed").GetString());
            PrivateProjection(value);
        }
        using (var olderCandidates = await Send("candidates", overrideBody: new { purpose = "payment-allocation-correction", before = originalId.ToString() })) {
            Assert.True(olderCandidates.IsSuccessStatusCode, await olderCandidates.Content.ReadAsStringAsync()); var value = await olderCandidates.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(value.GetProperty("items").EnumerateArray()); Assert.False(value.GetProperty("hasMore").GetBoolean()); Assert.True(olderCandidates.Headers.CacheControl!.NoStore);
        }
        using (var shown = await Send("preview")) {
            Assert.True(shown.IsSuccessStatusCode, await shown.Content.ReadAsStringAsync()); Assert.True(shown.Headers.CacheControl!.NoStore);
            var value = await shown.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(preview.Binding, value.GetProperty("binding").GetString());
            Assert.Equal("3400.00", value.GetProperty("unapplied").GetString()); Assert.Equal("synthetic-independently-approved-linked-unallocation", value.GetProperty("procedureReference").GetString());
            Assert.Equal("2", value.GetProperty("invoices")[0].GetProperty("version").GetString()); PrivateProjection(value);
        }
        using var saved = await Send("save"); Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync()); Assert.True(saved.Headers.CacheControl!.NoStore); var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(originalId.ToString(), result.GetProperty("originalAllocationId").GetString()); Assert.Equal("billing.payment.allocation.correct", result.GetProperty("operation").GetString()); Assert.Equal("400.00", result.GetProperty("amount").GetString()); Assert.False(result.TryGetProperty("allocationId", out _));
        using (var replay = await Send("save")) { Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync()); Assert.Equal(result.GetRawText(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetRawText()); }
        using (var recovery = await Send("recover")) {
            Assert.True(recovery.IsSuccessStatusCode, await recovery.Content.ReadAsStringAsync()); Assert.True(recovery.Headers.CacheControl!.NoStore);
            var recovered = await recovery.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", recovered.GetProperty("status").GetString()); Assert.Equal(result.GetRawText(), recovered.GetProperty("result").GetRawText()); PrivateProjection(recovered);
        }
        using (var wrongOwner = await Send("recover", routeId: originalId + 1000)) Assert.Equal(HttpStatusCode.BadRequest, wrongOwner.StatusCode);
        using (var balances = await Send("query")) {
            Assert.True(balances.IsSuccessStatusCode, await balances.Content.ReadAsStringAsync()); Assert.True(balances.Headers.CacheControl!.NoStore);
            var value = await balances.Content.ReadFromJsonAsync<JsonElement>(); var current = value.GetProperty("view");
            Assert.Equal("3400.00", current.GetProperty("available").GetString()); Assert.Equal("600.00", current.GetProperty("invoices")[0].GetProperty("paid").GetString());
            Assert.Equal("0.00", current.GetProperty("reservedForRefund").GetString()); Assert.Equal("0.00", current.GetProperty("invoices")[0].GetProperty("credited").GetString());
            Assert.Equal("400.00", value.GetProperty("capacities")[0].GetProperty("previouslyReversed").GetString()); PrivateProjection(value);
        }
        Assert.Equal(1, await data.Rows(PaymentAllocationCorrectionBson.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.eventKind", "unallocated")));
        Assert.Equal(original, await data.Service.RecoverAsync(data.Actor, "http-correction-original"));
    }
    private static void PrivateProjection(JsonElement value) {
        foreach (var field in new[] { "people", "policy", "correctionPolicy", "originalAllocationHash", "originalJournalFingerprint", "accountNumber", "actorGrantVersion" }) Assert.DoesNotContain("\"" + field + "\":", value.GetRawText());
    }
}
