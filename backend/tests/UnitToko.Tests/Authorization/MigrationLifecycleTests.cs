using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class MigrationLifecycleTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();

    [Fact]
    public async Task Actual_v2_upgrade_preserves_history_rules_and_immutable_rows_and_raises_only_needed_sequences()
    {
        await InDatabase(async database =>
        {
            await ApplyVersionTwo(database);
            var before = await History(database);
            var scopes = database.GetCollection<BsonDocument>("authorizationScopes");
            const long importedId = 9_007_199_254_740_993L;
            await scopes.InsertOneAsync(Node(importedId));
            var audit = Audit(51L);
            await database.GetCollection<BsonDocument>("authorizationHistory").InsertOneAsync(audit);
            var oldRejected = AccessProposal(7L);
            oldRejected["status"] = "rejected";
            await database.GetCollection<BsonDocument>("authorizationProposals").InsertOneAsync(oldRejected);
            await database.GetCollection<BsonDocument>("idCounters").InsertManyAsync([
                new() { { "_id", "authorizationScopes" }, { "value", 2L } },
                new() { { "_id", "authorizationHistory" }, { "value", 100L } }
            ]);
            var validator = await Validator(database, "authorizationHistory");
            var stronger = new BsonDocument("$and", new BsonArray
            {
                validator, new BsonDocument("reason", new BsonDocument("$regex", "^reviewed"))
            });
            await SetValidator(database, "authorizationHistory", stronger);

            var migrations = new CollectionMigrations(database, clock);
            Assert.False(await migrations.IsCurrentAsync());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            var after = await History(database);
            Assert.Equal(before, after.Take(2));
            Assert.Equal(Enumerable.Range(1, CollectionMigrations.CurrentVersion), after.Select(row => row["_id"].AsInt32));
            Assert.Equal(audit, await database.GetCollection<BsonDocument>("authorizationHistory")
                .Find(new BsonDocument("_id", 51L)).SingleAsync());
            Assert.Equal(oldRejected, await database.GetCollection<BsonDocument>("authorizationProposals")
                .Find(new BsonDocument("_id", 7L)).SingleAsync());
            Assert.Equal(importedId + 1, (await new IdAllocator(database).NextAsync("authorizationScopes")).Value);
            Assert.Equal(101L, (await new IdAllocator(database).NextAsync("authorizationHistory")).Value);
            Assert.Empty(await database.GetCollection<BsonDocument>("organizationProfiles")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
            Assert.Equal(2, await database.GetCollection<BsonDocument>("idCounters")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var denied = Audit(52L);
            denied["reason"] = "not reviewed";
            await Invalid(database, "authorizationHistory", denied);
            var appliedValidator = await Validator(database, "authorizationHistory");
            await migrations.ApplyAsync();
            Assert.Equal(appliedValidator, await Validator(database, "authorizationHistory"));
            Assert.Equal(after, await History(database));
            Assert.False(await new MigrationGate(database, clock, 2).IsCurrentAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new MigrationGate(database, clock, 2)
                .AcquireAsync(2, CancellationToken.None));
        });
    }

    [Fact]
    public async Task Incompatible_new_collection_stops_before_ddl_or_counter_changes_then_retries()
    {
        await InDatabase(async database =>
        {
            await ApplyVersionTwo(database);
            var before = await History(database);
            var oldValidator = await Validator(database, "authorizationProposals");
            await database.GetCollection<BsonDocument>("authorizationScopes").InsertOneAsync(Node(30L));
            var broken = Profile(30L);
            broken["parentId"] = 2; // Int32 must never become a valid internal reference.
            await database.GetCollection<BsonDocument>("organizationProfiles").InsertOneAsync(broken);
            var migrations = new CollectionMigrations(database, clock);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => migrations.ApplyAsync());
            Assert.Contains("organizationProfiles", failure.Message);
            Assert.Contains("reconcile", failure.Message);
            Assert.Equal(oldValidator, await Validator(database, "authorizationProposals"));
            Assert.Equal(before, (await History(database)).Take(2));
            Assert.DoesNotContain("authorizationReadEvents", await (await database.ListCollectionNamesAsync()).ToListAsync());
            Assert.Equal(0, await database.GetCollection<BsonDocument>("idCounters")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.False(await migrations.IsCurrentAsync());
            await database.GetCollection<BsonDocument>("organizationProfiles").ReplaceOneAsync(
                new BsonDocument("_id", 30L), Profile(30L));
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.Equal(31L, (await new IdAllocator(database).NextAsync("authorizationScopes")).Value);
        });
    }

    [Fact]
    public async Task Duplicate_scoped_codes_fail_preflight_without_weakening_previous_validation()
    {
        await InDatabase(async database =>
        {
            await ApplyVersionTwo(database);
            var oldValidator = await Validator(database, "authorizationProposals");
            await database.GetCollection<BsonDocument>("organizationProfiles").InsertManyAsync([Profile(20L), Profile(21L)]);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(database, clock).ApplyAsync());
            Assert.Contains("duplicate scoped codes", failure.Message);
            Assert.DoesNotContain("FIXTURE", failure.Message);
            Assert.Equal(oldValidator, await Validator(database, "authorizationProposals"));
            Assert.DoesNotContain("authorizationReadEvents", await (await database.ListCollectionNamesAsync()).ToListAsync());
        });
    }

    [Fact]
    public async Task Invalid_high_water_counter_stops_before_schema_changes_and_is_not_repaired_implicitly()
    {
        await InDatabase(async database =>
        {
            await ApplyVersionTwo(database);
            var before = await Validator(database, "authorizationHistory");
            var counters = database.GetCollection<BsonDocument>("idCounters");
            await counters.InsertOneAsync(new BsonDocument { { "_id", "authorizationScopes" }, { "value", -1L } });
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(database, clock).ApplyAsync());
            Assert.Contains("idCounters", failure.Message);
            Assert.Contains("authorizationScopes", failure.Message);
            Assert.Equal(before, await Validator(database, "authorizationHistory"));
            Assert.Equal(-1L, (await counters.Find(new BsonDocument("_id", "authorizationScopes")).SingleAsync())["value"].AsInt64);
            Assert.DoesNotContain("organizationProfiles", await (await database.ListCollectionNamesAsync()).ToListAsync());
        });
    }

    [Fact]
    public async Task Cancelled_v3_releases_lease_and_resumes_only_v3_with_majority_ddl()
    {
        await InDatabase(async database =>
        {
            await ApplyVersionTwo(database);
            var before = await History(database);
            using var cancellation = new CancellationTokenSource();
            var commands = new ConcurrentBag<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(item => commands.Add(item.Command.DeepClone().AsBsonDocument));
                builder.Subscribe<CommandSucceededEvent>(item =>
                {
                    if (item.CommandName == "collMod") cancellation.Cancel();
                });
            };
            var observing = new MongoClient(settings).GetDatabase(database.DatabaseNamespace.DatabaseName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new CollectionMigrations(observing, clock).ApplyAsync(cancellation.Token));
            var pending = await database.GetCollection<BsonDocument>("schemaMigrations")
                .Find(new BsonDocument("_id", 3)).SingleAsync();
            Assert.Equal("applying", pending["state"].AsString);
            Assert.True(pending["leaseUntil"].ToUniversalTime() <= clock.UtcNow.UtcDateTime);
            Assert.False(await new CollectionMigrations(database, clock).IsCurrentAsync());
            await new CollectionMigrations(observing, clock).ApplyAsync();
            Assert.Equal(before, (await History(database)).Take(2));
            Assert.True(await new CollectionMigrations(database, clock).IsCurrentAsync());
            var ddl = commands.Where(command => command.Contains("create") || command.Contains("collMod") || command.Contains("createIndexes")).ToArray();
            Assert.NotEmpty(ddl);
            Assert.All(ddl, command => Assert.Equal("majority", command["writeConcern"]["w"].AsString));
            Assert.DoesNotContain(ddl, command => command.TryGetValue("collMod", out var value) &&
                new[] { "memberships", "orders", "authorizationScopes" }.Contains(value.AsString));
        });
    }

    [Fact]
    public async Task New_lifecycle_documents_enforce_types_hierarchy_decisions_and_unique_codes()
    {
        await InDatabase(async database =>
        {
            await new CollectionMigrations(database, clock).ApplyAsync();
            var profiles = database.GetCollection<BsonDocument>("organizationProfiles");
            await profiles.InsertOneAsync(Profile(20L));
            var duplicate = await Assert.ThrowsAsync<MongoWriteException>(() => profiles.InsertOneAsync(Profile(21L)));
            Assert.Equal(ServerErrorCategory.DuplicateKey, duplicate.WriteError.Category);
            foreach (var mutate in new Action<BsonDocument>[]
            {
                row => row["_id"] = 22,
                row => row["parentId"] = BsonNull.Value,
                row => row["version"] = 1,
                row => row["schemaVersion"] = 1L,
                row => row["normalizedCode"] = "lowercase",
                row => row["name"] = new string('x', 161),
                row => row["unexpected"] = true
            })
            {
                var row = Profile(22L); row["normalizedCode"] = "OTHER"; mutate(row);
                await Invalid(database, "organizationProfiles", row);
            }
            var proposals = database.GetCollection<BsonDocument>("organizationProposals");
            await proposals.InsertOneAsync(OrganizationProposal(40L));
            var approved = OrganizationProposal(41L);
            approved["status"] = "approved"; approved["approvedBy"] = 3L;
            approved["approvedAt"] = clock.UtcNow.UtcDateTime; approved["resultScopeId"] = 100L;
            await proposals.InsertOneAsync(approved);
            foreach (var status in new[] { "rejected", "withdrawn" })
            {
                var decided = OrganizationProposal(status == "rejected" ? 42L : 43L);
                decided["status"] = status; decided["decisionBy"] = 3L;
                decided["decisionAt"] = clock.UtcNow.UtcDateTime; decided["decisionReason"] = "reviewed fixture";
                await proposals.InsertOneAsync(decided);
            }
            foreach (var mutate in new Action<BsonDocument>[]
            {
                row => row["status"] = "approved",
                row => row["status"] = "withdrawn",
                row => row["approvedBy"] = 3L,
                row => row["scopeId"] = 99L,
                row => row["expectedScopeVersion"] = 1L,
                row => row["impact"]["affectedScopes"] = 0,
                row => row["impact"]["revision"] = "bad"
            })
            {
                var row = OrganizationProposal(44L); mutate(row);
                await Invalid(database, "organizationProposals", row);
            }
        });
    }

    [Fact]
    public async Task Withdrawal_metadata_is_atomic_and_read_audit_accepts_only_safe_selection_fields()
    {
        await InDatabase(async database =>
        {
            await new CollectionMigrations(database, clock).ApplyAsync();
            var resolved = AccessProposal(10L);
            resolved["status"] = "rejected"; resolved["resolutionKind"] = "withdrawn";
            resolved["resolvedBy"] = 2L; resolved["resolvedAt"] = clock.UtcNow.UtcDateTime;
            resolved["resolutionReason"] = "reviewed withdrawal";
            await database.GetCollection<BsonDocument>("authorizationProposals").InsertOneAsync(resolved);
            resolved["_id"] = 11L; resolved.Remove("resolvedAt");
            await Invalid(database, "authorizationProposals", resolved);
            resolved["resolvedAt"] = clock.UtcNow.UtcDateTime; resolved["status"] = "pending";
            await Invalid(database, "authorizationProposals", resolved);

            var audit = ReadEvent(1L);
            await database.GetCollection<BsonDocument>("authorizationReadEvents").InsertOneAsync(audit);
            foreach (var mutate in new Action<BsonDocument>[]
            {
                row => row["selection"]["rawKey"] = "secret",
                row => row["selection"]["body"] = new BsonDocument("permissions", "privileged"),
                row => row["selection"]["subjectId"] = 2,
                row => row["selection"]["keyHash"] = new string('A', 64),
                row => row["resultCount"] = 101,
                row => row["scopes"] = new BsonArray(Enumerable.Repeat(new BsonDocument("kind", "system"), 257)),
                row => row["purpose"] = "anything"
            })
            {
                var row = ReadEvent(2L); mutate(row);
                await Invalid(database, "authorizationReadEvents", row);
            }
            var history = Audit(5L); history["scope"] = new BsonDocument { { "kind", "buyingBranch" }, { "branchId", 2 } };
            await Invalid(database, "authorizationHistory", history);
        });
    }

    private async Task InDatabase(Func<IMongoDatabase, Task> action)
    {
        var name = "ut_lifecycle_mig_" + Guid.NewGuid().ToString("N");
        try { await action(fixture.Client.GetDatabase(name)); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }

    private async Task ApplyVersionTwo(IMongoDatabase database)
    {
        IMigrationStep[] steps = [new FoundationMigration(database.WithWriteConcern(WriteConcern.WMajority)),
            new AuthorizationMigration(database.WithWriteConcern(WriteConcern.WMajority))];
        var gate = new MigrationGate(database, clock, 2);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, CancellationToken.None);
            await step.ApplyAsync(lease!, CancellationToken.None);
            await lease!.CompleteAsync(CancellationToken.None);
        }
    }

    private static Task<List<BsonDocument>> History(IMongoDatabase database) => database
        .GetCollection<BsonDocument>("schemaMigrations").Find(FilterDefinition<BsonDocument>.Empty)
        .Sort(new BsonDocument("_id", 1)).ToListAsync();

    private static async Task<BsonDocument> Validator(IMongoDatabase database, string name) =>
        (await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }))
            .SingleAsync())["options"]["validator"].AsBsonDocument;

    private static Task<BsonDocument> SetValidator(IMongoDatabase database, string name, BsonDocument validator) =>
        database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", validator },
            { "validationLevel", "strict" }, { "validationAction", "error" } });

    private static async Task Invalid(IMongoDatabase database, string collection, BsonDocument row)
    {
        var error = await Assert.ThrowsAsync<MongoWriteException>(() => database.GetCollection<BsonDocument>(collection).InsertOneAsync(row));
        Assert.Equal(121, error.WriteError.Code);
    }

    private BsonDocument Audit(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "actorId", 2L }, { "subjectId", 3L }, { "operation", "fixture" },
        { "reason", "reviewed fixture" }, { "correlationId", "fixture" }, { "createdAt", clock.UtcNow.UtcDateTime },
        { "before", new BsonDocument() }, { "after", new BsonDocument() }
    };

    private static BsonDocument Node(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "kind", "branch" }, { "parentId", 1L },
        { "status", "active" }, { "version", 1L }, { "fence", 0L }
    };

    private BsonDocument Profile(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "kind", "branch" }, { "parentId", 1L },
        { "code", "Fixture" }, { "normalizedCode", "FIXTURE" }, { "name", "Synthetic fixture" },
        { "version", 1L }, { "createdBy", 2L }, { "updatedBy", 2L }, { "createdAt", clock.UtcNow.UtcDateTime },
        { "updatedAt", clock.UtcNow.UtcDateTime }, { "approvedProposalId", 4L }
    };

    private BsonDocument OrganizationProposal(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "changeKind", "create" }, { "scopeId", BsonNull.Value },
        { "kind", "branch" }, { "parentId", 1L }, { "code", "Fixture" }, { "normalizedCode", "FIXTURE" },
        { "name", "Synthetic fixture" }, { "expectedScopeVersion", 0L }, { "expectedProfileVersion", 0L },
        { "requestedBy", 2L }, { "reason", "reviewed fixture" }, { "status", "pending" }, { "version", 1L },
        { "createdAt", clock.UtcNow.UtcDateTime }, { "before", new BsonDocument() },
        { "impact", new BsonDocument { { "affectedScopes", 0L }, { "activeMemberships", 0L }, { "activeGrants", 0L },
            { "affectedAccounts", 0L }, { "revision", new string('a', 64) } } }
    };

    private BsonDocument AccessProposal(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "status", "pending" }, { "kind", "membership" },
        { "subjectId", 3L }, { "requestedBy", 2L }, { "reason", "reviewed fixture" },
        { "scope", new BsonDocument("kind", "system") }, { "request", new BsonDocument() },
        { "createdAt", clock.UtcNow.UtcDateTime }, { "version", 1L }
    };

    private BsonDocument ReadEvent(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "actorId", 2L }, { "actorGrantVersion", 1L },
        { "operation", "access.receipt.read" }, { "purpose", "operation-recovery" },
        { "correlationId", "fixture" }, { "createdAt", clock.UtcNow.UtcDateTime },
        { "selection", new BsonDocument { { "subjectId", 3L }, { "keyHash", new string('a', 64) } } },
        { "resultCount", 1 }, { "scopes", new BsonArray { new BsonDocument("kind", "system") } }
    };
}
