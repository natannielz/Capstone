using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;
using D = UnitToko.Tests.Inventory.StockSourceReconciliationTestData;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockSourceReconciliationFenceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Fresh_rejection_fences_the_current_pool_once_even_for_an_old_observation_and_invalidates_an_older_pool_CAS()
    {
        await using var data = await D.CreateAsync(fixture);
        var first = await data.ProposeAsync("rejection-fence-first");
        // A second genuine proposal advances the shared pool; the first baseline is now stale.
        await data.ProposeAsync("rejection-fence-second");
        data.Clock.UtcNow = data.Clock.UtcNow.AddMilliseconds(60002);
        var proposalId = first["proposalId"].AsInt64;
        var preview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, proposalId,
            StockSourceReconciliationDecision.Reject, null);
        Assert.True(preview["baseline"].IsBsonNull);
        var before = await PoolAsync(data);
        var original = await data.Rows(B.Proposals).Find(new BsonDocument("_id", proposalId)).SingleAsync();
        Assert.True(before["fence"].AsInt64 > original["committedPoolFence"].AsInt64);
        Assert.True(data.Clock.UtcNow - new DateTimeOffset(original["observation"]["observedAt"].ToUniversalTime()) >
            TimeSpan.FromMilliseconds(original["baseline"]["maximumAgeMilliseconds"].AsInt64));
        var protectedState = await data.ProtectedAsync();

        using var older = await fixture.Client.StartSessionAsync();
        older.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try
        {
            var olderPool = await data.Rows("inventoryPools").Find(older, new BsonDocument("_id", data.PoolId)).SingleAsync();
            Assert.Equal(before, olderPool);
            var result = await data.Service.DecideReconciliationAsync(data.Checker, proposalId, 1,
                StockSourceReconciliationDecision.Reject, null, preview["binding"].AsString, D.Meta("rejection-fence-decided"));
            Assert.Equal("rejected", result["status"].AsString);
            var after = before.DeepClone().AsBsonDocument;
            after["fence"] = checked(before["fence"].AsInt64 + 1);
            Assert.Equal(after, await PoolAsync(data));
            Assert.Equal(protectedState, await data.ProtectedAsync());
            var committedState = await data.StateAsync();
            Assert.Equal(1L, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument("proposalId", proposalId)));
            foreach (var (table, kind, owner) in new[] {
                ("auditEvents", "operation", "record.proposalId"),
                ("outboxEvents", "kind", "payload.proposalId"),
                ("idempotencyRecords", "operation", "result.proposalId") })
                Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument { { kind, B.DecideOperation }, { owner, proposalId } }));

            // Whole-command replay and original-key recovery must not fence the pool again.
            Assert.Equal(result, await data.Service.DecideReconciliationAsync(data.Checker, proposalId, 1,
                StockSourceReconciliationDecision.Reject, null, preview["binding"].AsString, D.Meta("rejection-fence-decided")));
            Assert.Equal(result, await data.Service.RecoverReconciliationAsync(data.Checker, B.DecideOperation, "rejection-fence-decided"));
            Assert.Equal(after, await PoolAsync(data));
            Assert.Equal(committedState, await data.StateAsync());
            Assert.Equal(protectedState, await data.ProtectedAsync());

            // This is a real earlier snapshot of the actual pool, not a fake obligation gate/head.
            var conflict = await Assert.ThrowsAnyAsync<MongoException>(() => data.Rows("inventoryPools").UpdateOneAsync(older,
                olderPool, Builders<BsonDocument>.Update.Inc("fence", 1L)));
            Assert.True(conflict.HasErrorLabel("TransientTransactionError"));
            await InventoryRepository.AbortAsync(older);
            Assert.False(older.IsInTransaction);
            Assert.Equal(after, await PoolAsync(data));
            Assert.Equal(committedState, await data.StateAsync());
        }
        finally { await InventoryRepository.AbortAsync(older); }
    }

    [WindowsStorageFact]
    public async Task Missing_stale_revoked_and_final_MFA_expired_rejections_leave_no_pool_fence_or_decision_effect()
    {
        await using var data = await D.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("rejection-rollback-proposal");
        var id = proposal["proposalId"].AsInt64;
        var preview = await data.Service.PreviewReconciliationDecisionAsync(data.Checker, id,
            StockSourceReconciliationDecision.Reject, null);
        var binding = preview["binding"].AsString;
        var pool = await PoolAsync(data);
        var state = await data.StateAsync();
        var protectedState = await data.ProtectedAsync();
        await RefusedAsync(data.Checker, long.MaxValue, 1, "rejection-missing-target");
        await RefusedAsync(data.Checker, id, 2, "rejection-stale-revision");
        await RefusedAsync(data.Checker with { GrantVersion = data.Checker.GrantVersion + 1 }, id, 1, "rejection-stale-actor");
        await RefusedAsync(data.Outsider, id, 1, "rejection-foreign-scope");

        var grantFilter = new BsonDocument("_id", data.Maker.UserId);
        var grant = await data.Rows("authorizationGrants").Find(grantFilter).SingleAsync();
        Assert.Equal(1L, (await data.Rows("authorizationGrants").UpdateOneAsync(grantFilter,
            Builders<BsonDocument>.Update.Set("status", "revoked"))).ModifiedCount);
        try { await RefusedAsync(data.Checker, id, 1, "rejection-revoked-maker"); }
        finally { Assert.Equal(1L, (await data.Rows("authorizationGrants").ReplaceOneAsync(grantFilter, grant)).ModifiedCount); }

        // Cross the actual checker MFA deadline only after the genuine pool write and outbox insert
        // succeeded inside the owner transaction. The final authority check must roll back both.
        var at = data.Clock.UtcNow;
        var poolRequest = 0; var poolWritten = 0; var outboxRequest = 0; var expired = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (value.DatabaseNamespace.DatabaseName != data.Database.DatabaseNamespace.DatabaseName) return;
                if (poolRequest == 0 && value.Command.GetValue("update", BsonNull.Value) == "inventoryPools") poolRequest = value.RequestId;
                if (outboxRequest == 0 && value.Command.GetValue("insert", BsonNull.Value) == "outboxEvents" &&
                    value.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("kind", BsonNull.Value) == B.DecideOperation))
                    outboxRequest = value.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(value => {
                if (poolRequest != 0 && value.RequestId == poolRequest) Interlocked.Exchange(ref poolWritten, 1);
                if (outboxRequest != 0 && value.RequestId == outboxRequest && Interlocked.CompareExchange(ref expired, 1, 0) == 0)
                    data.Clock.UtcNow = at.AddMinutes(6);
            });
        };
        var client = new MongoClient(settings);
        var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new StockSourceSetupService(database, new MongoUnitOfWork(client), new(database), data.Clock);
        try
        {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.DecideReconciliationAsync(data.Checker, id, 1,
                StockSourceReconciliationDecision.Reject, null, binding, D.Meta("rejection-final-mfa-expired")));
            Assert.Equal(1, poolWritten);
            Assert.Equal(1, expired);
            await UnchangedAsync();
        }
        finally { data.Clock.UtcNow = at; }

        async Task RefusedAsync(ActorContext actor, long proposalId, long revision, string key)
        {
            var error = await Record.ExceptionAsync(() => data.Service.DecideReconciliationAsync(actor, proposalId, revision,
                StockSourceReconciliationDecision.Reject, null, binding, D.Meta(key)));
            Assert.True(error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException,
                error?.ToString() ?? "Unexpected reconciliation rejection success");
            await UnchangedAsync();
        }
        async Task UnchangedAsync()
        {
            Assert.Equal(pool, await PoolAsync(data));
            Assert.Equal(state, await data.StateAsync());
            Assert.Equal(protectedState, await data.ProtectedAsync());
        }
    }

    private static Task<BsonDocument> PoolAsync(D data) =>
        data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
}
