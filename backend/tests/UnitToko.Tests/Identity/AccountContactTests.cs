using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class AccountContactTests(MongoFixture fixture)
{
    private static AccountContactInput Input(string name = "Petugas Cabang") => new(name, "+62 812 1234 5678", "PIC Cabang", "Tebet, Jakarta");
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Memperbarui kontak pribadi", "account-contact-test");

    [Fact]
    public async Task Self_contact_persists_with_original_receipt_and_history_without_changing_provider_facts_or_other_accounts()
    {
        await using var data = await Data.CreateAsync(fixture);
        var profile = new BsonDocument { { "_id", data.Owner.UserId }, { "schemaVersion", 1 }, { "displayName", "Verified Name" },
            { "verifiedEmail", "owner@example.test" }, { "verifiedAt", data.Store.Clock.UtcNow.UtcDateTime },
            { "updatedAt", data.Store.Clock.UtcNow.UtcDateTime }, { "version", 1L } };
        await data.Rows("accountProfiles").InsertOneAsync(profile);
        Assert.Equal(0, (await data.Service.CurrentAsync(data.Owner)).Revision);
        var original = await data.Service.SaveAsync(data.Owner, Input(), 0, Meta("original"));
        var originalBytes = (await data.Rows(AccountContactRecords.History).Find(new BsonDocument("_id", original.HistoryId)).SingleAsync()).ToBson();
        var changed = await data.Service.SaveAsync(data.Owner, Input("Nama Kontak Baru"), 1, Meta("second"));
        Assert.Equal(2, changed.Revision);
        Assert.Equal(original, await data.Service.SaveAsync(data.Owner, Input(), 0, Meta("original")));
        Assert.Equal(original, await data.Service.LookupAsync(data.Owner, "original"));
        Assert.Equal("Nama Kontak Baru", (await data.NewService().CurrentAsync(data.Owner)).Name);
        var page = await data.Service.HistoryAsync(data.Owner, limit: 1);
        Assert.Equal(changed.HistoryId, Assert.Single(page.Items).HistoryId);
        var older = await data.Service.HistoryAsync(data.Owner, page.NextBeforeHistoryId, 1);
        Assert.Equal(original.HistoryId, Assert.Single(older.Items).HistoryId); Assert.Null(older.NextBeforeHistoryId);
        Assert.Equal(0, (await data.Service.CurrentAsync(data.Foreign)).Revision);
        Assert.Empty((await data.Service.HistoryAsync(data.Foreign)).Items);
        Assert.Null(await data.Service.LookupAsync(data.Foreign, "original"));
        var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.SaveAsync(data.Owner,
            Input("Another Name"), 0, Meta("original")));
        Assert.Equal("idempotency_key_reused", conflict.Code);
        Assert.Equal(originalBytes, (await data.Rows(AccountContactRecords.History).Find(new BsonDocument("_id", original.HistoryId)).SingleAsync()).ToBson());
        Assert.Equal(profile.ToBson(), (await data.Rows("accountProfiles").Find(new BsonDocument("_id", data.Owner.UserId)).SingleAsync()).ToBson());
        Assert.Equal(2, await data.Rows(AccountContactRecords.Receipts).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", AccountContactRecords.Operation)).AnyAsync());
    }

    [Fact]
    public async Task Revision_race_and_same_key_retry_create_only_one_owned_change()
    {
        await using var data = await Data.CreateAsync(fixture);
        async Task<AccountContactResult?> Save(string key) { try { return await data.Service.SaveAsync(data.Owner, Input(), 0, Meta(key)); }
            catch (PersistenceConflictException) { return null; } }
        var race = await Task.WhenAll(Save("race-a"), Save("race-b"));
        Assert.Single(race, result => result is not null);
        Assert.Equal(1, await data.Rows(AccountContactRecords.History).CountDocumentsAsync(new BsonDocument()));
        var retries = await Task.WhenAll(data.Service.SaveAsync(data.Foreign, Input(), 0, Meta("retry")),
            data.Service.SaveAsync(data.Foreign, Input(), 0, Meta("retry")));
        Assert.Equal(retries[0], retries[1]);
        Assert.Equal(1, (await data.Service.CurrentAsync(data.Foreign)).Revision);
        Assert.Equal(1, await data.Rows(AccountContactRecords.Receipts).CountDocumentsAsync(new BsonDocument("actorId", data.Foreign.UserId)));
    }

    [Fact]
    public async Task Revoked_identity_stale_grant_missing_membership_suspended_branch_and_insufficient_factor_deny_current_and_original_lookup()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Service.SaveAsync(data.Owner, Input(), 0, Meta("approved-original"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CurrentAsync(data.Owner with { Assurance = FactorAssurance.SingleFactor }));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CurrentAsync(data.Owner with { ProviderSubject = "other-subject" }));
        foreach (var (table, id, field, value) in new (string, long, string, BsonValue)[] {
            ("authorizationScopes", 11, "status", "suspended"), ("memberships", data.Owner.UserId, "status", "revoked"),
            ("identities", data.Owner.UserId, "status", "suspended"), ("identities", data.Owner.UserId, "grantVersion", 2L) })
        {
            var original = await data.Rows(table).Find(new BsonDocument("_id", id)).SingleAsync();
            await data.Rows(table).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set(field, value));
            try {
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CurrentAsync(data.Owner));
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Owner, "approved-original"));
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.SaveAsync(data.Owner, Input(), 0, Meta("approved-original")));
            }
            finally { await data.Rows(table).ReplaceOneAsync(new BsonDocument("_id", id), original); }
        }
        Assert.Equal(1, await data.Rows(AccountContactRecords.History).CountDocumentsAsync(new BsonDocument()));
    }

    [Theory]
    [InlineData("history")]
    [InlineData("receipt")]
    [InlineData("head")]
    public async Task Missing_owned_link_never_returns_a_contact_or_replays_the_original_result(string removed)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Service.SaveAsync(data.Owner, Input(), 0, Meta("linked"));
        var table = removed == "history" ? AccountContactRecords.History : removed == "receipt" ? AccountContactRecords.Receipts : AccountContactRecords.Heads;
        await data.Rows(table).DeleteManyAsync(new BsonDocument());
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.CurrentAsync(data.Owner));
        Assert.Equal("account_contact_integrity_unavailable", error.Code);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.LookupAsync(data.Owner, "linked"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.SaveAsync(data.Owner, Input(), 0, Meta("linked")));
    }

    [Fact]
    public async Task Contact_and_metadata_require_canonical_bounded_unicode_and_no_privileged_fields_are_written()
    {
        foreach (var text in new[] { "", " name", "name ", "na\nme", "na\u200bme", "e\u0301", "\ud800", new string('x', 161) })
            Assert.Throws<DomainValidationException>(() => Input(text).Validate());
        Assert.Equal("é", Input("é").Validate().Name);
        Assert.Throws<DomainValidationException>(() => new AccountContactInput("Name", "", null, null).Validate());
        Assert.Throws<DomainValidationException>(() => new AccountContactInput("Name", null, null, new string('x', 1001)).Validate());
        await using var data = await Data.CreateAsync(fixture);
        foreach (var bad in new[] { " key", "key\n", "e\u0301", "\ud800" })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.SaveAsync(data.Owner, Input(), 0, Meta(bad)));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.SaveAsync(data.Owner, Input(), -1, Meta("negative")));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.HistoryAsync(data.Owner, 0, 1));
        Assert.False(await data.Rows(AccountContactRecords.History).Find(new BsonDocument()).AnyAsync());
    }

    private sealed class Data(StoreTestData store, MongoClient client, ActorContext owner, ActorContext foreign) : IAsyncDisposable
    {
        internal StoreTestData Store { get; } = store;
        internal ActorContext Owner { get; } = owner;
        internal ActorContext Foreign { get; } = foreign;
        internal AccountContactService Service { get; } = new(store.Database, new(client), new(store.Database), store.Store, store.Clock);
        internal AccountContactService NewService() => new(Store.Database, new(client), new(Store.Database), Store.Store, Store.Clock);
        internal IMongoCollection<BsonDocument> Rows(string table) => Store.Database.GetCollection<BsonDocument>(table);
        internal static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var store = await StoreTestData.CreateAsync(fixture);
            await AccountContactTestSchema.ApplyAsync(store.Database, store.Clock);
            return new(store, fixture.Client,
                await store.AddActorAsync(41, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 11), Permissions.OrderRead),
                await store.AddActorAsync(42, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 12), Permissions.OrderRead));
        }
        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
}

internal static class AccountContactTestSchema
{
    internal static async Task ApplyAsync(IMongoDatabase database, IClock clock)
    {
        if (await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 82 }, { "state", "applied" } }).AnyAsync()) return;
        var gate = new MigrationGate(database, clock, 82);
        var lease = await gate.AcquireAsync(82, default) ?? throw new InvalidOperationException("Expected uninstalled contact owner");
        await new AccountContactMigration(database).ApplyAsync(lease, default);
        await lease.CompleteAsync(default);
    }
}
