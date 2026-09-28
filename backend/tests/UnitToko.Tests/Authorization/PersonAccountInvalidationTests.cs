using System.Collections.Immutable;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonAccountInvalidationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Complete_alias_set_advances_both_authorities_preserves_binding_and_status_and_revokes_sessions()
    {
        await using var data = await Data.CreateAsync(fixture);
        foreach (var id in new long[] { 10, 20, 30, 40 }) await data.AddAsync(id);
        var active = await data.IssueAsync(10);
        var rotated = await data.RotateAsync(active, 10);
        var suspended = await data.IssueAsync(20);
        var revoked = await data.IssueAsync(30);
        var pending = await data.IssueAsync(40);
        await data.Sessions.RevokeAsync(revoked.OpaqueToken, "earlier_revocation");
        await data.Identities.UpdateOneAsync(new BsonDocument("_id", 20L), Builders<BsonDocument>.Update.Set("status", "suspended"));
        await data.Identities.UpdateOneAsync(new BsonDocument("_id", 30L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await data.Identities.UpdateOneAsync(new BsonDocument("_id", 40L), Builders<BsonDocument>.Update.Set("status", "pending").Unset("securityVersion"));
        var before = await data.IdentityRowsAsync();
        var oldSessions = await data.SessionRowsAsync();

        var result = await data.InvalidateAsync([10, 20, 30, 40]);

        Assert.Equal(new long[] { 10, 20, 30, 40 }, result.Select(row => row.UserId));
        Assert.All(result, row =>
        {
            Assert.Equal(1L, row.PreviousGrantVersion); Assert.Equal(2L, row.GrantVersion);
            Assert.Equal(1L, row.PreviousSecurityVersion); Assert.Equal(2L, row.SecurityVersion);
        });
        foreach (var old in before)
        {
            var expected = old.DeepClone().AsBsonDocument;
            expected["grantVersion"] = 2L; expected["securityVersion"] = 2L;
            expected["fence"] = old["fence"].AsInt64 + 1;
            Assert.Equal(expected, await data.IdentityAsync(old["_id"].AsInt64));
        }
        foreach (var old in oldSessions)
        {
            var current = await data.SessionRows.Find(new BsonDocument("_id", old["_id"])).SingleAsync();
            if (old["status"] == "revoked") Assert.Equal(old, current);
            else
            {
                Assert.Equal("revoked", current["status"].AsString);
                Assert.Equal("person_authority_changed", current["revocationReason"].AsString);
                Assert.Equal(old["version"].AsInt64 + 1, current["version"].AsInt64);
                Assert.Equal(old["fence"].AsInt64 + 1, current["fence"].AsInt64);
                Assert.Equal(old["protectedPayload"], current["protectedPayload"]);
                Assert.Equal(old["issuer"], current["issuer"]); Assert.Equal(old["subject"], current["subject"]);
            }
        }
        foreach (var session in new[] { active, rotated, suspended, revoked, pending })
            Assert.Null(await data.Sessions.ReadAsync(session.OpaqueToken));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.IssueAsync(10));
    }

    [Fact]
    public async Task Invalid_selection_or_missing_transaction_is_rejected_without_any_mutation()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10);
        var before = await data.IdentityRowsAsync();
        ImmutableArray<long>[] invalid = [default, [], [0], [-1], [10, 10], [20, 10], Enumerable.Range(1, 257).Select(id => (long)id).ToImmutableArray()];
        foreach (var ids in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => data.InvalidateAsync(ids));
        using var outside = await fixture.Client.StartSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Helper.InvalidateAsync(outside, [10]));
        Assert.Equal(before, await data.IdentityRowsAsync());
    }

    [Fact]
    public async Task Missing_later_alias_fails_before_any_account_or_session_mutation()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10); await data.IssueAsync(10);
        var before = await data.IdentityRowsAsync(); var sessions = await data.SessionRowsAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.InvalidateAsync([10, 20]));
        Assert.Equal(before, await data.IdentityRowsAsync());
        Assert.Equal(sessions, await data.SessionRowsAsync());
    }

    [Fact]
    public async Task Every_invalid_identity_revision_type_value_or_binding_is_rejected_before_mutations()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10); await data.AddAsync(20);
        await data.IssueAsync(10); await data.IssueAsync(20);
        var valid = await data.IdentityAsync(20);
        var sessions = await data.SessionRowsAsync();
        var first = await data.IdentityAsync(10);
        var mutations = new List<Action<BsonDocument>>
        {
            row => row.Remove("grantVersion"), row => row.Remove("fence"),
            row => row.Remove("issuer"), row => row["issuer"] = BsonNull.Value,
            row => row["issuer"] = new BsonArray { "https://approved.example/" },
            row => row["subject"] = "", row => row["subject"] = "invalid\nsubject",
            row => row["status"] = "unknown", row => row["status"] = new BsonArray { "active" },
            row => row["schemaVersion"] = 1L, row => row.Remove("schemaVersion")
        };
        foreach (var field in new[] { "grantVersion", "securityVersion", "fence" })
        {
            foreach (var value in new BsonValue[] { new BsonInt32(1), new BsonDouble(1), BsonNull.Value, new BsonInt64(-1), new BsonInt64(long.MaxValue), new BsonArray { 1L } })
                mutations.Add(row => row[field] = value);
            if (field != "fence") mutations.Add(row => row[field] = 0L);
        }
        foreach (var mutate in mutations)
        {
            var invalid = valid.DeepClone().AsBsonDocument; mutate(invalid);
            await data.Identities.ReplaceOneAsync(new BsonDocument("_id", 20L), invalid, new ReplaceOptions { BypassDocumentValidation = true });
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.InvalidateAsync([10, 20]));
            Assert.Equal(first, await data.IdentityAsync(10));
            Assert.Equal(invalid, await data.IdentityAsync(20));
            Assert.Equal(sessions, await data.SessionRowsAsync());
        }
        // Mongo numeric equality also matches a corrupt Int32 ID; parsing must not coerce it.
        await data.Identities.DeleteOneAsync(new BsonDocument("_id", 20L));
        var wrongId = valid.DeepClone().AsBsonDocument; wrongId["_id"] = 20;
        await data.Identities.InsertOneAsync(wrongId, new InsertOneOptions { BypassDocumentValidation = true });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.InvalidateAsync([10, 20]));
        Assert.Equal(first, await data.IdentityAsync(10));
    }

    [Fact]
    public async Task Malformed_or_overflowing_nonrevoked_session_rejects_whole_alias_set()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10); await data.AddAsync(20);
        await data.IssueAsync(10); var issued = await data.IssueAsync(20);
        var original = await data.SessionRows.Find(new BsonDocument("_id", issued.Session.Reference.Id)).SingleAsync();
        var identities = await data.IdentityRowsAsync();
        var mutations = new List<Action<BsonDocument>>
        {
            row => row["userId"] = 20, row => row["userId"] = new BsonArray { 20L },
            row => row["status"] = "unknown", row => row["status"] = new BsonArray { "active", "revoked" },
            row => row["schemaVersion"] = 1L,
            row => row.Remove("version"), row => row.Remove("fence")
        };
        foreach (var field in new[] { "version", "fence" })
            foreach (var value in new BsonValue[] { new BsonInt32(1), new BsonDouble(1), BsonNull.Value, new BsonInt64(-1), new BsonInt64(long.MaxValue), new BsonArray { 1L } })
                mutations.Add(row => row[field] = value);
        mutations.Add(row => row["version"] = 0L);
        foreach (var mutate in mutations)
        {
            var invalid = original.DeepClone().AsBsonDocument; mutate(invalid);
            await data.SessionRows.ReplaceOneAsync(new BsonDocument("_id", original["_id"]), invalid, new ReplaceOptions { BypassDocumentValidation = true });
            var sessions = await data.SessionRowsAsync();
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.InvalidateAsync([10, 20]));
            Assert.Equal(identities, await data.IdentityRowsAsync());
            Assert.Equal(sessions, await data.SessionRowsAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caught_later_preflight_failure_commits_unrelated_work_without_any_partial_invalidation(bool badSession)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10); await data.AddAsync(20);
        await data.IssueAsync(10); var issued = await data.IssueAsync(20);
        if (badSession)
            await data.SessionRows.UpdateOneAsync(new BsonDocument("_id", issued.Session.Reference.Id),
                Builders<BsonDocument>.Update.Set("fence", long.MaxValue), new UpdateOptions { BypassDocumentValidation = true });
        else
            await data.Identities.UpdateOneAsync(new BsonDocument("_id", 20L),
                Builders<BsonDocument>.Update.Set("grantVersion", 1), new UpdateOptions { BypassDocumentValidation = true });
        var identities = await data.IdentityRowsAsync(); var sessions = await data.SessionRowsAsync();
        await data.Database.CreateCollectionAsync("fixtureSentinels");
        var sentinels = data.Database.GetCollection<BsonDocument>("fixtureSentinels");

        await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Helper.InvalidateAsync(tx, [10, 20], ct));
            Assert.Equal(identities, await data.Identities.Find(tx, FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync(ct));
            Assert.Equal(sessions, await data.SessionRows.Find(tx, FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync(ct));
            await sentinels.InsertOneAsync(tx, new BsonDocument { { "_id", 1L }, { "committed", true } }, cancellationToken: ct);
            return true;
        });

        Assert.True((await sentinels.Find(new BsonDocument("_id", 1L)).SingleAsync())["committed"].AsBoolean);
        Assert.Equal(identities, await data.IdentityRowsAsync());
        Assert.Equal(sessions, await data.SessionRowsAsync());
    }

    [Fact]
    public async Task Caller_failure_after_invalidation_rolls_back_accounts_and_sessions_with_the_governed_change()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10); await data.AddAsync(20);
        await data.IssueAsync(10); await data.IssueAsync(20);
        var identities = await data.IdentityRowsAsync(); var sessions = await data.SessionRowsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Unit.ExecuteAsync<bool>(async (tx, ct) =>
        {
            await data.Helper.InvalidateAsync(tx, [10, 20], ct);
            throw new InvalidOperationException("Synthetic downstream governed-write failure");
        }));
        Assert.Equal(identities, await data.IdentityRowsAsync());
        Assert.Equal(sessions, await data.SessionRowsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_issue_or_step_up_with_old_snapshot_cannot_survive_completed_invalidation(bool stepUp)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10);
        var initial = await data.IssueAsync(10);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var pending = data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await data.Identities.Find(tx, new BsonDocument("_id", 10L)).SingleAsync(ct);
            captured.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return stepUp
                ? await data.Sessions.RotateForStepUpAsync(tx, initial.Session.Reference, data.Proof(10) with { ChallengeBinding = "test-challenge" },
                    "private-provider-payload", "test-challenge", data.Clock.UtcNow, ct)
                : await data.Sessions.CreateAsync(tx, data.Admission(10), data.Proof(10), "private-provider-payload", ct);
        });
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { await data.InvalidateAsync([10]); }
        finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.True(attempts >= 1);
        Assert.Null(await data.Sessions.ReadAsync(initial.OpaqueToken));
        Assert.All(await data.SessionRowsAsync(), row => Assert.Equal("revoked", row["status"].AsString));
        Assert.Equal(2L, (await data.IdentityAsync(10))["securityVersion"].AsInt64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalidation_started_before_issue_or_rotation_retries_and_revokes_the_new_session(bool stepUp)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.AddAsync(10);
        var initial = await data.IssueAsync(10);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var pending = data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await data.Identities.Find(tx, new BsonDocument("_id", 10L)).SingleAsync(ct);
            captured.TrySetResult(); await resume.Task.WaitAsync(ct);
            return await data.Helper.InvalidateAsync(tx, [10], ct);
        });
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(15));
        SessionIssue next;
        try { next = stepUp ? await data.RotateAsync(initial, 10) : await data.IssueAsync(10); }
        finally { resume.TrySetResult(); }
        await pending;
        Assert.True(attempts >= 2, "The snapshot before session issuance must be retried.");
        Assert.Null(await data.Sessions.ReadAsync(initial.OpaqueToken));
        Assert.Null(await data.Sessions.ReadAsync(next.OpaqueToken));
        Assert.All(await data.SessionRowsAsync(), row => Assert.Equal("revoked", row["status"].AsString));
        Assert.Equal(2L, (await data.IdentityAsync(10))["grantVersion"].AsInt64);
    }

    private sealed class Data : IAsyncDisposable
    {
        private readonly MongoClient client;
        public IMongoDatabase Database { get; }
        public MutableAuthorizationClock Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        public MongoUnitOfWork Unit { get; }
        public SessionPolicy Policy { get; } = new("https://approved.example/", "fixture-connection", "fixture-policy");
        public MongoIdentitySessionStore Sessions { get; }
        public PersonAccountInvalidation Helper { get; }
        public IMongoCollection<BsonDocument> Identities => Database.GetCollection<BsonDocument>("identities");
        public IMongoCollection<BsonDocument> SessionRows => Database.GetCollection<BsonDocument>("identitySessions");
        private Data(MongoFixture fixture)
        {
            client = fixture.Client; Database = client.GetDatabase("ut_person_invalidation_" + Guid.NewGuid().ToString("N"));
            Unit = new(client);
            Sessions = new(Database, Unit, Clock, Policy, SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
            Helper = new(Database, Sessions);
        }
        public static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var data = new Data(fixture);
            await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
            return data;
        }
        public Task AddAsync(long id) => Identities.InsertOneAsync(new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "issuer", Policy.TrustedIssuer }, { "subject", "subject-" + id },
            { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L }
        });
        public ProviderIdentityProof Proof(long id) => new(Policy.TrustedIssuer, "subject-" + id, "fixture@example.invalid", true,
            Policy.TrustedConnectionId, Clock.UtcNow, Clock.UtcNow, "otp", Policy.AssurancePolicyVersion);
        public IdentityAdmission Admission(long id) => new(id, 1, 1, Policy.TrustedIssuer, "subject-" + id);
        public Task<SessionIssue> IssueAsync(long id) => Unit.ExecuteAsync((tx, ct) =>
            Sessions.CreateAsync(tx, Admission(id), Proof(id), "private-provider-payload", ct));
        public Task<SessionIssue> RotateAsync(SessionIssue old, long id) => Unit.ExecuteAsync((tx, ct) =>
            Sessions.RotateForStepUpAsync(tx, old.Session.Reference, Proof(id) with { ChallengeBinding = "test-challenge" },
                "private-provider-payload", "test-challenge", Clock.UtcNow, ct));
        public Task<ImmutableArray<PersonAccountAuthorityRevision>> InvalidateAsync(ImmutableArray<long> ids) =>
            Unit.ExecuteAsync((tx, ct) => Helper.InvalidateAsync(tx, ids, ct));
        public Task<BsonDocument> IdentityAsync(long id) => Identities.Find(new BsonDocument("_id", id)).SingleAsync();
        public Task<List<BsonDocument>> IdentityRowsAsync() => Identities.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
        public Task<List<BsonDocument>> SessionRowsAsync() => SessionRows.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
        public ValueTask DisposeAsync() => new(client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName));
    }
}
