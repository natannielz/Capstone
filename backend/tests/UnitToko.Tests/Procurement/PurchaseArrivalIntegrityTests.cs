using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using A = UnitToko.Platform.Procurement.PurchaseArrivalRecords;
using D = UnitToko.Tests.Procurement.PurchaseOrderTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class PurchaseArrivalIntegrityTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual_arrival_capture_rechecks_history_receipt_and_clock_and_repeated_fence_preserves_valid_state()
    {
        await using var data = await D.CreateAsync(fixture);
        var order = await data.DecideAsync(await data.ProposeAsync("arrival-integrity-po"), "arrival-integrity-po-approved");
        var input = new PurchaseArrivalInput(order["orderId"].AsInt64, order["orderVersion"].AsInt64,
            order["decisionId"].AsInt64, data.Procurement.Sku.Id.Value, data.Procurement.Sku.CurrentDefinitionId!.Value.Value,
            1, data.Clock.UtcNow.AddDays(3), "SYNTHETIC independently confirmed remaining-supply arrival");
        var preview = await data.Service.PreviewArrivalAsync(data.Maker, input);
        var proposed = await data.Service.ProposeArrivalAsync(data.Maker, input, preview["binding"].AsString, D.Meta("arrival-integrity-propose"));
        var proposalId = proposed["eventId"].AsInt64;
        var review = await data.Service.PreviewArrivalDecisionAsync(data.Checker, proposalId, "approve");
        var approved = await data.Service.DecideArrivalAsync(data.Checker, proposalId, 2, "approve", review["binding"].AsString,
            D.Meta("arrival-integrity-approve"));
        var stockReader = await data.Base.AddActorAsync(640, ProcurementRequestTestData.Scope, Permissions.StockRead, Permissions.ProcurementRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, stockReader.UserId);
        var authorization = new AuthorizationStore(data.Database, data.Unit, data.Clock);
        var receiptFilter = new BsonDocument { { "actorId", data.Checker.UserId }, { "operation", A.Prefix + "approve" }, { "key", "arrival-integrity-approve" } };
        var businessBefore = await data.StateAsync();
        async Task<BsonDocument> History() {
            var state = new BsonDocument();
            foreach (var table in new[] { A.Heads, A.Events, A.Audits })
                state[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            state["receipts"] = new BsonArray(await data.Rows("idempotencyRecords")
                .Find(new BsonDocument("operation", new BsonRegularExpression(@"^procurement\.purchase\.arrival\.")))
                .Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
        var historyBefore = await History();
        static void Refused(Exception? failure) => Assert.True(failure is PersistenceConflictException or AuthorizationDeniedException,
            failure?.ToString() ?? "The changed arrival was accepted");
        async Task InTransaction(Func<IClientSessionHandle, Task> work) {
            using var tx = await fixture.Client.StartSessionAsync();
            tx.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            try { await work(tx); }
            finally { await InventoryRepository.AbortAsync(tx); }
            Assert.Equal(historyBefore, await History());
        }
        async Task<PurchaseArrivalReadCapture> Capture(IClientSessionHandle tx) {
            var authority = await authorization.LoadAsync(stockReader, tx, CancellationToken.None);
            return await data.Service.CaptureArrivalForReplenishmentAsync(stockReader, authority, tx,
                input.OrderId, input.SkuId, input.DefinitionId, CancellationToken.None);
        }

        await InTransaction(async tx => {
            var capture = await Capture(tx);
            Assert.True(capture.Reviewed); Assert.Equal(input.ExpectedAt, capture.ExpectedAt);
            await capture.FenceAsync(CancellationToken.None);
            await capture.FenceAsync(CancellationToken.None);
            capture.RequireCurrent();
        });
        foreach (var table in new[] { A.Events, A.Audits, "idempotencyRecords" }) {
            await InTransaction(async tx => {
                var capture = await Capture(tx);
                var selected = table == "idempotencyRecords" ? receiptFilter : new BsonDocument("_id", approved["eventId"]);
                Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(tx, selected)).DeletedCount);
                Refused(await Record.ExceptionAsync(() => capture.FenceAsync(CancellationToken.None)));
            });
        }
        foreach (var missing in new[] { true, false }) {
            await InTransaction(async tx => {
                var change = missing ? Builders<BsonDocument>.Update.Unset(PersonApprovalReceipt.Field) :
                    Builders<BsonDocument>.Update.Set(PersonApprovalReceipt.Field + ".approval.requestedBy", data.Maker.UserId + 100000L);
                Assert.Equal(1L, (await data.Rows("idempotencyRecords").UpdateOneAsync(tx, receiptFilter, change)).ModifiedCount);
                Refused(await Record.ExceptionAsync(() => Capture(tx)));
            });
        }
        await InTransaction(async tx => {
            var capture = await Capture(tx); var at = data.Clock.UtcNow;
            try {
                data.Clock.UtcNow = at.AddMilliseconds(-1);
                Refused(await Record.ExceptionAsync(() => capture.FenceAsync(CancellationToken.None)));
            } finally { data.Clock.UtcNow = at; }
        });

        // Recovery runs through its own authorization transaction, so make this
        // fixture-only orphan visible and restore the exact original event afterward.
        var eventFilter = new BsonDocument("_id", approved["eventId"]);
        var original = await data.Rows(A.Events).Find(eventFilter).SingleAsync();
        Assert.Equal(1L, (await data.Rows(A.Events).DeleteOneAsync(eventFilter)).DeletedCount);
        try {
            Refused(await Record.ExceptionAsync(() => data.Service.RecoverArrivalAsync(data.Checker, "approve", "arrival-integrity-approve")));
        } finally { await data.Rows(A.Events).InsertOneAsync(original); }
        Assert.Equal(historyBefore, await History());
        Assert.Equal(businessBefore, await data.StateAsync());
        await data.NoFinancialOrReceiptEffectsAsync();
    }
}
