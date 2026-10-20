using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Fulfillment;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Fulfillment;

/// <summary>Actual finite stock, production order/dispatch admission and actual issued
/// document/QR. Directory/person grants use the shared explicit test boundary only.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ShipmentCourierTestData : IAsyncDisposable
{
    internal GenuineFiniteDispatchTestData Data = null!;
    internal ShipmentCourierService Service = null!;
    internal ShipmentQueryService Queries = null!;
    internal ShipmentDocumentService Documents = null!;
    internal ShipmentReceiptService Receipts = null!;
    internal DeliveryAttemptService Attempts = null!;
    internal ActorContext Admin = null!, CourierA = null!, CourierB = null!, Recipient = null!, WrongScope = null!;
    internal long ShipmentId, DocumentId;
    internal ShipmentReceiptChallengeView Challenge = null!;
    internal IMongoCollection<BsonDocument> Rows(string name) => Data.Rows(name);
    internal static async Task<ShipmentCourierTestData> CreateAsync(MongoFixture fixture, bool systemClock = false) {
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock);
        var result = new ShipmentCourierTestData { Data = prepared.Data };
        try {
            var data = result.Data;
            var shop = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
            var courier = new GrantScope(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103);
            result.Admin = await data.Base.AddActorAsync(981001, shop, Permissions.ShipmentAssign);
            result.CourierA = data.Courier;
            await data.Base.AddGrantAsync(result.CourierA, 981011, courier, [Permissions.DeliveryEvidence]);
            result.CourierB = await data.Base.AddActorAsync(981002, courier, Permissions.ShipmentRead, Permissions.DeliveryAttempt, Permissions.DeliveryEvidence);
            result.Recipient = data.Buyer;
            await data.Base.AddGrantAsync(result.Recipient, 981012, CatalogAuthoringTestData.Audience, [Permissions.ReceiptAccept]);
            // This actual buying-branch scope never authorizes a fulfillment-shop task.
            result.WrongScope = await data.Base.AddActorAsync(981003, new(GrantScopeKind.BuyingBranch, 10, BranchId: 11), Permissions.ShipmentAssign);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, result.Admin.UserId, result.CourierB.UserId, result.WrongScope.UserId);
            foreach (var (actor, name) in new[] { (result.CourierB, "Synthetic replacement courier"), (result.Recipient, "Synthetic independent recipient") })
                if (!await data.Rows("accountProfiles").Find(new BsonDocument("_id", actor.UserId)).AnyAsync())
                    await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                        { "displayName", name }, { "verifiedEmail", "courier-proof@example.invalid" }, { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime } });
            await result.RefreshActorsAsync(); data.Dispatcher = await data.IssueExistingSessionAsync(data.Dispatcher);
            var preview = await data.PrepareDispatchAsync(12, "courier-fixture-preparation");
            var dispatch = await data.DispatchAsync(preview, "courier-fixture-dispatch"); result.ShipmentId = dispatch["shipmentId"].AsInt64;
            result.Queries = new(data.Database, data.Unit, data.Clock, data.Orders, data.Preparations);
            result.Documents = new(data.Database, data.Unit, new(data.Database), data.Clock, result.Queries);
            result.Receipts = new(data.Database, data.Unit, new(data.Database), data.Clock, result.Queries, result.Documents);
            result.Attempts = new(data.Database, data.Unit, new(data.Database), data.Clock, result.Queries, result.Receipts);
            result.Service = new(data.Database, data.Unit, new(data.Database), data.Clock, result.Queries, result.Receipts);
            await result.RefreshActorsAsync(); data.Dispatcher = await data.IssueExistingSessionAsync(data.Dispatcher);
            result.DocumentId = (await result.Documents.IssueAsync(data.Dispatcher, result.ShipmentId, 1, GenuineFiniteDispatchTestData.Meta("courier-fixture-document"))).Id.Value;
            result.Challenge = await result.Receipts.IssueChallengeAsync(result.CourierA, result.ShipmentId, result.DocumentId, result.Recipient.UserId,
                GenuineFiniteDispatchTestData.Meta("courier-fixture-challenge"));
            Assert.NotNull(result.Challenge.Token); return result;
        } catch { await result.DisposeAsync(); throw; }
    }
    internal async Task RefreshActorsAsync() {
        Admin = await Data.IssueExistingSessionAsync(Admin); CourierA = await Data.IssueExistingSessionAsync(CourierA);
        CourierB = await Data.IssueExistingSessionAsync(CourierB); Recipient = await Data.IssueExistingSessionAsync(Recipient);
        WrongScope = await Data.IssueExistingSessionAsync(WrongScope); Data.Courier = CourierA; Data.Buyer = Recipient;
    }
    internal async Task<BsonDocument> BusinessAsync() {
        var snapshot = await Data.BusinessAsync();
        foreach (var expected in new[] { "auditEvents", "outboxEvents", "idempotencyRecords" }) snapshot.Remove(expected);
        foreach (var name in new[] { "orderDecisions", "inventoryPools", "invoices", "paymentDeclarations", "paymentAllocations", "refundPayouts", "issuedDocuments" }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); snapshot.Add(name, new BsonArray(rows));
        }
        return snapshot;
    }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
