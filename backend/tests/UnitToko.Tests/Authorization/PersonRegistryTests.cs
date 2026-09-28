using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonRegistryTests(MongoFixture fixture)
{
    [Fact]
    public async Task Missing_bindings_remain_unknown_without_backfill_or_private_metadata()
    {
        await using var data = await Data.Create(fixture);
        var values = await data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, [101, 102], ct));
        Assert.All(values, value => Assert.Equal(new(value.UserId, null, PersonVerification.Unknown, 0), value));
        foreach (var name in data.Collections) Assert.Equal(0, await data.Database.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, 101, 102);
        var verified = await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var proof = await data.Registry.ResolveProofsAsync(tx, [101, 102], ct);
            await new PersonAuthorizationFence(data.Database, data.Clock).TouchAsync(tx, proof, ct);
            return proof.Evidence;
        });
        Assert.True(PersonIndependencePolicy.AreIndependent(verified[0], verified[1]));
        var json = JsonSerializer.Serialize(verified);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("source", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approvedBy", json);
        Assert.Equal(1L, (await data.Row("personEvidenceSources", PersonTestData.SourceId))["fence"].AsInt64);
        Assert.False((await data.Row("personEvidenceVersions", PersonTestData.EvidenceId(101))).Contains("fence"));
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("suspended-source")]
    [InlineData("source-version")]
    [InlineData("adapter")]
    [InlineData("subject")]
    [InlineData("person-int32")]
    [InlineData("person-array")]
    [InlineData("bad-date")]
    [InlineData("date-overflow")]
    [InlineData("extra-field")]
    [InlineData("members-duplicate")]
    [InlineData("members-int32")]
    [InlineData("members-omitted")]
    [InlineData("cross-linked")]
    [InlineData("superseded")]
    [InlineData("pointer-cycle")]
    [InlineData("source-not-yet-reviewed")]
    [InlineData("self-approved")]
    [InlineData("disputed")]
    public async Task Invalid_or_ambiguous_authority_is_a_controlled_denial(string change)
    {
        await using var data = await Data.Create(fixture, 101);
        var collection = "personAccountBindings";
        var id = 101L;
        string field = "personId";
        BsonValue value = 1;
        switch (change)
        {
            case "missing-source": await data.Database.GetCollection<BsonDocument>("personEvidenceSources").DeleteOneAsync(new BsonDocument("_id", PersonTestData.SourceId)); break;
            case "suspended-source": collection = "personEvidenceSources"; id = PersonTestData.SourceId; field = "status"; value = "suspended"; break;
            case "source-version": collection = "personEvidenceSources"; id = PersonTestData.SourceId; field = "version"; value = 2L; break;
            case "adapter": collection = "personEvidenceVersions"; id = PersonTestData.EvidenceId(101); field = "adapterId"; value = "other"; break;
            case "subject": collection = "personEvidenceVersions"; id = PersonTestData.EvidenceId(101); field = "sourceSubjectKey"; value = "other"; break;
            case "person-int32": value = (int)PersonTestData.PersonId(101); break;
            case "person-array": value = new BsonArray { PersonTestData.PersonId(101) }; break;
            case "bad-date": field = "effectiveAt"; value = "yesterday"; break;
            case "date-overflow": field = "effectiveAt"; value = new BsonDateTime(long.MaxValue); break;
            case "extra-field": field = "unreviewed"; value = true; break;
            case "members-duplicate": collection = "personEquivalenceGroups"; id = PersonTestData.GroupId(101); field = "memberUserIds"; value = new BsonArray { 101L, 101L }; break;
            case "members-int32": collection = "personEquivalenceGroups"; id = PersonTestData.GroupId(101); field = "memberUserIds"; value = new BsonArray { 101 }; break;
            case "members-omitted": collection = "personEquivalenceGroups"; id = PersonTestData.GroupId(101); field = "memberUserIds"; value = new BsonArray { 102L }; break;
            case "cross-linked": collection = "personEvidenceVersions"; id = PersonTestData.EvidenceId(101); field = "personId"; value = 999L; break;
            case "superseded": collection = "personEquivalenceGroups"; id = PersonTestData.GroupId(101); field = "status"; value = "superseded"; break;
            case "pointer-cycle": collection = "personRecords"; id = PersonTestData.PersonId(101); field = "currentEquivalenceId"; value = PersonTestData.PersonId(101); break;
            case "source-not-yet-reviewed": collection = "personEvidenceSources"; id = PersonTestData.SourceId; field = "reviewedAt"; value = new BsonDateTime(data.Clock.UtcNow.AddMinutes(1).UtcDateTime); break;
            case "self-approved": field = "approvedBy"; value = 101L; break;
            case "disputed": field = "verification"; value = "disputed"; break;
        }
        if (change != "missing-source") await data.Database.GetCollection<BsonDocument>(collection).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set(field, value));
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, [101], ct)));
        Assert.Equal("person_authority_unavailable", error.Code);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task Complete_graph_includes_departed_accounts_and_rejects_unlisted_bindings_and_people()
    {
        await using var data = await Data.Create(fixture, 101);
        var second = PersonTestData.Binding(102, data.Clock.UtcNow);
        second["personId"] = PersonTestData.PersonId(101); second["evidenceId"] = PersonTestData.EvidenceId(101);
        await data.Database.GetCollection<BsonDocument>("personAccountBindings").InsertOneAsync(second);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, [101], ct)));
        await data.Database.GetCollection<BsonDocument>("personEquivalenceGroups").UpdateOneAsync(new BsonDocument("_id", PersonTestData.GroupId(101)),
            Builders<BsonDocument>.Update.Set("memberUserIds", new BsonArray { 101L, 102L }).Inc("version", 1L));
        // Historical maker account may have departed. Identity activity is not person identity evidence.
        await data.Database.GetCollection<BsonDocument>("identities").InsertOneAsync(new BsonDocument { { "_id", 102L }, { "status", "revoked" } });
        var values = await data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, [101, 102], ct));
        Assert.Equal(values[0].PersonId, values[1].PersonId);
        var omitted = PersonTestData.Person(103); omitted["currentEquivalenceId"] = PersonTestData.GroupId(101);
        await data.Database.GetCollection<BsonDocument>("personRecords").InsertOneAsync(omitted);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, [101], ct)));
    }

    [Theory]
    [InlineData("personEvidenceSources", "policyReference")]
    [InlineData("personRecords", "sourceSubjectKey")]
    [InlineData("personAccountBindings", "reason")]
    [InlineData("personEquivalenceGroups", "approvalVersion")]
    [InlineData("personEvidenceVersions", "sourceReference")]
    public async Task Exact_fence_rejects_unversioned_changes_to_every_contributing_record(string collection, string field)
    {
        await using var data = await Data.Create(fixture, 101);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var proof = await data.Registry.ResolveProofsAsync(tx, [101], ct);
            var row = await data.Database.GetCollection<BsonDocument>(collection).Find(tx, FilterDefinition<BsonDocument>.Empty).SingleAsync(ct);
            BsonValue changed = field == "approvalVersion" ? new BsonInt64(2) : new BsonString("changed-private-field");
            await data.Database.GetCollection<BsonDocument>(collection).UpdateOneAsync(tx, new BsonDocument("_id", row["_id"]), Builders<BsonDocument>.Update.Set(field, changed), cancellationToken: ct);
            await new PersonAuthorizationFence(data.Database, data.Clock).TouchAsync(tx, proof, ct);
            return true;
        }));
        Assert.Equal(0L, (await data.Row("personEvidenceSources", PersonTestData.SourceId))["fence"].AsInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_or_clock_reversal_after_an_awaited_fence_write_rolls_back_all_fences(bool reverseClock)
    {
        await using var data = await Data.Create(fixture, 101);
        var original = data.Clock.UtcNow;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var proof = await data.Registry.ResolveProofsAsync(tx, [101], ct);
            var clock = new AdvancingClock(original, reverseClock ? original.AddMinutes(-1) : original.AddDays(31));
            await new PersonAuthorizationFence(data.Database, clock).TouchAsync(tx, proof, ct);
            return true;
        }));
        foreach (var name in data.Collections.Where(name => name != "personEvidenceVersions"))
            Assert.Equal(0L, (await data.Database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["fence"].AsInt64);
    }

    [Fact]
    public async Task Late_equivalence_changes_shared_heads_and_retries_into_same_person_denial()
    {
        await using var data = await Data.Create(fixture, 101, 102);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var pending = data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var proof = await data.Registry.ResolveProofsAsync(tx, [101, 102], ct);
            Interlocked.Increment(ref attempts);
            if (!PersonIndependencePolicy.AreIndependent(proof.Evidence[0], proof.Evidence[1])) throw new AuthorizationDeniedException("same_person");
            read.TrySetResult(); await resume.Task.WaitAsync(ct);
            await new PersonAuthorizationFence(data.Database, data.Clock).TouchAsync(tx, proof, ct);
            await data.Database.GetCollection<BsonDocument>("effects").InsertOneAsync(tx, new BsonDocument("_id", 1L), cancellationToken: ct);
            return true;
        });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await data.Unit.ExecuteAsync(async (tx, ct) =>
            {
                var merged = PersonTestData.Group(101, data.Clock.UtcNow); merged["_id"] = 900000L;
                merged["memberPersonIds"] = new BsonArray { PersonTestData.PersonId(101), PersonTestData.PersonId(102) };
                merged["memberUserIds"] = new BsonArray { 101L, 102L };
                await data.Database.GetCollection<BsonDocument>("personEquivalenceGroups").InsertOneAsync(tx, merged, cancellationToken: ct);
                foreach (var user in new[] { 101L, 102L })
                {
                    await data.Database.GetCollection<BsonDocument>("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(user)),
                        Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct);
                    await data.Database.GetCollection<BsonDocument>("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(user)),
                        Builders<BsonDocument>.Update.Set("currentEquivalenceId", 900000L).Inc("version", 1L), cancellationToken: ct);
                }
                return true;
            });
        }
        finally { resume.TrySetResult(); }
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal("same_person", error.Code);
        Assert.True(attempts >= 2);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("effects").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(PersonTestData.PersonId(102), (await data.Row("personEvidenceVersions", PersonTestData.EvidenceId(102)))["personId"].AsInt64);
    }

    [Fact]
    public async Task Inserting_an_account_with_shared_head_update_invalidates_a_paused_snapshot()
    {
        await using var data = await Data.Create(fixture, 101);
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        var proof = await data.Registry.ResolveProofsAsync(tx, [101]);
        await data.Unit.ExecuteAsync(async (writer, ct) =>
        {
            await data.Database.GetCollection<BsonDocument>("identities").InsertOneAsync(writer, new BsonDocument { { "_id", 102L }, { "status", "revoked" } }, cancellationToken: ct);
            var extra = PersonTestData.Binding(102, data.Clock.UtcNow); extra["personId"] = PersonTestData.PersonId(101); extra["evidenceId"] = PersonTestData.EvidenceId(101);
            await data.Database.GetCollection<BsonDocument>("personAccountBindings").InsertOneAsync(writer, extra, cancellationToken: ct);
            await data.Database.GetCollection<BsonDocument>("personEquivalenceGroups").UpdateOneAsync(writer, new BsonDocument("_id", PersonTestData.GroupId(101)),
                Builders<BsonDocument>.Update.Set("memberUserIds", new BsonArray { 101L, 102L }).Inc("version", 1L), cancellationToken: ct);
            return true;
        });
        await Assert.ThrowsAnyAsync<MongoException>(() => new PersonAuthorizationFence(data.Database, data.Clock).TouchAsync(tx, proof));
        if (tx.IsInTransaction) await tx.AbortTransactionAsync();
        var fresh = await data.Unit.ExecuteAsync((current, ct) => data.Registry.ResolveAsync(current, [101, 102], ct));
        Assert.Equal(fresh[0].PersonId, fresh[1].PersonId);
    }

    [Theory]
    [InlineData("int32")]
    [InlineData("array")]
    [InlineData("extra")]
    [InlineData("missing-expiry")]
    [InlineData("counter-overflow")]
    public async Task Fence_requires_exact_types_fields_and_safe_counter(string change)
    {
        await using var data = await Data.Create(fixture, 101);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var proof = await data.Registry.ResolveProofsAsync(tx, [101], ct);
            var update = change switch
            {
                "int32" => Builders<BsonDocument>.Update.Set("personId", (int)PersonTestData.PersonId(101)),
                "array" => Builders<BsonDocument>.Update.Set("personId", new BsonArray { PersonTestData.PersonId(101) }),
                "extra" => Builders<BsonDocument>.Update.Set("unknownAuthority", true),
                "missing-expiry" => Builders<BsonDocument>.Update.Unset("expiresAt"),
                _ => Builders<BsonDocument>.Update.Set("fence", long.MaxValue)
            };
            await data.Database.GetCollection<BsonDocument>("personAccountBindings").UpdateOneAsync(tx, new BsonDocument("_id", 101L), update, cancellationToken: ct);
            await new PersonAuthorizationFence(data.Database, data.Clock).TouchAsync(tx, proof, ct);
            return true;
        }));
    }

    [Fact]
    public async Task Operational_fence_changes_do_not_change_semantic_authority()
    {
        await using var data = await Data.Create(fixture, 101);
        await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var proof = await data.Registry.ResolveProofsAsync(tx, [101], ct);
            var fence = new PersonAuthorizationFence(data.Database, data.Clock);
            await fence.TouchAsync(tx, proof, ct);
            await fence.TouchAsync(tx, proof, ct);
            return true;
        });
        Assert.Equal(2L, (await data.Row("personAccountBindings", 101))["fence"].AsInt64);
    }
    [Fact]
    public async Task Participant_ids_are_nonempty_positive_unique_and_bounded()
    {
        await using var data = await Data.Create(fixture);
        ImmutableArray<long>[] invalid = [default, [], [0], [-1], [101, 101], Enumerable.Range(1, 257).Select(id => (long)id).ToImmutableArray()];
        foreach (var ids in invalid)
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, ids, ct)));
    }

    [Fact]
    public async Task Approved_provenance_requires_existing_accounts_but_accepts_inactive_reviewers()
    {
        await using var data = await Data.Create(fixture, 101);
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", PersonTestData.ReviewerId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        var known = await data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, [101], ct));
        Assert.Equal(PersonVerification.Verified, known[0].Verification);
        await data.Database.GetCollection<BsonDocument>("identities").DeleteOneAsync(new BsonDocument("_id", PersonTestData.ReviewerId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync((tx, ct) => data.Registry.ResolveAsync(tx, [101], ct)));
    }
    private sealed class AdvancingClock(DateTimeOffset before, DateTimeOffset after) : IClock
    {
        private int reads;
        public DateTimeOffset UtcNow => Interlocked.Increment(ref reads) >= 3 ? after : before;
    }
    private sealed class Data(MongoFixture fixture) : IAsyncDisposable
    {
        internal readonly string[] Collections = ["personEvidenceSources", "personRecords", "personAccountBindings", "personEquivalenceGroups", "personEvidenceVersions"];
        internal IMongoDatabase Database { get; } = fixture.Client.GetDatabase(fixture.DatabaseName + "_p_" + Guid.NewGuid().ToString("N")[..8]);
        internal MutableAuthorizationClock Clock { get; } = new(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        internal MongoUnitOfWork Unit => new(fixture.Client);
        internal PersonRegistryService Registry => new(Database, Clock);
        internal static async Task<Data> Create(MongoFixture fixture, params long[] users)
        {
            var data = new Data(fixture);
            foreach (var name in data.Collections.Concat(["effects", "identities"])) await data.Database.CreateCollectionAsync(name);
            if (users.Length != 0) await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, users);
            return data;
        }
        internal Task<BsonDocument> Row(string collection, long id) => Database.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", id)).SingleAsync();
        public async ValueTask DisposeAsync() => await fixture.Client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
    }
}
