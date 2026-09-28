using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonWithdrawalTargetTests(MongoFixture fixture)
{
    [Fact]
    public async Task Structural_target_accepts_disputed_expired_inactive_aliases_without_granting_authority()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId), U.Set("status", "disputed"));
        await data.Rows("personEvidenceSources").UpdateOneAsync(Id(PersonTestData.SourceId),
            U.Set("status", "revoked").Set("version", 2L).Set("adapterId", "changed-adapter")
                .Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("personRecords").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, U.Set("status", "disputed"));
        await data.Rows("personAccountBindings").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            U.Set("status", "revoked").Set("verification", "disputed").Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("personEvidenceVersions").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            U.Set("expiresAt", data.Clock.UtcNow.AddHours(-1).UtcDateTime));
        await data.Rows("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, U.Set("status", "suspended"));

        await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new PersonRegistryService(data.Database, data.Clock).ResolveAsync(tx, [10, 20, 30], ct));
            var target = await data.Loader.LoadAsync(tx, data.GroupId, ct);
            Assert.Equal(data.GroupId, target.GroupId); Assert.Equal(1L, target.GroupVersion);
            Assert.Equal("disputed", target.GroupStatus); Assert.Equal(PersonTestData.PersonId(10), target.CanonicalPersonId);
            Assert.Equal(new long[] { PersonTestData.PersonId(10), PersonTestData.PersonId(20) }, target.PersonIds);
            Assert.Equal(new long[] { 10, 20, 30 }, target.UserIds);
            Assert.Equal(13, target.Snapshot["heads"].AsBsonArray.Count);
            Assert.DoesNotContain("private-source-file", target.Snapshot.ToJson());
            Assert.DoesNotContain("synthetic-person.example", target.Snapshot.ToJson());
            Assert.DoesNotContain("private-person", target.Snapshot.ToJson());
            await target.TouchAsync(data.Database, tx, ct);
            return true;
        });
        Assert.Equal("disputed", (await data.RowAsync("personEquivalenceGroups", data.GroupId))["status"].AsString);
        Assert.Equal(1L, (await data.RowAsync("identities", 10))["grantVersion"].AsInt64);
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("superseded")]
    public async Task Terminal_group_cannot_be_a_new_withdrawal_target(string status)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId), U.Set("status", status));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.LoadAsync());
    }

    [Theory]
    [InlineData("extra-person")]
    [InlineData("extra-alias")]
    [InlineData("missing-person")]
    [InlineData("missing-alias")]
    [InlineData("outside-person")]
    [InlineData("wrong-pointer")]
    [InlineData("wrong-evidence")]
    [InlineData("wrong-source")]
    [InlineData("future-source-version")]
    [InlineData("same-version-wrong-adapter")]
    [InlineData("conflicting-group")]
    [InlineData("malformed-group")]
    [InlineData("unsorted-members")]
    [InlineData("missing-provenance")]
    [InlineData("malformed-provenance-id")]
    [InlineData("malformed-affected-identity")]
    [InlineData("overflow-fence")]
    public async Task Partial_malformed_or_ambiguous_closure_is_denied(string mutation)
    {
        await using var data = await Data.CreateAsync(fixture);
        switch (mutation)
        {
            case "extra-person":
                var person = PersonTestData.Person(40); person["currentEquivalenceId"] = data.GroupId;
                await data.Rows("personRecords").InsertOneAsync(person); break;
            case "extra-alias":
                var alias = PersonTestData.Binding(40, data.Clock.UtcNow); alias["personId"] = PersonTestData.PersonId(10); alias["evidenceId"] = PersonTestData.EvidenceId(10);
                await data.Rows("personAccountBindings").InsertOneAsync(alias); break;
            case "missing-person": await data.Rows("personRecords").DeleteOneAsync(Id(PersonTestData.PersonId(20))); break;
            case "missing-alias": await data.Rows("personAccountBindings").DeleteOneAsync(Id(30)); break;
            case "outside-person": await data.Rows("personAccountBindings").UpdateOneAsync(Id(30), U.Set("personId", 999999L)); break;
            case "wrong-pointer": await data.Rows("personRecords").UpdateOneAsync(Id(PersonTestData.PersonId(20)), U.Set("currentEquivalenceId", PersonTestData.GroupId(20))); break;
            case "wrong-evidence": await data.Rows("personAccountBindings").UpdateOneAsync(Id(30), U.Set("evidenceId", PersonTestData.EvidenceId(20))); break;
            case "wrong-source": await data.Rows("personEvidenceVersions").UpdateOneAsync(Id(PersonTestData.EvidenceId(20)), U.Set("sourceSubjectKey", "different-person")); break;
            case "future-source-version": await data.Rows("personEvidenceVersions").UpdateOneAsync(Id(PersonTestData.EvidenceId(20)), U.Set("sourceVersion", 2L)); break;
            case "same-version-wrong-adapter": await data.Rows("personEvidenceVersions").UpdateOneAsync(Id(PersonTestData.EvidenceId(20)), U.Set("adapterId", "different-adapter")); break;
            case "conflicting-group": await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(PersonTestData.GroupId(20)), U.Set("status", "disputed")); break;
            case "malformed-group": await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId), U.Set("extra", true), new UpdateOptions { BypassDocumentValidation = true }); break;
            case "unsorted-members": await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId), U.Set("memberUserIds", new BsonArray { 30L, 20L, 10L })); break;
            case "missing-provenance": await data.Rows("identities").DeleteOneAsync(Id(PersonTestData.RequesterId)); break;
            case "malformed-provenance-id":
                var old = await data.RowAsync("identities", PersonTestData.RequesterId);
                await data.Rows("identities").DeleteOneAsync(Id(PersonTestData.RequesterId));
                old["_id"] = (int)PersonTestData.RequesterId;
                await data.Rows("identities").InsertOneAsync(old, new InsertOneOptions { BypassDocumentValidation = true }); break;
            case "malformed-affected-identity": await data.Rows("identities").UpdateOneAsync(Id(30), U.Set("grantVersion", 1), new UpdateOptions { BypassDocumentValidation = true }); break;
            case "overflow-fence": await data.Rows("personRecords").UpdateOneAsync(Id(PersonTestData.PersonId(20)), U.Set("fence", long.MaxValue)); break;
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.LoadAsync());
    }

    [Theory]
    [InlineData("declared-members")]
    [InlineData("current-people")]
    [InlineData("current-aliases")]
    [InlineData("superseded-overlaps")]
    public async Task Oversized_closure_is_rejected_without_silent_truncation(string boundary)
    {
        await using var data = await Data.CreateAsync(fixture);
        if (boundary == "declared-members")
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId),
                U.Set("memberUserIds", new BsonArray(Enumerable.Range(1, 257).Select(id => (long)id))),
                new UpdateOptions { BypassDocumentValidation = true });
        else
        {
            var records = new List<BsonDocument>();
            for (var index = 0; index < 257; index++)
            {
                var account = 100L + index;
                if (boundary == "current-people")
                {
                    var row = PersonTestData.Person(account); row["currentEquivalenceId"] = data.GroupId; records.Add(row);
                }
                else if (boundary == "current-aliases")
                {
                    var row = PersonTestData.Binding(account, data.Clock.UtcNow);
                    row["personId"] = PersonTestData.PersonId(10); row["evidenceId"] = PersonTestData.EvidenceId(10); records.Add(row);
                }
                else
                {
                    var row = PersonTestData.Group(account, data.Clock.UtcNow);
                    row["canonicalPersonId"] = PersonTestData.PersonId(10); row["memberPersonIds"] = new BsonArray { PersonTestData.PersonId(10) };
                    row["memberUserIds"] = new BsonArray { 10L }; row["status"] = "superseded"; records.Add(row);
                }
            }
            await data.Rows(boundary == "current-people" ? "personRecords" : boundary == "current-aliases" ? "personAccountBindings" : "personEquivalenceGroups")
                .InsertManyAsync(records);
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.LoadAsync());
    }

    [Fact]
    public async Task Historical_provenance_overlap_is_not_reinterpreted_as_current_approval()
    {
        await using var data = await Data.CreateAsync(fixture);
        // This deliberately disputed legacy fact cannot grant authority; a current independent
        // governed reviewer still needs to be able to withdraw the structurally complete target.
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId),
            U.Set("approvedBy", 10L).Set("status", "disputed"));
        var target = await data.LoadAsync();
        Assert.Equal(new long[] { 10, 20, 30 }, target.UserIds);
    }

    [Fact]
    public async Task Snapshot_is_defensive_deterministic_and_changes_for_every_semantic_head_not_operational_fences()
    {
        await using var data = await Data.CreateAsync(fixture);
        var first = (await data.LoadAsync()).Snapshot;
        Assert.Equal(new[] { "groupId", "groupVersion", "groupStatus", "canonicalPersonId", "personIds", "userIds", "heads" }, first.Names);
        var target = await data.LoadAsync();
        var external = target.Snapshot; external["groupId"] = 999L; external["heads"].AsBsonArray.Clear();
        Assert.Equal(first, target.Snapshot);
        foreach (var collection in new[] { "personEvidenceSources", "personRecords", "personAccountBindings", "personEquivalenceGroups", "identities" })
            await data.Rows(collection).UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, U.Inc("fence", 1L));
        Assert.Equal(first, (await data.LoadAsync()).Snapshot);
        await data.Rows("identities").UpdateOneAsync(Id(10), U.Set("securityVersion", 1L));
        var afterIdentity = (await data.LoadAsync()).Snapshot;
        Assert.NotEqual(first, afterIdentity);
        await data.Rows("personEvidenceVersions").UpdateOneAsync(Id(PersonTestData.EvidenceId(10)), U.Set("reason", "Changed private evidence fact"));
        Assert.NotEqual(afterIdentity, (await data.LoadAsync()).Snapshot);

        var a = new BsonDocument { { "z", 1L }, { "a", new BsonDocument { { "y", "text" }, { "x", 2 } } }, { "fence", 5L } };
        var b = new BsonDocument { { "fence", 99L }, { "a", new BsonDocument { { "x", 2 }, { "y", "text" } } }, { "z", 1L } };
        Assert.Equal(PersonWithdrawalTarget.Digest(a, true), PersonWithdrawalTarget.Digest(b, true));
        Assert.NotEqual(PersonWithdrawalTarget.Digest(a, false), PersonWithdrawalTarget.Digest(b, false));
        b["z"] = 1;
        Assert.NotEqual(PersonWithdrawalTarget.Digest(a, true), PersonWithdrawalTarget.Digest(b, true));
    }

    [Fact]
    public async Task Target_is_bound_to_exact_transaction_attempt_and_database()
    {
        await using var data = await Data.CreateAsync(fixture);
        using var tx = await fixture.Client.StartSessionAsync();
        tx.StartTransaction();
        var target = await data.Loader.LoadAsync(tx, data.GroupId);
        using var other = await fixture.Client.StartSessionAsync();
        other.StartTransaction();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => target.TouchAsync(data.Database, other));
        await other.AbortTransactionAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => target.TouchAsync(fixture.Client.GetDatabase("other_fixture"), tx));
        await tx.AbortTransactionAsync();
        tx.StartTransaction();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => target.TouchAsync(data.Database, tx));
        await tx.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("personEvidenceSources", "version")]
    [InlineData("personRecords", "version")]
    [InlineData("personAccountBindings", "version")]
    [InlineData("personEquivalenceGroups", "version")]
    [InlineData("identities", "grantVersion")]
    public async Task Concurrent_semantic_change_forces_reload_instead_of_committing_old_target(string collection, string revision)
    {
        await using var data = await Data.CreateAsync(fixture);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0; BsonDocument? original = null;
        var pending = data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var target = await data.Loader.LoadAsync(tx, data.GroupId, ct);
            Interlocked.Increment(ref attempts);
            original ??= target.Snapshot;
            if (!target.Snapshot.Equals(original)) return false;
            loaded.TrySetResult(); await resume.Task.WaitAsync(ct);
            await target.TouchAsync(data.Database, tx, ct);
            return true;
        });
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var id = collection switch
        {
            "personEvidenceSources" => PersonTestData.SourceId,
            "personRecords" => PersonTestData.PersonId(10),
            "personAccountBindings" or "identities" => 10L,
            _ => data.GroupId
        };
        try { await data.Rows(collection).UpdateOneAsync(Id(id), U.Inc(revision, 1L).Inc("fence", 1L)); }
        finally { resume.TrySetResult(); }
        Assert.False(await pending);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task Late_alias_insert_with_common_group_head_requires_reloading_complete_closure()
    {
        await using var data = await Data.CreateAsync(fixture);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var pending = data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var target = await data.Loader.LoadAsync(tx, data.GroupId, ct);
            Interlocked.Increment(ref attempts);
            if (target.UserIds.Length != 3) return false;
            loaded.TrySetResult(); await resume.Task.WaitAsync(ct);
            await target.TouchAsync(data.Database, tx, ct);
            return true;
        });
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await data.Unit.ExecuteAsync(async (tx, ct) =>
            {
                var alias = PersonTestData.Binding(40, data.Clock.UtcNow);
                alias["personId"] = PersonTestData.PersonId(10); alias["evidenceId"] = PersonTestData.EvidenceId(10);
                await data.Rows("identities").InsertOneAsync(tx, Data.Identity(40), cancellationToken: ct);
                await data.Rows("personAccountBindings").InsertOneAsync(tx, alias, cancellationToken: ct);
                await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, Id(data.GroupId),
                    U.Set("memberUserIds", new BsonArray { 10L, 20L, 30L, 40L }).Inc("version", 1L).Inc("fence", 1L), cancellationToken: ct);
                return true;
            });
        }
        finally { resume.TrySetResult(); }
        Assert.False(await pending); Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task Same_transaction_changed_immutable_evidence_cannot_be_fenced_as_original()
    {
        await using var data = await Data.CreateAsync(fixture);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var target = await data.Loader.LoadAsync(tx, data.GroupId, ct);
            await data.Rows("personEvidenceVersions").UpdateOneAsync(tx, Id(PersonTestData.EvidenceId(10)),
                U.Set("reason", "Unsupported mutation must be detected"), cancellationToken: ct);
            await target.TouchAsync(data.Database, tx, ct);
            return true;
        }));
        Assert.Equal("Synthetic evidence review", (await data.RowAsync("personEvidenceVersions", PersonTestData.EvidenceId(10)))["reason"].AsString);
    }

    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private static BsonDocument Id(long id) => new("_id", id);
    private sealed class Data : IAsyncDisposable
    {
        private readonly MongoClient client;
        public IMongoDatabase Database { get; }
        public MongoUnitOfWork Unit { get; }
        public PersonWithdrawalTargetLoader Loader { get; }
        public MutableAuthorizationClock Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        public long GroupId => PersonTestData.GroupId(10);
        private Data(MongoFixture fixture)
        {
            client = fixture.Client; Database = client.GetDatabase("ut_withdrawal_target_" + Guid.NewGuid().ToString("N"));
            Unit = new(client); Loader = new(Database);
        }
        public static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var data = new Data(fixture);
            await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, 10, 20);
            await data.Rows("identities").InsertOneAsync(Identity(30));
            var alias = PersonTestData.Binding(30, data.Clock.UtcNow);
            alias["personId"] = PersonTestData.PersonId(10); alias["evidenceId"] = PersonTestData.EvidenceId(10);
            await data.Rows("personAccountBindings").InsertOneAsync(alias);
            await data.Rows("personRecords").UpdateOneAsync(Id(PersonTestData.PersonId(20)), U.Set("currentEquivalenceId", data.GroupId));
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(PersonTestData.GroupId(20)), U.Set("status", "superseded"));
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(Id(data.GroupId),
                U.Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(10), PersonTestData.PersonId(20) })
                    .Set("memberUserIds", new BsonArray { 10L, 20L, 30L }));
            return data;
        }
        internal static BsonDocument Identity(long id) => new()
        {
            { "_id", id }, { "schemaVersion", 1 }, { "issuer", "https://synthetic-person.example/" }, { "subject", "person-fixture-" + id },
            { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L }
        };
        public IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        public Task<BsonDocument> RowAsync(string name, long id) => Rows(name).Find(Id(id)).SingleAsync();
        public Task<PersonWithdrawalTarget> LoadAsync() => Unit.ExecuteAsync((tx, ct) => Loader.LoadAsync(tx, GroupId, ct));
        public ValueTask DisposeAsync() => new(client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName));
    }
}
