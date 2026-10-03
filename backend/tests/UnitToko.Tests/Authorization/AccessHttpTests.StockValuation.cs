using System.Net;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests
{
    [Fact]
    public void Stock_valuation_checker_is_independent_of_stored_proposer_and_invoice_matcher()
    {
        const string permission = Permissions.ProcurementStockValuationDecide;
        Assert.True(ApprovalPolicy.RequiresEvidence(permission));
        Assert.True(PersonApprovalPolicy.RequiresEvidence(permission));
        var request = new ApprovalRequest(1, 1, RequestedBy: 101, RecordedBy: 102);
        Assert.False(ApprovalPolicy.Evaluate(103, permission, request with { RecordedBy = null }).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(103, permission, request with { RequestedBy = null }).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, permission, request).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(102, permission, request).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(103, permission, request with { ExpectedRevision = 2 }).Allowed);
        Assert.Equal(new long[] { 101, 102, 103 }, PersonApprovalPolicy.RequiredAccounts(103, permission, request).ToArray());
        Assert.False(PersonApprovalPolicy.Evaluate(103, permission, request,
            [new(101, 501, PersonVerification.Verified, 1), new(102, 502, PersonVerification.Verified, 1), new(103, 502, PersonVerification.Verified, 1)]).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(103, permission, request,
            [new(101, 501, PersonVerification.Verified, 1), new(102, 502, PersonVerification.Verified, 1), new(103, 501, PersonVerification.Verified, 1)]).Allowed);
        Assert.True(PersonApprovalPolicy.Evaluate(103, permission, request,
            [new(101, 501, PersonVerification.Verified, 1), new(102, 502, PersonVerification.Verified, 1), new(103, 503, PersonVerification.Verified, 1)]).Allowed);
        Assert.True(PersonApprovalPolicy.Evaluate(103, permission, request with { RecordedBy = 101 },
            [new(101, 501, PersonVerification.Verified, 1), new(103, 503, PersonVerification.Verified, 1)]).Allowed);
        foreach (var template in RoleTemplates.Templates.Keys)
            Assert.DoesNotContain(permission, RoleTemplates.Materialize(template, [], []).Permissions);
    }

    [Fact]
    public async Task Stock_valuation_discovery_preserves_separate_review_and_factor_renewal()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
        var maker = await data.AddActorAsync(7371, scope, Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose);
        var checker = await data.AddActorAsync(7372, scope, Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationDecide);
        var reader = await data.AddActorAsync(7373, scope, Permissions.ProcurementStockValuationRead);
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        }
        var make = await Actions(maker); var review = await Actions(checker);
        Assert.Contains(Permissions.ProcurementStockValuationPropose, make); Assert.DoesNotContain(Permissions.ProcurementStockValuationDecide, make);
        Assert.Contains(Permissions.ProcurementStockValuationDecide, review); Assert.DoesNotContain(Permissions.ProcurementStockValuationPropose, review);
        Assert.Equal(new[] { Permissions.ProcurementStockValuationRead }, await Actions(reader));
        Assert.DoesNotContain(Permissions.ProcurementSupplierDocumentEvidenceRead, make);
        Assert.DoesNotContain(Permissions.JournalPost, review);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var (actor, permission) in new[] { (maker, Permissions.ProcurementStockValuationPropose), (checker, Permissions.ProcurementStockValuationDecide) }) {
            var current = await data.Store.ReadCurrentAsync(actor);
            Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
            Assert.Contains(permission, await Actions(actor));
            Assert.Equal("step_up_required", ScopePolicy.Compile(actor, current, permission, data.Clock.UtcNow).DenialCode);
            Assert.True(ScopePolicy.CanRequestStepUp(actor, current, permission, data.Clock.UtcNow));
        }
    }
}
