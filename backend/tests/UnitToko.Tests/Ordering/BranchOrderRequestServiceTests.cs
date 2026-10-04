using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Organizations;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BranchOrderRequestServiceTests(MongoFixture fixture) {
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic branch request review", "branch-request-test");

    [Fact]
    public async Task Actual_catalog_request_is_private_until_staff_accepts_once_and_does_not_touch_stock_or_money() {
        await using var source = await StoreTestData.CreateAsync(fixture);
        var profiles = await OrganizationAddressTestData.PrepareAsync(source, fixture.Client);
        foreach (var (profileId, kind, parent, code, name) in new (long, ScopeNodeKind, long, string, string)[] {
            (12, ScopeNodeKind.Branch, 10, "OTHER", "Synthetic other branch"),
            (103, ScopeNodeKind.Channel, 100, "WEB", "Synthetic web channel") }) {
            var proposal = await profiles.Organizations.ProposeAsync(profiles.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, kind, parent, code, name, profileId, 1, 0), Meta("profile-" + profileId));
            await profiles.Organizations.ApproveAsync(profiles.ProfileChecker, proposal.Id, 1, Meta("profile-approve-" + profileId));
        }
        var catalog = await CatalogAuthoringTestData.AttachAsync(source, fixture.Client);
        var sku = await catalog.SkuAsync(); await catalog.AssortmentsAsync();
        await catalog.Service.SetPublicationsAsync(catalog.Maker,
            new(sku.Id, 1, [new LongId(501)], [new(new LongId(501), 1)]), CatalogAuthoringTestData.Metadata("request-publish"));
        await source.AddGrantAsync(catalog.Buyer, 6001, CatalogAuthoringTestData.Audience,
            [Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit]);
        var outsider = await catalog.ActorAsync(6002, new(GrantScopeKind.BuyerOwned, 10, 12),
            Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit);
        var staff = await catalog.ActorAsync(7000, CatalogAuthoringTestData.Shop,
            Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestDecide);
        var otherStaff = await catalog.ActorAsync(7001, CatalogAuthoringTestData.Shop,
            Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestDecide);
        await PersonTestData.SeedDistinctPeopleAsync(source.Database, source.Clock, catalog.Buyer.UserId, outsider.UserId, staff.UserId, otherStaff.UserId);
        var service = new BranchOrderRequestService(source.Database, new(fixture.Client), new(source.Database), source.Clock);
        var context = new OrderBuyingContext(new(10), new(11));
        var definition = (await catalog.Service.SkuAsync(catalog.Maker, sku.Id.Value,
            CatalogAuthoringTestData.Audit)).Definition;
        var input = new BranchOrderRequestInput(context, ImmutableArray.Create(new BranchOrderRequestLine(
            sku.Id, definition.Id, definition.Version, 2)), "For branch review");
        await Assert.ThrowsAsync<DomainValidationException>(() => service.PreviewAsync(catalog.Buyer, input with {
            Lines = ImmutableArray.Create(input.Lines[0] with { Packages = 0 }) }));
        var picker = await service.PackagesAsync(catalog.Buyer, context);
        Assert.Single(picker.Items); Assert.Null(picker.Items[0].CurrentPrice);
        var preview = await service.PreviewAsync(catalog.Buyer, input);
        Assert.Equal("Synthetic fulfillment shop", preview["labels"]["shop"].AsString);
        var submitted = await service.SubmitAsync(catalog.Buyer, input, preview["binding"].AsString, Meta("request-submit"));
        Assert.Equal("pending", submitted["state"].AsString);
        var replay = await service.SubmitAsync(catalog.Buyer, input, preview["binding"].AsString, Meta("request-submit"));
        Assert.Equal(submitted, replay);
        var id = submitted["requestId"].AsInt64;
        Assert.Equal(1, await source.Database.GetCollection<BsonDocument>("branchOrderRequests").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(id, (await service.ReadAsync(catalog.Buyer, id))["id"].AsInt64);
        Assert.Equal(id, (await service.ListAsync(catalog.Buyer, false))["items"][0]["id"].AsInt64);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(outsider, id));
        Assert.Empty((await service.ListAsync(outsider, false))["items"].AsBsonArray);
        Assert.Empty((await service.ListAsync(outsider, true))["items"].AsBsonArray);
        Assert.Equal(id, (await service.ListAsync(staff, true))["items"][0]["id"].AsInt64);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.PreviewDecisionAsync(catalog.Buyer, id, BranchOrderRequestDecision.Accept));
        var review = await service.PreviewDecisionAsync(staff, id, BranchOrderRequestDecision.Accept);
        var accepted = await service.DecideAsync(staff, id, 1, BranchOrderRequestDecision.Accept, review["binding"].AsString, Meta("request-accept"));
        Assert.Equal("accepted", accepted["state"].AsString);
        Assert.Equal(accepted, await service.DecideAsync(staff, id, 1, BranchOrderRequestDecision.Accept, review["binding"].AsString, Meta("request-accept")));
        Assert.Equal("accepted", (await service.ReadAsync(catalog.Buyer, id))["state"].AsString);
        Assert.Equal("confirmed", (await service.LookupAsync(staff, "ordering.branchrequest.decide", "request-accept"))["state"].AsString);
        var staleDecision = await service.PreviewDecisionAsync(otherStaff, id, BranchOrderRequestDecision.Reject);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.DecideAsync(otherStaff, id, 1,
            BranchOrderRequestDecision.Reject, staleDecision["binding"].AsString, Meta("double-decision")));
        var rejectedPreview = await service.PreviewAsync(catalog.Buyer, input);
        var second = await service.SubmitAsync(catalog.Buyer, input, rejectedPreview["binding"].AsString, Meta("request-second"));
        var secondId = second["requestId"].AsInt64;
        var reject = await service.PreviewDecisionAsync(staff, secondId, BranchOrderRequestDecision.Reject);
        var rejected = await service.DecideAsync(staff, secondId, 1, BranchOrderRequestDecision.Reject, reject["binding"].AsString, Meta("request-reject"));
        Assert.Equal("rejected", rejected["state"].AsString);
        Assert.Equal("Synthetic branch request review", (await service.ReadAsync(catalog.Buyer, secondId))["decision"]["reason"].AsString);
        // Costing must upgrade past published migration78 without replaying or changing requests.
        await Procurement.ProcurementQualityValuationMigrationTests.VerifyPublishedRequestUpgradeAsync(source.Database, source.Clock);
        Assert.Equal("accepted", (await service.ReadAsync(catalog.Buyer, id))["state"].AsString);
        Assert.Equal("rejected", (await service.ReadAsync(catalog.Buyer, secondId))["state"].AsString);
        var thirdPreview = await service.PreviewAsync(catalog.Buyer, input);
        var third = await service.SubmitAsync(catalog.Buyer, input, thirdPreview["binding"].AsString, Meta("request-race"));
        var thirdId = third["requestId"].AsInt64;
        var acceptPreview = await service.PreviewDecisionAsync(staff, thirdId, BranchOrderRequestDecision.Accept);
        var denyPreview = await service.PreviewDecisionAsync(otherStaff, thirdId, BranchOrderRequestDecision.Reject);
        var outcomes = await Task.WhenAll(new[] {
            Task.Run(async () => { try { await service.DecideAsync(staff, thirdId, 1, BranchOrderRequestDecision.Accept,
                acceptPreview["binding"].AsString, Meta("race-accept")); return true; } catch { return false; } }),
            Task.Run(async () => { try { await service.DecideAsync(otherStaff, thirdId, 1, BranchOrderRequestDecision.Reject,
                denyPreview["binding"].AsString, Meta("race-reject")); return true; } catch { return false; } }) });
        Assert.Equal(1, outcomes.Count(value => value));
        Assert.Equal(1, await source.Database.GetCollection<BsonDocument>("branchOrderRequestDecisions")
            .CountDocumentsAsync(new BsonDocument("requestId", thirdId)));
        await source.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(
            new BsonDocument("_id", 7000L), Builders<BsonDocument>.Update.Set("status", "revoked")
                .Set("revokedAt", source.Clock.UtcNow.UtcDateTime).Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ListAsync(staff, true));
        Assert.Equal(0, await source.Database.GetCollection<BsonDocument>("orders").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, await source.Database.GetCollection<BsonDocument>("reservations").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, await source.Database.GetCollection<BsonDocument>("stockMovements").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, await source.Database.GetCollection<BsonDocument>("journalEntries").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
