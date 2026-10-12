using System.Runtime.Versioning;
using System.Collections.Immutable;
using System.Globalization;
using UnitToko.Tests.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Inventory;
using Xunit.Abstractions;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionShipmentReadTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsStorageFact]
    public async Task Actual_A_to_B_to_A_repriced_shipments_read_the_selected_historical_line_and_preserve_initial_line_reads() {
        using var timing = new StockReleaseTestDiagnostics(output);
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock: false);
        await using var data = prepared.Data;
        await data.Base.AddGrantAsync(data.Courier, 791201,
            new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), [Permissions.DeliveryEvidence]);
        await data.Base.AddGrantAsync(data.Buyer, 791202, CatalogAuthoringTestData.Audience, [Permissions.ReceiptAccept]);
        if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", data.Buyer.UserId)).AnyAsync())
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", data.Buyer.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Synthetic lineage recipient" }, { "verifiedEmail", "lineage-recipient@example.invalid" },
                { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
        async Task RenewAsync(string key) {
            var spend = await data.Orders.ReviewOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending);
            await data.Orders.DecideOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending,
                spend.Revision, spend.Review, GenuineFiniteDispatchTestData.Meta(key + "-spending"));
            var store = await data.Orders.ReviewOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store);
            await data.Orders.DecideOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store,
                store.Revision, store.Review, GenuineFiniteDispatchTestData.Meta(key + "-store"));
        }
        async Task<long> ReplaceAsync(long skuId, long lineRevision, int packages, long replacementSkuId, int replacementPackages, string key) {
            var target = Assert.Single((await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items);
            var original = Assert.Single(target.Lines, line => line.Catalog.Definition.SkuId.Value == skuId && line.Catalog.LineRevision == lineRevision);
            Assert.True(original.RemainingPackages >= packages);
            var replacement = Assert.Single((await data.Orders.SubstitutionPackagesAsync(data.Dispatcher, data.OrderId, data.SuborderId)).Items,
                line => line.Definition.SkuId.Value == replacementSkuId);
            var input = new OrderSubstitutionInput(new(data.OrderId), new(data.SuborderId), target.Revision, target.DispatchSequence,
                new(new(skuId), packages, new(replacement.Definition.SkuId, replacement.Definition.Id, replacement.Definition.Version,
                    replacement.Price.Id, replacement.Price.Version, replacementPackages), lineRevision));
            var preview = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
            var proposal = await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, preview.Review, GenuineFiniteDispatchTestData.Meta(key + "-proposal"));
            var consent = await data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposal["proposalId"].AsInt64, OrderSubstitutionDecision.Accept);
            var accepted = await data.Orders.DecideSubstitutionAsync(data.Buyer, consent.ProposalId.Value, consent.Revision,
                OrderSubstitutionDecision.Accept, consent.Review, GenuineFiniteDispatchTestData.Meta(key + "-consent"));
            Assert.Equal(accepted, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, key + "-consent", OrderSubstitutionDecision.Accept));
            await RenewAsync(key);
            return accepted["revision"].AsInt64;
        }
        var bRevision = await ReplaceAsync(data.SkuId, 0, 4, prepared.ReplacementSku, 1, "read-a-b");
        var aRevision = await ReplaceAsync(prepared.ReplacementSku, bRevision, 1, data.SkuId, 4, "read-b-a");
        var prices = new CatalogPriceService(data.Database, data.Unit, new(data.Database), data.Clock);
        var prior = await prices.PreviewAsync(prepared.PriceMaker, data.SkuId, CatalogPriceAccess.Prepare);
        var price = await prices.ProposeAsync(prepared.PriceMaker, data.SkuId, prior.Expected, new("IDR", new Money(21000m)),
            GenuineFiniteDispatchTestData.Meta("read-a-price"));
        await prices.DecideAsync(prepared.PriceChecker, price["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "Explicit renewed price for this buyer line", GenuineFiniteDispatchTestData.Meta("read-a-price-approved"));
        var revised = await ReplaceAsync(data.SkuId, aRevision, 4, data.SkuId, 4, "read-a-repriced");
        var targetNow = Assert.Single((await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items);
        Assert.Equal(new Money(20000m), Assert.Single(targetNow.Lines,
            line => line.Catalog.Definition.SkuId.Value == data.SkuId && line.Catalog.LineRevision == 0).Catalog.Price.UnitPrice);
        Assert.Equal(new Money(21000m), Assert.Single(targetNow.Lines,
            line => line.Catalog.Definition.SkuId.Value == data.SkuId && line.Catalog.LineRevision == revised).Catalog.Price.UnitPrice);

        async Task<BsonDocument> DispatchLineAsync(long lineRevision, int packages, string key) {
            var source = await data.Orders.ReadFulfillmentOrderAsync(data.Dispatcher, data.SuborderId);
            var previous = await data.Preparations.ReadAsync(data.Dispatcher, data.SuborderId);
            await data.Preparations.SaveAsync(data.Dispatcher, data.SuborderId, previous?.Revision ?? 0, source.Review,
                [new(new(data.SkuId), packages, lineRevision)], GenuineFiniteDispatchTestData.Meta(key + "-preparation"));
            var ready = await data.Dispatch.ReadPreparationAsync(data.Dispatcher, data.SuborderId);
            var lot = Assert.Single(ready.Lots, item => item.Reservation.SkuId.Value == data.SkuId && item.Reservation.LineRevision == lineRevision);
            Assert.Equal(lineRevision, lot.Reservation.LineRevision);
            Assert.True(lot.Reservation.RemainingPackages >= packages);
            var exactLine = Assert.Single(ready.Source.Lines, line => line.SkuId.Value == data.SkuId && line.LineRevision == lineRevision);
            var scanRequest = new ScanRequest("dispatch", "sku", exactLine.SkuCode, null, null, null, data.SuborderId,
                ready.Source.Revision, ready.PreparationId.Value, ready.PreparationRevision, ready.Source.Review);
            var match = Assert.Single(ScanRules.Resolve(scanRequest,
                await data.Dispatch.ScanChoicesAsync(data.Dispatcher, scanRequest, default)).Candidates);
            var selection = Assert.IsType<OperationalScanSelection>(match.Selection);
            Assert.Equal(exactLine.SkuId, selection.Identifier!.SkuId);
            Assert.Equal(exactLine.Package.Id, selection.Identifier.DefinitionId);
            var review = await data.Dispatch.PreviewAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value,
                ready.PreparationRevision, data.Courier.UserId, [new(lot.Reservation.ReservationId, packages)]);
            var result = await data.Dispatch.DispatchAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value,
                ready.PreparationRevision, data.Courier.UserId, review.Selected, review.Review,
                GenuineFiniteDispatchTestData.Meta(key + "-dispatch"), scanSelections: [selection]);
            Assert.Equal(result, await data.Dispatch.RecoverAsync(data.Dispatcher, key + "-dispatch"));
            Assert.Equal("scanned", (await data.Rows(OperationalScanCommandBindings.Collection)
                .Find(new BsonDocument("requestKey", key + "-dispatch")).SingleAsync())["mode"].AsString);
            return await data.Rows("shipments").Find(new BsonDocument("_id", result["shipmentId"])).SingleAsync();
        }
        var newerShipment = await DispatchLineAsync(revised, 4, "read-newer");
        var initialShipment = await DispatchLineAsync(0, 8, "read-initial");
        // Both dispatches retain their actual full source graph. The initial and revised
        // A share one SKU; selecting by SKU alone is ambiguous even though a shipment
        // contains only one selected revision of that SKU.
        foreach (var row in new[] { newerShipment, initialShipment }) {
            var source = ShipmentPreparationBson.Source(row["source"].AsBsonDocument);
            Assert.True(source.Lines.Count(line => line.SkuId.Value == data.SkuId) >= 2);
        }
        var queries = new ShipmentQueryService(data.Database, data.Unit, data.Clock, data.Orders, data.Preparations);
        var documents = new ShipmentDocumentService(data.Database, data.Unit, new(data.Database), data.Clock, queries);
        var receipts = new ShipmentReceiptService(data.Database, data.Unit, new(data.Database), data.Clock, queries, documents);
        foreach (var pair in new[] { (Row: newerShipment, Revision: revised, Key: "read-newer"), (Row: initialShipment, Revision: 0L, Key: "read-initial") }) {
            var shipmentId = pair.Row["_id"].AsInt64;
            var issued = await documents.IssueAsync(data.Dispatcher, shipmentId, 1, GenuineFiniteDispatchTestData.Meta(pair.Key + "-document"));
            var challenge = await receipts.IssueChallengeAsync(data.Courier, shipmentId, issued.Id.Value, data.Buyer.UserId,
                GenuineFiniteDispatchTestData.Meta(pair.Key + "-challenge"));
            var review = await receipts.ReviewAsync(data.Buyer, challenge.Id.Value, challenge.Token!);
            var declarations = review.Lines.Select(line => new ReceiptLineDeclaration(line.SkuId, new(line.Packages, 0), new(0, 0), new(0, 0), null,
                long.Parse(line.LineRevision, CultureInfo.InvariantCulture))).ToImmutableArray();
            var accepted = await receipts.ConfirmAsync(data.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion,
                true, declarations, GenuineFiniteDispatchTestData.Meta(pair.Key + "-receipt"));
            Assert.Equal(accepted.Id, (await receipts.RecoverConfirmationAsync(data.Buyer, pair.Key + "-receipt"))!.Id);
            Assert.Equal(accepted.Id, (await receipts.ReadAsync(data.Courier, shipmentId))!.Id);
            var documentRow = await data.Rows("issuedDocuments").Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync();
            var source = await data.Unit.ExecuteAsync((tx, ct) => queries.FulfillmentStockSourceDocumentAsync(tx, shipmentId, ct));
            Assert.Equal(documentRow["snapshot"], source.Snapshot);
            var sourceLine = Assert.Single(source.Snapshot["lines"].AsBsonArray).AsBsonDocument;
            Assert.Equal(pair.Revision, OrderLineBson.Revision(sourceLine));
            Assert.Equal(pair.Revision != 0, sourceLine.Contains("lineRevision"));
            // The private receipt owner must authenticate the same revision-bearing
            // document facts, including legacy revision0 with no added BSON field.
            var retained = await data.Unit.ExecuteAsync((tx, ct) => receipts.ReadFulfillmentStockSourceReceiptAsync(tx, accepted.Id.Value, ct));
            Assert.Equal(accepted.Id.Value, retained.Id);
            Assert.Equal("receipt", retained.Kind);
            Assert.Equal(shipmentId, retained.ShipmentId);
            Assert.False(retained.Open);
        }
        var beforeReads = await data.BusinessAsync();
        foreach (var pair in new[] { (Row: newerShipment, Revision: revised, Packages: 4), (Row: initialShipment, Revision: 0L, Packages: 8) }) {
            var selected = Assert.Single(ShipmentPreparationBson.Lines(pair.Row["lines"].AsBsonArray));
            Assert.Equal(pair.Revision, selected.LineRevision);
            var expected = Assert.Single(ShipmentPreparationBson.Source(pair.Row["source"].AsBsonDocument).Lines,
                line => line.SkuId == selected.SkuId && line.LineRevision == selected.LineRevision);
            var read = await queries.ReadAsync(data.Courier, pair.Row["_id"].AsInt64);
            Assert.Equal(data.OrderId, read.OrderId.Value);
            Assert.Equal(data.SuborderId, read.SuborderId.Value);
            var line = Assert.Single(read.Lines);
            Assert.Equal(expected.SkuId, line.SkuId);
            Assert.Equal(expected.SkuCode, line.SkuCode);
            Assert.Equal(expected.ProductName, line.ProductName);
            // Mongo materializes separate immutable collections for each read.
            Assert.Equal(expected.Package.Attributes.ToArray(), line.Definition.Attributes.ToArray());
            Assert.Equal(expected.Package.ImageAssetIds.ToArray(), line.Definition.ImageAssetIds.ToArray());
            Assert.Equal(expected.Package, line.Definition with {
                Attributes = expected.Package.Attributes, ImageAssetIds = expected.Package.ImageAssetIds });
            Assert.Equal(pair.Packages, line.Packages);
            Assert.Equal(checked((long)pair.Packages * expected.Package.BaseUnitsPerPackage), line.BaseUnits);
        }
        var page = await queries.ListAsync(data.Courier);
        Assert.False(page.HasMore);
        Assert.Equal(new[] { initialShipment["_id"].AsInt64, newerShipment["_id"].AsInt64 }, page.Items.Select(item => item.Id.Value));
        Assert.Equal(new[] { 8, 4 }, page.Items.Select(item => Assert.Single(item.Lines).Packages));
        Assert.Equal(beforeReads, await data.BusinessAsync());
    }
}
