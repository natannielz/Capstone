using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonApprovalSessionTests(MongoFixture fixture)
{
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static AuthorizationCommandMetadata Metadata => new("person-session", "Synthetic independently reviewed approval", "session-final-boundary");

    [Theory]
    [InlineData("idle", "receipt-result", false)]
    [InlineData("absolute", "receipt-result", false)]
    [InlineData("factor", "receipt-result", false)]
    [InlineData("idle", "final-session-read", false)]
    [InlineData("absolute", "final-session-read", false)]
    [InlineData("factor", "final-session-read", false)]
    [InlineData("idle", "final-session-read", true)]
    [InlineData("absolute", "final-session-read", true)]
    [InlineData("factor", "final-session-read", true)]
    public async Task Expiry_after_final_database_work_prevents_new_effect_or_replay_result(string expiry, string boundary, bool replay)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        data.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // Session expiry is isolated from factor freshness. Payment verification now requires
        // five-minute MFA, so it cannot reach a 29-minute idle or eight-hour absolute boundary.
        // Reconciliation retains the same recorded-by/current-person approval predicate without
        // an MFA age limit; dedicated payment verification tests cover its stricter deadline.
        var permission = expiry == "factor" ? Permissions.RefundApprove : Permissions.ReconciliationVerify;
        if (expiry != "factor") Assert.Null(Permissions.Find(permission)!.Assurance.MaximumAge);
        var actor = await PrepareAsync(data, permission, expiry);
        var expiresAt = data.Clock.UtcNow + (expiry == "factor" ? TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(1) : TimeSpan.FromMinutes(1));
        if (replay)
            Assert.Equal(2L, (await ApproveAsync(data.Store, data.Database, actor, permission))["version"].AsInt64);

        var beforeIdentity = await RowAsync(data.Database, "identities", actor.UserId);
        var beforeSession = await data.Database.GetCollection<BsonDocument>("identitySessions")
            .Find(new BsonDocument("_id", actor.Session!.Id)).SingleAsync();
        var beforePerson = await RowAsync(data.Database, "personAccountBindings", actor.UserId);
        var beforeReceipts = await data.Database.GetCollection<BsonDocument>("idempotencyRecords").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        var selectedRequestId = 0;
        var observedBoundary = false;
        var observedPersonFence = false;
        var observedReceiptResult = false;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.Command.TryGetValue("update", out var collection))
                {
                    if (collection == "personAccountBindings") observedPersonFence = true;
                    if (collection == "idempotencyRecords" &&
                        command.Command["updates"].AsBsonArray.Any(update =>
                            update["u"].AsBsonDocument.TryGetValue("$set", out var fields) &&
                            fields.AsBsonDocument.Contains("result")))
                    {
                        observedReceiptResult = true;
                        if (boundary == "receipt-result") selectedRequestId = command.RequestId;
                    }
                }
                if (boundary == "final-session-read" &&
                    command.Command.TryGetValue("find", out var findCollection) && findCollection == "identitySessions" &&
                    command.Command.TryGetValue("projection", out var projection) &&
                    projection.AsBsonDocument.Contains("idleExpiresAt") &&
                    projection.AsBsonDocument.Contains("absoluteExpiresAt"))
                    selectedRequestId = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command =>
            {
                if (command.RequestId != selectedRequestId || observedBoundary) return;
                observedBoundary = true;
                // This runs after the server has accepted the receipt write, or after it has
                // returned a live session based on the earlier clock. No sleeps or scheduling race.
                data.Clock.UtcNow = expiresAt;
            });
        };
        var client = new MongoClient(settings);
        var observedDatabase = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var store = new AuthorizationStore(observedDatabase, new(client), data.Clock);
        var writes = 0;
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            ApproveAsync(store, observedDatabase, actor, permission, () => writes++));

        Assert.True(observedBoundary, "The selected final database operation must complete before expiry.");
        Assert.True(observedPersonFence, "The operation must reach current person fencing before the final boundary.");
        Assert.Equal(!replay, observedReceiptResult);
        Assert.Equal(replay ? 0 : 1, writes);
        Assert.Equal(expiry == "factor" ? "step_up_required" : "stale_or_inactive_session", denied.Code);
        var payment = await RowAsync(data.Database, "payments", 1);
        Assert.Equal(replay ? 2L : 1L, payment["version"].AsInt64);
        Assert.Equal(replay, payment.Contains("approved"));
        var afterReceipts = await data.Database.GetCollection<BsonDocument>("idempotencyRecords").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.Equal(beforeReceipts, afterReceipts);
        Assert.Equal(beforeIdentity["fence"], (await RowAsync(data.Database, "identities", actor.UserId))["fence"]);
        Assert.Equal(beforePerson["fence"], (await RowAsync(data.Database, "personAccountBindings", actor.UserId))["fence"]);
        var afterSession = await data.Database.GetCollection<BsonDocument>("identitySessions")
            .Find(new BsonDocument("_id", actor.Session.Id)).SingleAsync();
        Assert.Equal(beforeSession["fence"], afterSession["fence"]);
    }

    private async Task<ActorContext> PrepareAsync(StoreTestData data, string permission, string expiry)
    {
        var actor = await data.AddActorAsync(1000, Shop, permission);
        await data.AddActorAsync(2000, Shop, Permissions.PaymentRecord);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, actor.UserId, 2000);
        await data.Database.GetCollection<BsonDocument>("payments").InsertOneAsync(new BsonDocument
        {
            { "_id", 1L }, { "schemaVersion", 1 }, { "version", 1L }, { "recordedBy", 2000L }, { "requestedBy", 2000L },
            { "fulfillmentOrganizationId", 100L }, { "shopId", 101L }, { "channelId", 103L }
        });
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId),
            Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var unit = new MongoUnitOfWork(fixture.Client);
        var sessions = new MongoIdentitySessionStore(data.Database, unit, data.Clock,
            new(actor.ProviderIssuer, "fixture", "fixture"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "fixture@example.test", true,
            "fixture", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture");
        var issued = await unit.ExecuteAsync((tx, token) => sessions.CreateAsync(tx,
            new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "fixture", token));
        actor = actor with { Session = issued.Session.Reference };
        // All persisted dates originate from the real session store; a fresh cookie never
        // manufactures a newer factor. Tests move only the injected application clock.
        if (expiry == "absolute")
        {
            var beforeAbsoluteExpiry = data.Clock.UtcNow + SessionPolicy.AbsoluteLifetime - TimeSpan.FromMinutes(1);
            while (data.Clock.UtcNow < beforeAbsoluteExpiry)
            {
                var next = data.Clock.UtcNow + TimeSpan.FromMinutes(29);
                data.Clock.UtcNow = next < beforeAbsoluteExpiry ? next : beforeAbsoluteExpiry;
                Assert.NotNull(await sessions.ReadAsync(issued.OpaqueToken));
            }
            var persisted = await data.Database.GetCollection<BsonDocument>("identitySessions")
                .Find(new BsonDocument("_id", actor.Session.Id)).SingleAsync();
            Assert.Equal(data.Clock.UtcNow.AddMinutes(1).UtcDateTime, persisted["absoluteExpiresAt"].ToUniversalTime());
        }
        else if (permission == Permissions.RefundApprove) data.Clock.UtcNow += TimeSpan.FromMinutes(4);
        else data.Clock.UtcNow += TimeSpan.FromMinutes(29);
        return actor;
    }

    private static Task<BsonDocument> ApproveAsync(AuthorizationStore store, IMongoDatabase database, ActorContext actor,
        string permission, Action? onWrite = null)
    {
        var payments = database.GetCollection<BsonDocument>("payments");
        return store.ExecuteAuthorizedAsync(actor, "fixture.person.approve", permission, Metadata, new { Id = 1L, ExpectedVersion = 1L },
            async (session, token) =>
            {
                var row = await payments.Find(session, new BsonDocument("_id", 1L)).SingleAsync(token);
                return new(AuthorizationBson.Resource(row), new(row["version"].AsInt64, 1,
                    RecordedBy: row["recordedBy"].AsInt64, RequestedBy: row["requestedBy"].AsInt64));
            }, async (session, token) =>
            {
                onWrite?.Invoke();
                await MongoUnitOfWork.RequireVersionAsync(payments, session, new BsonDocument("_id", 1L), 1,
                    Builders<BsonDocument>.Update.Set("approved", true), token);
                return new BsonDocument("version", 2L);
            });
    }

    private static Task<BsonDocument> RowAsync(IMongoDatabase database, string collection, long id) =>
        database.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", id)).SingleAsync();
}
