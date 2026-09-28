using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonReviewPreviewMigrationTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();
    private const string Collection = "personReviewPreviews";
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime Date = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;

    [Fact]
    public async Task Fresh_v10_is_empty_strict_private_repeatable_without_retention_or_authority_seeds()
    {
        await InDatabase(async db =>
        {
            await ApplyV9(db);
            var before = await Definitions(db); var history = await History(db);
            var indexes = new Dictionary<string, List<BsonDocument>>();
            foreach (var row in before) indexes[row["name"].AsString] = await Indexes(db, row["name"].AsString);
            await ApplyV10(db);
            Assert.Equal(history, (await History(db)).Take(9));
            foreach (var row in before)
            {
                var name = row["name"].AsString;
                Assert.Equal(row, await Definition(db, name)); Assert.Equal(indexes[name], await Indexes(db, name));
            }
            var options = (await Definition(db, Collection))["options"];
            Assert.Equal("strict", options["validationLevel"]); Assert.Equal("error", options["validationAction"]);
            Assert.Equal(PersonReviewPreviewSchemas.Validator(Collection), options["validator"]);
            Assert.DoesNotContain(await Indexes(db, Collection), row => row.Contains("expireAfterSeconds"));
            Assert.Contains(await Indexes(db, Collection), row => row["name"] == "ux_reference" && row["unique"] == true);
            Assert.Equal(0, await Rows(db).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.Equal(0, await db.GetCollection<BsonDocument>("personGovernancePolicies").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            Assert.Equal(0, await db.GetCollection<BsonDocument>("idCounters").CountDocumentsAsync(new BsonDocument("_id", Collection)));
            var applied = await History(db); await ApplyV10(db); Assert.Equal(applied, await History(db));
        });
    }

    [Fact]
    public async Task Exact_closed_shapes_validate_both_kinds_and_states_and_reject_malformed_snapshots()
    {
        await InDatabase(async db =>
        {
            await ApplyV9(db); await ApplyV10(db);
            foreach (var approve in new[] { false, true })
                foreach (var consumed in new[] { false, true })
                {
                    var valid = Preview(approve, consumed);
                    Assert.True(PersonReviewPreviewSchemas.IsValid(Collection, valid));
                    await Rows(db).InsertOneAsync(valid); await Rows(db).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                    foreach (var field in valid.Names)
                    {
                        var missing = Clone(valid); missing.Remove(field); await Invalid(db, missing);
                        var wrong = Clone(valid); wrong[field] = field == "_id" ? new BsonInt64(0) : new BsonArray { valid[field] }; await Invalid(db, wrong);
                    }
                    var extra = Clone(valid); extra["unreviewed"] = true; await Invalid(db, extra);
                    var duplicate = new BsonDocument { AllowDuplicateNames = true }; duplicate.AddRange(valid.Elements); duplicate.Add("actorId", valid["actorId"]);
                    Assert.False(PersonReviewPreviewSchemas.IsValid(Collection, duplicate));
                }
            var mutations = new List<Action<BsonDocument>>
            {
                row => row["_id"] = 400,
                row => row["schemaVersion"] = 1L,
                row => row["reference"] = "6B833E68-55A3-49B3-82A0-1D3310B8A8F0",
                row => row["reference"] = "6b833e68-55a3-19b3-82a0-1d3310b8a8f0",
                row => row["reference"] = "6b833e68-55a3-49b3-72a0-1d3310b8a8f0",
                row => row["reference"] = "6b833e6855a349b382a01d3310b8a8f0",
                row => row["kind"] = "merge", row => row["status"] = "expired", row => row["version"] = 2L,
                row => row["actorId"] = -1L, row => row["grantVersion"] = 1.0,
                row => row["session"]["securityVersion"] = 2L,
                row => row["session"]["id"] = new string('A', 64), row => row["session"]["version"] = 1,
                row => row["session"]["extra"] = true,
                row => row["expiresAt"] = row["createdAt"], row => row["createdAt"] = new BsonDateTime(long.MinValue),
                row => row["expiresAt"] = new BsonDateTime(long.MaxValue),
                row => row["selectionDigest"] = new string('a', 63), row => row["policy"]["extra"] = true,
                row => row["target"]["canonicalPersonId"] = 999L,
                row => row["target"]["personIds"] = new BsonArray { 1 },
                row => row["target"]["userIds"] = new BsonArray { 10L, 10L },
                row => row["target"]["heads"][0]["collection"] = "accountProfiles",
                row => row["target"]["heads"][0]["revision"] = 1,
                row => row["target"]["heads"] = new BsonArray(row["target"]["heads"].AsBsonArray.Reverse()),
                row => { var heads = row["target"]["heads"].AsBsonArray; var duplicate = Clone(heads[0].AsBsonDocument); duplicate["revision"] = 2L; heads.Insert(1, duplicate); },
                row => row["profiles"] = new BsonArray(),
                row => row["profiles"][0]["displayName"] = "   ",
                row => row["profiles"][0]["displayName"] = "\u00a0",
                row => row["profiles"][0]["displayName"] = "Bad\nName",
                row => row["profiles"][0]["displayName"] = new string('x', 201),
                row => row["profiles"][0]["verifiedEmail"] = "   ",
                row => row["profiles"][0]["verifiedEmail"] = new string('a', 321),
                row => row["profiles"][0]["status"] = "admin",
                row => row["profiles"][0]["userId"] = 10,
                row => row["profiles"][0]["extra"] = true,
                row => row["profiles"].AsBsonArray.Add(Clone(row["profiles"][0].AsBsonDocument)),
                row => row["profiles"] = new BsonArray { Profile(20) },
                row => row["proposalId"] = 200L,
                row => row["consumption"] = Consumption(false)
            };
            foreach (var mutation in mutations) { var row = Preview(); mutation(row); await Invalid(db, row); }
            foreach (var mutation in new Action<BsonDocument>[]
            {
                row => row["proposalVersion"] = 2L,
                row => row["proposalDigest"] = new string('A', 64),
                row => row["profiles"] = new BsonArray(row["profiles"].AsBsonArray.Reverse()),
                row => row["profiles"] = new BsonArray { Profile(10) },
                row => row["approvalRequestedBy"] = 30L,
                row => row["consumption"]["operation"] = "person.review.propose",
                row => row["consumption"]["resultProposalId"] = 201L,
                row => row["consumption"]["resultHistoryId"] = 302,
                row => row["consumption"]["consumedAt"] = Date.AddSeconds(-1),
                row => row["consumption"]["consumedAt"] = Date.AddMinutes(10),
                row => row["consumption"]["keyHash"] = new string('a', 63),
                row => row["consumption"]["extra"] = true
            }) { var row = Preview(true, true); mutation(row); await Invalid(db, row); }
            var nullable = Preview(); nullable["profiles"][0]["displayName"] = BsonNull.Value;
            Assert.True(PersonReviewPreviewSchemas.IsValid(Collection, nullable)); await Rows(db).InsertOneAsync(nullable);
            var duplicateReference = Clone(nullable); duplicateReference["_id"] = 401L;
            Assert.Equal(11000, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(db).InsertOneAsync(duplicateReference))).WriteError.Code);
        });
    }

    [Theory]
    [InlineData("record")]
    [InlineData("duplicate-reference")]
    [InlineData("counter-type")]
    [InlineData("counter-negative")]
    [InlineData("counter-overflow")]
    [InlineData("row-overflow")]
    [InlineData("missing-policy")]
    [InlineData("future-policy")]
    [InlineData("missing-profile")]
    [InlineData("future-profile")]
    [InlineData("missing-head")]
    [InlineData("wrong-head-id-type")]
    [InlineData("future-head")]
    [InlineData("missing-actor")]
    [InlineData("actor-version")]
    [InlineData("proposal-digest")]
    [InlineData("proposal-requester")]
    [InlineData("proposal-target")]
    [InlineData("missing-history")]
    [InlineData("wrong-history")]
    [InlineData("history-actor")]
    [InlineData("history-request")]
    public async Task All_records_references_and_counters_preflight_before_any_ddl_or_highwater_write(string fault)
    {
        await InDatabase(async db =>
        {
            await ApplyV9(db); await Seed(db);
            var preview = Preview(true, true);
            if (fault == "record") preview["profiles"][0]["version"] = 1;
            if (fault == "row-overflow") preview["_id"] = long.MaxValue;
            if (fault == "future-policy") preview["policy"]["version"] = 2L;
            if (fault == "future-profile") preview["profiles"][0]["version"] = 2L;
            if (fault == "future-head") preview["target"]["heads"][0]["revision"] = 2L;
            if (fault == "actor-version") preview["grantVersion"] = 2L;
            if (fault == "proposal-digest") preview["proposalDigest"] = new string('b', 64);
            if (fault == "proposal-requester") { preview["approvalRequestedBy"] = 30L; preview["profiles"] = new BsonArray { Profile(10), Profile(30) }; }
            if (fault == "proposal-target") preview["target"]["groupVersion"] = 2L;
            if (fault == "wrong-history") preview["consumption"]["resultHistoryId"] = 301L;
            if (fault == "history-actor") preview["actorId"] = 20L;
            await Rows(db).InsertOneAsync(preview);
            if (fault == "duplicate-reference") { var duplicate = Clone(preview); duplicate["_id"] = 401L; await Rows(db).InsertOneAsync(duplicate); }
            if (fault == "missing-policy") await db.GetCollection<BsonDocument>("personGovernancePolicies").DeleteOneAsync(new BsonDocument("_id", 100L));
            if (fault == "missing-profile") await db.GetCollection<BsonDocument>("accountProfiles").DeleteOneAsync(new BsonDocument("_id", 10L));
            if (fault == "missing-head") await db.GetCollection<BsonDocument>("personEvidenceSources").DeleteOneAsync(new BsonDocument("_id", PersonTestData.SourceId));
            if (fault == "wrong-head-id-type")
            {
                var identities = db.GetCollection<BsonDocument>("identities"); var identity = await identities.Find(new BsonDocument("_id", 10L)).SingleAsync();
                await identities.DeleteOneAsync(new BsonDocument("_id", 10L)); identity["_id"] = 10;
                await identities.InsertOneAsync(identity, new InsertOneOptions { BypassDocumentValidation = true });
            }
            if (fault == "missing-actor") await db.GetCollection<BsonDocument>("identities").DeleteOneAsync(new BsonDocument("_id", 30L));
            if (fault == "missing-history") await db.GetCollection<BsonDocument>("personReviewHistory").DeleteOneAsync(new BsonDocument("_id", 302L));
            if (fault == "history-request") await db.GetCollection<BsonDocument>("personReviewHistory").UpdateOneAsync(new BsonDocument("_id", 301L),
                U.Set("reason", "Rewritten original request").Set("proposal.reason", "Rewritten original request"));
            BsonValue counter = fault switch { "counter-type" => new BsonInt32(1), "counter-negative" => new BsonInt64(-1), "counter-overflow" => new BsonInt64(long.MaxValue), _ => new BsonInt64(1) };
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", Collection }, { "value", counter } }, new InsertOneOptions { BypassDocumentValidation = true });
            var before = await Definitions(db); var indexes = await Indexes(db, Collection);
            var counters = await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            var history = (await History(db)).Take(9).ToArray();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV10(db)); Assert.Contains("reconcile", error.Message);
            Assert.Equal(before, await Definitions(db)); Assert.Equal(indexes, await Indexes(db, Collection));
            Assert.Equal(counters, await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
            Assert.Equal(history, (await History(db)).Take(9));
        });
    }

    [Theory]
    [InlineData("ttl")]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("hidden")]
    [InlineData("collation")]
    [InlineData("name")]
    public async Task Unsafe_indexes_are_rejected_before_validator_or_counter_changes(string fault)
    {
        await InDatabase(async db =>
        {
            await ApplyV9(db); await Seed(db); await Rows(db).InsertOneAsync(Preview());
            var options = new CreateIndexOptions<BsonDocument> { Name = "ux_reference", Unique = fault != "unique" };
            if (fault == "ttl") options.ExpireAfter = TimeSpan.FromDays(1000);
            if (fault == "sparse") options.Sparse = true;
            if (fault == "partial") options.PartialFilterExpression = new BsonDocument("actorId", 20L);
            if (fault == "hidden") options.Hidden = true;
            if (fault == "collation") options.Collation = new Collation("en");
            await Rows(db).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument(fault is "ttl" or "name" ? "createdAt" : "reference", 1), options));
            var before = await Definitions(db); var indexes = await Indexes(db, Collection);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV10(db));
            Assert.Equal(before, await Definitions(db)); Assert.Equal(indexes, await Indexes(db, Collection));
            Assert.Equal(0, await db.GetCollection<BsonDocument>("idCounters").CountDocumentsAsync(new BsonDocument("_id", Collection)));
        });
    }

    [Theory]
    [InlineData("capped")]
    [InlineData("view")]
    [InlineData("timeseries")]
    [InlineData("collation")]
    [InlineData("retention")]
    public async Task Unsafe_collection_options_do_not_alter_existing_definitions(string fault)
    {
        await InDatabase(async db =>
        {
            await ApplyV9(db);
            if (fault == "capped") await db.CreateCollectionAsync(Collection, new CreateCollectionOptions { Capped = true, MaxSize = 4096 });
            if (fault == "collation") await db.CreateCollectionAsync(Collection, new CreateCollectionOptions { Collation = new Collation("en") });
            if (fault == "view") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", Collection }, { "viewOn", "fixtureSource" }, { "pipeline", new BsonArray() } });
            if (fault == "timeseries") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", Collection }, { "timeseries", new BsonDocument("timeField", "createdAt") } });
            if (fault == "retention") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", Collection }, { "clusteredIndex", new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } } }, { "expireAfterSeconds", 86400L } });
            var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV10(db)); Assert.Equal(before, await Definitions(db));
        });
    }

    [Fact]
    public async Task Majority_resume_preserves_stronger_rules_highwater_and_historical_previews_after_session_ttl()
    {
        await InDatabase(async db =>
        {
            await ApplyV9(db); await Seed(db);
            await Rows(db).InsertOneAsync(Preview(true, true));
            var proposed = Preview(false, true); proposed["_id"] = 401L; proposed["reference"] = Guid.NewGuid().ToString("D"); await Rows(db).InsertOneAsync(proposed);
            var prior = new BsonDocument("actorId", new BsonDocument("$ne", 999L));
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", Collection }, { "validator", prior } });
            await Rows(db).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("reference", 1), new CreateIndexOptions { Name = "reviewed_reference", Unique = true }));
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", Collection }, { "value", 500L } });
            await db.GetCollection<BsonDocument>("personGovernancePolicies").UpdateOneAsync(new BsonDocument("_id", 100L), U.Set("version", 8L).Set("status", "revoked").Set("manifestDigest", new string('b', 64)));
            await db.GetCollection<BsonDocument>("personEquivalenceGroups").UpdateOneAsync(new BsonDocument("_id", PersonTestData.GroupId(10)), U.Set("version", 9L).Set("status", "suspended"));
            await db.GetCollection<BsonDocument>("identities").UpdateManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { 10L, 20L, 30L })), U.Set("grantVersion", 3L).Set("securityVersion", 3L).Set("status", "suspended"));
            await db.GetCollection<BsonDocument>("accountProfiles").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, U.Set("version", 2L).Set("displayName", "Renamed since preview"));
            Assert.Equal(0, await db.GetCollection<BsonDocument>("identitySessions").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            var retained = await Rows(db).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
            using var stop = new CancellationTokenSource(); var commands = new ConcurrentBag<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(command => commands.Add(Clone(command.Command)));
                builder.Subscribe<CommandSucceededEvent>(command => { if (command.CommandName == "collMod") stop.Cancel(); });
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyV10(new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName), stop.Token));
            var combined = (await Definition(db, Collection))["options"]["validator"];
            Assert.Contains(combined["$and"].AsBsonArray, rule => rule == prior);
            await ApplyV10(db); Assert.Equal(combined, (await Definition(db, Collection))["options"]["validator"]);
            Assert.Equal(retained, await Rows(db).Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync());
            Assert.Contains(await Indexes(db, Collection), index => index["name"] == "reviewed_reference");
            Assert.DoesNotContain(await Indexes(db, Collection), index => index["name"] == "ux_reference");
            Assert.Equal(501L, (await new IdAllocator(db).NextAsync(Collection)).Value);
            var ddl = commands.Where(command => command.Contains("collMod") || command.Contains("create") || command.Contains("createIndexes")).ToArray();
            Assert.NotEmpty(ddl); Assert.All(ddl, command => Assert.Equal("majority", command["writeConcern"]["w"]));
            var forbidden = Preview(); forbidden["_id"] = 402L; forbidden["reference"] = Guid.NewGuid().ToString("D"); forbidden["actorId"] = 999L;
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(db).InsertOneAsync(forbidden))).WriteError.Code);
        });
    }

    private static BsonDocument Policy() => new()
    {
        { "_id", 100L }, { "schemaVersion", 1 }, { "status", "active" }, { "version", 1L }, { "fence", 0L },
        { "manifestReference", "synthetic-reviewed-manifest" }, { "manifestDigest", Digest }, { "effectiveAt", Date }, { "expiresAt", BsonNull.Value }
    };
    private static BsonDocument Pin() => new() { { "id", 100L }, { "version", 1L }, { "digest", Digest } };
    private static BsonDocument Head(string collection, long id) => new() { { "collection", collection }, { "id", id }, { "revision", 1L }, { "digest", Digest } };
    private static BsonArray Heads(long userId) => new(new[]
    {
        Head("identities", userId), Head("personAccountBindings", userId), Head("personEquivalenceGroups", PersonTestData.GroupId(userId)),
        Head("personEvidenceSources", PersonTestData.SourceId), Head("personEvidenceVersions", PersonTestData.EvidenceId(userId)), Head("personRecords", PersonTestData.PersonId(userId))
    });
    private static BsonDocument Person(long userId) => new() { { "userId", userId }, { "canonicalPersonId", PersonTestData.PersonId(userId) }, { "heads", Heads(userId) } };
    private static BsonDocument Proposal(string status = "pending")
    {
        var row = new BsonDocument
        {
            { "_id", 200L }, { "schemaVersion", 1 }, { "kind", "equivalence-group-suspension" }, { "version", status == "pending" ? 1L : 2L }, { "status", status },
            { "requestedBy", 20L }, { "requesterGrantVersion", 1L }, { "requesterSecurityVersion", 1L }, { "createdAt", Date },
            { "reason", "Reviewed synthetic suspension request" }, { "correlationId", "fixture-propose" },
            { "target", new BsonDocument { { "groupId", PersonTestData.GroupId(10) }, { "groupVersion", 1L }, { "groupStatus", "active" },
                { "canonicalPersonId", PersonTestData.PersonId(10) }, { "personIds", new BsonArray { PersonTestData.PersonId(10) } },
                { "userIds", new BsonArray { 10L } }, { "heads", Heads(10) } } },
            { "requesterPerson", Person(20) }, { "policy", Pin() }, { "requestDigest", Digest }
        };
        if (status == "pending") return row;
        var actor = status == "withdrawn" ? 20L : 30L;
        row.Add("resolution", new BsonDocument
        {
            { "actorId", actor }, { "actorGrantVersion", 1L }, { "actorSecurityVersion", 1L }, { "decidedAt", Date.AddMinutes(1) },
            { "reason", "Independent synthetic decision" }, { "correlationId", "fixture-decide" }, { "person", Person(actor) }, { "policy", Pin() },
            { "resultGroupVersion", status == "approved" ? 2L : 1L },
            { "affectedRevisions", status == "approved" ? new BsonArray { new BsonDocument
                { { "userId", 10L }, { "previousGrantVersion", 1L }, { "grantVersion", 2L }, { "previousSecurityVersion", 1L }, { "securityVersion", 2L } } } : new BsonArray() }
        });
        return row;
    }
    private static BsonDocument HistoryRow(BsonDocument proposal)
    {
        var pending = proposal["status"] == "pending";
        var metadata = pending ? proposal : proposal["resolution"].AsBsonDocument;
        return new()
        {
            { "_id", pending ? 301L : 302L }, { "schemaVersion", 1 }, { "proposalId", proposal["_id"] },
            { "transitionVersion", proposal["version"] }, { "event", pending ? "proposed" : proposal["status"] },
            { "actorId", metadata[pending ? "requestedBy" : "actorId"] }, { "actorGrantVersion", metadata[pending ? "requesterGrantVersion" : "actorGrantVersion"] },
            { "actorSecurityVersion", metadata[pending ? "requesterSecurityVersion" : "actorSecurityVersion"] },
            { "createdAt", metadata[pending ? "createdAt" : "decidedAt"] }, { "reason", metadata["reason"] },
            { "correlationId", metadata["correlationId"] }, { "proposal", Clone(proposal) }
        };
    }
    private static BsonDocument Profile(long id) => new()
    {
        { "userId", id }, { "version", 1L }, { "displayName", "Synthetic account " + id },
        { "verifiedEmail", "synthetic-" + id + "@example.invalid" }, { "status", "active" }
    };
    private static BsonDocument Consumption(bool approve) => new()
    {
        { "operation", approve ? "person.review.approve" : "person.review.propose" }, { "keyHash", Digest },
        { "resultProposalId", 200L }, { "resultHistoryId", approve ? 302L : 301L }, { "consumedAt", Date.AddMinutes(1) }
    };
    private static BsonDocument Preview(bool approve = false, bool consumed = false)
    {
        var row = new BsonDocument
        {
            { "_id", 400L }, { "schemaVersion", 1 }, { "reference", "6b833e68-55a3-49b3-82a0-1d3310b8a8f0" },
            { "kind", approve ? "approve" : "propose" }, { "status", consumed ? "consumed" : "ready" }, { "version", consumed ? 2L : 1L },
            { "actorId", approve ? 30L : 20L }, { "grantVersion", 1L }, { "securityVersion", 1L },
            { "session", new BsonDocument { { "id", Digest }, { "version", 1L }, { "securityVersion", 1L } } },
            { "createdAt", Date }, { "expiresAt", Date.AddMinutes(10) }, { "selectionDigest", Digest }, { "policy", Pin() },
            { "target", Clone(Proposal()["target"].AsBsonDocument) },
            { "profiles", approve ? new BsonArray { Profile(10), Profile(20) } : new BsonArray { Profile(10) } }
        };
        if (approve) row.AddRange(new BsonDocument { { "proposalId", 200L }, { "proposalVersion", 1L }, { "proposalDigest", Digest }, { "approvalRequestedBy", 20L } });
        if (consumed) row.Add("consumption", Consumption(approve));
        return row;
    }
    private static async Task Seed(IMongoDatabase db)
    {
        await PersonTestData.SeedDistinctPeopleAsync(db, new MutableAuthorizationClock(new DateTimeOffset(Date)), 10, 20, 30);
        await db.GetCollection<BsonDocument>("personGovernancePolicies").InsertOneAsync(Policy());
        await db.GetCollection<BsonDocument>("personReviewProposals").InsertOneAsync(Proposal("approved"));
        await db.GetCollection<BsonDocument>("personReviewHistory").InsertManyAsync([HistoryRow(Proposal()), HistoryRow(Proposal("approved"))]);
        foreach (var id in new long[] { 10, 20, 30 })
            await db.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(new BsonDocument
            {
                { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic account " + id },
                { "verifiedEmail", "synthetic-" + id + "@example.invalid" }, { "verifiedAt", Date }, { "updatedAt", Date }
            });
    }
    private async Task InDatabase(Func<IMongoDatabase, Task> run)
    {
        var name = "ut_person_preview_migration_" + Guid.NewGuid().ToString("N");
        try { await run(fixture.Client.GetDatabase(name)); } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private async Task ApplyV9(IMongoDatabase db)
    {
        IMigrationStep[] steps = [new FoundationMigration(db), new AuthorizationMigration(db), new AuthorizationLifecycleMigration(db), new IdentityMigration(db),
            new ProviderLifecycleMigration(db), new CatalogVisibilityMigration(db), new HistoricalReadMigration(db), new PersonAuthorityMigration(db), new PersonGovernanceMigration(db)];
        var gate = new MigrationGate(db, clock, 10);
        foreach (var step in steps) { var lease = await gate.AcquireAsync(step.Version, CancellationToken.None); await step.ApplyAsync(lease!, CancellationToken.None); await lease!.CompleteAsync(CancellationToken.None); }
    }
    private async Task ApplyV10(IMongoDatabase db, CancellationToken token = default)
    {
        var lease = await new MigrationGate(db, clock, 10).AcquireAsync(10, token); if (lease is null) return;
        try { await new PersonReviewPreviewMigration(db).ApplyAsync(lease, token); await lease.CompleteAsync(token); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private static BsonDocument Clone(BsonDocument value) => value.DeepClone().AsBsonDocument;
    private static IMongoCollection<BsonDocument> Rows(IMongoDatabase db) => db.GetCollection<BsonDocument>(Collection);
    private static async Task Invalid(IMongoDatabase db, BsonDocument row)
    {
        Assert.False(PersonReviewPreviewSchemas.IsValid(Collection, row));
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => Rows(db).InsertOneAsync(row))).WriteError.Code);
    }
    private static Task<List<BsonDocument>> History(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations").Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> Definition(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> Indexes(IMongoDatabase db, string name) => await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
}
