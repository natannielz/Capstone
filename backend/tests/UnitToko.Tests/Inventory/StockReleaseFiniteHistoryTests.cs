using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Inventory.StockReleaseBson;
using D = UnitToko.Tests.Inventory.StockReleaseServiceTests.Data;
using Q = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using V = UnitToko.Platform.Procurement.ProcurementStockValuationBson;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockReleaseFiniteHistoryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Same_finite_handle_reauthenticates_real_valued_release_without_business_writes() {
        await using var data = await D.CreateAsync(fixture);
        var proposed = await data.ProposeAsync("finite-history-release-proposed");
        var approved = await data.DecideAsync(proposed["proposalId"].AsInt64, "finite-history-release-approved");
        var row = await data.Rows(B.Decisions).Find(new BsonDocument("_id", approved["decisionId"])).SingleAsync();
        var service = data.NewService(); var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64);
        var before = await SnapshotAsync(data);
        await data.Unit.ExecuteAsync(async (tx, ct) => {
            var handle = service.RetainFiniteSourceHistory(tx, new ShipmentDispatchValueRecords.HistoricalRead(data.Database, data.Clock, tx));
            for (var read = 0; read < 2; read++) {
                handle.Require(service, data.Database, tx);
                await handle.AuthenticateAsync(data.Database, tx, source, row["causeHash"].AsString, ct);
            }
            return true;
        }, default);
        Assert.Equal("11250.00", approved["transferredValue"].AsString);
        Assert.Equal(before, await SnapshotAsync(data));
    }

    [WindowsStorageFact]
    public async Task Actual_native_finite_handle_refuses_foreign_owner_database_client_session_next_attempt_and_clock_regression() {
        await using var data = await D.CreateAsync(fixture);
        var proposed = await data.ProposeAsync("finite-context-release-proposed");
        var approved = await data.DecideAsync(proposed["proposalId"].AsInt64, "finite-context-release-approved");
        var row = await data.Rows(B.Decisions).Find(new BsonDocument("_id", approved["decisionId"])).SingleAsync();
        var service = data.NewService(); var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64);
        var before = await SnapshotAsync(data); var refusals = 0;
        void Broken(Action action) { Assert.Equal(B.Broken().Code, Assert.Throws<PersistenceConflictException>(action).Code); refusals++; }
        using var tx = await data.Database.Client.StartSessionAsync(); tx.StartTransaction();
        var handle = service.RetainFiniteSourceHistory(tx, new ShipmentDispatchValueRecords.HistoricalRead(data.Database, data.Clock, tx));
        try {
            await handle.AuthenticateAsync(data.Database, tx, source, row["causeHash"].AsString, default);
            handle.Require(service, data.Database, tx);
            Broken(() => handle.Require(data.NewService(), data.Database, tx));
            Broken(() => handle.Require(service, data.Database.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "-foreign"), tx));
            var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "release-finite-history-foreign-" + Guid.NewGuid().ToString("N");
            using var foreignClient = new MongoClient(settings);
            Broken(() => handle.Require(service, foreignClient.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), tx));
            using var otherSession = await data.Database.Client.StartSessionAsync(); otherSession.StartTransaction();
            try { Broken(() => handle.Require(service, data.Database, otherSession)); }
            finally { await otherSession.AbortTransactionAsync(); }
            using var foreignSession = await foreignClient.StartSessionAsync(); foreignSession.StartTransaction();
            try { Broken(() => handle.Require(service, data.Database, foreignSession)); }
            finally { await foreignSession.AbortTransactionAsync(); }
            var now = data.Clock.UtcNow;
            try { data.Clock.UtcNow = now.AddMilliseconds(-1); Broken(() => handle.Require(service, data.Database, tx)); }
            finally { data.Clock.UtcNow = now; }
            handle.Require(service, data.Database, tx);
            await tx.AbortTransactionAsync(); tx.StartTransaction();
            Broken(() => handle.Require(service, data.Database, tx));
        } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }
        Assert.Equal(7, refusals); Assert.Equal(before, await SnapshotAsync(data));
    }

    [WindowsStorageFact]
    public async Task Warm_same_handle_rechecks_actual_release_proposal_quality_value_membership_and_private_pin_with_full_rollback() {
        await using var data = await D.CreateAsync(fixture);
        var proposed = await data.ProposeAsync("finite-tamper-release-proposed");
        var approved = await data.DecideAsync(proposed["proposalId"].AsInt64, "finite-tamper-release-approved");
        var row = await data.Rows(B.Decisions).Find(new BsonDocument("_id", approved["decisionId"])).SingleAsync();
        var proposal = await data.Rows(B.Proposals).Find(new BsonDocument("_id", row["proposalId"])).SingleAsync();
        var quality = await data.Rows(Q.Allocations).Find(new BsonDocument("_id", row["input"]["qualityAllocationId"])).SingleAsync();
        var value = await data.Rows(V.Allocations).Find(new BsonDocument("_id", row["input"]["valuationAllocationId"])).SingleAsync();
        var inspection = await data.Rows("procurementInspectionSubmissions").Find(new BsonDocument("_id", data.Valuation.Documents.InspectionId)).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", inspection["pinId"])).SingleAsync();
        var service = data.NewService(); var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64);
        var before = await SnapshotAsync(data); var warmed = 0; var refusals = 0; var deletedPin = 0;
        async Task OwnerRefusal(Func<Task> attempt) {
            var failure = await Record.ExceptionAsync(attempt);
            Assert.True(failure is PersistenceConflictException or DomainValidationException or AuthorizationDeniedException, failure?.ToString());
            refusals++;
        }
        async Task Attempt() => await data.Unit.ExecuteAsync(async (tx, ct) => {
            var handle = service.RetainFiniteSourceHistory(tx, new ShipmentDispatchValueRecords.HistoricalRead(data.Database, data.Clock, tx));
            Task Authenticate() => handle.AuthenticateAsync(data.Database, tx, source, row["causeHash"].AsString, ct);
            await Authenticate(); warmed++;
            var changed = row.DeepClone().AsBsonDocument; changed["transferredValue"] = "0.01";
            Assert.Equal(1L, (await data.Rows(B.Decisions).ReplaceOneAsync(tx, new BsonDocument("_id", row["_id"]), changed, cancellationToken: ct)).ModifiedCount);
            Assert.Equal(B.Broken().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(Authenticate)).Code); refusals++;
            Assert.True(tx.IsInTransaction);
            Assert.Equal(1L, (await data.Rows(B.Decisions).ReplaceOneAsync(tx, new BsonDocument("_id", row["_id"]), row, cancellationToken: ct)).ModifiedCount);
            foreach (var (table, original) in new[] { (B.Proposals, proposal), (Q.Allocations, quality), (V.Allocations, value) }) {
                Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(tx, new BsonDocument("_id", original["_id"]), cancellationToken: ct)).DeletedCount);
                await OwnerRefusal(Authenticate);
                Assert.True(tx.IsInTransaction);
                await data.Rows(table).InsertOneAsync(tx, original, cancellationToken: ct);
            }
            foreach (var (table, original) in new[] { (Q.Allocations, quality), (V.Allocations, value) }) {
                // Negative orphan membership only: distinct unique keys permit
                // insertion, but no genuine proposal, decision or journal owns it.
                var orphan = original.DeepClone().AsBsonDocument;
                foreach (var field in table == Q.Allocations ? new[] { "_id", "proposalId", "decisionId", "sequence" }
                    : new[] { "_id", "proposalId", "decisionId", "journalId", "receiptSequence", "matchSequence" })
                    orphan[field] = original[field].AsInt64 + 100000;
                await data.Rows(table).InsertOneAsync(tx, orphan, new InsertOneOptions { BypassDocumentValidation = true }, ct);
                await OwnerRefusal(Authenticate);
                Assert.True(tx.IsInTransaction);
                Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(tx, new BsonDocument("_id", orphan["_id"]), cancellationToken: ct)).DeletedCount);
            }
            await Authenticate();
            Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(tx, new BsonDocument("_id", pin["_id"]), cancellationToken: ct)).DeletedCount); deletedPin++;
            // The genuine private owner refusal exits the unit of work so this
            // final negative deletion and every business effect roll back together.
            await handle.AuthenticateOriginAsync(data.Database, tx, source, row["causeHash"].AsString, new(row["input"]["receiptId"].AsInt64,
                row["input"]["qualityAllocationId"].AsInt64, row["input"]["valuationAllocationId"].AsInt64), ct);
            return true;
        }, default);
        await OwnerRefusal(Attempt);
        Assert.Equal(1, warmed); Assert.Equal(7, refusals); Assert.Equal(1, deletedPin);
        foreach (var (table, original) in new[] { (B.Decisions, row), (B.Proposals, proposal), (Q.Allocations, quality), (V.Allocations, value), ("evidencePins", pin) })
            Assert.Equal(original, await data.Rows(table).Find(new BsonDocument("_id", original["_id"])).SingleAsync());
        Assert.Equal(before, await SnapshotAsync(data));
    }

    private static async Task<BsonDocument> SnapshotAsync(D data) {
        var state = new BsonDocument { { "release", await data.BusinessEffectsAsync() }, { "valuation", await data.Valuation.StateAsync() },
            { "documents", await data.Valuation.Documents.StateAsync() } };
        foreach (var table in new[] { Q.Proposals, Q.Decisions, Q.Allocations, Q.ReceiptHeads, "evidencePins", "auditEvents", "outboxEvents", "idempotencyRecords" })
            state[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
}
