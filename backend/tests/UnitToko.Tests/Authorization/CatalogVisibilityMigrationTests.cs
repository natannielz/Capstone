using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class CatalogVisibilityMigrationTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();
    private const string Entitlements = "catalogEntitlements";
    private const string Assortments = "catalogAssortments";
    private const string EntitlementIndex = "ix_userId_membershipId_status_effectiveAt_assortmentId";

    [Fact]
    public async Task Fresh_database_has_strict_catalog_collections_and_repeat_preserves_history_and_definitions()
    {
        await InDatabase(async db =>
        {
            var migrations = new CollectionMigrations(db, clock);
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            var before = await History(db);
            Assert.Equal(Enumerable.Range(1, CollectionMigrations.CurrentVersion), before.Select(row => row["_id"].AsInt32));
            var definitions = new Dictionary<string, BsonDocument>();
            var indexes = new Dictionary<string, List<BsonDocument>>();
            foreach (var name in new[] { Entitlements, Assortments })
            {
                definitions[name] = await Definition(db, name);
                indexes[name] = await Indexes(db, name);
                Assert.Equal("strict", definitions[name]["options"]["validationLevel"].AsString);
                Assert.Equal("error", definitions[name]["options"]["validationAction"].AsString);
                Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                Assert.DoesNotContain(indexes[name], index => index.Contains("expireAfterSeconds"));
                Assert.Single(indexes[name], index => index["name"] == "_id_");
            }
            Assert.Contains(indexes[Entitlements], index => index["name"] == EntitlementIndex && index["key"] == EntitlementKey() && !index.GetValue("unique", false).AsBoolean);
            Assert.Contains(indexes[Assortments], index => index["name"] == "ix_status" && index["key"] == new BsonDocument("status", 1));
            await migrations.ApplyAsync();
            Assert.Equal(before, await History(db));
            foreach (var name in new[] { Entitlements, Assortments })
            {
                Assert.Equal(definitions[name], await Definition(db, name));
                Assert.Equal(indexes[name], await Indexes(db, name));
            }
        });
    }

    [Fact]
    public async Task V5_upgrade_preserves_history_all_existing_validators_and_legacy_skus_without_inferred_assignments()
    {
        await InDatabase(async db =>
        {
            await ApplyV5(db);
            var legacy = new BsonDocument { { "_id", 901L }, { "schemaVersion", 1 }, { "legacyCatalogMarker", "unreviewed" } };
            await db.GetCollection<BsonDocument>("skus").InsertOneAsync(legacy);
            var names = await (await db.ListCollectionNamesAsync()).ToListAsync();
            var definitions = new Dictionary<string, BsonDocument>();
            foreach (var name in names.Where(name => name != "schemaMigrations")) definitions[name] = await Definition(db, name);
            var history = await History(db);
            var counters = await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            await new CollectionMigrations(db, clock).ApplyAsync();
            Assert.Equal(history, (await History(db)).Take(5));
            foreach (var (name, definition) in definitions) Assert.Equal(definition, await Definition(db, name));
            Assert.Equal(legacy.ToBson(), (await db.GetCollection<BsonDocument>("skus").Find(new BsonDocument("_id", 901L)).SingleAsync()).ToBson());
            Assert.Equal(counters, await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
            Assert.Equal(0, await db.GetCollection<BsonDocument>(Entitlements).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.Equal(0, await db.GetCollection<BsonDocument>(Assortments).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.False(await new MigrationGate(db, clock, 5).IsCurrentAsync(CancellationToken.None));
        });
    }

    [Fact]
    public async Task Complete_approved_records_and_reviewed_replacements_are_valid_without_a_business_key_unique_constraint()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            var entitlements = db.GetCollection<BsonDocument>(Entitlements);
            await entitlements.InsertOneAsync(Entitlement(1));
            await entitlements.InsertOneAsync(Entitlement(2));
            var suspended = Entitlement(3); suspended["status"] = "suspended";
            suspended["expiresAt"] = clock.UtcNow.AddDays(1).UtcDateTime; suspended["revokedAt"] = BsonNull.Value;
            await entitlements.InsertOneAsync(suspended);
            var revoked = Entitlement(4); revoked["status"] = "revoked";
            revoked["expiresAt"] = BsonNull.Value; revoked["revokedAt"] = clock.UtcNow.UtcDateTime;
            await entitlements.InsertOneAsync(revoked);
            var maximum = Entitlement(long.MaxValue); maximum["version"] = long.MaxValue; maximum["fence"] = long.MaxValue;
            await entitlements.InsertOneAsync(maximum);
            var duplicate = await Assert.ThrowsAsync<MongoWriteException>(() => entitlements.InsertOneAsync(Entitlement(1)));
            Assert.Equal(ServerErrorCategory.DuplicateKey, duplicate.WriteError.Category);
            foreach (var (kind, branch, division) in new (string, long?, long?)[]
            {
                ("buyingOrganization", null, null), ("buyingBranch", 20, null), ("buyingDivision", 20, 30),
                ("buyerOwned", null, null), ("buyerOwned", 20, null), ("buyerOwned", 20, 30)
            })
            {
                var row = Entitlement(10 + await entitlements.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                var scope = new BsonDocument { { "kind", kind }, { "organizationId", 10L },
                    { "branchId", branch is { } b ? new BsonInt64(b) : BsonNull.Value },
                    { "divisionId", division is { } d ? new BsonInt64(d) : BsonNull.Value },
                    { "shopId", BsonNull.Value }, { "warehouseId", BsonNull.Value }, { "channelId", BsonNull.Value } };
                row["audienceScope"] = scope;
                await entitlements.InsertOneAsync(row);
            }
            var assortments = db.GetCollection<BsonDocument>(Assortments);
            await assortments.InsertOneAsync(Assortment(1));
            var warehouse = Assortment(2); warehouse["ownerScope"]["kind"] = "fulfillmentWarehouse";
            warehouse["ownerScope"]["warehouseId"] = 80L;
            await assortments.InsertOneAsync(warehouse);
        });
    }

    [Fact]
    public async Task Catalog_fields_reject_missing_approval_evidence_unbounded_shapes_and_non_long_numbers()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            foreach (var field in new[] { "_id", "userId", "membershipId", "assortmentId", "version", "requestedBy", "approvedBy", "approvalId", "approvalVersion" })
            foreach (var bad in new BsonValue[] { 0L, -1L, 1, 1.0, new BsonDecimal128(1m), "1", new BsonArray { 1L }, BsonNull.Value })
            {
                var row = Entitlement(100); row[field] = bad;
                // MongoDB rejects array identities before collection validation runs.
                if (field == "_id" && bad.IsBsonArray)
                    Assert.Equal(53, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(Entitlements).InsertOneAsync(row))).WriteError.Code);
                else await Invalid(db, Entitlements, row);
            }
            foreach (var required in Entitlement(100).Names)
            {
                var row = Entitlement(100); row.Remove(required); await Invalid(db, Entitlements, row);
            }
            foreach (var mutate in new Action<BsonDocument>[]
            {
                row => row["schemaVersion"] = 1L, row => row["schemaVersion"] = 2,
                row => row["fence"] = 0, row => row["fence"] = -1L, row => row["fence"] = 1.0,
                row => row["status"] = "pending", row => row["status"] = new string('x', 10000),
                row => row["effectiveAt"] = "now", row => row["approvedAt"] = BsonNull.Value,
                row => row["approvedAt"] = new BsonArray { clock.UtcNow.UtcDateTime },
                row => row["expiresAt"] = 1L, row => row["revokedAt"] = "yesterday",
                row => row["unreviewedPermissions"] = new BsonArray { "catalog.read" },
                row => row["audienceScope"] = new BsonArray { BuyingScope() }
            }) { var row = Entitlement(100); mutate(row); await Invalid(db, Entitlements, row); }
            foreach (var required in Assortment(100).Names)
            {
                var row = Assortment(100); row.Remove(required); await Invalid(db, Assortments, row);
            }
            foreach (var mutate in new Action<BsonDocument>[]
            {
                row => row["_id"] = 100, row => row["schemaVersion"] = 1L, row => row["version"] = 0L,
                row => row["version"] = 1.0, row => row["fence"] = 0, row => row["fence"] = -1L,
                row => row["status"] = "pending", row => row["ownerScope"] = new BsonArray { OwnerScope() },
                row => row["extra"] = true
            }) { var row = Assortment(100); mutate(row); await Invalid(db, Assortments, row); }
        });
    }

    [Fact]
    public async Task Scope_shapes_require_exact_ids_and_complete_buying_or_fulfillment_hierarchies()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            foreach (var bad in new BsonValue[] { 1, 1.0, new BsonDecimal128(Decimal128.Parse("9223372036854775808")), 0L, -1L, "10", new BsonArray { 10L } })
            foreach (var field in new[] { "organizationId", "branchId", "divisionId" })
            {
                var row = Entitlement(100); row["audienceScope"][field] = bad; await Invalid(db, Entitlements, row);
            }
            foreach (var mutate in new Action<BsonDocument>[]
            {
                scope => scope.Remove("organizationId"), scope => scope.Remove("branchId"), scope => scope.Remove("divisionId"),
                scope => scope["kind"] = "system", scope => scope["kind"] = "BuyingDivision",
                scope => scope["kind"] = "fulfillmentShop", scope => scope["organizationId"] = BsonNull.Value,
                scope => scope["shopId"] = 40L, scope => scope["channelId"] = 50L,
                scope => scope["kind"] = "buyingBranch", scope => scope["kind"] = "buyingOrganization",
                scope => { scope["kind"] = "buyerOwned"; scope.Remove("branchId"); },
                scope => scope["extra"] = "unreviewed"
            }) { var row = Entitlement(100); mutate(row["audienceScope"].AsBsonDocument); await Invalid(db, Entitlements, row); }
            foreach (var field in new[] { "organizationId", "shopId", "channelId" })
            {
                var row = Assortment(100); row["ownerScope"].AsBsonDocument.Remove(field); await Invalid(db, Assortments, row);
                foreach (var bad in new BsonValue[] { 1, 1.0, new BsonArray { 1L }, BsonNull.Value, 0L })
                {
                    row = Assortment(100); row["ownerScope"][field] = bad; await Invalid(db, Assortments, row);
                }
            }
            foreach (var mutate in new Action<BsonDocument>[]
            {
                scope => scope["kind"] = "courierAssignment", scope => scope["kind"] = "system",
                scope => scope["kind"] = "fulfillmentWarehouse", scope => scope["warehouseId"] = 80L,
                scope => scope["branchId"] = 20L, scope => scope["divisionId"] = 30L, scope => scope["extra"] = 1L
            }) { var row = Assortment(100); mutate(row["ownerScope"].AsBsonDocument); await Invalid(db, Assortments, row); }
        });
    }

    [Fact]
    public async Task Fulfillment_audiences_require_complete_shop_or_warehouse_scopes_without_buying_ids()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            var shop = Entitlement(1); shop["audienceScope"] = OwnerScope();
            await db.GetCollection<BsonDocument>(Entitlements).InsertOneAsync(shop);
            var warehouse = Entitlement(2); warehouse["audienceScope"] = OwnerScope();
            warehouse["audienceScope"]["kind"] = "fulfillmentWarehouse";
            warehouse["audienceScope"]["warehouseId"] = 80L;
            await db.GetCollection<BsonDocument>(Entitlements).InsertOneAsync(warehouse);
            foreach (var mutate in new Action<BsonDocument>[]
            {
                scope => scope.Remove("organizationId"), scope => scope.Remove("shopId"), scope => scope.Remove("channelId"),
                scope => scope["kind"] = "fulfillmentWarehouse", scope => scope["warehouseId"] = 80L,
                scope => scope["branchId"] = 20L, scope => scope["divisionId"] = 30L,
                scope => scope["kind"] = "buyingOrganization", scope => scope["kind"] = "buyerOwned",
                scope => scope["kind"] = "courierAssignment", scope => scope["kind"] = "system",
                scope => scope["extra"] = true
            })
            {
                var row = Entitlement(100); row["audienceScope"] = OwnerScope();
                mutate(row["audienceScope"].AsBsonDocument); await Invalid(db, Entitlements, row);
            }
            foreach (var field in new[] { "organizationId", "shopId", "channelId" })
            foreach (var bad in new BsonValue[] { 1, 1.0, 0L, BsonNull.Value, new BsonArray { 1L } })
            {
                var row = Entitlement(100); row["audienceScope"] = OwnerScope();
                row["audienceScope"][field] = bad; await Invalid(db, Entitlements, row);
            }
        });
    }

    [Theory]
    [InlineData(Entitlements)]
    [InlineData(Assortments)]
    public async Task Malformed_existing_rows_fail_before_either_collection_or_prior_history_changes(string malformedCollection)
    {
        await InDatabase(async db =>
        {
            await ApplyV5(db);
            await db.GetCollection<BsonDocument>(Entitlements).InsertOneAsync(Entitlement(1));
            var prior = new BsonDocument("approvalId", new BsonDocument("$gte", 1L));
            await SetValidator(db, Entitlements, prior);
            var malformed = malformedCollection == Entitlements ? Entitlement(2) : Assortment(2);
            malformed["version"] = 1.0;
            await db.GetCollection<BsonDocument>(malformedCollection).InsertOneAsync(malformed);
            var history = await History(db);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains(malformedCollection, failure.Message);
            Assert.Contains("reconcile", failure.Message);
            Assert.Equal(prior, await Validator(db, Entitlements));
            Assert.Single(await Indexes(db, Entitlements));
            Assert.Equal(history, (await History(db)).Take(5));
            Assert.Equal(malformed.ToBson(), (await db.GetCollection<BsonDocument>(malformedCollection).Find(new BsonDocument("_id", 2L)).SingleAsync()).ToBson());
            Assert.False(await new CollectionMigrations(db, clock).IsCurrentAsync());
            if (malformedCollection == Entitlements) Assert.DoesNotContain(Assortments, await (await db.ListCollectionNamesAsync()).ToListAsync());
        });
    }

    [Theory]
    [InlineData("ttl")]
    [InlineData("name")]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("collation")]
    [InlineData("hidden")]
    public async Task Conflicting_assortment_indexes_fail_before_entitlement_validator_or_index_mutation(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV5(db);
            await db.GetCollection<BsonDocument>(Entitlements).InsertOneAsync(Entitlement(1));
            var prior = new BsonDocument("approvalId", new BsonDocument("$gte", 1L));
            await SetValidator(db, Entitlements, prior);
            await db.CreateCollectionAsync(Assortments);
            var key = kind is "ttl" or "name" ? new BsonDocument("createdAt", 1) : new BsonDocument("status", 1);
            var options = new CreateIndexOptions<BsonDocument> { Name = kind == "ttl" ? "unsafe_expiry" : "ix_status" };
            if (kind == "ttl") options.ExpireAfter = TimeSpan.FromDays(30);
            if (kind == "unique") options.Unique = true;
            if (kind == "sparse") options.Sparse = true;
            if (kind == "partial") options.PartialFilterExpression = new BsonDocument("status", "active");
            if (kind == "collation") options.Collation = new Collation("en");
            if (kind == "hidden") options.Hidden = true;
            await db.GetCollection<BsonDocument>(Assortments).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, options));
            var indexes = await Indexes(db, Assortments);
            var history = await History(db);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("index", failure.Message);
            Assert.Contains(Assortments, failure.Message);
            Assert.Equal(prior, await Validator(db, Entitlements));
            Assert.Single(await Indexes(db, Entitlements));
            Assert.Equal(indexes, await Indexes(db, Assortments));
            Assert.Equal(history, (await History(db)).Take(5));
        });
    }

    [Theory]
    [InlineData("capped")]
    [InlineData("retention")]
    [InlineData("timeseries")]
    [InlineData("collation")]
    [InlineData("view")]
    public async Task Incompatible_assortment_collection_options_fail_before_any_catalog_mutation(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV5(db);
            await db.GetCollection<BsonDocument>(Entitlements).InsertOneAsync(Entitlement(1));
            var prior = new BsonDocument("approvalId", new BsonDocument("$gte", 1L));
            await SetValidator(db, Entitlements, prior);
            if (kind == "capped") await db.CreateCollectionAsync(Assortments, new CreateCollectionOptions { Capped = true, MaxSize = 4096 });
            if (kind == "collation") await db.CreateCollectionAsync(Assortments, new CreateCollectionOptions { Collation = new Collation("en") });
            if (kind == "retention") await db.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "create", Assortments }, { "clusteredIndex", new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } } },
                { "expireAfterSeconds", 86400L }
            });
            if (kind == "timeseries") await db.RunCommandAsync<BsonDocument>(new BsonDocument
                { { "create", Assortments }, { "timeseries", new BsonDocument("timeField", "createdAt") } });
            if (kind == "view") await db.RunCommandAsync<BsonDocument>(new BsonDocument
                { { "create", Assortments }, { "viewOn", "fixtureSource" }, { "pipeline", new BsonArray() } });
            var definition = await Definition(db, Assortments);
            var history = await History(db);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("options", failure.Message);
            Assert.Contains(Assortments, failure.Message);
            Assert.Equal(prior, await Validator(db, Entitlements));
            Assert.Single(await Indexes(db, Entitlements));
            Assert.Equal(definition, await Definition(db, Assortments));
            Assert.Equal(history, (await History(db)).Take(5));
            Assert.False(await new CollectionMigrations(db, clock).IsCurrentAsync());
        });
    }

    [Fact]
    public async Task Interrupted_v6_retains_stronger_rules_and_alternate_indexes_then_resumes_registered_chain_with_majority_ddl()
    {
        await InDatabase(async db =>
        {
            await ApplyV5(db);
            await db.GetCollection<BsonDocument>(Entitlements).InsertOneAsync(Entitlement(1));
            var prior = new BsonDocument("approvalId", new BsonDocument("$gte", 500L));
            await SetValidator(db, Entitlements, prior);
            await db.GetCollection<BsonDocument>(Entitlements).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                EntitlementKey(), new CreateIndexOptions { Name = "reviewed_catalog_lookup" }));
            var before = await History(db);
            using var stop = new CancellationTokenSource();
            var commands = new ConcurrentBag<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(item => commands.Add(item.Command.DeepClone().AsBsonDocument));
                builder.Subscribe<CommandSucceededEvent>(item => { if (item.CommandName == "collMod") stop.Cancel(); });
            };
            var observed = new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(observed, clock).ApplyAsync(stop.Token));
            Assert.Equal(before, (await History(db)).Take(5));
            var pending = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 6)).SingleAsync();
            Assert.Equal("applying", pending["state"].AsString);
            Assert.True(pending["leaseUntil"].ToUniversalTime() <= clock.UtcNow.UtcDateTime);
            var combined = await Validator(db, Entitlements);
            Assert.Contains(combined["$and"].AsBsonArray, item => item == prior);
            await new CollectionMigrations(observed, clock).ApplyAsync();
            Assert.Equal(combined, await Validator(db, Entitlements));
            Assert.Equal(before, (await History(db)).Take(5));
            Assert.True(await new CollectionMigrations(db, clock).IsCurrentAsync());
            var denied = Entitlement(2); denied["approvalId"] = 499L;
            await Invalid(db, Entitlements, denied);
            Assert.Contains(await Indexes(db, Entitlements), index => index["name"] == "reviewed_catalog_lookup");
            Assert.DoesNotContain(await Indexes(db, Entitlements), index => index["name"] == EntitlementIndex);
            var ddl = commands.Where(command => command.Contains("create") || command.Contains("collMod") || command.Contains("createIndexes")).ToArray();
            Assert.NotEmpty(ddl);
            Assert.All(ddl, command =>
            {
                Assert.Equal("majority", command["writeConcern"]["w"].AsString);
                Assert.Contains(command.GetElement(0).Value.AsString, new[] { Entitlements, Assortments, "businessReadEvents" });
            });
        });
    }

    private async Task InDatabase(Func<IMongoDatabase, Task> run)
    {
        var name = "ut_catalog_migration_" + Guid.NewGuid().ToString("N");
        try { await run(fixture.Client.GetDatabase(name)); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private async Task ApplyV5(IMongoDatabase db)
    {
        var durable = db.WithWriteConcern(WriteConcern.WMajority);
        IMigrationStep[] steps = [new FoundationMigration(durable), new AuthorizationMigration(durable),
            new AuthorizationLifecycleMigration(durable), new IdentityMigration(durable), new ProviderLifecycleMigration(durable)];
        var gate = new MigrationGate(db, clock, 5);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, CancellationToken.None);
            await step.ApplyAsync(lease!, CancellationToken.None);
            await lease!.CompleteAsync(CancellationToken.None);
        }
    }
    private static Task<List<BsonDocument>> History(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations")
        .Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<BsonDocument> Definition(IMongoDatabase db, string name) =>
        (await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync());
    private static async Task<BsonDocument> Validator(IMongoDatabase db, string name) => (await Definition(db, name))["options"]["validator"].AsBsonDocument;
    private static async Task<List<BsonDocument>> Indexes(IMongoDatabase db, string name) =>
        await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
    private static Task<BsonDocument> SetValidator(IMongoDatabase db, string name, BsonDocument rules) =>
        db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", rules }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task Invalid(IMongoDatabase db, string collection, BsonDocument row) =>
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(collection).InsertOneAsync(row))).WriteError.Code);
    private static BsonDocument EntitlementKey() => new() { { "userId", 1 }, { "membershipId", 1 }, { "status", 1 }, { "effectiveAt", 1 }, { "assortmentId", 1 } };
    private BsonDocument Entitlement(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "userId", 1L }, { "membershipId", 2L }, { "assortmentId", 3L },
        { "audienceScope", BuyingScope() }, { "status", "active" }, { "effectiveAt", clock.UtcNow.UtcDateTime },
        { "version", 1L }, { "fence", 0L }, { "requestedBy", 4L }, { "approvedBy", 5L },
        { "approvalId", 500L }, { "approvalVersion", 1L }, { "approvedAt", clock.UtcNow.UtcDateTime }
    };
    private static BsonDocument Assortment(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "ownerScope", OwnerScope() }, { "status", "active" }, { "version", 1L }, { "fence", 0L }
    };
    private static BsonDocument BuyingScope() => new() { { "kind", "buyingDivision" }, { "organizationId", 10L }, { "branchId", 20L }, { "divisionId", 30L } };
    private static BsonDocument OwnerScope() => new() { { "kind", "fulfillmentShop" }, { "organizationId", 50L }, { "shopId", 60L }, { "channelId", 70L } };
}
