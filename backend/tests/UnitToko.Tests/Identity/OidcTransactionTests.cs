using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class OidcTransactionTests(MongoFixture fixture)
{
    private static OidcTransactionRequest Login() => new(OidcTransactionPurpose.Login, Auth0ClaimsTests.Settings().Issuer, Auth0ClaimsTests.Settings().ClientId, "/");
    [Fact]
    public async Task Durable_state_contains_hashes_not_secrets_and_is_consumed_once_with_the_session_transaction()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var store = new OidcTransactionStore(data.Database, data.Clock); var unit = new MongoUnitOfWork(fixture.Client);
        var start = await store.BeginAsync(Login() with { InvitationId = 9007199254740993L, InvitationSecretHash = new string('a', 64) });
        var persisted = await data.Database.GetCollection<BsonDocument>("oidcTransactions").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(OidcTransactionStore.Hash(start.StateToken), persisted["_id"].AsString);
        Assert.Equal(OidcTransactionStore.Hash(start.ChallengeBinding), persisted["challengeHash"].AsString);
        Assert.DoesNotContain(start.StateToken, persisted.ToJson()); Assert.DoesNotContain(start.ChallengeBinding, persisted.ToJson());
        Assert.DoesNotContain(start.StateToken, start.ToString()); Assert.True(persisted["invitationId"].IsInt64);
        Assert.True(OidcTransactionStore.MatchesChallenge(await store.ReadPendingAsync(start.StateToken), start.ChallengeBinding));
        await unit.ExecuteAsync(async (session, token) => { await store.ConsumeAsync(await store.ReadPendingAsync(start.StateToken, session, token), session, token); return true; });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => store.ReadPendingAsync(start.StateToken));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => unit.ExecuteAsync(async (session, token) => { await store.ConsumeAsync(start.Transaction, session, token); return true; }));
    }
    [Fact]
    public async Task Failed_session_issue_rolls_back_state_consumption_and_concurrent_consumption_has_one_winner()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var store = new OidcTransactionStore(data.Database, data.Clock); var unit = new MongoUnitOfWork(fixture.Client);
        var start = await store.BeginAsync(Login());
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteAsync<bool>(async (session, token) =>
        { await store.ConsumeAsync(start.Transaction, session, token); throw new InvalidOperationException("Synthetic session issue failure"); }));
        Assert.NotNull(await store.ReadPendingAsync(start.StateToken));
        async Task<bool> Consume()
        {
            try { return await unit.ExecuteAsync(async (session, token) => { await store.ConsumeAsync(start.Transaction, session, token); return true; }); }
            catch (PersistenceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Consume(), Consume()); Assert.Single(results, result => result);
    }
    [Fact]
    public async Task Expiration_is_enforced_without_waiting_for_TTL_and_stepup_binds_all_session_versions()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var store = new OidcTransactionStore(data.Database, data.Clock);
        var stepup = new OidcTransactionRequest(OidcTransactionPurpose.StepUp, Login().Issuer, Login().ClientId, "/administration/access",
            9007199254740993L, new(new string('c', 64), 7, 9), 11, "auth0|subject", "access.approve");
        await Assert.ThrowsAsync<DomainValidationException>(() => store.BeginAsync(stepup with { ExpectedGrantVersion = null }));
        await Assert.ThrowsAsync<DomainValidationException>(() => store.BeginAsync(stepup with { ExpectedSession = null }));
        var start = await store.BeginAsync(stepup);
        var loaded = await store.ReadPendingAsync(start.StateToken); Assert.Equal(stepup, loaded.Request);
        data.Clock.UtcNow = loaded.ExpiresAt;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => store.ReadPendingAsync(start.StateToken));
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("oidcTransactions").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
