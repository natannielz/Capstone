using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Work;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Work;

[Collection("Mongo")]
public sealed class WorkPermissionsTests(MongoFixture fixture)
{
    // A buyer who may only submit orders sees no staff queue: every source is filtered out before any queue is read.
    [Fact]
    public async Task Buyer_with_only_order_submit_is_offered_no_staff_work_source() {
        await using var data = await CatalogPriceTestData.CreateAsync(fixture);
        await new CollectionMigrations(data.Catalog.Database, data.Catalog.Clock).ApplyAsync();
        await data.Catalog.Base.AddGrantAsync(data.Catalog.Buyer, 6200, CatalogAuthoringTestData.Audience, [Permissions.OrderSubmit]);
        var permissions = new AuthorizedWorkPermissions(new AuthorizationStore(data.Catalog.Database, new(data.Catalog.Client), data.Catalog.Clock), data.Catalog.Clock);
        var allowed = await permissions.ReadAsync(data.Catalog.Buyer, default);
        Assert.True(allowed(Permissions.OrderSubmit));
        IWorkSource[] sources = [new PaymentReviewWorkSource(null!), new OrderReviewWorkSource(null!), new IncomingRequestWorkSource(null!), new DispatchWorkSource(null!)];
        Assert.DoesNotContain(sources, source => source.AvailableTo(allowed));
        Assert.Empty(await new WorkSummaryService(sources, permissions).ReadAsync(data.Catalog.Buyer, default));
    }
}
