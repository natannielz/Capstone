using MongoDB.Bson;
using MongoDB.Driver;
using System.Runtime.Versioning;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentDispatchGenuineValueTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_warmed_funding_proof_accepts_own_buyer_source_fences_but_refuses_scope_changes_and_rolls_back() {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, createOrder: false);
        var quote = await new OrderQuoteService(data.Database, data.Unit, data.Clock).PreviewAsync(data.Buyer, data.Input.Context, data.Input.Selections);
        var before = await data.BusinessAsync();
        var scopes = data.Rows("authorizationScopes");
        var originalScopes = new BsonArray(await scopes.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var checkedChanges = new List<string>(); var fenceAdvances = 0;
        var failure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var receiving = data.Stock.Valuation.Inspection.Receiving; var owner = receiving.Orders.Service;
            var proof = await PurchaseOrderHistoricalSourceProof.ReadAsync(owner, data.Database, tx, receiving.OrderId, 1, receiving.ApprovalId, ct);
            var originalSource = proof.Source;
            var sellerFilter = new BsonDocument("_id", new BsonDocument("$in", new BsonArray { 100L, 101L, 102L, 103L }));
            var capturedScopes = await scopes.Find(tx, sellerFilter).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            Assert.Equal(4, capturedScopes.Count); Assert.All(capturedScopes, row => Assert.True(row["fence"].AsInt64 > 0));
            var authority = await data.Base.Store.LoadAsync(data.Buyer, tx, ct);
            var physical = await new InventoryRepository(data.Database).ReadAsync(tx, data.BalanceId, ct);
            var pool = await data.Rows("inventoryPools").Find(tx, new BsonDocument("_id", physical.PhysicalPoolId)).SingleAsync(ct);
            var sourceOwner = data.Stock.Valuation.Sources; var line = Assert.Single(quote.Catalog.Lines);
            var source = await sourceOwner.CaptureOrderSubmitSourceAsync(data.Buyer, authority, tx, data.Input.Context, line,
                physical.Balance.Key.Location, physical.PhysicalPoolId, pool["revision"].AsInt64, ct);
            source.RequireContext(sourceOwner, data.Buyer, authority, tx, data.Input.Context, line);
            await source.FenceAsync(ct);
            source.RequireContext(sourceOwner, data.Buyer, authority, tx, data.Input.Context, line);
            await proof.RequireAsync(owner, data.Database, tx, originalSource, ct);
            Assert.Equal(originalSource, proof.Source);
            var currentScopes = await scopes.Find(tx, sellerFilter).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            foreach (var original in capturedScopes) {
                var current = currentScopes.Single(row => row["_id"] == original["_id"]);
                Assert.True(current["fence"].AsInt64 > original["fence"].AsInt64); fenceAdvances++;
                var withoutAdvance = current.DeepClone().AsBsonDocument; withoutAdvance["fence"] = original["fence"];
                Assert.Equal(original, withoutAdvance);
            }
            var node = currentScopes.Single(row => row["_id"] == 101L);
            async Task RefuseAsync(string change, Action<BsonDocument> mutate) {
                var changed = node.DeepClone().AsBsonDocument; mutate(changed);
                Assert.Equal(1L, (await scopes.ReplaceOneAsync(tx, new BsonDocument("_id", node["_id"]), changed,
                    new ReplaceOptions { BypassDocumentValidation = true }, ct)).ModifiedCount);
                var refused = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => proof.RequireAsync(owner, data.Database, tx, originalSource, ct));
                Assert.Equal("procurement_quality_acceptance_integrity_unavailable", refused.Code); checkedChanges.Add(change);
                Assert.Equal(1L, (await scopes.ReplaceOneAsync(tx, new BsonDocument("_id", node["_id"]), node, cancellationToken: ct)).ModifiedCount);
                await proof.RequireAsync(owner, data.Database, tx, originalSource, ct); Assert.Equal(originalSource, proof.Source);
            }
            await RefuseAsync("lower-fence", row => row["fence"] = 0L);
            await RefuseAsync("negative-fence", row => row["fence"] = -1L);
            await RefuseAsync("terminal-fence", row => row["fence"] = long.MaxValue);
            await RefuseAsync("wrong-fence-type", row => row["fence"] = 1);
            await RefuseAsync("missing-fence", row => row.Remove("fence"));
            await RefuseAsync("version", row => row["version"] = checked(row["version"].AsInt64 + 1));
            await RefuseAsync("status", row => row["status"] = "suspended");
            await RefuseAsync("parent", row => row["parentId"] = 10L);
            await RefuseAsync("kind", row => row["kind"] = "channel");
            await RefuseAsync("kind-alias", row => row["kind"] = "Shop");
            await RefuseAsync("schema", row => row["schemaVersion"] = 2);
            await RefuseAsync("unknown-field", row => row["unreviewed"] = true);
            Assert.Equal(1L, (await scopes.DeleteOneAsync(tx, new BsonDocument("_id", node["_id"]), cancellationToken: ct)).DeletedCount);
            checkedChanges.Add("missing-node");
            await proof.RequireAsync(owner, data.Database, tx, originalSource, ct);
            return true;
        }));
        Assert.Equal("procurement_quality_acceptance_integrity_unavailable", failure.Code);
        Assert.Equal(4, fenceAdvances);
        Assert.Equal(new[] { "lower-fence", "negative-fence", "terminal-fence", "wrong-fence-type", "missing-fence", "version", "status", "parent",
            "kind", "kind-alias", "schema", "unknown-field", "missing-node" }, checkedChanges);
        Assert.Equal(originalScopes, new BsonArray(await scopes.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(before, await data.BusinessAsync());
    }
    [WindowsStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_released_or_converted_reserved_lot_dispatch_consumes_finite_value_posts_exact_cogs_and_replays_original_receipts(bool converted) {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted, reviewedSafetyBufferBaseUnits: 0);
        var originalJournals = new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var before = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        var value = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal("11250.00", value["remainingValue"].AsString);
        Assert.Equal(converted ? 12L : 1L, before["reserved"].AsInt64);
        var review = await data.PrepareDispatchAsync(1, "genuine-dispatch-first-preparation");
        var first = await data.DispatchAsync(review, "genuine-dispatch-first");
        var firstRow = await data.Rows("shipments").Find(new BsonDocument("_id", first["shipmentId"])).SingleAsync();
        Assert.Equal(2, firstRow["schemaVersion"].AsInt32); Assert.Equal("committed", firstRow["phase"].AsString);
        var firstEffect = Assert.Single(firstRow["valueEffects"].AsBsonArray);
        Assert.Equal(converted ? "937.50" : "11250.00", firstEffect["transferredValue"].AsString);
        var transition = await data.Rows(LotValueStore.Transitions).Find(new BsonDocument("_id", firstEffect["transitionId"])).SingleAsync();
        Assert.Equal("consume", transition["operation"].AsString); Assert.Equal("consumeLot", transition["source"]["eventKind"].AsString);
        Assert.Equal(data.BalanceId, transition["source"]["occurrence"].AsInt64);
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", firstRow["journalId"])).SingleAsync();
        var amount = Money.Parse(firstEffect["transferredValue"].AsString).Value;
        Assert.Equal(amount, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "cogs")["debit"].AsDecimal);
        Assert.Equal(amount, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "inventory")["credit"].AsDecimal);
        Assert.Equal(first, await data.DispatchAsync(review, "genuine-dispatch-first"));
        Assert.Equal(first, await data.Dispatch.RecoverAsync(data.Dispatcher, "genuine-dispatch-first"));
        if (converted) {
            var secondReview = await data.PrepareDispatchAsync(11, "genuine-dispatch-second-preparation", review.Preparation.PreparationRevision);
            var second = await data.DispatchAsync(secondReview, "genuine-dispatch-second");
            var secondRow = await data.Rows("shipments").Find(new BsonDocument("_id", second["shipmentId"])).SingleAsync();
            Assert.Equal("10312.50", Assert.Single(secondRow["valueEffects"].AsBsonArray)["transferredValue"].AsString);
            Assert.Equal(first, await data.Dispatch.RecoverAsync(data.Dispatcher, "genuine-dispatch-first"));
            Assert.Equal(second, await data.Dispatch.RecoverAsync(data.Dispatcher, "genuine-dispatch-second"));
        }
        var final = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(before["onHand"].AsInt64 - (converted ? 12 : 1), final["onHand"].AsInt64); Assert.Equal(0L, final["reserved"].AsInt64);
        Assert.Equal(before["held"], final["held"]); Assert.Equal(before["expired"], final["expired"]);
        var head = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(0L, head["remainingPackages"].AsInt64); Assert.Equal("0.00", head["remainingValue"].AsString);
        Assert.Equal(originalJournals, new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", new BsonDocument("$ne", "fulfillment.shipment")))
            .Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(converted ? 2 : 1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "fulfillment.shipment")));
        var stable = await data.BusinessAsync();
        Assert.Equal(first, await data.Dispatch.RecoverAsync(data.Dispatcher, "genuine-dispatch-first")); Assert.Equal(stable, await data.BusinessAsync());
    }
    [WindowsStorageFact]
    public async Task A_real_reserved_finite_lot_without_an_approved_cogs_mapping_refuses_preview_without_physical_value_or_journal_effects() {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, cogs: false, reviewedSafetyBufferBaseUnits: 0);
        var source = await data.Orders.ReadFulfillmentOrderAsync(data.Dispatcher, data.SuborderId);
        await data.Preparations.SaveAsync(data.Dispatcher, data.SuborderId, 0, source.Review, [new(new(data.SkuId), 1)], GenuineFiniteDispatchTestData.Meta("missing-cogs-preparation"));
        var prepared = await data.Dispatch.ReadPreparationAsync(data.Dispatcher, data.SuborderId); var before = await data.BusinessAsync();
        var failure = await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.Dispatch.PreviewAsync(data.Dispatcher, data.SuborderId,
            prepared.PreparationId.Value, prepared.PreparationRevision, data.Courier.UserId, [new(Assert.Single(prepared.Lots).Reservation.ReservationId, 1)]));
        Assert.Equal("shipment_dispatch_accounting_changed", failure.Code); Assert.Equal(before, await data.BusinessAsync());
    }
    [WindowsStorageFact]
    public async Task Factor_expiry_after_the_actual_physical_dispatch_rolls_back_reservations_value_cogs_and_all_required_effects() {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, reviewedSafetyBufferBaseUnits: 0);
        var review = await data.PrepareDispatchAsync(1, "late-factor-preparation"); var before = await data.BusinessAsync(); var now = data.Clock.UtcNow; var reached = 0;
        data.Admission.BeforeComplete = async (tx, ct) => {
            if (await data.Rows("movements").Find(tx, new BsonDocument("source.namespace", "fulfillment.shipment")).AnyAsync(ct) && Interlocked.CompareExchange(ref reached, 1, 0) == 0)
                data.Clock.UtcNow = data.Dispatcher.FactorCompletedAt.Add(Permissions.Catalog[Permissions.ShipmentDispatch].Assurance.MaximumAge ?? SessionPolicy.FreshFactorLifetime).AddMilliseconds(1);
        };
        try {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => data.DispatchAsync(review, "late-factor-dispatch"));
            Assert.True(failure is AuthorizationDeniedException { Code: "step_up_required" } or DomainValidationException { Code: "shipment_dispatch_admission_unavailable" }, failure.ToString());
        } finally { data.Clock.UtcNow = now; data.Admission.BeforeComplete = null; }
        Assert.Equal(1, reached); Assert.Equal(before, await data.BusinessAsync());
        Assert.Null(await data.Dispatch.RecoverAsync(data.Dispatcher, "late-factor-dispatch"));
    }
}
