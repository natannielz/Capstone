using System.Net;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests
{
    [Fact]
    public void Supplier_claim_approval_requires_the_stored_requester_and_a_distinct_verified_person()
    {
        const string permission = Permissions.SupplierClaimApprove;
        Assert.True(ApprovalPolicy.RequiresEvidence(permission));
        Assert.True(PersonApprovalPolicy.RequiresEvidence(permission));
        Assert.False(ApprovalPolicy.Evaluate(102, permission, new(1, 1)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, permission, new(1, 1, RequestedBy: 101)).Allowed);
        Assert.True(PersonApprovalPolicy.RequiredAccounts(102, permission, null).IsDefault);
        var request = new ApprovalRequest(1, 1, RequestedBy: 101);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(102, permission, request).ToArray());
        Assert.False(PersonApprovalPolicy.Evaluate(102, permission, request,
            [new(101, 501, PersonVerification.Verified, 1), new(102, 501, PersonVerification.Verified, 1)]).Allowed);
        Assert.True(PersonApprovalPolicy.Evaluate(102, permission, request,
            [new(101, 501, PersonVerification.Verified, 1), new(102, 502, PersonVerification.Verified, 1)]).Allowed);
    }

    [Fact]
    public async Task Supplier_claim_discovery_separates_internal_review_and_keeps_aged_factor_renewal()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
        var maker = await data.AddActorAsync(7171, scope, Permissions.ProcurementRead, Permissions.SupplierClaim);
        var checker = await data.AddActorAsync(7172, scope, Permissions.ProcurementRead, Permissions.SupplierClaimApprove);
        var reader = await data.AddActorAsync(7173, scope, Permissions.ProcurementRead);
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        }
        var make = await Actions(maker); var review = await Actions(checker);
        Assert.Contains(Permissions.SupplierClaim, make); Assert.DoesNotContain(Permissions.SupplierClaimApprove, make);
        Assert.Contains(Permissions.SupplierClaimApprove, review); Assert.DoesNotContain(Permissions.SupplierClaim, review);
        Assert.Equal(new[] { Permissions.ProcurementRead }, await Actions(reader));
        Assert.DoesNotContain(Permissions.ProcurementSupplierDocumentEvidenceRead, make);
        Assert.DoesNotContain(Permissions.ProcurementInspectionEvidenceRead, review);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var (actor, permission) in new[] { (maker, Permissions.SupplierClaim), (checker, Permissions.SupplierClaimApprove) }) {
            var current = await data.Store.ReadCurrentAsync(actor);
            Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
            Assert.Contains(permission, await Actions(actor));
            Assert.Equal("step_up_required", ScopePolicy.Compile(actor, current, permission, data.Clock.UtcNow).DenialCode);
            Assert.True(ScopePolicy.CanRequestStepUp(actor, current, permission, data.Clock.UtcNow));
        }
    }
}
