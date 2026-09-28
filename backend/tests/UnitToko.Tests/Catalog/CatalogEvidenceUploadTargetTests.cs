using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using static UnitToko.Tests.Catalog.CatalogAuthoringTestData;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed partial class CatalogEvidenceUploadTargetTests(MongoFixture fixture)
{
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorized_active_unpublished_target_composes_in_callers_transaction(bool skuTarget)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = skuTarget ? await data.SkuAsync() : await data.FamilyAsync();
        var target = new EvidenceTargetRef(skuTarget ? EvidenceTargetKind.CatalogSku : EvidenceTargetKind.CatalogFamily, created.Id.Value);
        await new MongoUnitOfWork(data.Client).ExecuteAsync(async (tx, ct) =>
        {
            return await data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker, target, 1, async (snapshot, token) =>
            {
                Assert.Equal(target, snapshot.Target); Assert.Equal(Shop, snapshot.Owner); Assert.Equal(1, snapshot.Revision);
                Assert.Equal(skuTarget ? created.CurrentDefinitionId?.Value : null, snapshot.DefinitionId);
                Assert.Equal(skuTarget ? created.DefinitionVersion : null, snapshot.DefinitionVersion);
                await data.Rows("evidenceUploadProbe").InsertOneAsync(tx, new BsonDocument("_id", 1L), cancellationToken: token);
                Assert.True(tx.IsInTransaction); Assert.Equal(0, await data.CountAsync("evidenceUploadProbe"));
                return snapshot.Revision;
            }, ct);
        });
        Assert.Equal(1, await data.CountAsync("evidenceUploadProbe"));
        Assert.Equal(0, await data.CountAsync("catalogEntitlements"));
        Assert.Equal(0, await data.CountAsync("catalogAssortments"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_owner_and_read_only_buyer_cannot_invoke_upload_callback(bool buyer)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync(); var invoked = false;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, buyer ? data.Buyer : data.Other,
                new(EvidenceTargetKind.CatalogFamily, created.Id.Value), 1, (_, _) => { invoked = true; return Task.FromResult(1); }, ct)));
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("catalogCategories")]
    [InlineData("productFamilies")]
    [InlineData("skus")]
    public async Task Inactive_target_or_parent_rejects_upload_before_callback(string collection)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.SkuAsync(); var invoked = false;
        await data.Rows(collection).UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, U.Set("active", false));
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker,
                new(EvidenceTargetKind.CatalogSku, created.Id.Value), 1, (_, _) => { invoked = true; return Task.FromResult(1); }, ct)));
        Assert.Equal("catalog_resource_inactive", error.Code); Assert.False(invoked);
    }

    [Fact]
    public async Task Changed_expected_head_denies_reservation_without_callback()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync(); var invoked = false;
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => new MongoUnitOfWork(data.Client).ExecuteAsync((tx, ct) =>
            data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker,
                new(EvidenceTargetKind.CatalogFamily, created.Id.Value), 2, (_, _) => { invoked = true; return Task.FromResult(1); }, ct)));
        Assert.Equal("catalog_revision_conflict", error.Code); Assert.False(invoked);
    }

    [Theory]
    [InlineData("no_transaction")]
    [InlineData("other_client")]
    [InlineData("other_database")]
    public async Task Foreign_or_absent_caller_transaction_is_rejected_before_callback(string mismatch)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync(); var invoked = false;
        var client = mismatch == "other_client" ? new MongoClient("mongodb://127.0.0.1:27028/?replicaSet=rs0") : data.Client;
        using var tx = await client.StartSessionAsync();
        if (mismatch != "no_transaction") tx.StartTransaction();
        var commandDatabase = mismatch == "other_database" ? data.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "_other") : data.Database;
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Service.WithCatalogImageUploadTargetAsync(commandDatabase, tx, data.Maker,
            new(EvidenceTargetKind.CatalogFamily, created.Id.Value), 1, (_, _) => { invoked = true; return Task.FromResult(1); }));
        Assert.False(invoked);
        if (tx.IsInTransaction) await tx.AbortTransactionAsync();
    }

    [Fact]
    public async Task Owner_catching_callback_failure_still_cannot_commit_any_earlier_effect()
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync();
        await data.Rows("evidenceUploadProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await data.Rows("evidenceUploadProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1));
        var original = new InvalidDataException("synthetic required audit failure");
        var caught = await Assert.ThrowsAsync<InvalidDataException>(() => data.Service.WithCatalogImageUploadTargetAsync<int>(data.Database, tx, data.Maker,
            new(EvidenceTargetKind.CatalogFamily, created.Id.Value), 1, (_, _) => throw original));
        Assert.Same(original, caught); Assert.False(tx.IsInTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitTransactionAsync());
        Assert.Equal(0, (await data.RowAsync("evidenceUploadProbe", 1))["value"].AsInt32);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("session")]
    public async Task Authority_expiring_during_callback_aborts_caller_even_if_error_is_caught(string expiry)
    {
        await using var data = await CatalogAuthoringTestData.CreateAsync(fixture);
        var created = await data.FamilyAsync(); var deadline = data.Clock.UtcNow.AddSeconds(1);
        if (expiry == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), U.Set("expiresAt", deadline.UtcDateTime));
        if (expiry == "membership") await data.Rows("memberships").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), U.Set("expiresAt", deadline.UtcDateTime));
        if (expiry == "session") await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", data.Maker.Session!.Id), U.Set("idleExpiresAt", deadline.UtcDateTime));
        await data.Rows("evidenceUploadProbe").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "value", 0 } });
        using var tx = await data.Client.StartSessionAsync(); tx.StartTransaction();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.WithCatalogImageUploadTargetAsync(data.Database, tx, data.Maker,
            new(EvidenceTargetKind.CatalogFamily, created.Id.Value), 1, async (_, token) =>
            {
                await data.Rows("evidenceUploadProbe").UpdateOneAsync(tx, new BsonDocument("_id", 1L), U.Inc("value", 1), cancellationToken: token);
                data.Clock.UtcNow = deadline; return 1;
            }));
        Assert.False(tx.IsInTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.CommitTransactionAsync());
        Assert.Equal(0, (await data.RowAsync("evidenceUploadProbe", 1))["value"].AsInt32);
    }
}
