using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class AccountContactMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Empty_interrupted_owner_preserves_stronger_validator_uuid_and_verified_profiles_without_seeding_contacts()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture);
        await RestoreV81Async(data.Database);
        var actor = await data.AddActorAsync(73, new(GrantScopeKind.BuyerOwned, OrganizationId: 10, BranchId: 11), Permissions.OrderRead);
        var profile = new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "displayName", "Provider Verified Name" },
            { "verifiedEmail", "verified.owner@example.test" }, { "verifiedAt", data.Clock.UtcNow.UtcDateTime },
            { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "version", 1L } };
        await data.Database.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(profile);
        var retained = await SnapshotAsync(data.Database, excludeContactOwners: true);
        var stronger = new BsonDocument("$and", new BsonArray { AccountContactSchemas.Validator(AccountContactRecords.Heads),
            new BsonDocument("_id", new BsonDocument("$ne", 999L)) });
        var existingHead = await (await data.Database.ListCollectionNamesAsync(new ListCollectionNamesOptions {
            Filter = new BsonDocument("name", AccountContactRecords.Heads) })).AnyAsync();
        if (existingHead) await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", AccountContactRecords.Heads }, { "validator", stronger },
            { "validationLevel", "strict" }, { "validationAction", "error" } });
        else await data.Database.CreateCollectionAsync(AccountContactRecords.Heads, new CreateCollectionOptions<BsonDocument> {
            Validator = stronger, ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
        var before = await DefinitionAsync(data.Database, AccountContactRecords.Heads);
        await AccountContactTestSchema.ApplyAsync(data.Database, data.Clock);
        await AccountContactStorage.RequireAsync(data.Database, default);
        Assert.Equal(before.ToBson(), (await DefinitionAsync(data.Database, AccountContactRecords.Heads)).ToBson());
        Assert.Equal(retained.ToBson(), (await SnapshotAsync(data.Database, excludeContactOwners: true)).ToBson());
        foreach (var name in AccountContactSchemas.Collections)
        {
            Assert.False(await data.Database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Database.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        var completed = await SnapshotAsync(data.Database);
        await AccountContactTestSchema.ApplyAsync(data.Database, data.Clock);
        Assert.Equal(completed.ToBson(), (await SnapshotAsync(data.Database)).ToBson());
    }

    [Fact]
    public async Task Positive_or_malformed_counter_and_weakened_provider_predecessor_refuse_before_any_ddl()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture);
        await RestoreV81Async(data.Database);
        var counters = data.Database.GetCollection<BsonDocument>("idCounters");
        foreach (var value in new BsonValue[] { 1L, -1L, 0 })
        {
            await counters.InsertOneAsync(new BsonDocument { { "_id", AccountContactRecords.History }, { "value", value } },
                new InsertOneOptions { BypassDocumentValidation = true });
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await counters.DeleteOneAsync(new BsonDocument("_id", AccountContactRecords.History)); }
        }
        var original = await DefinitionAsync(data.Database, "identities");
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "identities" },
            { "validator", IdentitySchemas.Collections()["identities"] } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "identities" },
            { "validator", original["options"]["validator"] } }); }
        foreach (var operation in new[] { AccountContactRecords.Operation, "identity.accountcontact.save" })
        {
            var receipt = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", 999L },
                { "operation", operation }, { "key", "unsupported-preinstallation" }, { "bodyHash", new string('a', 64) },
                { "result", new BsonDocument() }, { "createdAt", data.Clock.UtcNow.UtcDateTime } };
            await data.Database.GetCollection<BsonDocument>("idempotencyRecords").InsertOneAsync(receipt);
            try { await RefusesAsync(data.Database, data.Clock); }
            finally { await data.Database.GetCollection<BsonDocument>("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", receipt["_id"])); }
        }
    }

    [Fact]
    public async Task Unsupported_preinstallation_row_and_hidden_retry_index_are_preserved_and_refused_without_relabelling_owner()
    {
        await using var data = await UnitToko.Tests.Procurement.SupplierDocumentPdfMatchMigrationRehearsal.CreateAsync(fixture);
        await AccountContactTestSchema.ApplyAsync(data.Database, data.Clock);
        await RestoreV81Async(data.Database);
        var rows = data.Database.GetCollection<BsonDocument>(AccountContactRecords.Heads);
        var unowned = new BsonDocument { { "_id", 999L }, { "schemaVersion", 1 }, { "revision", 1L },
            { "name", "Unowned contact" }, { "phone", BsonNull.Value }, { "position", BsonNull.Value }, { "address", BsonNull.Value },
            { "lastHistoryId", 998L }, { "lastHash", new string('a', 64) }, { "updatedAt", data.Clock.UtcNow.UtcDateTime }, { "fence", 0L } };
        await rows.InsertOneAsync(unowned);
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await rows.DeleteOneAsync(new BsonDocument("_id", 999L)); }
        var key = AccountContactSchemas.Keys(AccountContactRecords.Receipts)[0];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", AccountContactRecords.Receipts },
            { "index", new BsonDocument { { "name", key.Name }, { "hidden", true } } } });
        try { await RefusesAsync(data.Database, data.Clock); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", AccountContactRecords.Receipts },
            { "index", new BsonDocument { { "name", key.Name }, { "hidden", false } } } }); }
    }

    // Test-only empty suffix rehearsal. Existing data and applied predecessor migrations are never removed.
    internal static async Task RestoreV81Async(IMongoDatabase db)
    {
        await UnitToko.Tests.Documents.PdfEvidenceValidationMigrationTests.RestoreV82Async(db);
        var history = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        if (history.Count <= 81) return;
        Assert.Equal(82, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        await AccountContactStorage.RequireAsync(db, default);
        foreach (var name in AccountContactSchemas.Collections)
        {
            Assert.False(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        }
        Assert.Equal(1L, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(
            new BsonDocument { { "_id", 82 }, { "state", "applied" } })).DeletedCount);
    }

    private static async Task RefusesAsync(IMongoDatabase db, IClock clock)
    {
        var before = await SnapshotAsync(db);
        var gate = new MigrationGate(db, clock, 82);
        var record = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 82)).SingleOrDefaultAsync();
        if (record is not null) await db.GetCollection<BsonDocument>("schemaMigrations").UpdateOneAsync(new BsonDocument("_id", 82),
            Builders<BsonDocument>.Update.Set("leaseUntil", clock.UtcNow.AddSeconds(-1).UtcDateTime));
        var lease = await gate.AcquireAsync(82, default) ?? throw new InvalidOperationException("Expected contact installation lease");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new AccountContactMigration(db).ApplyAsync(lease, default));
        Assert.Equal("Account contact migration found incompatible retained storage", failure.Message);
        Assert.Equal(before.ToBson(), (await SnapshotAsync(db)).ToBson());
    }

    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) =>
        await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db, bool excludeContactOwners = false)
    {
        var result = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal))
        {
            var name = definition["name"].AsString;
            if (name is "schemaMigrations" or "migrationLocks" || excludeContactOwners && AccountContactSchemas.Collections.Contains(name)) continue;
            result[name] = new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) },
                { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
}
