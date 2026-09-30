using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Api.Identity;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Documents;
using W = UnitToko.Platform.Billing.PaymentReviewActionRecords;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentReviewActionTests(MongoFixture fixture)
{
    internal static async Task Grant(PaymentVerificationTestData data) => await data.Read.Catalog.Base.AddGrantAsync(data.Reviewer, 74600,
        Catalog.CatalogAuthoringTestData.Shop, [Permissions.PaymentReviewManage]);
    [Fact]
    public void Actions_preserve_closed_reviews_and_management_is_a_separate_independent_MFA_permission() {
        Assert.Equal(("assigned", (long?)2), PaymentReviewActionPolicy.Apply("pending", null, "claim", 2));
        Assert.Equal(("pending", (long?)null), PaymentReviewActionPolicy.Apply("assigned", 2, "release", 3));
        Assert.Throws<DomainValidationException>(() => PaymentReviewActionPolicy.Apply("rejected", 2, "claim", 3));
        Assert.Throws<DomainValidationException>(() => PaymentReviewActionPolicy.Apply("replacement_requested", 2, "claim", 3));
        Assert.Throws<DomainValidationException>(() => PaymentReviewActionPolicy.Apply("assigned", 2, "claim", 2));
        Assert.Throws<DomainValidationException>(() => PaymentReviewActionPolicy.Apply("pending", null, "release", 2));
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.PaymentReviewManage)); Assert.True(PersonApprovalPolicy.RequiresEvidence(Permissions.PaymentReviewManage));
        Assert.False(ApprovalPolicy.Evaluate(2, Permissions.PaymentReviewManage, new(1, 1, RecordedBy: 2)).Allowed);
        Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Catalog[Permissions.PaymentReviewManage].Assurance.MaximumAge);
    }
    [WindowsStorageFact]
    public async Task Ownership_replacement_request_and_original_key_recovery_keep_immutable_source_and_block_stale_verification_and_correction_without_money() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await Grant(data);
        var before = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); var payment = await data.PreviewAsync();
        var view = await data.Service.ReadReviewActionsAsync(data.Reader, data.Read.DeclarationId); Assert.Equal(0, view.ReviewRevision); Assert.False(view.Verified);
        var input = new PaymentReviewActionInput(view.Binding, "claim"); var meta = PaymentVerificationTestData.Meta("claim-review");
        var claimed = await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, input, meta);
        Assert.Equal(claimed, await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, input, meta));
        view = await data.Service.ReadReviewActionsAsync(data.Reader, data.Read.DeclarationId); Assert.Equal(1, view.ReviewRevision); Assert.Equal(data.Reviewer.UserId, view.Latest!["assigneeId"].AsInt64);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("stale-claim")));
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 1, new(view.Binding, "release"), PaymentVerificationTestData.Meta("release-review"));
        view = await data.Service.ReadReviewActionsAsync(data.Reader, data.Read.DeclarationId); Assert.Equal("pending", view.Latest!["state"]); Assert.True(view.Latest["assigneeId"].IsBsonNull);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 2, new(view.Binding, "request_replacement"), PaymentVerificationTestData.Meta("replace-review"));
        var closed = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.VerifyAsync(payment)); Assert.Equal("payment_review_closed", closed.Code);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.PreviewAsync());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Read.Service.CorrectReviewAsync(data.Reviewer, data.Read.DeclarationId, 1, data.Corrections.Binding,
            PaymentCorrectionTests.Changed(data.Corrections, "4500.00"), PaymentVerificationTestData.Meta("closed-review-correction")));
        Assert.Equal(claimed, await data.Service.RecoverReviewActionAsync(data.Reviewer, data.Read.DeclarationId, meta.IdempotencyKey));
        Assert.Equal(3, await data.Rows(W.Versions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", W.Operation }, { "key", meta.IdempotencyKey } });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverReviewActionAsync(data.Reviewer, data.Read.DeclarationId, meta.IdempotencyKey));
    }
    [WindowsStorageFact]
    public async Task Buyer_reads_only_their_exact_follow_up_without_staff_bank_details_or_money_effects_and_missing_history_denies() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await Grant(data);
        var buyer = data.Read.Actor; var owner = data.Read.Payment.Service; var id = data.Read.DeclarationId;
        var original = await owner.ReadAsync(buyer, id); var journals = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var versions = await data.Rows(PaymentDraftBson.Versions).Find(new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", 1)).ToListAsync();
        var first = await owner.ReadFollowUpAsync(buyer, id, original.Revision); Assert.Equal("pending", first.State); Assert.Equal(0, first.ReviewRevision); Assert.Null(first.RecordedAt);
        Assert.Equal(original.Submission!.Id.Value, first.SubmissionId); Assert.Equal(original.Submission.Evidence, first.Evidence); Assert.Equal(original.Submission.SubmittedAt, first.SubmittedAt);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.ReadFollowUpAsync(data.Reader, id, original.Revision));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => owner.ReadFollowUpAsync(buyer, id, original.Revision + 1));
        var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, id); var meta = PaymentVerificationTestData.Meta("buyer-follow-up-claim");
        await data.Service.RecordReviewActionAsync(data.Reviewer, id, 0, new(shown.Binding, "claim"), meta);
        Assert.Equal("assigned", (await owner.ReadFollowUpAsync(buyer, id, original.Revision)).State);
        shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, id);
        await data.Service.RecordReviewActionAsync(data.Reviewer, id, 1, new(shown.Binding, "request_replacement"), PaymentVerificationTestData.Meta("buyer-follow-up-replacement"));
        var closed = await owner.ReadFollowUpAsync(buyer, id, original.Revision); Assert.Equal("replacement_requested", closed.State); Assert.Equal(2, closed.ReviewRevision); Assert.NotNull(closed.RecordedAt);
        Assert.Equal(original.Submission, (await owner.ReadAsync(buyer, id)).Submission);
        Assert.Equal(versions, await data.Rows(PaymentDraftBson.Versions).Find(new BsonDocument("declarationId", id)).Sort(new BsonDocument("revision", 1)).ToListAsync());
        Assert.Equal(journals, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var grants = await data.Rows("authorizationGrants").Find(new BsonDocument("userId", buyer.UserId)).ToListAsync(); Assert.NotEmpty(grants);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", buyer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.ReadFollowUpAsync(buyer, id, original.Revision));
        foreach (var grant in grants) await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", W.Operation }, { "key", meta.IdempotencyKey } });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.ReadFollowUpAsync(buyer, id, original.Revision));
    }
    [WindowsStorageFact]
    public async Task Private_workflow_routes_enforce_actor_origin_CSRF_versions_and_exact_lookup_only_recovery() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await Grant(data);
        var view = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Read.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Read.Catalog.Sessions);
            services.RemoveAll<PaymentVerificationService>(); services.AddSingleton(data.Service);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Read.Payment.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Read.Catalog.Issued[data.Reviewer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Send(string kind, string? fault = null) {
            object body = kind switch { "query" => new { purpose = "payment-review" }, "recover" => new { purpose = "operation-recovery", key = "http-review-action" },
                _ => new { binding = view.Binding, action = fault == "action" ? "invalid" : "claim", reason = "Synthetic reviewed queue ownership" } };
            var suffix = kind == "query" ? "/query" : kind == "recover" ? "/operations/query" : "";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/payment-reviews/{data.Read.DeclarationId}/workflow" + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Reviewer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "key") request.Headers.Add("Idempotency-Key", "http-review-action");
            if (fault != "revision") request.Headers.Add("If-Match", kind == "query" ? "\"1\"" : "\"0\""); return await client.SendAsync(request);
        }
        foreach (var kind in new[] { "query", "save", "recover" }) foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest) }) {
            using var denied = await Send(kind, fault); Assert.Equal(status, denied.StatusCode);
        }
        foreach (var fault in new[] { "key", "revision", "action" }) { using var denied = await Send("save", fault); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); }
        using var query = await Send("query"); Assert.True(query.IsSuccessStatusCode, await query.Content.ReadAsStringAsync()); Assert.True(query.Headers.CacheControl!.NoStore);
        using var save = await Send("save"); Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync()); Assert.True(save.Headers.CacheControl!.NoStore);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("assigned", saved.GetProperty("state").GetString());
        using var recovery = await Send("recover"); var result = await recovery.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("committed", result.GetProperty("status").GetString()); Assert.Equal(saved.GetRawText(), result.GetProperty("result").GetRawText());
        Assert.Equal(1, await data.Rows(W.Versions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        using var buyer = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        buyer.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Read.Catalog.Issued[data.Read.Actor.UserId].OpaqueToken);
        var buyerCsrf = (await buyer.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> FollowUp(string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/payment-declarations/{data.Read.DeclarationId}/follow-up/query") { Content = JsonContent.Create(new { purpose = "payment-follow-up" }) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", data.Read.Actor.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", buyerCsrf);
            if (fault != "revision") request.Headers.Add("If-Match", "\"1\""); return await buyer.SendAsync(request);
        }
        foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest), ("revision", HttpStatusCode.BadRequest) }) {
            using var denied = await FollowUp(fault); Assert.Equal(status, denied.StatusCode);
        }
        using var follow = await FollowUp(); Assert.True(follow.IsSuccessStatusCode, await follow.Content.ReadAsStringAsync()); Assert.True(follow.Headers.CacheControl!.NoStore);
        var visible = await follow.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("assigned", visible.GetProperty("state").GetString());
        Assert.Equal(new[] { "declarationId", "declarationRevision", "evidence", "recordedAt", "reviewRevision", "state", "submissionId", "submittedAt" }, visible.EnumerateObject().Select(value => value.Name).Order().ToArray());
    }
    [WindowsStorageFact]
    public async Task Missing_permission_MFA_audit_rollback_and_concurrent_rejection_verification_never_leave_two_winners() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture);
        var view = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId); var input = new PaymentReviewActionInput(view.Binding, "reject");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("ungranted-reject")));
        await Grant(data);
        foreach (var actor in new[] { data.Reader, data.Read.Actor, data.Reviewer with { AuthenticatedAt = data.Read.Catalog.Clock.UtcNow.AddMinutes(-6) } })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecordReviewActionAsync(actor, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("denied-review")));
        var definition = await (await data.Read.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync(); var old = definition["options"]["validator"];
        await data.Read.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray { old, new BsonDocument("kind", new BsonDocument("$ne", W.Operation)) }) } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("reject-audit-denial")));
        Assert.Equal(0, await data.Rows(W.Versions).CountDocumentsAsync(new BsonDocument())); Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", W.Operation)).AnyAsync());
        await data.Read.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", old } });
        var payment = await data.PreviewAsync();
        var reject = Record.ExceptionAsync(() => data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("reject-vs-verify")));
        var verify = Record.ExceptionAsync(() => data.VerifyAsync(payment)); Assert.Single(await Task.WhenAll(reject, verify), failure => failure is null);
        var actions = await data.Rows(W.Versions).CountDocumentsAsync(new BsonDocument()); var decisions = await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()); Assert.Equal(1, actions + decisions);
        Assert.Equal(1, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument())); Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
