using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementStockReceiptTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementStockReceiptTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_inspected_receipt_creates_only_canonical_held_stock_and_replays_without_duplicate_quantity() {
        await using var data = await D.CreateAsync(fixture); var inspected = await data.Inspection.StateAsync();
        var policyProof = new BsonDocument();
        foreach (var table in new[] { "stockSourcePolicyProposals", "stockSourcePolicyDecisions", "stockSourcePolicies" })
            policyProof[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var input = data.Input(packages: 2); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var result = await data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString, D.Meta("held-receipt"));
        Assert.Equal("held_pending_valuation_and_release", result["state"].AsString);
        foreach (var table in policyProof.Names)
            Assert.Equal(policyProof[table], new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        var state = await data.StateAsync(); Assert.Equal(result, await data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString, D.Meta("held-receipt")));
        Assert.Equal(result, await data.Service.LookupAsync(data.Maker, D.Operation, "held-receipt")); Assert.Equal(state, await data.StateAsync());
        foreach (var table in policyProof.Names)
            Assert.Equal(policyProof[table], new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        var read = await data.Service.ReadAsync(data.Reader, result["receiptId"].AsInt64);
        Assert.Equal(2L, read["onHand"].AsInt64); Assert.Equal(2L, read["held"].AsInt64); Assert.Equal(0L, read["available"].AsInt64);
        Assert.Equal(0L, read["reserved"].AsInt64); Assert.Equal(0L, read["expired"].AsInt64); Assert.True(read["valuation"].IsBsonNull);
        Assert.Equal("pending", read["valuationState"].AsString); Assert.Equal(1L, read["after"]["remainingPackages"].AsInt64);
        var balance = await data.Rows("stockBalances").Find(new BsonDocument("_id", result["balanceId"])).SingleAsync();
        Assert.Equal(2L, balance["version"].AsInt64); Assert.Equal(2L, balance["held"].AsInt64);
        var lot = await data.Rows("lots").Find(new BsonDocument("_id", result["lotId"])).SingleAsync();
        Assert.Equal("procurementReceipt", lot["origin"].AsString); Assert.Equal(lot["_id"], lot["rootLotId"]); Assert.True(lot["parentLotId"].IsBsonNull);
        Assert.Equal(result["receiptId"], lot["sourceId"]); Assert.Equal(1L, lot["sourceLineId"].AsInt64);
        var movement = await data.Rows("movements").Find(new BsonDocument("_id", result["movementId"])).SingleAsync();
        Assert.Single(movement["legs"].AsBsonArray); Assert.Equal("ReceiveHeld", movement["legs"][0]["action"].AsString);
        Assert.Equal(0L, movement["legs"][0]["before"]["onHand"].AsInt64); Assert.Equal(balance, movement["legs"][0]["after"]);
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync())["active"].AsBoolean);
        Assert.Equal(inspected, await data.Inspection.StateAsync());
        // Claims and receipts consume the same physical quantity; shortage capacity is distinct.
        await data.Claims.RequestAsync("remaining-claim", data.Claims.Input("REMAINING", 1));
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input("EXCESS")));
        await data.Claims.RequestAsync("observed-shortage", data.Claims.Input("SHORTAGE", 1, SupplierClaimBasis.ShortageObserved));
        await NoMoneyAsync(data);
    }

    [WindowsStorageFact]
    public async Task Atomic_rollback_and_competing_claim_receipt_share_capacity_and_missing_history_never_reopens_stock() {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(packages: 2); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var before = await data.StateAsync(); var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone().AsBsonDocument;
        try {
            await GuardAsync(data, new("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", D.Operation)) }));
            Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString, D.Meta("rolled-back"))));
            Assert.Equal(before, await data.StateAsync()); Assert.Null(await data.Service.LookupAsync(data.Maker, D.Operation, "rolled-back"));
        } finally { await GuardAsync(data, validator); }
        var claim = data.Claims.Input("COMPETING", 2); var claimPreview = await data.Claims.Service.PreviewAsync(data.Claims.Maker, claim);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.Service.ReceiveAsync(data.Maker, input, preview["binding"].AsString, D.Meta("race-receipt"))),
            Record.ExceptionAsync(() => data.Claims.Service.RequestAsync(data.Claims.Maker, claim, claimPreview["binding"].AsString, SupplierClaimTestData.Meta("race-claim"))));
        Assert.Single(results, value => value is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, value => value is not null));
        // Whichever wins, a fresh one-package receipt uses exactly the last unoccupied physical package.
        var accepted = await data.RecordAsync("last-package", data.Input("LAST", 1)); var id = accepted["receiptId"].AsInt64;
        var receipt = await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", id)).SingleAsync(); var committed = await data.StateAsync();
        foreach (var (table, filter) in new[] { ("procurementStockReceipts", new BsonDocument("_id", id)), ("lots", new BsonDocument("_id", accepted["lotId"])),
            ("stockBalances", new BsonDocument("_id", accepted["balanceId"])), ("movements", new BsonDocument("_id", accepted["movementId"])),
            ("procurementQuarantineDispositions", new BsonDocument("_id", receipt["disposition"]["allocationId"])),
            ("procurementQuarantineDispositionHeads", new BsonDocument("_id", receipt["disposition"]["headId"])),
            ("auditEvents", new BsonDocument("_id", receipt["auditId"])), ("outboxEvents", new BsonDocument { { "kind", D.Operation }, { "requestKey", "last-package" } }),
            ("idempotencyRecords", new BsonDocument { { "operation", D.Operation }, { "key", "last-package" } }) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try {
                await DeniedAsync(() => data.Service.ReadAsync(data.Reader, id)); await DeniedAsync(() => data.Service.LookupAsync(data.Maker, D.Operation, "last-package"));
                await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input("AFTER-CORRUPTION")));
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        Assert.Equal(committed, await data.StateAsync()); Assert.Equal(accepted, await data.Service.LookupAsync(data.Maker, D.Operation, "last-package")); await NoMoneyAsync(data);
    }

    [WindowsStorageTheory]
    [InlineData("grant")] [InlineData("person")] [InlineData("mfa")] [InlineData("source_policy")]
    public async Task Scope_factor_person_and_source_changes_cannot_escape_an_active_receipt_preview(string change) {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(); var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, input));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input))).Code);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requestId = 0; var observed = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (requestId != 0 || value.DatabaseNamespace.DatabaseName != data.Database.DatabaseNamespace.DatabaseName) return;
                var target = change == "person"
                    ? value.Command.GetValue("find", BsonNull.Value) == "personAccountBindings" &&
                      value.Command.TryGetValue("filter", out var filter) && filter.IsBsonDocument &&
                      filter.AsBsonDocument.GetValue("_id", BsonNull.Value) is { IsInt64: true } actorId && actorId.AsInt64 == data.Maker.UserId
                    : value.Command.GetValue("find", BsonNull.Value) == "procurementInspectionSubmissions";
                if (target) requestId = value.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(value => { if (requestId != 0 && value.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Held receipt source race did not complete");
            } });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var catalog = new CatalogAuthoringService(db, unit, new(db), data.Clock); var requests = new ProcurementRequestService(db, unit, new(db), data.Clock, catalog);
        var orders = new PurchaseOrderService(db, unit, new(db), data.Clock, catalog, requests, new(db, unit, new(db), data.Clock, catalog));
        var service = new ProcurementStockReceiptService(db, unit, new(db), data.Clock, catalog, orders, new(db, unit, new(db), data.Clock, catalog, orders, requests), new(db, unit, new(db), data.Clock));
        var pending = Task.Run(() => service.PreviewAsync(data.Maker, input));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "person") {
                var changed = await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
                Assert.Equal(1L, changed.ModifiedCount);
                Assert.Equal("revoked", (await data.Rows("personAccountBindings").Find(new BsonDocument("_id", data.Maker.UserId)).SingleAsync())["status"].AsString);
            }
            if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
            if (change == "source_policy") {
                var removed = await data.Rows("stockSourcePolicies").DeleteOneAsync(new BsonDocument("_id", data.PolicyId));
                Assert.Equal(1L, removed.DeletedCount);
                Assert.False(await data.Rows("stockSourcePolicies").Find(new BsonDocument("_id", data.PolicyId)).AnyAsync());
            }
        } finally { release.Set(); }
        await DeniedAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(90))); Assert.Equal(1, observed);
        Assert.Equal(before, await data.StateAsync()); await NoMoneyAsync(data);
    }
    private static async Task DeniedAsync(Func<Task> action) { var error = await Record.ExceptionAsync(action); Assert.True(error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException, error?.ToString() ?? "Unexpected success"); }
    private static Task GuardAsync(D data, BsonDocument validator) => data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task NoMoneyAsync(D data) {
        foreach (var name in new[] { "journalEntries", "cashMovements", "paymentAllocations", "procurementFundingAllocations", "procurementDisbursements", "procurementSettlements", "reservations" })
            Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
    }
}
