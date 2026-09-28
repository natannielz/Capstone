using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class ProviderLifecycleMigrationTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();

    [Fact]
    public async Task V4_upgrade_preserves_history_existing_documents_and_validators_without_retroactive_holds_or_counters()
    {
        await InDatabase(async db =>
        {
            await ApplyV4(db);
            var rows = new Dictionary<string, BsonDocument>
            {
                ["identities"] = Identity(), ["identitySessions"] = Session(), ["identityProviderIntents"] = Intent()
            };
            var validators = new Dictionary<string, BsonDocument>();
            foreach (var (name, row) in rows)
            {
                await db.GetCollection<BsonDocument>(name).InsertOneAsync(row);
                validators[name] = await Validator(db, name);
            }
            var before = await History(db);
            var counters = await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            await new CollectionMigrations(db, clock).ApplyAsync();
            Assert.Equal(Enumerable.Range(1, CollectionMigrations.CurrentVersion),
                (await History(db)).Select(row => row["_id"].AsInt32));
            Assert.True(await new CollectionMigrations(db, clock).IsCurrentAsync());
            Assert.Equal(before, (await History(db)).Take(4));
            foreach (var (name, row) in rows)
            {
                Assert.Equal(row.ToBson(), (await db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).ToBson());
                Assert.Equal(validators[name], await Validator(db, name));
            }
            Assert.Equal(counters, await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
            foreach (var name in new[] { "identitySecurityEvents", "identityAdmissionHolds", "identityProviderSubjects" })
            {
                Assert.Contains(name, await (await db.ListCollectionNamesAsync()).ToListAsync());
                Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                Assert.DoesNotContain(await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync(), index => index.Contains("expireAfterSeconds"));
            }
            var applied = await History(db);
            await new CollectionMigrations(db, clock).ApplyAsync();
            Assert.Equal(applied, await History(db));
            Assert.False(await new MigrationGate(db, clock, 4).IsCurrentAsync(CancellationToken.None));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_rows_or_duplicate_provider_event_identity_fail_before_any_collection_change(bool duplicate)
    {
        await InDatabase(async db =>
        {
            await ApplyV4(db);
            await db.CreateCollectionAsync("identitySecurityEvents");
            var prior = new BsonDocument("eventId", new BsonDocument("$ne", "forbidden-event"));
            await SetValidator(db, "identitySecurityEvents", prior);
            var first = Event('a');
            var second = Event('b');
            if (duplicate) second["eventId"] = first["eventId"];
            else second["userId"] = 1; // Int32 must never be accepted as an internal user reference.
            await db.GetCollection<BsonDocument>("identitySecurityEvents").InsertManyAsync([first, second]);
            var history = await History(db);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("identitySecurityEvents", failure.Message);
            Assert.DoesNotContain(first["eventId"].AsString, failure.Message);
            Assert.Equal(prior, await Validator(db, "identitySecurityEvents"));
            Assert.DoesNotContain("identityAdmissionHolds", await (await db.ListCollectionNamesAsync()).ToListAsync());
            Assert.Equal(history, (await History(db)).Take(4));
            Assert.False(await new CollectionMigrations(db, clock).IsCurrentAsync());
        });
    }

    [Theory]
    [InlineData("ttl")]
    [InlineData("name")]
    [InlineData("partial")]
    [InlineData("nonunique")]
    public async Task Incompatible_existing_indexes_are_rejected_before_validator_mutation(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV4(db);
            var collection = db.GetCollection<BsonDocument>("identitySecurityEvents");
            await collection.InsertOneAsync(Event('a'));
            var prior = new BsonDocument("issuer", new BsonDocument("$ne", "forbidden-issuer"));
            await SetValidator(db, "identitySecurityEvents", prior);
            var key = kind == "ttl" ? new BsonDocument("receivedAt", 1) : kind == "name" ? new BsonDocument("fingerprint", 1) : EventKey();
            var options = new CreateIndexOptions<BsonDocument> { Name = kind == "ttl" ? "unsafe_expiry" : "ux_issuer_eventId" };
            if (kind == "ttl") options.ExpireAfter = TimeSpan.FromDays(30);
            if (kind == "partial") { options.Unique = true; options.PartialFilterExpression = new BsonDocument("outcome", "applied"); }
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, options));
            var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("index", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(prior, await Validator(db, "identitySecurityEvents"));
            Assert.Equal(indexes, await (await collection.Indexes.ListAsync()).ToListAsync());
            Assert.DoesNotContain("identityAdmissionHolds", await (await db.ListCollectionNamesAsync()).ToListAsync());
        });
    }

    [Fact]
    public async Task Incompatible_hold_is_preflighted_before_the_event_collection_validator_changes()
    {
        await InDatabase(async db =>
        {
            await ApplyV4(db);
            await db.GetCollection<BsonDocument>("identitySecurityEvents").InsertOneAsync(Event('a'));
            var prior = new BsonDocument("eventId", new BsonDocument("$regex", "^reviewed-"));
            await SetValidator(db, "identitySecurityEvents", prior);
            var malformed = Hold('a'); malformed["sourceEventId"] = Hash('b');
            await db.GetCollection<BsonDocument>("identityAdmissionHolds").InsertOneAsync(malformed);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("identityAdmissionHolds", failure.Message);
            Assert.Equal(prior, await Validator(db, "identitySecurityEvents"));
            Assert.Single(await (await db.GetCollection<BsonDocument>("identitySecurityEvents").Indexes.ListAsync()).ToListAsync());
            Assert.Equal(malformed.ToBson(), (await db.GetCollection<BsonDocument>("identityAdmissionHolds").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).ToBson());
        });
    }

    [Theory]
    [InlineData("identitySecurityEvents", true)]
    [InlineData("identityAdmissionHolds", true)]
    [InlineData("identityProviderSubjects", true)]
    [InlineData("identitySecurityEvents", false)]
    [InlineData("identityAdmissionHolds", false)]
    [InlineData("identityProviderSubjects", false)]
    public async Task Unsafe_collection_retention_cannot_expire_security_state_or_break_transactional_writers(string name, bool capped)
    {
        await InDatabase(async db =>
        {
            await ApplyV4(db);
            if (capped) await db.CreateCollectionAsync(name, new CreateCollectionOptions { Capped = true, MaxSize = 4096 });
            else await db.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "create", name }, { "clusteredIndex", new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } } },
                { "expireAfterSeconds", 86400L }
            });
            if (name != "identitySecurityEvents") await db.CreateCollectionAsync("identitySecurityEvents");
            var prior = new BsonDocument("eventId", new BsonDocument("$ne", "forbidden-event"));
            await SetValidator(db, "identitySecurityEvents", prior);
            var history = await History(db);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains(name, failure.Message);
            Assert.Contains("options", failure.Message);
            Assert.Equal(prior, await Validator(db, "identitySecurityEvents"));
            Assert.Single(await (await db.GetCollection<BsonDocument>("identitySecurityEvents").Indexes.ListAsync()).ToListAsync());
            Assert.Equal(history, (await History(db)).Take(4));
            Assert.False(await new CollectionMigrations(db, clock).IsCurrentAsync());
        });
    }

    [Fact]
    public async Task Interrupted_v5_preserves_stronger_rules_resumes_only_v5_and_all_ddl_is_majority()
    {
        await InDatabase(async db =>
        {
            await ApplyV4(db);
            var prior = new BsonDocument("eventId", new BsonDocument("$regex", "^reviewed-"));
            await db.CreateCollectionAsync("identitySecurityEvents");
            await SetValidator(db, "identitySecurityEvents", prior);
            var historical = Event('a');
            await db.GetCollection<BsonDocument>("identitySecurityEvents").InsertOneAsync(historical);
            // A compatible index with a different name must be kept, not recreated under another name.
            await db.GetCollection<BsonDocument>("identitySecurityEvents").Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(EventKey(), new CreateIndexOptions { Name = "reviewed_provider_event_identity", Unique = true }));
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
            Assert.Equal(before, (await History(db)).Take(4));
            var pending = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 5)).SingleAsync();
            Assert.Equal("applying", pending["state"].AsString);
            Assert.True(pending["leaseUntil"].ToUniversalTime() <= clock.UtcNow.UtcDateTime);
            var firstRules = await Validator(db, "identitySecurityEvents");
            await new CollectionMigrations(observed, clock).ApplyAsync();
            Assert.Equal(firstRules, await Validator(db, "identitySecurityEvents"));
            Assert.Equal(before, (await History(db)).Take(4));
            Assert.Equal(historical.ToBson(), (await db.GetCollection<BsonDocument>("identitySecurityEvents").Find(new BsonDocument("_id", historical["_id"])).SingleAsync()).ToBson());
            var denied = Event('b'); denied["eventId"] = "not-reviewed";
            await Invalid(db, "identitySecurityEvents", denied);
            var ddl = commands.Where(command => command.Contains("create") || command.Contains("collMod") || command.Contains("createIndexes")).ToArray();
            Assert.NotEmpty(ddl);
            Assert.All(ddl, command =>
            {
                Assert.Equal("majority", command["writeConcern"]["w"].AsString);
                // Resuming the registered chain also applies the following catalog and historical-read migrations.
                Assert.Contains(command.GetElement(0).Value.AsString, new[] { "identitySecurityEvents", "identityAdmissionHolds",
                    "identityProviderSubjects", "catalogEntitlements", "catalogAssortments", "businessReadEvents", "personEvidenceSources", "personRecords", "personAccountBindings", "personEquivalenceGroups", "personEvidenceVersions", "personGovernancePolicies", "personReviewProposals", "personReviewHistory", "personReviewPreviews", "catalogCategories", "productFamilies", "skus", "variantDefinitions", "catalogIdentifiers", "catalogIdentifierHistory", "accountingBooks", "accountingChartVersions", "journalEntries", "periods", "accountingPeriodHistory", "accountingAuditEvents", "evidenceStorePolicies", "evidenceLineages", "documentVersions", "evidenceUploadReservations", "evidenceUploadAttempts", "auditEvents", "idempotencyRecords", "outboxEvents", "stockCountObservations", "stockSourceProposals", "stockSourceDecisions", "inventoryPoolRegistrations", "inventoryPools", "stockSourceRevisionProposals", "stockSourceRevisionDecisions", "stockSourceRevisions" });
            });
            Assert.Contains(await (await db.GetCollection<BsonDocument>("identitySecurityEvents").Indexes.ListAsync()).ToListAsync(),
                index => index["name"] == "reviewed_provider_event_identity");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsafe_subject_fences_fail_before_any_earlier_collection_changes(bool expiring)
    {
        await InDatabase(async db =>
        {
            await ApplyV4(db);
            await db.GetCollection<BsonDocument>("identitySecurityEvents").InsertOneAsync(Event('a'));
            var prior = new BsonDocument("eventId", new BsonDocument("$regex", "^reviewed-"));
            await SetValidator(db, "identitySecurityEvents", prior);
            var subject = new BsonDocument
            {
                { "_id", Hash('a') }, { "schemaVersion", 1 }, { "issuer", "https://fixture.example/" },
                { "fence", 0L }, { "admissionRestricted", true }, { "updatedAt", clock.UtcNow.UtcDateTime }
            };
            if (!expiring) subject["fence"] = 0; // A legacy Int32 fence cannot authorize admission.
            var subjects = db.GetCollection<BsonDocument>("identityProviderSubjects");
            await subjects.InsertOneAsync(subject);
            if (expiring) await subjects.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument("updatedAt", 1), new CreateIndexOptions { Name = "unsafe_expiry", ExpireAfter = TimeSpan.FromDays(30) }));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("identityProviderSubjects", failure.Message);
            Assert.Equal(prior, await Validator(db, "identitySecurityEvents"));
            Assert.Single(await (await db.GetCollection<BsonDocument>("identitySecurityEvents").Indexes.ListAsync()).ToListAsync());
            Assert.DoesNotContain("identityAdmissionHolds", await (await db.ListCollectionNamesAsync()).ToListAsync());
            Assert.Equal(subject.ToBson(), (await subjects.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync()).ToBson());
        });
    }

    [Fact]
    public async Task Event_schema_enforces_types_bounded_shapes_outcome_metadata_and_unique_provider_identity()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            var collection = db.GetCollection<BsonDocument>("identitySecurityEvents");
            await collection.InsertOneAsync(Event('a'));
            var unknown = Event('b'); unknown["outcome"] = "unknown";
            unknown["userId"] = unknown["previousSecurityVersion"] = unknown["securityVersion"] = BsonNull.Value;
            await collection.InsertOneAsync(unknown);
            var duplicate = Event('c'); duplicate["eventId"] = Event('a')["eventId"];
            Assert.Equal(ServerErrorCategory.DuplicateKey, (await Assert.ThrowsAsync<MongoWriteException>(() => collection.InsertOneAsync(duplicate))).WriteError.Category);
            foreach (var mutation in new Action<BsonDocument>[]
            {
                row => row["_id"] = new string('A', 64), row => row["schemaVersion"] = 1L,
                row => row["issuer"] = new string('x', 2049), row => row["eventId"] = new string('x', 301),
                row => row["subjectHash"] = "bad", row => row["fingerprint"] = new string('F', 64),
                row => row["change"] = "accountRestored", row => row["occurredAt"] = "now",
                row => row["userId"] = 1, row => row["previousSecurityVersion"] = 0L,
                row => row["securityVersion"] = BsonNull.Value, row => row["outcome"] = "unknown",
                row => row["providerToken"] = "never-accepted"
            }) { var row = Event('d'); mutation(row); await Invalid(db, "identitySecurityEvents", row); }
            unknown["_id"] = Hash('e'); unknown["eventId"] = "reviewed-other"; unknown.Remove("securityVersion");
            await Invalid(db, "identitySecurityEvents", unknown);
        });
    }

    [Fact]
    public async Task Holds_enforce_event_binding_exact_numbers_and_no_implicit_release_state()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            await db.GetCollection<BsonDocument>("identityAdmissionHolds").InsertOneAsync(Hold('a'));
            foreach (var mutation in new Action<BsonDocument>[]
            {
                row => row["_id"] = 1L, row => row["schemaVersion"] = 1L, row => row["userId"] = 1,
                row => row["sourceEventId"] = Hash('c'), row => row["securityVersion"] = 0L,
                row => row["status"] = "released", row => row["reason"] = "passwordChanged",
                row => row["version"] = 1, row => row["releaseAt"] = clock.UtcNow.UtcDateTime
            }) { var row = Hold('b'); mutation(row); await Invalid(db, "identityAdmissionHolds", row); }
        });
    }

    [Fact]
    public async Task Subject_fences_require_hash_identity_and_typed_restriction_without_raw_provider_subjects()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            BsonDocument Subject(char id) => new()
            {
                { "_id", Hash(id) }, { "schemaVersion", 1 }, { "issuer", "https://fixture.example/" },
                { "fence", 0L }, { "admissionRestricted", false }, { "updatedAt", clock.UtcNow.UtcDateTime }
            };
            var collection = db.GetCollection<BsonDocument>("identityProviderSubjects");
            await collection.InsertOneAsync(Subject('a'));
            var restricted = Subject('b'); restricted["admissionRestricted"] = true; restricted["fence"] = 1L;
            restricted["proofNotBefore"] = clock.UtcNow.UtcDateTime;
            await collection.InsertOneAsync(restricted);
            var noCutoff = Subject('d'); noCutoff["proofNotBefore"] = BsonNull.Value;
            await collection.InsertOneAsync(noCutoff);
            foreach (var mutation in new Action<BsonDocument>[]
            {
                row => row["_id"] = new string('A', 64), row => row["schemaVersion"] = 1L,
                row => row["issuer"] = "", row => row["fence"] = 0, row => row["fence"] = -1L,
                row => row["admissionRestricted"] = 1, row => row["subject"] = "never-stored-raw",
                row => row.Remove("updatedAt"), row => row["proofNotBefore"] = "not-a-date"
            }) { var row = Subject('c'); mutation(row); await Invalid(db, "identityProviderSubjects", row); }
        });
    }

    private async Task InDatabase(Func<IMongoDatabase, Task> run)
    {
        var name = "ut_provider_migration_" + Guid.NewGuid().ToString("N");
        try { await run(fixture.Client.GetDatabase(name)); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private async Task ApplyV4(IMongoDatabase db)
    {
        var durable = db.WithWriteConcern(WriteConcern.WMajority);
        IMigrationStep[] steps = [new FoundationMigration(durable), new AuthorizationMigration(durable),
            new AuthorizationLifecycleMigration(durable), new IdentityMigration(durable)];
        var gate = new MigrationGate(db, clock, 4);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, CancellationToken.None);
            await step.ApplyAsync(lease!, CancellationToken.None);
            await lease!.CompleteAsync(CancellationToken.None);
        }
    }
    private static Task<List<BsonDocument>> History(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations")
        .Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<BsonDocument> Validator(IMongoDatabase db, string name) =>
        (await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync())["options"]["validator"].AsBsonDocument;
    private static Task<BsonDocument> SetValidator(IMongoDatabase db, string name, BsonDocument rules) =>
        db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", rules }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task Invalid(IMongoDatabase db, string collection, BsonDocument row) =>
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(collection).InsertOneAsync(row))).WriteError.Code);
    private static string Hash(char value) => new(value, 64);
    private static BsonDocument EventKey() => new() { { "issuer", 1 }, { "eventId", 1 } };
    private BsonDocument Event(char id) => new()
    {
        { "_id", Hash(id) }, { "schemaVersion", 1 }, { "issuer", "https://fixture.example/" }, { "eventId", "reviewed-event-" + id },
        { "subjectHash", Hash('e') }, { "fingerprint", Hash('f') }, { "change", "passwordChanged" },
        { "occurredAt", clock.UtcNow.UtcDateTime }, { "receivedAt", clock.UtcNow.UtcDateTime }, { "outcome", "applied" },
        { "userId", 1L }, { "previousSecurityVersion", 1L }, { "securityVersion", 2L }
    };
    private BsonDocument Hold(char id) => new()
    {
        { "_id", Hash(id) }, { "schemaVersion", 1 }, { "userId", 1L }, { "sourceEventId", Hash(id) },
        { "reason", "providerBlocked" }, { "status", "active" }, { "createdAt", clock.UtcNow.UtcDateTime }, { "securityVersion", 2L }, { "version", 1L }
    };
    private static BsonDocument Identity() => new()
    {
        { "_id", 1L }, { "schemaVersion", 1 }, { "issuer", "https://fixture.example/" }, { "subject", "fixture-user" },
        { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L }
    };
    private BsonDocument Intent() => new()
    {
        { "_id", 1L }, { "schemaVersion", 1 }, { "kind", "factorRecovery" }, { "userId", 1L }, { "invitationId", BsonNull.Value },
        { "requestedBy", 2L }, { "approvedBy", 3L }, { "securityVersion", 1L }, { "status", "blocked" },
        { "reason", "synthetic recovery intent" }, { "createdAt", clock.UtcNow.UtcDateTime }, { "protectedPayload", "encrypted-fixture" }, { "version", 1L }
    };
    private BsonDocument Session() => new()
    {
        { "_id", Hash('a') }, { "schemaVersion", 1 }, { "familyId", Hash('a') }, { "userId", 1L }, { "issuer", "https://fixture.example/" },
        { "subject", "fixture-user" }, { "connectionId", "fixture-connection" }, { "grantVersion", 1L }, { "securityVersion", 1L },
        { "status", "active" }, { "version", 1L }, { "fence", 0L }, { "createdAt", clock.UtcNow.UtcDateTime }, { "lastUsedAt", clock.UtcNow.UtcDateTime },
        { "idleExpiresAt", clock.UtcNow.AddMinutes(30).UtcDateTime }, { "absoluteExpiresAt", clock.UtcNow.AddHours(8).UtcDateTime },
        { "providerAuthenticatedAt", clock.UtcNow.UtcDateTime }, { "factorCompletedAt", clock.UtcNow.UtcDateTime },
        { "factorType", "otp" }, { "assurancePolicyVersion", "fixture-v1" }, { "protectedPayload", "encrypted-fixture" }
    };
}
