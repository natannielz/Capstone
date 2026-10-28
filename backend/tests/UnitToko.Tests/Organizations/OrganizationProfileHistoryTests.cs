using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.Tests.Organizations;

/// <summary>G-034 diagnosis. Procurement, address, spending-policy, commercial-policy and accounting-book pages all start from the
/// approved organisation profile of the scope they work in. An organisation created or named through the application
/// (technical.configure proposes, an independent organization.approve holder approves) carries that approval history and works.
/// A profile written straight into the database, as the local pilot seed does, has no approval history, so every one of those first
/// reads refuses with 409 organization_address_integrity_unavailable instead of trusting an unapproved name or address.</summary>
[Collection("Mongo")]
public sealed class OrganizationProfileHistoryTests(MongoFixture fixture)
{
    [Fact]
    public async Task An_organisation_named_through_the_application_works_and_a_profile_without_approval_history_is_refused()
    {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);

        // Named through the application: the first read works.
        var targets = await data.Service.TargetsAsync(data.Reader, OrganizationAddressPurpose.Delivery);
        Assert.Equal(11L, Assert.Single(targets["items"].AsBsonArray)["target"]["id"].AsInt64);

        // The same profile without its approval record, which is what a seed that inserts organizationProfiles directly leaves behind.
        Assert.Equal(1, (await data.Rows("authorizationHistory").DeleteManyAsync(new BsonDocument {
            { "operation", "organization.approved" }, { "after.profile._id", 11L } })).DeletedCount);
        Assert.Equal("organization_address_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.TargetsAsync(data.Reader, OrganizationAddressPurpose.Delivery))).Code);
    }
}
