using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentDispatchLiveValueVerificationTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_live_publication_refuses_wrong_source_kind_or_altered_whole_row_with_exact_rollback(bool alteredWholeRow) {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, reviewedSafetyBufferBaseUnits: 0);
        var review = await data.PrepareDispatchAsync(1, "publication-mismatch-preparation");
        var names = (await data.BusinessAsync()).Names.Concat(["inventoryPools", "authorizationScopes"]).Distinct().ToArray();
        async Task<BsonDocument> ExactAsync() {
            var snapshot = new BsonDocument();
            foreach (var name in names) snapshot.Add(name, new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            return snapshot;
        }
        var before = await ExactAsync(); var valuation = await data.Stock.Valuation.StateAsync(); var reached = 0;
        const string key = "publication-mismatch-original";
        data.Admission.BeforeComplete = async (tx, ct) => {
            var row = await data.Rows("shipments").Find(tx, new BsonDocument { { "suborderId", data.SuborderId }, { "phase", "committed" } }).SingleOrDefaultAsync(ct);
            if (row is null || Interlocked.CompareExchange(ref reached, 1, 0) != 0) return;
            var live = data.Dispatch.LiveRead(tx, row["_id"].AsInt64);
            var capture = await data.Dispatch.RequireLiveValueCaptureAsync(data.Database, data.Clock, tx, row, ct);
            var currentHead = await data.Rows(LotValueStore.AvailableHeads).Find(tx, new BsonDocument("_id", data.BalanceId)).SingleAsync(ct);
            var currentHistory = await data.Rows(LotValueStore.Transitions).Find(tx, new BsonDocument("$or", new BsonArray {
                new BsonDocument("availableId", data.BalanceId), new BsonDocument("destinationId", data.BalanceId) })).ToListAsync(ct);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Dispatch.AuthenticateValueHeadAsync(data.Database, tx,
                row["_id"].AsInt64, data.BalanceId, row["causeHash"].AsString, currentHead, currentHistory, ct));
            var actual = await OrderSubmissionService.ReadStockSourceEventAsync(data.Database, tx, "shipment", row["_id"].AsInt64, false, ct,
                Assert.Single(capture.Lots).ValueHistory, data.Clock, live);
            live.RequirePublicationSource(data.Database, data.Clock, tx, actual);
            OrderStockSourceEvent mismatched;
            if (alteredWholeRow) {
                // Negative copy only: no foreign/positive shipment or proof is
                // inserted, and the genuine ID/kind remain unchanged.
                var changed = actual.Record.DeepClone().AsBsonDocument;
                changed["journalFingerprint"] = new string('0', 64);
                Assert.NotEqual(actual.Record, changed); mismatched = actual with { Record = changed };
            } else mismatched = await OrderSubmissionService.ReadStockSourceEventAsync(data.Database, tx, "submission", data.OrderId, true, ct);
            // Exit the real owning command so its physical/value/journal/source
            // writes and fences must all roll back together.
            live.RequirePublicationSource(data.Database, data.Clock, tx, mismatched);
        };
        try {
            var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.DispatchAsync(review, key));
            Assert.Equal("shipment_unavailable", denied.Code);
        } finally { data.Admission.BeforeComplete = null; }
        Assert.Equal(1, reached); Assert.Equal(before, await ExactAsync()); Assert.Equal(valuation, await data.Stock.Valuation.StateAsync());
        Assert.Null(await data.Dispatch.RecoverAsync(data.Dispatcher, key));
    }
    [WindowsStorageFact]
    public async Task Actual_live_final_verifier_reuses_owned_history_but_refuses_foreign_rows_and_changed_acquisition_with_whole_rollback() {
        // This fixture's admissions are explicitly test-only. The stock, actual
        // reserved order, dispatch transaction, finite consume and journal owners
        // are genuine; no positive shipment/value/effect rows are inserted here.
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, reviewedSafetyBufferBaseUnits: 0);
        var review = await data.PrepareDispatchAsync(1, "live-verification-preparation");
        var originalValue = await data.Rows(ProcurementStockValuationBson.Decisions).Find(new BsonDocument("decision", "approve")).SingleAsync();
        var before = await data.BusinessAsync(); var valuation = await data.Stock.Valuation.StateAsync();
        var warmed = 0; var refused = 0; var changed = 0; BsonDocument? retainedRow = null;
        data.Admission.BeforeComplete = async (tx, ct) => {
            var row = await data.Rows("shipments").Find(tx, new BsonDocument { { "suborderId", data.SuborderId }, { "phase", "committed" } }).SingleOrDefaultAsync(ct);
            if (row is null || Interlocked.CompareExchange(ref warmed, 1, 0) != 0) return;
            retainedRow = row.DeepClone().AsBsonDocument;
            var capture = await data.Dispatch.RequireLiveValueCaptureAsync(data.Database, data.Clock, tx, row, ct);
            var lot = Assert.Single(capture.Lots);
            var effect = Assert.Single(row["valueEffects"].AsBsonArray);
            var transition = await data.Rows("lotValueTransitions").Find(tx, new BsonDocument("_id", effect["transitionId"])).SingleAsync(ct);
            // This command's new consume cannot substitute for the exact prior
            // source authenticated when the finite capture was created.
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => lot.RequireConsumedInventoryAccountAsync(
                data.Database, tx, lot.Before, lot.Value.Revision, ShipmentDispatchService.ValueSource(row["_id"].AsInt64, lot.Before.Id),
                row["causeHash"].AsString, transition, ct));
            var liveRead = data.Dispatch.LiveRead(tx, row["_id"].AsInt64);
            Assert.Equal(row, await new ShipmentDispatchRecords(data.Database, data.Clock, liveRead: liveRead).ReadAsync(tx, row["_id"].AsInt64, ct));
            Assert.Same(capture, await data.Dispatch.RequireLiveValueCaptureAsync(data.Database, data.Clock, tx, row, ct));
            var foreign = new ShipmentDispatchService(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, data.Preparations, data.Admission);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ShipmentDispatchValueRecords.VerifyLiveAsync(foreign, data.Database, data.Clock, tx, row, ct)); refused++;
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Dispatch.RequireLiveValueCaptureAsync(
                data.Database.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "-foreign"), data.Clock, tx, row, ct)); refused++;
            var alteredRow = row.DeepClone().AsBsonDocument; alteredRow["journalFingerprint"] = new string('0', 64);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Dispatch.RequireLiveValueCaptureAsync(data.Database, data.Clock, tx, alteredRow, ct)); refused++;
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => liveRead.VerifyAsync(data.Database, data.Clock, tx, alteredRow, ct));
            var corrupted = originalValue.DeepClone().AsBsonDocument;
            var changedInventoryId = checked(corrupted["accounting"]["inventory"]["accountId"].AsInt64 + 100000);
            Assert.NotEqual(corrupted["policy"]["receiptAccrualAccountId"].AsInt64, changedInventoryId);
            // Keep the local schema tuple consistent, but leave the genuine
            // approval hash stale so the owner must authenticate and refuse it.
            corrupted["accounting"]["inventory"]["accountId"] = changedInventoryId;
            corrupted["policy"]["inventoryAccountId"] = changedInventoryId;
            Assert.Equal(originalValue["snapshotHash"], corrupted["snapshotHash"]);
            Assert.NotEqual(corrupted["snapshotHash"].AsString, ProcurementStockValuationBson.Hash(corrupted));
            Assert.Equal(1L, (await data.Rows(ProcurementStockValuationBson.Decisions).ReplaceOneAsync(tx, new BsonDocument("_id", originalValue["_id"]),
                corrupted, cancellationToken: ct)).ModifiedCount); changed++;
            // The live mapping must still agree with the freshly authenticated
            // original acquisition. This refusal exits the actual owning command.
            await ShipmentDispatchValueRecords.VerifyLiveAsync(data.Dispatch, data.Database, data.Clock, tx, row, ct);
        };
        try {
            var failure = await Record.ExceptionAsync(() => data.DispatchAsync(review, "live-verification-original"));
            Assert.True(failure is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, failure?.ToString());
        } finally { data.Admission.BeforeComplete = null; }
        Assert.Equal(1, warmed); Assert.Equal(3, refused); Assert.Equal(1, changed); Assert.NotNull(retainedRow);
        Assert.Equal(before, await data.BusinessAsync()); Assert.Equal(valuation, await data.Stock.Valuation.StateAsync());
        Assert.Equal(originalValue, await data.Rows(ProcurementStockValuationBson.Decisions).Find(new BsonDocument("_id", originalValue["_id"])).SingleAsync());
        Assert.Null(await data.Dispatch.RecoverAsync(data.Dispatcher, "live-verification-original"));
        await data.Unit.ExecuteAsync(async (tx, ct) => {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ShipmentDispatchValueRecords.VerifyLiveAsync(
                data.Dispatch, data.Database, data.Clock, tx, retainedRow!, ct));
            return true;
        });
    }
}
