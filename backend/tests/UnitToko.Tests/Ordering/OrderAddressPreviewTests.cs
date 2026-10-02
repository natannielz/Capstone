using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
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
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderAddressPreviewTests(MongoFixture fixture)
{
    private static readonly OrderBuyingContext Branch = new(new(10), new(11));
    private static readonly PostalAddress Billing = OrganizationAddressTestData.Postal with { Line1 = "Jalan Legal Billing 5", Line2 = null, PostalCode = "12820" };
    private static OrderQuoteService Service(CatalogAuthoringTestData data, MongoClient? client = null) {
        var selected = client ?? data.Client;
        return new(selected.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), new(selected), data.Clock);
    }
    private static async Task<OrganizationAddressTestData> PrepareAsync(CatalogAuthoringTestData data) {
        await data.Base.AddGrantAsync(data.Buyer, 6200, CatalogAuthoringTestData.Audience, [Permissions.OrderSubmit]);
        var postal = await OrganizationAddressTestData.PrepareAsync(data.Base, data.Client);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Buyer.UserId);
        await postal.DecideAsync(await postal.ProposeAsync("order-delivery"), "order-delivery-approved");
        await postal.DecideAsync(await postal.ProposeAsync("order-billing", postal.Input(target: OrganizationAddressTestData.Buyer,
            purpose: OrganizationAddressPurpose.LegalBilling) with { Address = Billing }), "order-billing-approved");
        return postal;
    }
    [Fact]
    public async Task Actual_orderer_reads_both_exact_approved_postal_sources_without_contact_or_order_effects() {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var postal = await PrepareAsync(data);
        var before = await postal.StateAsync(); var result = await Service(data).AddressesAsync(data.Buyer, Branch);
        Assert.Equal(Branch, result.Context); Assert.Equal(data.Clock.UtcNow, result.ObservedAt);
        Assert.Equal(OrganizationAddressTestData.Buyer, result.LegalBilling.Target); Assert.Equal(OrganizationAddressPurpose.LegalBilling, result.LegalBilling.Purpose);
        Assert.Equal(Billing, result.LegalBilling.Address); Assert.Equal("BUYER", result.LegalBilling.Code); Assert.Equal("Synthetic buying organization", result.LegalBilling.Name);
        Assert.Equal(OrganizationAddressTestData.Branch, result.Delivery.Target); Assert.Equal(OrganizationAddressPurpose.Delivery, result.Delivery.Purpose);
        Assert.Equal(OrganizationAddressTestData.Postal, result.Delivery.Address); Assert.Equal("TEBET", result.Delivery.Code); Assert.Equal("Synthetic Tebet branch", result.Delivery.Name);
        foreach (var value in new[] { result.LegalBilling, result.Delivery }) {
            var version = await postal.Rows(B.Versions).Find(new BsonDocument("_id", value.VersionId.Value)).SingleAsync();
            Assert.Equal(value.AddressId.Value, version["addressId"].AsInt64); Assert.Equal(1L, value.Version);
            Assert.Equal(2L, value.SourceScopeVersion); Assert.Equal(1L, value.ProfileVersion); Assert.Equal(value.Target.Id, value.ProfileId.Value);
            Assert.Equal(value.SnapshotHash, version["snapshotHash"].AsString); Assert.Equal(value.SourceHash, version["source"]["snapshotHash"].AsString);
            Assert.Equal(value.EffectiveAt.UtcDateTime, version["effectiveAt"].ToUniversalTime());
        }
        Assert.Matches("^[0-9a-f]{64}$", result.Binding);
        Assert.Equal(result.Binding, (await Service(data).AddressesAsync(data.Buyer, Branch)).Binding);
        var other = await data.ActorAsync(6300, CatalogAuthoringTestData.Audience, Permissions.OrderSubmit);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, other.UserId);
        Assert.NotEqual(result.Binding, (await Service(data).AddressesAsync(other, Branch)).Binding);
        Assert.Equal(before, await postal.StateAsync());
        foreach (var table in new[] { "orderDrafts", "orders", "stockBalances", "movements", "reservations", "journalEntries", "invoices" })
            Assert.Equal(0, await data.CountAsync(table));
    }
    [Fact]
    public async Task Historical_postal_snapshot_remains_available_to_its_owner_but_is_not_a_current_order_address() {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var postal = await PrepareAsync(data);
        var original = await Service(data).AddressesAsync(data.Buyer, Branch);
        await ChangeProfileAsync(postal);
        var history = await postal.Service.ReadAsync(postal.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery);
        Assert.False(history["current"].AsBoolean); Assert.Equal(original.Delivery.VersionId.Value, history["versionId"].AsInt64);
        Assert.Equal(original.Delivery.Name, history["name"].AsString); Assert.Equal(B.Details(original.Delivery.Address), history["address"]);
        Assert.Equal("organization_address_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => Service(data).AddressesAsync(data.Buyer, Branch))).Code);
    }
    [Fact]
    public async Task Postal_or_catalog_permissions_do_not_replace_the_exact_ordering_branch_scope() {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var postal = await PrepareAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).AddressesAsync(postal.Reader, Branch));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).AddressesAsync(data.Buyer, new(new(10), new(12))));
        var foreign = await data.ActorAsync(6300, new(GrantScopeKind.BuyerOwned, 20, 21), Permissions.CatalogRead, Permissions.OrderSubmit);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).AddressesAsync(foreign, Branch))).Code);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(data).AddressesAsync(data.Buyer, Branch));
    }
    [Fact]
    public async Task Selected_division_requires_its_own_approved_delivery_address_and_never_falls_back_to_branch() {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var postal = await PrepareAsync(data);
        var proposal = await postal.Organizations.ProposeAsync(postal.ProfileMaker,
            new(OrganizationChangeKind.Create, ScopeNodeKind.Division, 11, "DIVISION", "Synthetic selected division"), OrganizationAddressTestData.Meta("order-division"));
        var approved = await postal.Organizations.ApproveAsync(postal.ProfileChecker, proposal.Id, 1, OrganizationAddressTestData.Meta("order-division-approved"));
        var division = Assert.IsType<long>(approved.ScopeId); var context = Branch with { DivisionId = new(division) };
        await data.Base.AddGrantAsync(data.Buyer, 6201, new(GrantScopeKind.BuyerOwned, 10, 11, division), [Permissions.OrderSubmit]);
        Assert.Equal("organization_address_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => Service(data).AddressesAsync(data.Buyer, context))).Code);
        var input = new OrganizationAddressInput(new(ScopeNodeKind.Division, division), OrganizationAddressPurpose.Delivery, 1, 1, 0,
            OrganizationAddressTestData.Postal with { Line1 = "Jalan Divisi 8", Line2 = null });
        await postal.DecideAsync(await postal.ProposeAsync("order-division-delivery", input), "order-division-delivery-approved");
        var result = await Service(data).AddressesAsync(data.Buyer, context);
        Assert.Equal(context, result.Context); Assert.Equal(input.Target, result.Delivery.Target); Assert.Equal(input.Address, result.Delivery.Address);
        Assert.Equal("Synthetic selected division", result.Delivery.Name); Assert.Equal(Billing, result.LegalBilling.Address);
        Assert.Equal(OrganizationAddressTestData.Postal, (await Service(data).AddressesAsync(data.Buyer, Branch)).Delivery.Address);
    }
    [Theory]
    [InlineData("grant")] [InlineData("session")] [InlineData("profile")] [InlineData("address")]
    public async Task Concurrent_authority_profile_or_address_change_cannot_return_a_stale_postal_pair(string change) {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture); var postal = await PrepareAsync(data);
        var original = await Service(data).AddressesAsync(data.Buyer, Branch);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == B.Heads && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Postal preview race did not complete");
                }
            });
        };
        var pending = Task.Run(() => Service(data, new(settings)).AddressesAsync(data.Buyer, Branch));
        BsonDocument? approved = null;
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await data.Sessions.RevokeAsync(data.Issued[data.Buyer.UserId].OpaqueToken, "Synthetic postal preview session revocation");
            if (change == "profile") await ChangeProfileAsync(postal);
            if (change == "address") approved = await postal.DecideAsync(await postal.ProposeAsync("order-next-delivery", postal.Input(1)
                with { Address = OrganizationAddressTestData.Postal with { Line1 = "Jalan Baru 99" } }), "order-next-delivery-approved");
        } finally { release.Set(); }
        OrderAddressPreview? result = null;
        var failure = await Record.ExceptionAsync(async () => { result = await pending.WaitAsync(TimeSpan.FromSeconds(30)); });
        Assert.Equal(1, observed);
        if (failure is not null) Assert.True(failure is AuthorizationDeniedException or PersistenceConflictException, failure.ToString());
        else {
            Assert.Equal("address", change); Assert.NotNull(approved); Assert.NotNull(result);
            Assert.Equal(approved["versionId"].AsInt64, result.Delivery.VersionId.Value); Assert.Equal(2L, result.Delivery.Version);
            Assert.Equal("Jalan Baru 99", result.Delivery.Address.Line1); Assert.Equal(original.LegalBilling, result.LegalBilling);
            Assert.NotEqual(original.Binding, result.Binding);
        }
        if (change is "grant" or "session") Assert.IsType<AuthorizationDeniedException>(failure);
        if (change == "profile") Assert.Equal("organization_address_changed", Assert.IsType<PersistenceConflictException>(failure).Code);
    }
    private static async Task ChangeProfileAsync(OrganizationAddressTestData data) {
        var proposal = await data.Organizations.ProposeAsync(data.ProfileMaker,
            new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Branch, 10, "TEBET", "Synthetic reviewed changed branch", 11, 2, 1), OrganizationAddressTestData.Meta("order-profile-change"));
        await data.Organizations.ApproveAsync(data.ProfileChecker, proposal.Id, 1, OrganizationAddressTestData.Meta("order-profile-change-approved"));
    }
}
