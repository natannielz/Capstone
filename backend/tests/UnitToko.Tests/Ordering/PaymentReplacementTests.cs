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
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Api.Identity;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;
using B = UnitToko.Platform.Billing.PaymentDraftBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentReplacementTests(MongoFixture fixture)
{
    internal static string Key(long parent) => "rp." + parent + "." + Guid.NewGuid().ToString("D");
    private static async Task Request(PaymentVerificationTestData data) {
        await PaymentReviewActionTests.Grant(data); var shown = await data.Service.ReadReviewActionsAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordReviewActionAsync(data.Reviewer, data.Read.DeclarationId, 0, new(shown.Binding, "request_replacement"), PaymentVerificationTestData.Meta("request-linked-replacement"));
    }
    [WindowsStorageFact(Skip = Retired.HistoricalUpgrade)]
    public async Task Forward_upgrade_preserves_actual_submitted_history_and_refuses_a_missing_previous_receipt() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var db = data.Read.Catalog.Database;
        // Rehearse the previous schema on this isolated synthetic fixture, before any replacement writes.
        await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 44));
        await data.Rows(B.Versions).Indexes.DropOneAsync(PaymentReplacementSchemas.IndexName);
        await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Versions }, { "validator", PaymentDraftSchemas.Validator(B.Versions) } });
        var names = new[] { B.Heads, B.Versions, "paymentSubmissions", "idempotencyRecords", "auditEvents", "outboxEvents", "evidencePins", "journalEntries" };
        var snapshots = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in names) snapshots[name] = await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await new CollectionMigrations(db, data.Read.Catalog.Clock).ApplyAsync();
        foreach (var name in names) Assert.Equal(snapshots[name], await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", 44));
        var first = snapshots[B.Versions][0]; await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "actorId", first["recordedBy"] }, { "operation", B.Operation }, { "key", first["requestKey"] } });
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionMigrations(db, data.Read.Catalog.Clock).ApplyAsync());
        Assert.False(await data.Rows("schemaMigrations").Find(new BsonDocument { { "_id", 44 }, { "state", "applied" } }).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Private_http_preview_create_origin_and_recovery_require_actor_csrf_source_revision_and_original_key() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await Request(data); var actor = data.Read.Actor; var parent = data.Read.DeclarationId;
        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Read.Catalog.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Read.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Read.Catalog.Sessions);
            services.RemoveAll<PaymentDraftService>(); services.AddSingleton(data.Read.Payment.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id"; using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Read.Catalog.Issued[actor.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Send(string path, object body, string? key = null, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/payment-declarations/" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", fault == "origin" ? "https://outside.invalid" : origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Reader.UserId.ToString() : actor.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (fault != "revision") request.Headers.Add("If-Match", fault == "stale" ? "\"2\"" : "\"1\"");
            if (key is not null) request.Headers.Add("Idempotency-Key", key); return await client.SendAsync(request);
        }
        foreach (var (fault, status) in new[] { ("origin", HttpStatusCode.Forbidden), ("csrf", HttpStatusCode.Forbidden), ("actor", HttpStatusCode.BadRequest), ("different-actor", HttpStatusCode.Conflict), ("revision", HttpStatusCode.BadRequest), ("stale", HttpStatusCode.Conflict) }) {
            using var denied = await Send(parent + "/replacement/query", new { purpose = "payment-replacement" }, fault: fault); Assert.Equal(status, denied.StatusCode);
        }
        using var preview = await Send(parent + "/replacement/query", new { purpose = "payment-replacement" }); Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync()); Assert.True(preview.Headers.CacheControl!.Private); Assert.True(preview.Headers.CacheControl.NoStore);
        using var normalOrigin = await Send(parent + "/replacement-origin/query", new { purpose = "payment-replacement-origin" }); Assert.True(normalOrigin.IsSuccessStatusCode);
        Assert.Equal(JsonValueKind.Null, (await normalOrigin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("origin").ValueKind);
        var view = await preview.Content.ReadFromJsonAsync<JsonElement>(); var key = Key(parent); var command = new { request = view.GetProperty("request"), reason = "Synthetic buyer replacement" };
        using var badKey = await Send(parent + "/replacement", command, "rp." + (parent + 1) + "." + Guid.NewGuid().ToString("D")); Assert.Equal(HttpStatusCode.Forbidden, badKey.StatusCode);
        using var saved = await Send(parent + "/replacement", command, key); Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync()); var value = await saved.Content.ReadFromJsonAsync<JsonElement>();
        using var recovered = await Send(parent + "/replacement/operations/query", new { purpose = "operation-recovery", key }); Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync());
        var receipt = await recovered.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(value.GetRawText(), receipt.GetProperty("result").GetRawText());
        using var linked = await Send(value.GetProperty("declarationId").GetString() + "/replacement-origin/query", new { purpose = "payment-replacement-origin" }); Assert.True(linked.IsSuccessStatusCode, await linked.Content.ReadAsStringAsync());
        Assert.Equal(parent.ToString(), (await linked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("origin").GetProperty("parentDeclarationId").GetString());
        Assert.Equal(1, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument("schemaVersion", 2))); Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Linked_replacement_creates_one_separate_editable_draft_with_exact_request_and_original_key_recovery_and_no_financial_effect() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await Request(data);
        var owner = data.Read.Payment.Service; var actor = data.Read.Actor; var parent = data.Read.DeclarationId;
        var original = await owner.ReadAsync(actor, parent); var originals = await data.Rows("evidencePins").Find(new BsonDocument()).ToListAsync();
        var journals = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var shown = await owner.PreviewReplacementAsync(actor, parent, original.Revision); Assert.Null(shown.Existing);
        var metadata = PaymentVerificationTestData.Meta(Key(parent));
        var saved = await owner.CreateReplacementAsync(actor, parent, original.Revision, shown.Request, metadata); var childId = saved["declarationId"].AsInt64;
        Assert.True(childId > parent); Assert.Equal(parent, saved["parentDeclarationId"].AsInt64); Assert.Equal(original.Revision, saved["parentDeclarationRevision"].AsInt64);
        Assert.Equal(saved, await owner.CreateReplacementAsync(actor, parent, original.Revision, shown.Request, metadata));
        Assert.Equal(saved, await owner.RecoverReplacementAsync(actor, parent, metadata.IdempotencyKey));
        Assert.Equal(saved, (await owner.PreviewReplacementAsync(actor, parent, original.Revision)).Existing);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => owner.CreateReplacementAsync(actor, parent, original.Revision, shown.Request, PaymentVerificationTestData.Meta(Key(parent))));
        var child = await owner.ReadAsync(actor, childId); Assert.Equal("draft", child.State); Assert.Null(child.Submission);
        Assert.Null(await owner.ReadReplacementOriginAsync(actor, parent, original.Revision));
        Assert.Equal(new PaymentReplacementOrigin(parent, original.Revision), await owner.ReadReplacementOriginAsync(actor, childId, 1));
        Assert.Equal(B.Input(original.Input), B.Input(child.Input));
        Assert.Empty(await data.Read.Service.ListAsync(actor, childId));
        await owner.SaveAsync(actor, childId, 1, child.Input with { DeclaredAmount = new(4500m), Note = "Synthetic replacement correction before a new original" }, PaymentVerificationTestData.Meta("edit-linked-replacement"));
        child = await owner.ReadAsync(actor, childId); Assert.Equal(2, child.Revision); Assert.Equal(4500m, child.Input.DeclaredAmount.Value);
        Assert.Equal(new PaymentReplacementOrigin(parent, original.Revision), await owner.ReadReplacementOriginAsync(actor, childId, 2));
        Assert.Equal(saved, await owner.RecoverReplacementAsync(actor, parent, metadata.IdempotencyKey));
        Assert.Equal(original.Submission, (await owner.ReadAsync(actor, parent)).Submission);
        Assert.Equal(originals, await data.Rows("evidencePins").Find(new BsonDocument()).ToListAsync());
        Assert.Equal(journals, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Equal(0, await data.Rows("paymentAllocations").CountDocumentsAsync(new BsonDocument()));
        await data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", B.ReplacementOperation + ":" + saved["versionId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.ReadAsync(actor, childId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.RecoverReplacementAsync(actor, parent, metadata.IdempotencyKey));
    }
    [WindowsStorageFact]
    public async Task Concurrent_creations_and_missing_source_or_authority_never_fork_the_request_or_reopen_the_parent() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); await Request(data);
        var owner = data.Read.Payment.Service; var actor = data.Read.Actor; var parent = data.Read.DeclarationId;
        var original = await owner.ReadAsync(actor, parent); var shown = await owner.PreviewReplacementAsync(actor, parent, original.Revision);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.PreviewReplacementAsync(data.Reader, parent, original.Revision));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => owner.CreateReplacementAsync(actor, parent, original.Revision, shown.Request with { ActionHash = new string('f', 64) }, PaymentVerificationTestData.Meta(Key(parent))));
        var firstKey = Key(parent); var secondKey = Key(parent);
        async Task<Exception?> Create(string key) => await Record.ExceptionAsync(() => owner.CreateReplacementAsync(actor, parent, original.Revision, shown.Request, PaymentVerificationTestData.Meta(key)));
        var results = await Task.WhenAll(Create(firstKey), Create(secondKey)); Assert.Single(results, result => result is null);
        Assert.Equal(1, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument("replacement.source.declarationId", parent)));
        var child = (await owner.PreviewReplacementAsync(actor, parent, original.Revision)).Existing!;
        var committedKey = results[0] is null ? firstKey : secondKey;
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "actorId", actor.UserId }, { "operation", B.ReplacementOperation }, { "key", committedKey } });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.RecoverReplacementAsync(actor, parent, committedKey));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.ReadAsync(actor, child["declarationId"].AsInt64));
        Assert.Equal(0, await data.Rows(PaymentVerificationBson.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
}
