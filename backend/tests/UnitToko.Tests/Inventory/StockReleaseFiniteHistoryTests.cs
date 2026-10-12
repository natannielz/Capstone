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

    [WindowsStorageFact]
    public async Task Release_conversion_history_keeps_original_receipt_and_rechecks_warmed_rows_membership_and_private_pins() {
        await using var prepared = await PackConversionServiceTests.PreparedConversion.CreateAsync(fixture);
        var data = prepared.Data; var service = data.NewService();
        var conversion = await data.Rows(PackConversionBson.Conversions).Find(new BsonDocument("_id", prepared.Result!["conversionId"])).SingleAsync();
        var selected = PackConversionBson.Source(conversion["_id"].AsInt64, conversion["before"]["physicalPoolId"].AsInt64);
        var receipt = await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", data.Valuation.ReceiptId)).SingleAsync();
        var inspection = await data.Rows("procurementInspectionSubmissions").Find(new BsonDocument("_id", data.Valuation.Documents.InspectionId)).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", inspection["pinId"])).SingleAsync();
        var destination = await data.Rows("stockBalances").Find(new BsonDocument("_id", prepared.Result["destinationBalanceId"])).SingleAsync();
        var before = await SnapshotAsync(data); var convertedBefore = await prepared.StateAsync();
        var warmed = 0; var refused = 0;
        foreach (var fault in new[] { "none", "receipt", "destination-membership", "private-pin" }) {
            async Task Attempt() => await data.Unit.ExecuteAsync(async (tx, ct) => {
                var handle = service.RetainFiniteSourceHistory(tx, new ShipmentDispatchValueRecords.HistoricalRead(data.Database, data.Clock, tx));
                // This is the actual release owner capture that the conversion
                // reader must keep, including its earlier private evidence pins.
                var original = await handle.ReadOriginAsync(data.Database, tx,
                    new(data.Valuation.ReceiptId, data.QualityAllocationId, data.ValueAllocationId), ct);
                Assert.Equal(receipt, original.Origin.Receipt);
                // A separate genuine release owner consumes the original owner's lease;
                // its own valuation/PO service instances cannot inherit donor proofs.
                var nested = data.NewService().RetainFiniteSourceHistory(tx,
                    new ShipmentDispatchValueRecords.HistoricalRead(data.Database, data.Clock, tx),
                    handle.RetainReceiptOrigins(data.Database, tx));
                Task Authenticate() => nested.AuthenticateAsync(data.Database, tx, selected, conversion["causeHash"].AsString, ct);
                await Authenticate(); warmed++;
                if (fault == "none") {
                    await Authenticate();
                    Assert.Equal(receipt, original.Origin.Receipt);
                    var lease = new StockReleaseService.ReceiptOriginRead(service, tx, (id, _) => {
                        Assert.Equal(original.Origin.ReceiptId, id); return Task.FromResult(original.Origin);
                    });
                    Assert.Same(original.Origin, await lease.ReadAsync(data.Database, data.Clock, tx, data.Valuation.ReceiptId, ct));
                    void Denied(Action action) => Assert.Equal(B.Broken().Code, Assert.Throws<PersistenceConflictException>(action).Code);
                    Denied(() => lease.Require(data.Database.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "-foreign"), data.Clock, tx));
                    Denied(() => lease.Require(data.Database, new UnitToko.Tests.Authorization.MutableAuthorizationClock(data.Clock.UtcNow), tx));
                    using var other = await data.Database.Client.StartSessionAsync(cancellationToken: ct); other.StartTransaction();
                    try { Denied(() => lease.Require(data.Database, data.Clock, other)); }
                    finally { await other.AbortTransactionAsync(ct); }
                    var clockBefore = data.Clock.UtcNow; var systemClock = data.Clock.UseSystemClock;
                    try {
                        data.Clock.UseSystemClock = false; data.Clock.UtcNow = clockBefore.AddDays(-1);
                        Denied(() => lease.Require(data.Database, data.Clock, tx));
                    } finally { data.Clock.UtcNow = clockBefore; data.Clock.UseSystemClock = systemClock; }
                    lease.Require(data.Database, data.Clock, tx);
                    return true;
                }
                if (fault == "receipt") {
                    var changed = receipt.DeepClone().AsBsonDocument; changed["reason"] = "Unowned warmed receipt rewrite";
                    changed["input"]["reason"] = changed["reason"];
                    Assert.Equal(1L, (await data.Rows("procurementStockReceipts").ReplaceOneAsync(tx,
                        new BsonDocument("_id", receipt["_id"]), changed, cancellationToken: ct)).ModifiedCount);
                } else if (fault == "destination-membership") {
                    var orphan = destination.DeepClone().AsBsonDocument;
                    orphan["_id"] = destination["_id"].AsInt64 + 100000;
                    orphan["skuId"] = destination["skuId"].AsInt64 + 100000;
                    await data.Rows("stockBalances").InsertOneAsync(tx, orphan, new InsertOneOptions { BypassDocumentValidation = true }, ct);
                } else {
                    Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(tx,
                        new BsonDocument("_id", pin["_id"]), cancellationToken: ct)).DeletedCount);
                }
                await Authenticate();
                return true;
            }, default);
            if (fault == "none") await Attempt();
            else {
                var failure = await Record.ExceptionAsync(Attempt);
                Assert.True(failure is PersistenceConflictException or DomainValidationException or AuthorizationDeniedException, failure?.ToString());
                refused++;
            }
        }
        Assert.Equal(4, warmed); Assert.Equal(3, refused);
        Assert.Equal(receipt, await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", receipt["_id"])).SingleAsync());
        Assert.Equal(pin, await data.Rows("evidencePins").Find(new BsonDocument("_id", pin["_id"])).SingleAsync());
        Assert.Equal(before, await SnapshotAsync(data)); Assert.Equal(convertedBefore, await prepared.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Finite_root_axis_joins_original_release_and_conversion_and_rechecks_private_sources() {
        await using var prepared = await PackConversionServiceTests.PreparedConversion.CreateAsync(fixture);
        var data = prepared.Data;
        var physical = InventoryBson.ReadBalance(await data.BalanceAsync());
        var value = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", physical.Id)).SingleAsync();
        // This real source axis has been released and then converted out. Zero
        // availability grants no ordering quantity, but its history must still join.
        Assert.Equal(0L, value["remainingPackages"].AsInt64);
        var receipt = await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", data.Valuation.ReceiptId)).SingleAsync();
        var inspection = await data.Rows("procurementInspectionSubmissions").Find(new BsonDocument("_id", data.Valuation.Documents.InspectionId)).SingleAsync();
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("_id", inspection["pinId"])).SingleAsync();
        var before = await SnapshotAsync(data); var convertedBefore = await prepared.StateAsync();
        var warmed = 0; var refused = 0;
        foreach (var fault in new[] { "none", "receipt", "private-pin" }) {
            async Task Attempt() => await data.Unit.ExecuteAsync(async (tx, ct) => {
                var source = new ShipmentDispatchFiniteSource(data.Database, data.Unit, new(data.Database), data.Clock);
                var capture = await source.ReadAsync(tx, physical, ct);
                Assert.Equal(physical, capture.Before);
                Assert.Equal(0L, capture.Value.RemainingPackages);
                Assert.Equal(value["remainingValue"].AsString, capture.Value.RemainingValue.ToString());
                warmed++;
                if (fault == "receipt") {
                    var changed = receipt.DeepClone().AsBsonDocument; changed["reason"] = "Unowned finite root receipt rewrite";
                    changed["input"]["reason"] = changed["reason"];
                    Assert.Equal(1L, (await data.Rows("procurementStockReceipts").ReplaceOneAsync(tx,
                        new BsonDocument("_id", receipt["_id"]), changed, cancellationToken: ct)).ModifiedCount);
                } else if (fault == "private-pin") {
                    Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(tx,
                        new BsonDocument("_id", pin["_id"]), cancellationToken: ct)).DeletedCount);
                }
                var current = await capture.ReadCurrentValueWithOriginAsync(data.Database, tx, ct);
                Assert.Equal(physical, current.Physical); Assert.Equal(capture.Value, current.Value);
                return true;
            }, default);
            if (fault == "none") await Attempt();
            else {
                var failure = await Record.ExceptionAsync(Attempt);
                Assert.True(failure is PersistenceConflictException or DomainValidationException or AuthorizationDeniedException, failure?.ToString());
                refused++;
            }
        }
        Assert.Equal(3, warmed); Assert.Equal(2, refused);
        Assert.Equal(receipt, await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", receipt["_id"])).SingleAsync());
        Assert.Equal(pin, await data.Rows("evidencePins").Find(new BsonDocument("_id", pin["_id"])).SingleAsync());
        Assert.Equal(before, await SnapshotAsync(data)); Assert.Equal(convertedBefore, await prepared.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Current_ancestry_handoff_keeps_first_origin_and_rechecks_quality_accounting_and_private_pin() {
        await using var data = await D.CreateAsync(fixture, useSystemClock: true);
        var observed = data.Clock.UtcNow; data.Clock.UseSystemClock = false; data.Clock.UtcNow = observed;
        // Only an actual release gives the current movement owner an immutable
        // ancestry handle. Genesis alone must not manufacture that handoff.
        var proposal = await data.ProposeAsync("ancestry-handoff-original-release");
        await data.DecideAsync(proposal["proposalId"].AsInt64, "ancestry-handoff-original-approved");
        var catalog = data.Valuation.Inspection.Receiving.Procurement.Catalog;
        var orders = data.Valuation.Inspection.Receiving.Orders.Service;
        var valueOwner = data.Valuation.Service;
        var source = new StockReleaseSource(data.Database, data.Unit, data.Clock, catalog, orders, data.Valuation.Sources, valueOwner);
        var selected = new StockReleaseSelection(data.Valuation.ReceiptId, data.QualityAllocationId, data.ValueAllocationId);
        var book = await data.Rows("accountingBooks").Find(new BsonDocument()).SingleAsync();
        var before = await SnapshotAsync(data); var warmed = 0; var refused = 0;
        foreach (var fault in new[] { "none", "quality", "accounting", "private-pin" }) {
            async Task Attempt() => await data.Unit.ExecuteAsync(async (tx, ct) => {
                var first = await HeldReceiptOriginProof.CaptureAsync(data.Database, data.Unit, data.Clock,
                    catalog, orders, data.Valuation.Sources, tx, selected.ReceiptId, ct);
                var current = await source.ReadCurrentAsync(tx, selected, ct);
                var currentOrigin = current.CurrentOrigin(data.Database, tx);
                Assert.NotNull(currentOrigin); Assert.NotSame(first, currentOrigin);
                var paired = current.CurrentOriginReader(data.Database, tx, first);
                Assert.NotNull(paired);
                var read = await paired.ReadAsync(valueOwner, data.Database, tx, first,
                    selected.QualityAllocationId, selected.ValuationAllocationId, ct);
                Assert.Equal(current.ValuationAllocation, read.Value.Allocation);
                Assert.Equal(first.Receipt, read.Quality.Receipt); warmed++;
                if (fault == "none") {
                    var repeated = await paired.ReadAsync(valueOwner, data.Database, tx, first,
                        selected.QualityAllocationId, selected.ValuationAllocationId, ct);
                    Assert.Equal(read.Value.Allocation, repeated.Value.Allocation);
                    Assert.Throws<AuthorizationDeniedException>(() => paired.Require(valueOwner, data.Database, tx, currentOrigin));
                    Assert.Throws<AuthorizationDeniedException>(() => paired.Require(data.Valuation.Service, data.Database, tx, first));
                    Assert.Throws<AuthorizationDeniedException>(() => paired.Require(valueOwner,
                        data.Database.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "-foreign"), tx, first));
                    using var foreign = await data.Database.Client.StartSessionAsync(cancellationToken: ct); foreign.StartTransaction();
                    try { Assert.Throws<AuthorizationDeniedException>(() => paired.Require(valueOwner, data.Database, foreign, first)); }
                    finally { await foreign.AbortTransactionAsync(ct); }
                    var at = data.Clock.UtcNow;
                    try {
                        data.Clock.UtcNow = at.AddDays(-1);
                        Assert.Throws<AuthorizationDeniedException>(() => paired.Require(valueOwner, data.Database, tx, first));
                    } finally { data.Clock.UtcNow = at; }
                    paired.Require(valueOwner, data.Database, tx, first); return true;
                }
                if (fault == "quality") {
                    Assert.Equal(1L, (await data.Rows(Q.Decisions).UpdateOneAsync(tx,
                        new BsonDocument("_id", read.Quality.Decision["_id"]),
                        Builders<BsonDocument>.Update.Set("reason", "Unowned current ancestry quality rewrite"), cancellationToken: ct)).ModifiedCount);
                } else if (fault == "accounting") {
                    Assert.Equal(1L, (await data.Rows("accountingBooks").UpdateOneAsync(tx,
                        new BsonDocument("_id", book["_id"]), Builders<BsonDocument>.Update.Inc("chartVersionId", 1L), cancellationToken: ct)).ModifiedCount);
                } else {
                    Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(tx,
                        new BsonDocument("_id", first.Receipt["inspection"]["pinId"]), cancellationToken: ct)).DeletedCount);
                }
                await paired.ReadAsync(valueOwner, data.Database, tx, first,
                    selected.QualityAllocationId, selected.ValuationAllocationId, ct); return true;
            }, default);
            if (fault == "none") await Attempt();
            else {
                var failure = await Record.ExceptionAsync(Attempt);
                Assert.True(failure is PersistenceConflictException or DomainValidationException or AuthorizationDeniedException, failure?.ToString());
                refused++;
            }
        }
        Assert.Equal(4, warmed); Assert.Equal(3, refused);
        Assert.Equal(book, await data.Rows("accountingBooks").Find(new BsonDocument("_id", book["_id"])).SingleAsync());
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
