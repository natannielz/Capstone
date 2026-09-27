using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class SessionStoreTests(MongoFixture fixture)
{
    [Fact]
    public async Task Opaque_reference_is_hashed_and_payload_encrypted_and_second_instance_resolves_current_identity()
    {
        await using var data = await Data.CreateAsync(fixture);
        var issued = await data.CreateAsync();
        var row = await data.Rows.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.NotEqual(issued.OpaqueToken, row["_id"].AsString);
        Assert.DoesNotContain(issued.OpaqueToken, row.ToJson());
        Assert.DoesNotContain("private-provider-token", row.ToJson());
        Assert.DoesNotContain("invitee@example.invalid", row.ToJson());
        var second = data.NewStore();
        var loaded = Assert.IsType<IdentitySession>(await second.ReadAsync(issued.OpaqueToken));
        Assert.Equal(issued.Session.Reference, loaded.Reference);
        Assert.Equal(issued.Session.Proof, loaded.Proof);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.CreateAsync(data.Proof() with { EmailVerified = false }));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.CreateAsync(data.Proof() with { FactorType = "recovery-code" }));
        Assert.Null(await second.ReadAsync("not-a-session"));
    }

    [Fact]
    public async Task Idle_renewal_never_changes_absolute_lifetime_or_factor_freshness_and_expiration_does_not_rely_on_TTL()
    {
        await using var data = await Data.CreateAsync(fixture);
        var issued = await data.CreateAsync();
        var factor = issued.Session.Proof.FactorCompletedAt;
        for (var index = 0; index < 15; index++)
        {
            data.Clock.UtcNow += TimeSpan.FromMinutes(29);
            var current = Assert.IsType<IdentitySession>(await data.Store.ReadAsync(issued.OpaqueToken));
            Assert.Equal(issued.Session.AbsoluteExpiresAt, current.AbsoluteExpiresAt);
            Assert.Equal(factor, current.Proof.FactorCompletedAt);
        }
        data.Clock.UtcNow = issued.Session.AbsoluteExpiresAt;
        Assert.Null(await data.Store.ReadAsync(issued.OpaqueToken));
        Assert.Equal(1, await data.Rows.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var idle = await data.CreateAsync();
        data.Clock.UtcNow += SessionPolicy.IdleTimeout;
        Assert.Null(await data.Store.ReadAsync(idle.OpaqueToken));
    }

    [Fact]
    public async Task Step_up_requires_new_bound_factor_and_rotates_once_without_extending_original_absolute_lifetime()
    {
        await using var data = await Data.CreateAsync(fixture);
        var original = await data.CreateAsync();
        data.Clock.UtcNow += TimeSpan.FromMinutes(10);
        var started = data.Clock.UtcNow;
        const string challenge = "reviewed-challenge-binding";
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.RotateAsync(original,
            data.Proof() with { FactorCompletedAt = original.Session.Proof.FactorCompletedAt, ChallengeBinding = challenge }, challenge, started));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.RotateAsync(original,
            data.Proof() with { ChallengeBinding = "other-challenge" }, challenge, started));
        var updated = await data.RotateAsync(original, data.Proof() with { ChallengeBinding = challenge }, challenge, started);
        Assert.NotEqual(original.OpaqueToken, updated.OpaqueToken);
        Assert.Equal(original.Session.AbsoluteExpiresAt, updated.Session.AbsoluteExpiresAt);
        Assert.Equal(original.Session.CreatedAt, updated.Session.CreatedAt);
        Assert.Null(await data.Store.ReadAsync(original.OpaqueToken));
        Assert.NotNull(await data.NewStore().ReadAsync(updated.OpaqueToken));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.RotateAsync(original,
            data.Proof() with { ChallengeBinding = challenge }, challenge, started));
        // A delayed logout carrying the previous cookie still closes its rotated descendant.
        await data.Store.RevokeAsync(original.OpaqueToken, "logout");
        Assert.Null(await data.Store.ReadAsync(updated.OpaqueToken));
    }

    [Fact]
    public async Task Recovery_epoch_revokes_all_sessions_and_prevents_old_admission_creating_a_new_session()
    {
        await using var data = await Data.CreateAsync(fixture);
        var first = await data.CreateAsync(); var second = await data.CreateAsync();
        await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(tx, new BsonDocument("_id", 1L),
                Builders<BsonDocument>.Update.Inc("securityVersion", 1L).Inc("fence", 1L), cancellationToken: ct);
            await data.Store.RevokeUserSessionsAsync(tx, 1L, "account_recovery", ct);
            return true;
        });
        Assert.Null(await data.NewStore().ReadAsync(first.OpaqueToken));
        Assert.Null(await data.Store.ReadAsync(second.OpaqueToken));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.CreateAsync());
        Assert.All(await data.Rows.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(), row =>
        { Assert.Equal("revoked", row["status"].AsString); Assert.Equal(2L, row["version"].AsInt64); });
    }

    [Fact]
    public async Task Tampered_ciphertext_or_binding_is_denied_without_returning_provider_payload()
    {
        await using var data = await Data.CreateAsync(fixture);
        var first = await data.CreateAsync(); var second = await data.CreateAsync();
        var source = await data.Rows.Find(new BsonDocument("_id", first.Session.Reference.Id)).SingleAsync();
        await data.Rows.UpdateOneAsync(new BsonDocument("_id", second.Session.Reference.Id),
            Builders<BsonDocument>.Update.Set("protectedPayload", source["protectedPayload"]));
        Assert.Null(await data.Store.ReadAsync(second.OpaqueToken));
        await data.Rows.UpdateOneAsync(new BsonDocument("_id", first.Session.Reference.Id),
            Builders<BsonDocument>.Update.Set("factorCompletedAt", data.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        Assert.Null(await data.Store.ReadAsync(first.OpaqueToken));
    }

    private sealed class Clock(DateTimeOffset value) : IClock { public DateTimeOffset UtcNow { get; set; } = value; }
    private sealed class Data : IAsyncDisposable
    {
        private readonly MongoClient client;
        public IMongoDatabase Database { get; }
        public Clock Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        public MongoUnitOfWork Unit { get; }
        public SessionPayloadProtector Protector { get; } = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
        public SessionPolicy Policy { get; } = new("https://approved.example/", "reviewed-database", "otp-v1");
        public MongoIdentitySessionStore Store { get; }
        public IMongoCollection<BsonDocument> Rows => Database.GetCollection<BsonDocument>("identitySessions");
        private Data(MongoFixture fixture)
        {
            client = fixture.Client; Database = client.GetDatabase("ut_sessions_" + Guid.NewGuid().ToString("N"));
            Unit = new(client); Store = NewStore();
        }
        public MongoIdentitySessionStore NewStore() => new(Database, Unit, Clock, Policy, Protector);
        public static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var data = new Data(fixture);
            await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
            await data.Database.GetCollection<BsonDocument>("identities").InsertOneAsync(new BsonDocument
            {
                { "_id", 1L }, { "schemaVersion", 1 }, { "issuer", data.Policy.TrustedIssuer }, { "subject", "provider-user" },
                { "status", "active" }, { "grantVersion", 1L }, { "securityVersion", 1L }, { "fence", 0L }
            });
            return data;
        }
        public ProviderIdentityProof Proof() => new(Policy.TrustedIssuer, "provider-user", "invitee@example.invalid", true,
            Policy.TrustedConnectionId, Clock.UtcNow, Clock.UtcNow, "otp", Policy.AssurancePolicyVersion);
        public Task<SessionIssue> CreateAsync(ProviderIdentityProof? proof = null) => Unit.ExecuteAsync((tx, ct) =>
            Store.CreateAsync(tx, new(1, 1, 1, Policy.TrustedIssuer, "provider-user"), proof ?? Proof(), "private-provider-token", ct));
        public Task<SessionIssue> RotateAsync(SessionIssue old, ProviderIdentityProof proof, string challenge, DateTimeOffset started) =>
            Unit.ExecuteAsync((tx, ct) => Store.RotateForStepUpAsync(tx, old.Session.Reference, proof, "private-provider-token", challenge, started, ct));
        public ValueTask DisposeAsync() => new(client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName));
    }
}
