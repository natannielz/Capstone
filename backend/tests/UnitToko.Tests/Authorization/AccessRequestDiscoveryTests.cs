using System.Net;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests {
    [Fact]
    public async Task Current_access_advertises_only_scoped_branch_request_actions_for_buyer_and_staff() {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var buyer = await data.AddActorAsync(8101, new(GrantScopeKind.BuyingBranch, 10, BranchId: 11),
            Permissions.BranchPic, Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit);
        var staff = await data.AddActorAsync(8102, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103),
            Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestDecide);
        var branchReader = await data.AddActorAsync(8103, new(GrantScopeKind.BuyingBranch, 10, BranchId: 12),
            Permissions.BranchPic, Permissions.BranchOrderRequestRead);
        var unrelated = await data.AddActorAsync(8104, new(GrantScopeKind.BuyerOwned, 10, BranchId: 11), Permissions.CatalogRead);
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(value => value!.GetValue<string>()).Order().ToArray();
        }
        Assert.Equal(new[] { Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit }.Order(), await Actions(buyer));
        Assert.Equal(new[] { Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestDecide }.Order(), await Actions(staff));
        Assert.Equal(new[] { Permissions.BranchOrderRequestRead }, await Actions(branchReader));
        Assert.Equal(new[] { Permissions.CatalogRead }, await Actions(unrelated));
    }
}
