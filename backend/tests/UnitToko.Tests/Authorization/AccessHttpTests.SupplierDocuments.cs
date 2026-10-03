using System.Net;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests
{
    [Fact]
    public async Task Supplier_document_discovery_keeps_explicit_reader_privacy_and_fresh_factor_renewal()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
        var writer = await data.AddActorAsync(7071, scope, Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentRecord,
            Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.ProcurementSupplierDocumentMatch);
        var reader = await data.AddActorAsync(7072, scope, Permissions.ProcurementRead);
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        }
        var allowed = await Actions(writer); var readOnly = await Actions(reader);
        foreach (var permission in new[] { Permissions.ProcurementSupplierDocumentRecord, Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.ProcurementSupplierDocumentMatch }) {
            Assert.Contains(permission, allowed); Assert.DoesNotContain(permission, readOnly);
        }
        Assert.Equal(new[] { Permissions.ProcurementRead }, readOnly);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        var aged = await Actions(writer); var current = await data.Store.ReadCurrentAsync(writer);
        foreach (var permission in new[] { Permissions.ProcurementSupplierDocumentRecord, Permissions.ProcurementSupplierDocumentMatch }) {
            Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
            Assert.Contains(permission, aged); Assert.Equal("step_up_required", ScopePolicy.Compile(writer, current, permission, data.Clock.UtcNow).DenialCode);
            Assert.True(ScopePolicy.CanRequestStepUp(writer, current, permission, data.Clock.UtcNow));
        }
        Assert.Contains(Permissions.ProcurementSupplierDocumentEvidenceRead, aged);
        Assert.DoesNotContain("procurement.supplier-document.record", allowed);
    }
}
