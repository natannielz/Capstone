using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.ProcurementRequestTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementRequestTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(ProcurementRoute.OmiRegular, ProcurementRequestKind.StockNeed)]
    [InlineData(ProcurementRoute.OmiRegular, ProcurementRequestKind.IncomingNotice)]
    [InlineData(ProcurementRoute.Ddo, ProcurementRequestKind.PurchaseRequest)]
    [InlineData(ProcurementRoute.Smart, ProcurementRequestKind.PurchaseRequest)]
    [InlineData(ProcurementRoute.PasarKering, ProcurementRequestKind.PurchaseRequest)]
    public async Task Every_route_preserves_real_packaging_and_independent_review_without_supplier_stock_or_money_effects(ProcurementRoute route, ProcurementRequestKind kind)
    {
        await using var data = await D.CreateAsync(fixture);
        var input = data.Input(route, kind) with { Lines = [D.Line(data.SecondSku, 3), D.Line(data.Sku, 2) with { Specification = "Synthetic packaging specification" }] };
        var preview = await data.Service.PreviewAsync(data.Maker, input);
        Assert.Equal("Dus", preview["lines"][0]["packageLabel"].AsString);
        Assert.Equal(12, preview["lines"][0]["baseUnitsPerPackage"].AsInt32);
        Assert.Equal("Satuan", preview["lines"][1]["packageLabel"].AsString);
        Assert.Equal(1, preview["lines"][1]["baseUnitsPerPackage"].AsInt32);
        var submitted = await data.Service.SubmitAsync(data.Maker, input, preview["binding"].AsString, D.Meta("request-original"));
        Assert.Equal(submitted, await data.Service.SubmitAsync(data.Maker, input with { Lines = [.. input.Lines.Reverse()] }, preview["binding"].AsString, D.Meta("request-original")));
        Assert.Equal(submitted, await data.Service.LookupAsync(data.Maker, D.Prefix + "submit", "request-original"));
        var request = await data.Rows(D.Requests).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Maker.UserId, request["actorId"].AsInt64); Assert.Equal(2, request["input"]["lines"].AsBsonArray.Count);
        Assert.Equal(input.Lines.Select(line => line.SkuId).Order(), request["input"]["lines"].AsBsonArray.Select(line => line["skuId"].AsInt64));
        Assert.Equal(data.Sku.CurrentDefinitionId!.Value.Value, request["input"]["lines"][0]["definitionId"].AsInt64);
        Assert.Equal(2, request["input"]["lines"][0]["packages"].ToInt32()); Assert.Equal(3, request["input"]["lines"][1]["packages"].ToInt32());
        foreach (var source in request["catalog"].AsBsonArray)
            Assert.Equal(await data.Rows("variantDefinitions").Find(new BsonDocument("_id", source["definition"]["_id"])).SingleAsync(), source["definition"]);
        await data.NoBusinessEffectsAsync();
        var approved = await data.DecideAsync(submitted, "request-original-approved");
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "request-original-approved"));
        Assert.Equal(request, await data.Rows(D.Requests).Find(new BsonDocument()).SingleAsync());
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Reader, submitted["requestId"].AsInt64))["state"]);
        var decision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Checker.UserId, decision["actorId"].AsInt64); Assert.Equal(request["_id"], decision["requestId"]);
        Assert.Equal(1, await data.Rows(D.Decisions).CountDocumentsAsync(new BsonDocument())); await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Recovery_is_original_actor_and_key_only_and_does_not_resubmit_requests()
    {
        await using var data = await D.CreateAsync(fixture); var submitted = await data.SubmitAsync("request-recovery");
        var other = await data.Base.AddActorAsync(616, D.Scope, Permissions.ProcurementPrepare);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, other.UserId); var before = await data.StateAsync();
        Assert.Null(await data.Service.LookupAsync(other, D.Prefix + "submit", "request-recovery"));
        Assert.Null(await data.Service.LookupAsync(data.Maker, D.Prefix + "submit", "request-wrong-key"));
        Assert.Equal(submitted, await data.Service.LookupAsync(data.Maker, D.Prefix + "submit", "request-recovery")); Assert.Equal(before, await data.StateAsync());
        var rejected = await data.DecideAsync(submitted, "request-rejected", ProcurementRequestDecision.Reject);
        Assert.Equal(rejected, await data.Service.LookupAsync(data.Checker, D.Prefix + "reject", "request-rejected"));
        Assert.Equal("rejected", (await data.Service.ReadAsync(data.Reader, submitted["requestId"].AsInt64))["state"]);
        Assert.Equal(1, await data.Rows(D.Requests).CountDocumentsAsync(new BsonDocument())); await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Exact_location_and_real_owned_packaging_are_required_before_request_facts_are_exposed()
    {
        await using var data = await D.CreateAsync(fixture); var submitted = await data.SubmitAsync("request-scope");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, submitted["requestId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PackagesAsync(data.Outsider, D.Location, 0, 25, null));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, data.Input()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker, data.Input() with {
            Location = D.Location with { ShopId = data.ForeignShopId, WarehouseId = data.ForeignWarehouseId } }));
        var before = await data.StateAsync();
        var foreign = await Record.ExceptionAsync(() => data.Service.PreviewAsync(data.Maker, data.Input() with { Lines = [D.Line(data.ForeignSku)] }));
        Assert.True(foreign is AuthorizationDeniedException or DomainValidationException, foreign?.ToString());
        foreach (var line in new[] { D.Line(data.Sku) with { DefinitionId = data.SecondSku.CurrentDefinitionId!.Value.Value },
            D.Line(data.Sku) with { DefinitionVersion = 999 }, D.Line(data.Sku) with { ExpectedSkuRevision = 999 } }) {
            var error = await Record.ExceptionAsync(() => data.Service.PreviewAsync(data.Maker, data.Input() with { Lines = [line] }));
            Assert.True(error is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, error?.ToString());
        }
        Assert.Equal(before, await data.StateAsync()); await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Route_kind_reference_quantity_and_unique_line_bounds_cannot_be_guessed_or_silently_normalized()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(); var line = D.Line(data.Sku); var before = await data.StateAsync();
        foreach (var invalid in new[] {
            input with { Route = (ProcurementRoute)99 }, input with { Kind = (ProcurementRequestKind)99 }, input with { Route = ProcurementRoute.Ddo },
            input with { Kind = ProcurementRequestKind.PurchaseRequest }, input with { Kind = ProcurementRequestKind.IncomingNotice, ExternalReference = null },
            input with { Kind = ProcurementRequestKind.IncomingNotice, ExternalReference = " " }, input with { ExternalReference = new string('x', 129) },
            input with { Reason = "" }, input with { Reason = " padded " }, input with { Reason = new string('x', 1001) }, input with { BusinessDate = DateOnly.MinValue },
            input with { Lines = [] }, input with { Lines = [line, line] }, input with { Lines = [line with { Packages = 0 }] },
            input with { Lines = [line with { Packages = 1000001 }] }, input with { Lines = [line with { Specification = " padded " }] },
            input with { Lines = [line with { Specification = new string('x', 1001) }] }, input with { Lines = [.. Enumerable.Range(1, 101).Select(id => line with { SkuId = id })] } })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, invalid));
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("packaging")] [InlineData("profile")]
    public async Task Changed_sources_cannot_be_approved_but_can_be_independently_rejected_without_rewriting_the_submission(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var submitted = await data.SubmitAsync("request-stale"); var id = submitted["requestId"].AsInt64;
        var original = await data.Rows(D.Requests).Find(new BsonDocument()).SingleAsync();
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementRequestDecision.Approve);
        if (fault == "packaging") await RevisePackagingAsync(data); else await ChangeWarehouseAsync(data);
        var before = await data.StateAsync();
        var error = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementRequestDecision.Approve, preview["binding"].AsString, D.Meta("request-stale-approve")));
        Assert.True(error is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, error?.ToString()); Assert.Equal(before, await data.StateAsync());
        await data.DecideAsync(submitted, "request-stale-rejected", ProcurementRequestDecision.Reject);
        var historical = await data.Service.ReadAsync(data.Reader, id);
        Assert.Equal("rejected", historical["state"]); Assert.Equal("Dus", historical["lines"][0]["packageLabel"].AsString);
        Assert.Equal(data.Sku.CurrentDefinitionId!.Value.Value, historical["lines"][0]["definitionId"].AsInt64);
        Assert.Equal(original, await data.Rows(D.Requests).Find(new BsonDocument()).SingleAsync()); await data.NoBusinessEffectsAsync();
    }

    [Theory]
    [InlineData("definition")] [InlineData("authoring_receipt")]
    public async Task Frozen_catalog_facts_require_the_real_immutable_definition_and_original_authoring_receipt(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var submitted = await data.SubmitAsync("request-source-integrity");
        var id = submitted["requestId"].AsInt64;
        var original = await data.Rows(D.Requests).Find(new BsonDocument("_id", id)).SingleAsync();
        var source = original["catalog"][0].AsBsonDocument;
        // Corrupt the original catalog evidence only. The request, its hash and all request effects remain genuine.
        if (fault == "definition") {
            var definition = await data.Rows("variantDefinitions").Find(new BsonDocument("_id", source["definition"]["_id"])).SingleAsync();
            definition["packageLabel"] = "Fabricated packaging label";
            await data.Rows("variantDefinitions").ReplaceOneAsync(new BsonDocument("_id", definition["_id"]), definition);
        } else {
            var deleted = await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("catalogAuthoring.auditId", source["audits"]["sku"]));
            Assert.Equal(1, deleted.DeletedCount);
        }
        var before = await data.StateAsync();
        foreach (var read in new Func<Task>[] {
            async () => await data.Service.ReadAsync(data.Reader, id),
            async () => await data.Service.LookupAsync(data.Maker, D.Prefix + "submit", "request-source-integrity"),
            async () => await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementRequestDecision.Reject) }) {
            var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(read);
            Assert.Equal("catalog_resource_unavailable", error.Code);
        }
        Assert.Equal(original, await data.Rows(D.Requests).Find(new BsonDocument("_id", id)).SingleAsync());
        Assert.Equal(before, await data.StateAsync()); await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Fresh_permission_and_independent_canonical_person_are_required_even_for_distinct_accounts()
    {
        await using var data = await D.CreateAsync(fixture); var submitted = await data.SubmitAsync("request-person"); var id = submitted["requestId"].AsInt64;
        foreach (var actor in new[] { data.Maker, data.Reader, data.Checker with { Assurance = FactorAssurance.SingleFactor }, data.Checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) } })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(actor, id, ProcurementRequestDecision.Approve));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, data.Input()));
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementRequestDecision.Approve);
        await OrganizationAddressTests.MergePeopleAsync(data.Organizations, data.Maker.UserId, data.Checker.UserId);
        var people = await data.Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock).ResolveAsync(tx, [data.Maker.UserId, data.Checker.UserId], ct));
        Assert.Equal(people[0].PersonId, people[1].PersonId); var before = await data.StateAsync();
        var error = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementRequestDecision.Approve, preview["binding"].AsString, D.Meta("request-same-person")));
        Assert.True(error is AuthorizationDeniedException or DomainValidationException, error?.ToString()); Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Required_effect_failure_rolls_back_and_parallel_independent_checkers_create_only_one_decision()
    {
        await using var data = await D.CreateAsync(fixture); var request = await data.SubmitAsync("request-atomic"); var id = request["requestId"].AsInt64;
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementRequestDecision.Approve); var before = await data.StateAsync();
        foreach (var (table, field) in new[] { (D.Decisions, "decision"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = definition["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray {
                    validator, new BsonDocument(field, new BsonDocument("$ne", table == D.Decisions ? "approve" : D.Prefix + "approve")) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementRequestDecision.Approve, preview["binding"].AsString, D.Meta("request-atomic-approved"))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        var second = await data.Base.AddActorAsync(617, D.Scope, Permissions.ProcurementRead, Permissions.ProcurementApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, second.UserId);
        var other = await data.Service.PreviewDecisionAsync(second, id, ProcurementRequestDecision.Approve);
        var results = await Task.WhenAll(new[] { (data.Checker, preview, "request-atomic-approved"), (second, other, "request-other-approved") }.Select(async item =>
            await Record.ExceptionAsync(() => data.Service.DecideAsync(item.Item1, id, 1, ProcurementRequestDecision.Approve, item.Item2["binding"].AsString, D.Meta(item.Item3)))));
        Assert.Single(results, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, error => error is not null));
        Assert.Equal(1, await data.Rows(D.Decisions).CountDocumentsAsync(new BsonDocument())); await data.NoBusinessEffectsAsync();
    }

    [Theory]
    [InlineData("packaging")] [InlineData("grant")] [InlineData("member")] [InlineData("session")] [InlineData("mfa")]
    public async Task Concurrent_catalog_or_authority_changes_cannot_escape_real_preview_final_fences(string change)
    {
        await using var data = await D.CreateAsync(fixture);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(data.Maker.ProviderIssuer, data.Maker.ProviderSubject, "procurement-race@example.invalid", true,
            "fixture-connection", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture-policy");
        var issued = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), proof, "synthetic-procurement-race", ct));
        data.Maker = data.Maker with { Session = issued.Session.Reference };
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == "skus" && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Procurement preview race did not complete");
            } });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var service = new ProcurementRequestService(database, unit, new(database), data.Clock, new CatalogAuthoringService(database, unit, new(database), data.Clock));
        var pending = Task.Run(() => service.PreviewAsync(data.Maker, data.Input()));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "packaging") await RevisePackagingAsync(data);
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "member") await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await sessions.RevokeAsync(issued.OpaqueToken, "Synthetic procurement session revocation");
            if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        } finally { release.Set(); }
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, error?.ToString());
        Assert.False(await data.Rows(D.Requests).Find(new BsonDocument()).AnyAsync()); await data.NoBusinessEffectsAsync();
    }
    private static Task RevisePackagingAsync(D data) => data.Catalog.ReviseDefinitionAsync(data.Author,
        new(data.Sku.Id, data.Sku.Version, CatalogAuthoringTestData.Definition(12, "Dus revisi")), D.Meta("procurement-packaging-revised"));
    private static async Task ChangeWarehouseAsync(D data) {
        var changed = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
            new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Warehouse, 101, "WAREHOUSE", "Synthetic changed warehouse", 102, 2, 1), D.Meta("procurement-warehouse-change"));
        await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, changed.Id, 1, D.Meta("procurement-warehouse-change-approved"));
    }
}
