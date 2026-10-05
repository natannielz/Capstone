using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Inventory;

namespace UnitToko.Tests.Ordering;

/// <summary>Isolated local fixture, NOT an approved production policy/source adapter.</summary>
internal sealed class OrderSubmissionTestData(CatalogPriceTestData prices) : IAsyncDisposable
{
    private int fixturePoolSequence;
    internal CatalogPriceTestData Prices { get; } = prices;
    internal OrderDraftInput Input { get; private set; } = null!;
    internal FixtureAdmission Admission { get; private set; } = null!;
    internal ActorContext Buyer => Prices.Catalog.Buyer;
    internal IMongoDatabase Database => Prices.Catalog.Database;
    internal OrderSubmissionService Service => new(Database, new(Prices.Catalog.Client), new(Database), Prices.Catalog.Clock, Admission);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static async Task<OrderSubmissionTestData> CreateAsync(MongoFixture fixture, int stock = 10, CatalogPriceTestData? attachedPrices = null)
    {
        var data = new OrderSubmissionTestData(attachedPrices ?? await CatalogPriceTestData.CreateAsync(fixture));
        try {
            var prices = data.Prices; var catalog = prices.Catalog;
            await catalog.Base.AddGrantAsync(catalog.Buyer, 6200, CatalogAuthoringTestData.Audience, [Permissions.OrderSubmit]);
            await catalog.Base.AddGrantAsync(catalog.Buyer, 6250, CatalogAuthoringTestData.Audience, [Permissions.OrderRead]);
            await catalog.AssortmentsAsync();
            await catalog.Service.SetPublicationsAsync(prices.Maker, new(new(prices.SkuId), 1, [new(501)], [new(new(501), 1)]), CatalogPriceTestData.Meta("order-publish"));
            await prices.ApproveAsync(await prices.ProposeAsync());
            var item = Assert.Single((await catalog.BuyerService.QueryAsync(catalog.Buyer, 0, 25, null)).Items);
            data.Input = new(new(new(10), new(11)), [new(item.Sku.Id, item.Definition.Id, item.Definition.Version, item.CurrentPrice!.Id, item.CurrentPrice.Version, 2)],
                new("Penerima fixture", "081234567890", "Alamat cabang sintetis 1", "Jakarta", "12810", null), "PO fixture");
            // Exercise the real registered order migration. Only external policy/source inputs below
            // remain synthetic; the fixture never installs or weakens order validators itself.
            foreach (var name in OrderSubmissionSchemas.Collections) {
                await InventoryRepository.RequireStorageAsync(data.Database, name, OrderSubmissionSchemas.Validator(name), OrderSubmissionSchemas.Key(name), default);
            }
            await InventoryGraphTests.InstallBalances(catalog.Base);
            await InventoryRepository.RequireStorageAsync(data.Database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), default);
            var poolId = await data.ConfigureIsolatedOperationalPoolAsync(InventoryGraphTests.Balance().Balance.Key.Location);
            await data.Rows("lots").InsertOneAsync(InventoryGraphTests.Lot(202, item.Sku.Id.Value, item.Definition.Id.Value));
            var balance = InventoryGraphTests.Balance();
            balance = balance with { PhysicalPoolId = poolId,
                Balance = balance.Balance with { Key = balance.Balance.Key with { SkuId = item.Sku.Id, DefinitionId = item.Definition.Id }, OnHand = stock } };
            await data.Rows("stockBalances").InsertOneAsync(InventoryBson.Balance(balance));
            await data.Rows("fixtureOrderApprovals").InsertOneAsync(new BsonDocument { { "_id", 800L }, { "revision", 1L }, { "fence", 0L },
                { "approved", true }, { "expiresAt", catalog.Clock.UtcNow.AddHours(1).UtcDateTime }, { "branchId", 11L },
                { "tax", new BsonDecimal128(0m) }, { "charges", new BsonDecimal128(1500m) }, { "creditLimit", new BsonDecimal128(1000000m) } });
            data.Admission = new(data); return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => Prices.DisposeAsync();

    internal async Task AddSecondFulfillmentAsync(int stock) {
        var catalog = Prices.Catalog; var owner = CatalogAuthoringTestData.OtherOwner;
        await catalog.Base.AddGrantAsync(Prices.Maker, 7300, owner, [Permissions.CatalogManage, Permissions.PricingManage]);
        await catalog.Base.AddGrantAsync(Prices.Checker, 7400, owner, [Permissions.PricingApprove]);
        var family = await catalog.FamilyAsync(Prices.Maker, owner, "OTHER");
        var sku = await catalog.Service.CreateSkuAsync(Prices.Maker, new(family.Id, family.Version, "OTHER-10", "other-base", CatalogAuthoringTestData.Definition(10)), CatalogPriceTestData.Meta("other-sku"));
        await catalog.Service.SetPublicationsAsync(Prices.Maker, new(sku.Id, 1, [new(503)], [new(new(503), 1)]), CatalogPriceTestData.Meta("other-publish"));
        await Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(10003, Buyer.UserId, Buyer.UserId, 503,
            CatalogAuthoringTestData.Audience, AuthorizationStatus.Active, catalog.Clock.UtcNow, null, null, 1, 998, 999, 100603, 1, catalog.Clock.UtcNow)));
        var preview = await Prices.Service.PreviewAsync(Prices.Maker, sku.Id.Value, CatalogPriceAccess.Prepare);
        var proposed = await Prices.Service.ProposeAsync(Prices.Maker, sku.Id.Value, preview.Expected, new("IDR", new(10000m)), CatalogPriceTestData.Meta("other-price"));
        await Prices.Service.DecideAsync(Prices.Checker, proposed["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve, "Independent synthetic other source", CatalogPriceTestData.Meta("other-price-approve"));
        var item = (await catalog.BuyerService.QueryAsync(Buyer, 0, 25, null)).Items.Single(value => value.Sku.Id == sku.Id);
        Input = Input with { Selections = Input.Selections.Add(new(item.Sku.Id, item.Definition.Id, item.Definition.Version, item.CurrentPrice!.Id, item.CurrentPrice.Version, 3)) };
        var location = new StockLocation(new(100), new(201), new(202), new(203));
        var poolId = await ConfigureIsolatedOperationalPoolAsync(location);
        var original = InventoryGraphTests.Balance(); var balance = original with { Id = 1001, PhysicalPoolId = poolId,
            Balance = original.Balance with { OnHand = stock, Key = new(location, item.Sku.Id, item.Definition.Id, new(203)) } };
        await Rows("lots").InsertOneAsync(InventoryGraphTests.Lot(203, item.Sku.Id.Value, item.Definition.Id.Value));
        await Rows("stockBalances").InsertOneAsync(InventoryBson.Balance(balance));
    }

    private async Task<long> ConfigureIsolatedOperationalPoolAsync(StockLocation location)
    {
        var sequence = ++fixturePoolSequence;
        var catalog = Prices.Catalog;
        // These already verified, distinct synthetic people also own the real independent price workflow.
        // Do not seed a second person proof or manufacture Ordering producer records.
        Assert.NotEqual(Prices.Maker.UserId, Prices.Checker.UserId);
        await catalog.Base.AddGrantAsync(Prices.Maker, 6260 + sequence, location.ToScope(), [Permissions.StockSourcePropose, Permissions.StockSourceRead]);
        await catalog.Base.AddGrantAsync(Prices.Checker, 6270 + sequence, location.ToScope(), [Permissions.StockSourceApprove, Permissions.StockSourceRead]);
        var sources = new StockSourceSetupService(Database, new(catalog.Client), new(Database), catalog.Clock);
        var input = new StockSourceSetupInput(location, "isolated-order-pool-" + sequence, "Synthetic isolated order source",
            StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60000, 0, null, null,
            "isolated-discrepancy-owner", "isolated-reviewed-handoff");
        var proposed = await sources.ProposeAsync(Prices.Maker, input, CatalogPriceTestData.Meta("order-source-propose-" + sequence));
        var approved = await sources.DecideAsync(Prices.Checker, proposed["proposalId"].AsInt64, 1,
            StockSourceSetupDecision.Approve, "Independent synthetic source configuration review", CatalogPriceTestData.Meta("order-source-approve-" + sequence));
        var poolId = approved["poolId"].AsInt64;
        var pool = await Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync();
        Assert.False(pool["active"].AsBoolean); Assert.Equal(1L, pool["revision"].AsInt64); Assert.Equal(0L, pool["fence"].AsInt64);
        await new MongoUnitOfWork(catalog.Client).ExecuteAsync(async (tx, ct) => {
            var (head, history, state) = await new OrderStockSourceRecords(Database).HistoryAsync(tx, poolId, ct);
            var transition = Assert.Single(history); Assert.Empty(state);
            Assert.Equal(poolId, head["_id"].AsInt64); Assert.Equal(1L, head["revision"].AsInt64);
            Assert.Equal(approved["decisionId"], head["setupDecisionId"]);
            Assert.Equal("setup", transition["sourceKind"].AsString); Assert.Equal(approved["decisionId"], transition["sourceId"]);
            Assert.Equal(transition["_id"], head["lastTransitionId"]); Assert.Equal(transition["transitionHash"], head["lastHash"]);
            Assert.Equal(OrderStockSourceRecords.Hash(transition), transition["transitionHash"].AsString);
            return 0;
        });
        // UNIT ISOLATION ONLY: preserve the existing synthetic active/on-hand/FixtureAdmission harness.
        // This exact test-only CAS is not the seven-domain governed activation or commercial acceptance.
        // The producer head/hash and independent setup receipt remain exclusively real owner output.
        Assert.Equal(1L, (await Rows("inventoryPools").UpdateOneAsync(pool,
            Builders<BsonDocument>.Update.Set("active", true))).ModifiedCount);
        return poolId;
    }

    internal sealed class FixtureAdmission(OrderSubmissionTestData data) : IOrderAdmissionReader
    {
        public IMongoDatabase Database => data.Database;
        internal Func<OrderAdmission, OrderAdmission>? Transform { get; set; }
        internal Action? OnComplete { get; set; }
        private async Task<BsonDocument> Policy(IClientSessionHandle tx, CancellationToken ct) {
            var row = await data.Rows("fixtureOrderApprovals").Find(tx, new BsonDocument("_id", 800L)).SingleOrDefaultAsync(ct);
            if (row is null || !row["approved"].AsBoolean || row["expiresAt"].AsBsonDateTime.ToUniversalTime() <= data.Prices.Catalog.Clock.UtcNow.UtcDateTime)
                throw OrderAdmissionPolicy.Invalid();
            return row;
        }
        public async Task<OrderAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, OrderDraftInput input, OrderCatalogQuote quote, CancellationToken ct)
        {
            var row = await Policy(tx, ct);
            if (input.Context != data.Input.Context || input.Delivery != data.Input.Delivery) throw OrderAdmissionPolicy.Invalid();
            var total = Money.Add(Money.Add(quote.CatalogSubtotal, new(Decimal128.ToDecimal(row["tax"].AsDecimal128))), new(Decimal128.ToDecimal(row["charges"].AsDecimal128)));
            var outstanding = await data.Rows("orders").Find(tx, new BsonDocument("branchId", row["branchId"])).ToListAsync(ct);
            if (outstanding.Sum(order => Decimal128.ToDecimal(order["terms"]["total"].AsDecimal128)) + total.Value > Decimal128.ToDecimal(row["creditLimit"].AsDecimal128))
                throw OrderAdmissionPolicy.Invalid("order_credit_unavailable");
            await data.Rows("fixtureOrderApprovals").UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
            var allocations = ImmutableArray.CreateBuilder<OrderStockAllocation>();
            foreach (var line in quote.Lines) {
                var balance = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(tx, new BsonDocument("skuId", line.Definition.SkuId.Value)).SingleAsync(ct));
                var pool = await data.Rows("inventoryPools").Find(tx, new BsonDocument("_id", balance.PhysicalPoolId)).SingleAsync(ct);
                if (!pool["active"].AsBoolean) throw OrderAdmissionPolicy.Invalid();
                allocations.Add(new(line.Definition.SkuId.Value, new(900, 1), new(balance.PhysicalPoolId, balance.Balance.Key.Location, pool["revision"].AsInt64),
                    new(balance.Id, balance.PhysicalPoolId, balance.Balance.Key, balance.Balance.Revision, InventoryBalanceAction.Reserve, line.Packages)));
            }
            var terms = new OrderCommercialTerms(new(800, row["revision"].AsInt64), new(801, 1), new(802, row["revision"].AsInt64), new(803, 1),
                new(OrderSpendingMode.EveryOrder, null), new(new(804, 1), "Cabang fixture", "Alamat badan fixture"), new(new(805, 1), data.Input.Delivery!),
                new(Decimal128.ToDecimal(row["tax"].AsDecimal128)), new(Decimal128.ToDecimal(row["charges"].AsDecimal128)), total);
            var admitted = new OrderAdmission(input.Context, terms, allocations.ToImmutable()); return Transform?.Invoke(admitted) ?? admitted;
        }
        public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, OrderAdmission admission, CancellationToken ct) {
            OnComplete?.Invoke(); var row = await Policy(tx, ct);
            if (row["revision"].AsInt64 != admission.Terms.Policy.Revision) throw OrderAdmissionPolicy.Invalid();
        }
    }
}
