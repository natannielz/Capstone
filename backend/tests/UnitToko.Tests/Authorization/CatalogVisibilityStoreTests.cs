using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class CatalogVisibilityStoreTests(MongoFixture fixture)
{
    [Fact]
    public async Task Shared_catalog_uses_identical_scoping_before_count_page_search_detail_and_reference_reads_without_sharing_orders()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        foreach (var actor in new[] { data.BuyerA, data.BuyerB })
        {
            var list = await data.Catalog.QueryAsync(actor);
            Assert.Equal(3, list.Total);
            Assert.Equal(new long[] { 1, 3, 4 }, list.Items.Select(item => item.Id));
            Assert.Equal(list.Total, await data.Catalog.CountAsync(actor));
            Assert.Equal(3, (await data.Catalog.QueryAsync(actor, 1, 1)).Total);
            Assert.Equal(3, Assert.Single((await data.Catalog.QueryAsync(actor, 1, 1)).Items).Id);
            Assert.Empty((await data.Catalog.QueryAsync(actor, 3, 1)).Items);
            Assert.Equal(2, (await data.Catalog.QueryAsync(actor, search: "coffee")).Total);
            Assert.Equal(2, await data.Catalog.CountAsync(actor, "coffee"));
            Assert.Equal(1, (await data.Catalog.QueryAsync(actor, search: ".*")).Total);
            Assert.Equal(0, (await data.Catalog.QueryAsync(actor, search: "Hidden")).Total);
            Assert.Equal(0, await data.Catalog.CountAsync(actor, "Hidden"));
            var visible = await data.Catalog.ReadAsync(actor, 1);
            Assert.Equal(Assert.Single(list.Items, item => item.Id == 1), visible);
            Assert.Equal(visible, await data.Catalog.ReadPublicAsync(actor, data.PublicId(1)));
            Assert.Null(await data.Catalog.ReadAsync(actor, 2));
            Assert.Null(await data.Catalog.ReadAsync(actor, 900));
            Assert.Null(await data.Catalog.ReadPublicAsync(actor, data.PublicId(2)));
            foreach (var purpose in Enum.GetValues<CatalogMetadataPurpose>())
            {
                Assert.Equal(new CatalogMetadataReference(purpose, visible!), await data.Catalog.ReadReferenceAsync(actor, 1, purpose));
                Assert.Null(await data.Catalog.ReadReferenceAsync(actor, 2, purpose));
            }
            var serialized = JsonSerializer.Serialize(visible);
            Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { "DisplayName", "Id", "PublicId" }, typeof(CatalogMetadata).GetProperties().Select(property => property.Name).Order());
        }
        var orders = data.Base.Database.GetCollection<BsonDocument>("orders");
        Assert.NotNull(await data.Base.Store.ReadAsync(data.BuyerA, Permissions.OrderRead, orders, 70001));
        Assert.Null(await data.Base.Store.ReadAsync(data.BuyerB, Permissions.OrderRead, orders, 70001));
        await data.AddSkuAsync(10, [new(502, CatalogStoreTestData.ShopOwner)], "Hidden extra coffee");
        Assert.Equal(3, await data.Catalog.CountAsync(data.BuyerB));
        Assert.Equal(2, await data.Catalog.CountAsync(data.BuyerB, "Coffee"));
    }

    [Theory]
    [InlineData("scalar-listings")]
    [InlineData("object-listings")]
    [InlineData("scalar-element")]
    [InlineData("array-assortment")]
    [InlineData("int-assortment")]
    [InlineData("zero-assortment")]
    [InlineData("array-owner")]
    [InlineData("array-owner-organization")]
    [InlineData("array-organization")]
    [InlineData("int-organization")]
    [InlineData("wrong-organization")]
    [InlineData("duplicate-listing")]
    [InlineData("duplicate-id-different-owner")]
    [InlineData("mismatched-owner")]
    [InlineData("mixed-elements")]
    [InlineData("malformed-hidden-listing")]
    [InlineData("wrong-public-id")]
    [InlineData("public-id-array")]
    [InlineData("uuid-v1")]
    [InlineData("uuid-newline")]
    [InlineData("display-name-newline")]
    [InlineData("display-name-array")]
    [InlineData("int-id")]
    [InlineData("owner-branch")]
    [InlineData("shop-warehouse")]
    [InlineData("warehouse-missing")]
    [InlineData("too-many-listings")]
    public async Task Malformed_or_mixed_publication_metadata_is_excluded_before_counts_and_all_lookups(string malformed)
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        var row = data.Sku(30, [new(501, CatalogStoreTestData.ShopOwner)], "Malformed coffee");
        var listing = row["catalogListings"][0].AsBsonDocument;
        switch (malformed)
        {
            case "scalar-listings": row["catalogListings"] = 501L; break;
            case "object-listings": row["catalogListings"] = listing; break;
            case "scalar-element": row["catalogListings"] = new BsonArray { 501L }; break;
            case "array-assortment": listing["assortmentId"] = new BsonArray { 501L }; break;
            case "int-assortment": listing["assortmentId"] = 501; break;
            case "zero-assortment": listing["assortmentId"] = 0L; break;
            case "array-owner": listing["ownerScope"] = new BsonArray { listing["ownerScope"] }; break;
            case "array-owner-organization": listing["ownerScope"]["organizationId"] = new BsonArray { 100L }; break;
            case "array-organization": row["organizationId"] = new BsonArray { 100L }; break;
            case "int-organization": row["organizationId"] = 100; break;
            case "wrong-organization": row["organizationId"] = 200L; break;
            case "duplicate-listing": row["catalogListings"].AsBsonArray.Add(listing.DeepClone()); break;
            case "duplicate-id-different-owner": row["catalogListings"].AsBsonArray.Add(CatalogStoreTestData.Listing(new(501, CatalogStoreTestData.WarehouseOwner))); break;
            case "mismatched-owner": listing["ownerScope"] = AuthorizationBson.Scope(CatalogStoreTestData.WarehouseOwner); break;
            case "mixed-elements":
                row["catalogListings"] = new BsonArray
                {
                    CatalogStoreTestData.Listing(new(501, CatalogStoreTestData.WarehouseOwner)),
                    CatalogStoreTestData.Listing(new(502, CatalogStoreTestData.ShopOwner))
                }; break;
            case "malformed-hidden-listing": row["catalogListings"].AsBsonArray.Add(new BsonDocument("assortmentId", 502L)); break;
            case "wrong-public-id": row["publicId"] = "not-a-public-id"; break;
            case "public-id-array": row["publicId"] = new BsonArray { data.PublicId(30) }; break;
            case "uuid-v1": row["publicId"] = "00000000-0000-1000-8000-000000000030"; break;
            case "uuid-newline": row["publicId"] = data.PublicId(30) + "\n"; break;
            case "display-name-newline": row["displayName"] = "Malformed coffee\n"; break;
            case "display-name-array": row["displayName"] = new BsonArray { "Malformed coffee" }; break;
            case "int-id": row["_id"] = 30; break;
            case "owner-branch": listing["ownerScope"]["branchId"] = 11L; break;
            case "shop-warehouse": listing["ownerScope"]["warehouseId"] = 102L; break;
            case "warehouse-missing": listing["assortmentId"] = 503L; listing["ownerScope"]["kind"] = "fulfillmentWarehouse"; break;
            case "too-many-listings": row["catalogListings"] = new BsonArray(Enumerable.Range(501, 129)
                .Select(id => CatalogStoreTestData.Listing(new(id, CatalogStoreTestData.ShopOwner)))); break;
            default: throw new InvalidOperationException();
        }
        await data.Base.Database.GetCollection<BsonDocument>("skus").InsertOneAsync(row);
        Assert.Equal(3, await data.Catalog.CountAsync(data.BuyerA));
        Assert.Equal(new long[] { 1, 3, 4 }, (await data.Catalog.QueryAsync(data.BuyerA)).Items.Select(item => item.Id));
        Assert.Equal(0, await data.Catalog.CountAsync(data.BuyerA, "Malformed"));
        Assert.Null(await data.Catalog.ReadAsync(data.BuyerA, 30));
        Assert.Null(await data.Catalog.ReadPublicAsync(data.BuyerA, data.PublicId(30)));
        foreach (var purpose in Enum.GetValues<CatalogMetadataPurpose>())
            Assert.Null(await data.Catalog.ReadReferenceAsync(data.BuyerA, 30, purpose));
    }

    [Fact]
    public async Task Same_sku_in_multiple_entitled_assortments_is_counted_once_and_public_identifiers_fail_closed_only_for_visible_duplicates()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        Assert.Equal(1, (await data.Catalog.QueryAsync(data.BuyerA, search: "second")).Total);
        var duplicate = data.Sku(40, [new(502, CatalogStoreTestData.ShopOwner)], "Hidden duplicate");
        duplicate["publicId"] = data.PublicId(1);
        await data.Base.Database.GetCollection<BsonDocument>("skus").InsertOneAsync(duplicate);
        Assert.Equal(1, (await data.Catalog.ReadPublicAsync(data.BuyerA, data.PublicId(1)))!.Id);
        await data.Base.Database.GetCollection<BsonDocument>("skus").UpdateOneAsync(new BsonDocument("_id", 40L),
            Builders<BsonDocument>.Update.Set("catalogListings", new BsonArray { CatalogStoreTestData.Listing(new(501, CatalogStoreTestData.ShopOwner)) }));
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.ReadPublicAsync(data.BuyerA, data.PublicId(1)));
        Assert.Equal("ambiguous_catalog_identifier", denied.Code);
    }

    [Fact]
    public async Task Optional_product_metadata_is_not_required_before_the_catalog_product_module_supplies_it()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        var row = data.Sku(50, [new(501, CatalogStoreTestData.ShopOwner)], "Temporary name");
        row.Remove("publicId"); row.Remove("displayName");
        await data.Base.Database.GetCollection<BsonDocument>("skus").InsertOneAsync(row);
        Assert.Equal(new CatalogMetadata(50, null, null), await data.Catalog.ReadAsync(data.BuyerA, 50));
        Assert.Equal(4, await data.Catalog.CountAsync(data.BuyerA));
        Assert.Equal(0, await data.Catalog.CountAsync(data.BuyerA, "Temporary"));
    }

    [Theory]
    [InlineData("entitlement")]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("actor")]
    [InlineData("audience")]
    [InlineData("owner")]
    [InlineData("assortment")]
    [InlineData("entitlement-expiry")]
    [InlineData("grant-expiry")]
    [InlineData("membership-expiry")]
    [InlineData("approval-provenance")]
    public async Task A_paused_read_cannot_return_metadata_after_current_authority_changes(string change)
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        if (change.EndsWith("-expiry", StringComparison.Ordinal))
        {
            var collection = change switch { "grant-expiry" => "authorizationGrants", "membership-expiry" => "memberships", _ => "catalogEntitlements" };
            await data.Base.Database.GetCollection<BsonDocument>(collection).UpdateManyAsync(new BsonDocument("userId", data.BuyerA.UserId),
                Builders<BsonDocument>.Update.Set("expiresAt", data.Base.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        }
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new CatalogVisibilityStore(data.Base.Database, data.Base.Store, data.Base.Clock, async token =>
        {
            read.TrySetResult();
            await resume.Task.WaitAsync(token);
        });
        var pending = catalog.QueryAsync(data.BuyerA);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (change.EndsWith("-expiry", StringComparison.Ordinal)) data.Base.Clock.UtcNow += TimeSpan.FromMinutes(1);
            else if (change == "actor")
                await data.Base.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", data.BuyerA.UserId),
                    Builders<BsonDocument>.Update.Inc("grantVersion", 1L));
            else if (change is "audience" or "owner")
                await data.Base.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", change == "audience" ? 11L : 101L),
                    Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
            else if (change == "assortment")
                await data.Base.Database.GetCollection<BsonDocument>("catalogAssortments").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                    Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
            else if (change == "approval-provenance")
                await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").UpdateManyAsync(new BsonDocument("userId", data.BuyerA.UserId),
                    Builders<BsonDocument>.Update.Set("approvedBy", data.BuyerA.UserId).Inc("version", 1L));
            else
            {
                var collection = change switch { "grant" => "authorizationGrants", "membership" => "memberships", _ => "catalogEntitlements" };
                await data.Base.Database.GetCollection<BsonDocument>(collection).UpdateManyAsync(new BsonDocument("userId", data.BuyerA.UserId),
                    Builders<BsonDocument>.Update.Set("status", "revoked").Set("revokedAt", data.Base.Clock.UtcNow.UtcDateTime).Inc("version", 1L));
            }
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal(0, (await data.Base.Database.GetCollection<BsonDocument>("identities")
            .Find(new BsonDocument("_id", data.BuyerA.UserId)).SingleAsync())["fence"].AsInt64);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("expire")]
    public async Task Paused_reads_and_deferred_metadata_references_recheck_the_session(string change)
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        var unit = new MongoUnitOfWork(fixture.Client);
        var sessions = new MongoIdentitySessionStore(data.Base.Database, unit, data.Base.Clock,
            new(data.BuyerA.ProviderIssuer, "fixture", "fixture"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        await data.Base.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", data.BuyerA.UserId),
            Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var proof = new ProviderIdentityProof(data.BuyerA.ProviderIssuer, data.BuyerA.ProviderSubject, "fixture@example.test", true,
            "fixture", data.Base.Clock.UtcNow, data.Base.Clock.UtcNow, "otp", "fixture");
        var issued = await unit.ExecuteAsync((tx, token) => sessions.CreateAsync(tx,
            new(data.BuyerA.UserId, 1, data.BuyerA.GrantVersion, data.BuyerA.ProviderIssuer, data.BuyerA.ProviderSubject), proof, "fixture", token));
        var actor = data.BuyerA with { Session = issued.Session.Reference };
        var reference = await data.Catalog.ReadReferenceAsync(actor, 1, CatalogMetadataPurpose.Image);
        Assert.NotNull(reference);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new CatalogVisibilityStore(data.Base.Database, data.Base.Store, data.Base.Clock, async token =>
        {
            read.TrySetResult(); await resume.Task.WaitAsync(token);
        });
        var pending = catalog.ReadReferenceAsync(actor, 1, CatalogMetadataPurpose.Image);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (change == "revoke") await sessions.RevokeAsync(issued.OpaqueToken, "owner_logout");
            else data.Base.Clock.UtcNow += SessionPolicy.IdleTimeout;
        }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        foreach (var purpose in Enum.GetValues<CatalogMetadataPurpose>())
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.ReadReferenceAsync(actor, reference!.Item.Id, purpose));
    }

    [Fact]
    public async Task Revoked_entitlement_denies_every_new_read_and_previously_enumerated_delivery_reference()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        Assert.NotNull(await data.Catalog.ReadReferenceAsync(data.BuyerA, 1, CatalogMetadataPurpose.Export));
        await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").UpdateManyAsync(new BsonDocument("userId", data.BuyerA.UserId),
            Builders<BsonDocument>.Update.Set("status", "revoked").Set("revokedAt", data.Base.Clock.UtcNow.UtcDateTime).Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.QueryAsync(data.BuyerA));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.QueryAsync(data.BuyerA, search: "Coffee"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.CountAsync(data.BuyerA));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.ReadAsync(data.BuyerA, 1));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.ReadPublicAsync(data.BuyerA, data.PublicId(1)));
        foreach (var purpose in Enum.GetValues<CatalogMetadataPurpose>())
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.ReadReferenceAsync(data.BuyerA, 1, purpose));
        Assert.Equal(3, await data.Catalog.CountAsync(data.BuyerB));
    }

    [Fact]
    public async Task A_publication_change_with_its_required_assortment_version_fence_retries_with_current_counts()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new CatalogVisibilityStore(data.Base.Database, data.Base.Store, data.Base.Clock, async token =>
        {
            read.TrySetResult(); await resume.Task.WaitAsync(token);
        });
        var pending = catalog.QueryAsync(data.BuyerA);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await new MongoUnitOfWork(fixture.Client).ExecuteAsync(async (tx, token) =>
            {
                await data.Base.Database.GetCollection<BsonDocument>("skus").UpdateOneAsync(tx, new BsonDocument("_id", 1L),
                    Builders<BsonDocument>.Update.Set("catalogListings", new BsonArray()), cancellationToken: token);
                await data.Base.Database.GetCollection<BsonDocument>("catalogAssortments").UpdateOneAsync(tx, new BsonDocument("_id", 501L),
                    Builders<BsonDocument>.Update.Inc("version", 1L), cancellationToken: token);
                return true;
            });
        }
        finally { resume.TrySetResult(); }
        var page = await pending;
        Assert.Equal(2, page.Total);
        Assert.Equal(new long[] { 3, 4 }, page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Expired_and_revoked_history_does_not_consume_current_entitlement_capacity()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        var history = Enumerable.Range(1, 300).Select(id => CatalogVisibilityBson.Entitlement(
            id % 2 == 0 ? data.Entitlement(30000 + id, data.BuyerA, 501) with
            {
                Status = AuthorizationStatus.Revoked, RevokedAt = data.Base.Clock.UtcNow
            } : data.Entitlement(30000 + id, data.BuyerA, 501) with
            {
                EffectiveAt = data.Base.Clock.UtcNow.AddDays(-2), ApprovedAt = data.Base.Clock.UtcNow.AddDays(-2),
                ExpiresAt = data.Base.Clock.UtcNow.AddDays(-1)
            }));
        await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").InsertManyAsync(history);
        Assert.Equal(3, await data.Catalog.CountAsync(data.BuyerA));
    }

    [Fact]
    public async Task Fulfillment_staff_also_need_an_explicit_entitlement_bound_to_their_current_membership_and_owner_scope()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        var staff = await data.Base.AddActorAsync(2000, CatalogStoreTestData.ShopOwner, Permissions.CatalogRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.CountAsync(staff));
        await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(
            data.Entitlement(60001, staff, 501) with { AudienceScope = CatalogStoreTestData.ShopOwner }));
        Assert.Equal(new long[] { 1, 3 }, (await data.Catalog.QueryAsync(staff)).Items.Select(item => item.Id));
        Assert.Null(await data.Catalog.ReadAsync(staff, 4));
        await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 60001L),
            Builders<BsonDocument>.Update.Set("membershipId", data.BuyerA.UserId).Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.CountAsync(staff));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("array")]
    [InlineData("int")]
    public async Task Malformed_existing_grant_fence_counters_fail_closed_without_returning_metadata(string kind)
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        BsonValue invalid = kind switch { "negative" => new BsonInt64(-1), "array" => new BsonArray { 0L }, _ => new BsonInt32(0) };
        await data.Base.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.BuyerA.UserId),
            Builders<BsonDocument>.Update.Set("fence", invalid));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.QueryAsync(data.BuyerA));
    }

    [Theory]
    [InlineData("approvedAt", long.MaxValue)]
    [InlineData("approvedAt", long.MinValue)]
    [InlineData("expiresAt", long.MaxValue)]
    [InlineData("effectiveAt", long.MinValue)]
    public async Task Out_of_range_Bson_dates_produce_a_controlled_authorization_denial(string field, long milliseconds)
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").UpdateManyAsync(new BsonDocument("userId", data.BuyerA.UserId),
            Builders<BsonDocument>.Update.Set(field, new BsonDateTime(milliseconds)));
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.CountAsync(data.BuyerA));
        Assert.Equal("invalid_catalog_authorization_data", denied.Code);
    }

    [Fact]
    public async Task Malformed_request_bounds_and_authority_overflow_are_denied_without_arbitrary_projection_or_query_input()
    {
        await using var data = await CatalogStoreTestData.CreateAsync(fixture);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.QueryAsync(data.BuyerA, -1));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.QueryAsync(data.BuyerA, 10001));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.QueryAsync(data.BuyerA, limit: 101));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.QueryAsync(data.BuyerA, search: new string('x', 129)));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.CountAsync(data.BuyerA, "bad\nsearch"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.ReadAsync(data.BuyerA, 0));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.ReadPublicAsync(data.BuyerA, "00000000-0000-1000-8000-000000000001"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Catalog.ReadReferenceAsync(data.BuyerA, 1, (CatalogMetadataPurpose)99));
        var entitlements = Enumerable.Range(1, 255).Select(id => CatalogVisibilityBson.Entitlement(data.Entitlement(20000 + id, data.BuyerA, 501)));
        await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").InsertManyAsync(entitlements);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Catalog.QueryAsync(data.BuyerA));
        Assert.Equal("catalog_authority_limit", denied.Code);
    }
}

internal sealed class CatalogStoreTestData : IAsyncDisposable
{
    internal static readonly GrantScope ShopOwner = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    internal static readonly GrantScope WarehouseOwner = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    internal static readonly GrantScope Audience = new(GrantScopeKind.BuyerOwned, 10, 11);
    internal StoreTestData Base { get; }
    internal ActorContext BuyerA { get; private set; } = null!;
    internal ActorContext BuyerB { get; private set; } = null!;
    internal CatalogVisibilityStore Catalog { get; }
    private CatalogStoreTestData(StoreTestData data)
    {
        Base = data;
        Catalog = new(data.Database, data.Store, data.Clock);
    }
    internal static async Task<CatalogStoreTestData> CreateAsync(MongoFixture fixture)
    {
        var data = new CatalogStoreTestData(await StoreTestData.CreateAsync(fixture));
        // Session creation requires a current provider proof rather than a hard-coded date from another test fixture.
        data.Base.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        data.BuyerA = await data.Base.AddActorAsync(1000, Audience, Permissions.CatalogRead, Permissions.OrderRead);
        data.BuyerB = await data.Base.AddActorAsync(1001, Audience, Permissions.CatalogRead, Permissions.OrderRead);
        await data.Base.Database.GetCollection<BsonDocument>("catalogAssortments").InsertManyAsync(new[]
        {
            new CatalogAssortment(501, ShopOwner, AuthorizationStatus.Active, 1),
            new CatalogAssortment(502, ShopOwner, AuthorizationStatus.Active, 1),
            new CatalogAssortment(503, WarehouseOwner, AuthorizationStatus.Active, 1)
        }.Select(CatalogVisibilityBson.Assortment));
        await data.Base.Database.GetCollection<BsonDocument>("catalogEntitlements").InsertManyAsync(new[]
        {
            data.Entitlement(10001, data.BuyerA, 501), data.Entitlement(10002, data.BuyerB, 501),
            data.Entitlement(10003, data.BuyerA, 503), data.Entitlement(10004, data.BuyerB, 503)
        }.Select(CatalogVisibilityBson.Entitlement));
        await data.AddSkuAsync(1, [new(501, ShopOwner)], "Coffee .*");
        await data.AddSkuAsync(2, [new(502, ShopOwner)], "Hidden coffee");
        await data.AddSkuAsync(3, [new(501, ShopOwner), new(503, WarehouseOwner)], "Coffee second");
        await data.AddSkuAsync(4, [new(503, WarehouseOwner)], "Tea");
        await data.Base.AddResourceAsync(70001, 10, 11, data.BuyerA.UserId);
        return data;
    }
    internal CatalogEntitlement Entitlement(long id, ActorContext actor, long assortment) => new(id, actor.UserId, actor.UserId,
        assortment, Audience, AuthorizationStatus.Active, Base.Clock.UtcNow, null, null, 1, 998, 999, id + 100000, 1, Base.Clock.UtcNow);
    internal string PublicId(long id) => $"00000000-0000-4000-8000-{id:000000000000}";
    internal static BsonDocument Listing(CatalogListing listing) => new()
        { { "assortmentId", listing.AssortmentId }, { "ownerScope", AuthorizationBson.Scope(listing.OwnerScope) } };
    internal BsonDocument Sku(long id, ImmutableArray<CatalogListing> listings, string displayName) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "organizationId", 100L }, { "sku", "fixture-" + id },
        { "catalogListings", new BsonArray(listings.Select(Listing)) }, { "publicId", PublicId(id) }, { "displayName", displayName },
        { "cost", "secret cost" }, { "price", "secret price" }, { "bankEvidence", "secret bank" }
    };
    internal Task AddSkuAsync(long id, ImmutableArray<CatalogListing> listings, string displayName) =>
        Base.Database.GetCollection<BsonDocument>("skus").InsertOneAsync(Sku(id, listings, displayName));
    public ValueTask DisposeAsync() => Base.DisposeAsync();
}
