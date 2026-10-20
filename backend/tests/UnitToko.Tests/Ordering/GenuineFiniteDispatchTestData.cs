using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Organizations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Inventory;

namespace UnitToko.Tests.Ordering;

/// <summary>Local synthetic rehearsal only. Business stock, addresses, spending,
/// commercial terms, reservations, dispatch and journals use their real owners.
/// Reviewed people/catalog assignments and contact labels remain test bootstrap;
/// these test-only admissions are never registered as production adapters.</summary>
[SupportedOSPlatform("windows")]
internal sealed class GenuineFiniteDispatchTestData(IAsyncDisposable lifetime, StockReleaseServiceTests.Data stock, long balanceId) : IAsyncDisposable
{
    internal StockReleaseServiceTests.Data Stock => stock;
    internal IMongoDatabase Database => stock.Database;
    internal MongoUnitOfWork Unit => stock.Unit;
    internal MutableAuthorizationClock Clock => stock.Clock;
    internal StoreTestData Base => stock.Valuation.Base;
    internal long BalanceId => balanceId;
    internal long SkuId;
    internal CatalogAuthoringTestData Catalog = null!;
    internal ActorContext Buyer = null!, Spender = null!, Dispatcher = null!, Courier = null!;
    internal long OrderId, SuborderId;
    internal OrderDraftInput Input = null!;
    internal OrderSubmissionService Orders = null!;
    internal ShipmentPreparationService Preparations = null!;
    internal ShipmentDispatchService Dispatch = null!;
    internal DispatchAdmission Admission = null!;
    private OrganizationAddressService Addresses = null!;
    private OrderSpendingPolicyService Spending = null!;
    private OrderCommercialPolicyService Commercial = null!;
    private readonly ConcurrentDictionary<IClientSessionHandle, OrderFrame> orderReads = new();
    internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic rehearsal through genuine finite dispatch owners", "genuine-finite-dispatch");
    private static readonly OrderBuyingContext Buying = new(new(10), new(11));
    private static readonly OperationalBookKey Seller = new(100, 101, 103, "IDR");
    private static string Postal(PostalAddress address) => address.Line1 + (address.Line2 is null ? "" : "\n" + address.Line2);
    private static string Party(PostalAddress address) => string.Join(", ", new[] {
        address.Line1, address.Line2, address.City, address.Region, address.PostalCode, address.CountryCode }.OfType<string>());

    internal async Task<ActorContext> IssueExistingSessionAsync(ActorContext actor) {
        var issued = await Unit.ExecuteAsync(async (tx, ct) => {
            var identity = await Rows("identities").Find(tx, new BsonDocument { { "_id", actor.UserId }, { "issuer", actor.ProviderIssuer },
                { "subject", actor.ProviderSubject }, { "grantVersion", actor.GrantVersion }, { "status", "active" } }).SingleAsync(ct);
            long securityVersion;
            if (identity.TryGetValue("securityVersion", out var current)) {
                if (!current.IsInt64 || current.AsInt64 <= 0) throw new InvalidOperationException("The existing fixture identity security version is unavailable");
                securityVersion = current.AsInt64;
            } else {
                securityVersion = 1;
                if ((await Rows("identities").UpdateOneAsync(tx, identity, Builders<BsonDocument>.Update.Set("securityVersion", securityVersion), cancellationToken: ct)).ModifiedCount != 1)
                    throw new InvalidOperationException("The existing fixture identity changed before provider session admission");
            }
            var now = Clock.UtcNow;
            var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "dispatch-fixture@example.invalid", true,
                "fixture-connection", now, now, "otp", "fixture-policy");
            return await Catalog.Sessions.CreateAsync(tx, new(actor.UserId, securityVersion, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject),
                proof, "synthetic-genuine-dispatch-provider", ct);
        });
        var verified = await Catalog.Sessions.ReadAsync(issued.OpaqueToken) ?? throw new InvalidOperationException("The produced fixture session could not be verified");
        if (verified.UserId != actor.UserId || verified.GrantVersion != actor.GrantVersion || verified.Reference != issued.Session.Reference ||
            verified.Proof.Issuer != actor.ProviderIssuer || verified.Proof.Subject != actor.ProviderSubject)
            throw new InvalidOperationException("The verified fixture session does not belong to its existing identity");
        Catalog.Issued[actor.UserId] = issued;
        return new ActorContext(verified.UserId, verified.Proof.Issuer, verified.Proof.Subject, verified.GrantVersion,
            verified.Proof.FactorCompletedAt, FactorAssurance.MultiFactor, verified.Reference).Validate();
    }

    internal static async Task<GenuineFiniteDispatchTestData> CreateAsync(MongoFixture fixture, bool converted, bool cogs = true, bool createOrder = true,
        long reviewedSafetyBufferBaseUnits = 12, bool useSystemClock = false, bool includeCustomerMappings = false, bool includeCustomerCashMappings = false) {
        if (converted && includeCustomerCashMappings) throw new ArgumentException("The customer cash fixture seam uses genuine unconverted packages", nameof(includeCustomerCashMappings));
        StockReleaseServiceTests.Data stock; IAsyncDisposable lifetime; long balance;
        if (converted) {
            var conversion = await PackConversionServiceTests.PreparedConversion.CreateAsync(fixture, includeCogsMapping: cogs,
                reviewedSafetyBufferBaseUnits: reviewedSafetyBufferBaseUnits, useSystemClock: useSystemClock, includeCustomerMappings: includeCustomerMappings);
            stock = conversion.Data; lifetime = conversion; balance = conversion.Result!["destinationBalanceId"].AsInt64;
        } else {
            stock = await StockReleaseServiceTests.Data.CreateAsync(fixture, useSystemClock: useSystemClock, includeCogsMapping: cogs,
                reviewedSafetyBufferBaseUnits: reviewedSafetyBufferBaseUnits, includeCustomerMappings: includeCustomerMappings,
                includeCustomerCashMappings: includeCustomerCashMappings); lifetime = stock;
            var release = await stock.ProposeAsync("dispatch-source-release");
            await stock.DecideAsync(release["proposalId"].AsInt64, "dispatch-source-released");
            balance = stock.Valuation.Receipt["balanceId"].AsInt64;
        }
        var data = new GenuineFiniteDispatchTestData(lifetime, stock, balance);
        try { await data.PrepareAsync(fixture, converted ? 12 : 1, createOrder); return data; }
        catch (Exception failure) {
            foreach (var key in new[] { "purchaseOrderCapture.collection", "purchaseOrderCapture.expectedCount", "purchaseOrderCapture.actualCount",
                "purchaseOrderCapture.changedIds", "purchaseOrderCapture.changedFields" })
                if (failure.Data.Contains(key)) Console.Error.WriteLine(key + "=" + failure.Data[key]);
            await data.DisposeAsync(); throw;
        }
    }
    private async Task PrepareAsync(MongoFixture fixture, int packages, bool createOrder) {
        var physical = await Rows("stockBalances").Find(new BsonDocument("_id", balanceId)).SingleAsync(); SkuId = physical["skuId"].AsInt64;
        Catalog = await CatalogAuthoringTestData.AttachAsync(Base, fixture.Client); await Catalog.AssortmentsAsync(); Buyer = Catalog.Buyer;
        await Base.AddGrantAsync(Buyer, 710600, CatalogAuthoringTestData.Audience, [Permissions.OrderSubmit, Permissions.OrderRead, Permissions.OrderCancel]);
        var shop = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var priceMaker = await Base.AddActorAsync(710001, shop, Permissions.CatalogManage, Permissions.PricingManage);
        var priceChecker = await Base.AddActorAsync(710002, shop, Permissions.PricingApprove);
        Spender = await Base.AddActorAsync(710003, new(GrantScopeKind.BuyingBranch, 10, BranchId: 11), Permissions.OrderSpendApprove, Permissions.BranchPic);
        Dispatcher = await Base.AddActorAsync(710004, shop, Permissions.OrderAccept, Permissions.ShipmentPrepare, Permissions.ShipmentDispatch, Permissions.ShipmentAssign);
        Courier = await Base.AddActorAsync(710005, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), Permissions.ShipmentRead, Permissions.DeliveryAttempt);
        await PersonTestData.SeedDistinctPeopleAsync(Database, Clock, priceMaker.UserId, priceChecker.UserId, Spender.UserId, Dispatcher.UserId, Courier.UserId, Buyer.UserId);
        Spender = await IssueExistingSessionAsync(Spender); Dispatcher = await IssueExistingSessionAsync(Dispatcher); Courier = await IssueExistingSessionAsync(Courier);
        // Contact labels are synthetic participant bootstrap; no delivery/contact proof is asserted.
        await Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", Courier.UserId }, { "schemaVersion", 1 }, { "version", 1L },
            { "displayName", "Synthetic courier" }, { "verifiedEmail", "courier@example.invalid" }, { "verifiedAt", Clock.UtcNow.UtcDateTime }, { "updatedAt", Clock.UtcNow.UtcDateTime } });
        var sku = await Rows("skus").Find(new BsonDocument("_id", SkuId)).SingleAsync(); var assortment = await Rows("catalogAssortments").Find(new BsonDocument("_id", 501L)).SingleAsync();
        await Catalog.Service.SetPublicationsAsync(priceMaker, new(new(SkuId), sku["revision"].AsInt64, [new(501)], [new(new(501), assortment["version"].AsInt64)]), Meta("dispatch-published"));
        var prices = new CatalogPriceService(Database, Unit, new(Database), Clock); var price = await prices.PreviewAsync(priceMaker, SkuId, CatalogPriceAccess.Prepare);
        var proposedPrice = await prices.ProposeAsync(priceMaker, SkuId, price.Expected, new("IDR", Money.Parse("20000.00")), Meta("dispatch-price"));
        await prices.DecideAsync(priceChecker, proposedPrice["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve, "Synthetic independent selling-price review", Meta("dispatch-price-approved"));

        var organizations = stock.Valuation.Inspection.Receiving.Procurement.Organizations;
        Addresses = organizations.Service;
        foreach (var (target, purpose) in new[] { (new OrganizationAddressTarget(ScopeNodeKind.BuyingOrganization, 10), OrganizationAddressPurpose.LegalBilling),
            (new OrganizationAddressTarget(ScopeNodeKind.Branch, 11), OrganizationAddressPurpose.Delivery),
            (new OrganizationAddressTarget(ScopeNodeKind.FulfillmentOrganization, 100), OrganizationAddressPurpose.LegalBilling) }) {
            var current = await Unit.ExecuteAsync((tx, ct) => organizations.Organizations.ReadAddressProfileAsync(tx, target, ct));
            var head = await Rows("organizationAddressHeads").Find(new BsonDocument { { "target", OrganizationAddressBson.Target(target) }, { "purpose", OrganizationAddressBson.Purpose(purpose) } }).SingleOrDefaultAsync();
            var input = new OrganizationAddressInput(target, purpose, current["target"]["version"].AsInt64, current["profile"]["version"].AsInt64,
                head?["version"].AsInt64 ?? 0, UnitToko.Tests.Organizations.OrganizationAddressTestData.Postal);
            var preview = await Addresses.PreviewAsync(organizations.Maker, input);
            var proposal = await Addresses.ProposeAsync(organizations.Maker, input, preview.Binding, Meta("dispatch-postal-" + target.Id + "-" + purpose));
            var review = await Addresses.PreviewDecisionAsync(organizations.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve);
            await Addresses.DecideAsync(organizations.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve, review.Binding, Meta("dispatch-postal-approved-" + target.Id + "-" + purpose));
        }
        var policyMaker = await Base.AddActorAsync(710006, new(GrantScopeKind.BuyingBranch, 10, BranchId: 11), Permissions.OrderSpendingPolicyPropose, Permissions.BranchPic);
        var policyChecker = await Base.AddActorAsync(710007, new(GrantScopeKind.BuyingBranch, 10, BranchId: 11), Permissions.OrderSpendingPolicyApprove, Permissions.BranchPic);
        var commercialMaker = await Base.AddActorAsync(710008, shop, OrderCommercialPolicyBson.ProposePermission);
        var commercialChecker = await Base.AddActorAsync(710009, shop, OrderCommercialPolicyBson.ApprovePermission);
        await PersonTestData.SeedDistinctPeopleAsync(Database, Clock, policyMaker.UserId, policyChecker.UserId, commercialMaker.UserId, commercialChecker.UserId);
        var profiles = await Unit.ExecuteAsync(async (tx, ct) => (
            Branch: await organizations.Organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Branch, 11), ct),
            Organization: await organizations.Organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.FulfillmentOrganization, 100), ct),
            Shop: await organizations.Organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Shop, 101), ct),
            Channel: await organizations.Organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Channel, 103), ct)));
        Spending = new(Database, Unit, new(Database), Clock, organizations.Organizations);
        var spendInput = new OrderSpendingPolicyInput(new(ScopeNodeKind.Branch, 11), profiles.Branch["target"]["version"].AsInt64,
            profiles.Branch["profile"]["version"].AsInt64, 0, new(OrderSpendingMode.EveryOrder, null), Clock.UtcNow.AddMinutes(-1), Clock.UtcNow.AddDays(1));
        var spendPreview = await Spending.PreviewAsync(policyMaker, spendInput);
        var spendProposal = await Spending.ProposeAsync(policyMaker, spendInput, spendPreview.Binding, Meta("dispatch-spending"));
        var spendReview = await Spending.PreviewDecisionAsync(policyChecker, spendProposal["proposalId"].AsInt64, 1, OrderSpendingPolicyDecision.Approve);
        await Spending.DecideAsync(policyChecker, spendProposal["proposalId"].AsInt64, 1, OrderSpendingPolicyDecision.Approve, spendReview.Binding, Meta("dispatch-spending-approved"));
        Commercial = new(Database, Unit, new(Database), Clock, organizations.Organizations);
        var rule = OrderCommercialPolicyRuleTests.Rule with { Applicability = new(OrderCommercialApplicability.NotApplicable, OrderCommercialApplicability.NotApplicable,
            OrderCommercialApplicability.Required, OrderCommercialApplicability.NotApplicable) };
        var commercialInput = new OrderCommercialPolicyInput(new(ScopeNodeKind.Branch, 11, Seller), profiles.Branch["target"]["version"].AsInt64,
            profiles.Branch["profile"]["version"].AsInt64, new(profiles.Organization["target"]["version"].AsInt64, profiles.Organization["profile"]["version"].AsInt64,
                profiles.Shop["target"]["version"].AsInt64, profiles.Shop["profile"]["version"].AsInt64, profiles.Channel["target"]["version"].AsInt64, profiles.Channel["profile"]["version"].AsInt64),
            0, rule, Clock.UtcNow.AddMinutes(-1), Clock.UtcNow.AddDays(1));
        var commercialPreview = await Commercial.PreviewAsync(commercialMaker, commercialInput);
        var commercialProposal = await Commercial.ProposeAsync(commercialMaker, commercialInput, commercialPreview.Binding, Meta("dispatch-commercial"));
        var commercialReview = await Commercial.PreviewDecisionAsync(commercialChecker, commercialProposal["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve);
        await Commercial.DecideAsync(commercialChecker, commercialProposal["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve, commercialReview.Binding, Meta("dispatch-commercial-approved"));

        var item = Assert.Single((await Catalog.BuyerService.QueryAsync(Buyer, 0, 25, null)).Items.Where(value => value.Sku.Id.Value == SkuId));
        var postal = UnitToko.Tests.Organizations.OrganizationAddressTestData.Postal;
        Input = new(Buying, [new(item.Sku.Id, item.Definition.Id, item.Definition.Version, item.CurrentPrice!.Id, item.CurrentPrice.Version, packages)],
            new("Synthetic receiving participant", "+628123456789", Postal(postal), postal.City, postal.PostalCode, null), "Synthetic finite dispatch request");
        if (!createOrder) return;
        Orders = new(Database, Unit, new(Database), Clock, new OrderAdmissionReader(this));
        var orderPreview = await Orders.PreviewAsync(Buyer, Input); var ordered = await Orders.SubmitAsync(Buyer, Input, orderPreview.Review, Meta("dispatch-real-reservation"));
        OrderId = ordered["orderId"].AsInt64; SuborderId = Assert.Single(ordered["suborderIds"].AsBsonArray).AsInt64;
        var spendOrder = await Orders.ReviewOrderAsync(Spender, OrderId, null, OrderReviewStage.Spending);
        await Orders.DecideOrderAsync(Spender, OrderId, null, OrderReviewStage.Spending, spendOrder.Revision, spendOrder.Review, Meta("dispatch-order-spending-approved"));
        var store = await Orders.ReviewOrderAsync(Dispatcher, OrderId, SuborderId, OrderReviewStage.Store);
        await Orders.DecideOrderAsync(Dispatcher, OrderId, SuborderId, OrderReviewStage.Store, store.Revision, store.Review, Meta("dispatch-order-accepted"));
        Preparations = new(Database, Unit, new(Database), Clock, Orders); Admission = new(this);
        Dispatch = new(Database, Unit, new(Database), Clock, Orders, Preparations, Admission);
    }
    private sealed record OrderFrame(ActorContext Actor, AuthorizationSnapshot Authority, OrderAdmission Admission,
        OrganizationPostalSource Legal, OrganizationPostalSource Delivery, OrderSpendingPolicySource Spending, OrderCommercialPolicySource Commercial,
        ShipmentDispatchFiniteSource.Capture Stock, StockSourceSetupService SourceOwner, StockSourceSetupService.OrderSourceCapture Source,
        OrderQuotedLine Line);
    private sealed class OrderAdmissionReader(GenuineFiniteDispatchTestData data) : IOrderAdmissionReader {
        public IMongoDatabase Database => data.Database;
        public async Task<OrderAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, OrderDraftInput input, OrderCatalogQuote quote, CancellationToken ct) {
            var authority = await data.Base.Store.LoadAsync(actor, tx, ct);
            new OrderQuoteService(data.Database, data.Unit, data.Clock).Require(actor, authority, input.Context);
            var resource = input.Context.Resource(actor.UserId);
            var legal = await data.Addresses.ReadCurrentSourceAsync(actor, authority, tx, new(ScopeNodeKind.BuyingOrganization, 10), OrganizationAddressPurpose.LegalBilling, resource, Permissions.OrderSubmit, ct);
            var delivery = await data.Addresses.ReadCurrentSourceAsync(actor, authority, tx, new(ScopeNodeKind.Branch, 11), OrganizationAddressPurpose.Delivery, resource, Permissions.OrderSubmit, ct);
            var spending = await data.Spending.ReadCurrentSourceAsync(actor, authority, tx, input.Context, ct);
            var commercial = await data.Commercial.ReadCurrentSourceAsync(actor, authority, tx, input.Context, Seller, ct);
            if (commercial.Rule.Applicability.Budget == OrderCommercialApplicability.Required || commercial.Rule.Applicability.CostCenter == OrderCommercialApplicability.Required ||
                commercial.Rule.Applicability.CreditHold == OrderCommercialApplicability.Required) throw OrderAdmissionPolicy.Invalid("order_capacity_unavailable");
            if (input.Context != Buying || input.Delivery?.Address != Postal(delivery.Address) || input.Delivery.City != delivery.Address.City || input.Delivery.PostalCode != delivery.Address.PostalCode)
                throw OrderAdmissionPolicy.Invalid();
            var physical = await new InventoryRepository(data.Database).ReadAsync(tx, data.BalanceId, ct); var line = Assert.Single(quote.Lines);
            if (line.Definition.SkuId.Value != physical.Balance.Key.SkuId.Value || line.Definition.Id != physical.Balance.Key.DefinitionId ||
                line.FulfillmentOwner.OrganizationId != physical.Balance.Key.Location.OrganizationId.Value || line.FulfillmentOwner.ShopId != physical.Balance.Key.Location.ShopId.Value ||
                line.FulfillmentOwner.ChannelId != physical.Balance.Key.Location.ChannelId.Value || line.FulfillmentOwner.WarehouseId is { } warehouse && warehouse != physical.Balance.Key.Location.WarehouseId.Value)
                throw OrderAdmissionPolicy.Invalid();
            // The buyer is authorized by its actual quoted catalog entitlement;
            // no staff actor or StockConvert grant is borrowed for this read.
            var captured = await new ShipmentDispatchFiniteSource(data.Database, data.Unit, new(data.Database), data.Clock).ReadAsync(tx, physical, ct);
            _ = StockBalancePolicy.Reserve(physical.Balance, physical.Balance.Revision, line.Packages);
            var pool = await data.Rows("inventoryPools").Find(tx, new BsonDocument("_id", new BsonInt64(physical.PhysicalPoolId))).SingleAsync(ct);
            var sourceOwner = data.Stock.Valuation.Sources;
            var source = await sourceOwner.CaptureOrderSubmitSourceAsync(actor, authority, tx, input.Context, line, physical.Balance.Key.Location,
                physical.PhysicalPoolId, pool["revision"].AsInt64, ct);
            source.RequireContext(sourceOwner, actor, authority, tx, input.Context, line);
            var totals = commercial.Calculate(quote); var policy = new OrderPolicyReference(commercial.PolicyId, commercial.Version);
            var terms = new OrderCommercialTerms(policy, policy, policy, new(spending.PolicyId, spending.Version), spending.Rule,
                new(new(legal.AddressId, legal.Version), legal.Snapshot["source"]["profile"]["name"].AsString, Party(legal.Address)),
                new(new(delivery.AddressId, delivery.Version), input.Delivery), totals.Tax, totals.Charges, totals.Total);
            var admission = new OrderAdmission(input.Context, terms, [new(line.Definition.SkuId.Value, source.Authority,
                source.Pool, new(physical.Id, physical.PhysicalPoolId, physical.Balance.Key,
                    physical.Balance.Revision, InventoryBalanceAction.Reserve, line.Packages))]);
            data.orderReads[tx] = new(actor, authority, admission, legal, delivery, spending, commercial, captured, sourceOwner, source, line); return admission;
        }
        public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, OrderAdmission admission, CancellationToken ct) {
            if (!data.orderReads.TryGetValue(tx, out var read) || read.Actor != actor || read.Admission.Context != admission.Context || read.Admission.Terms != admission.Terms)
                throw OrderAdmissionPolicy.Invalid();
            foreach (var postal in new[] { read.Legal, read.Delivery }) { postal.RequireContext(actor, read.Authority, tx); await postal.FenceAsync(ct); }
            read.Spending.RequireContext(actor, read.Authority, tx); await read.Spending.FenceAsync(ct);
            read.Commercial.RequireContext(actor, read.Authority, tx); await read.Commercial.FenceAsync(ct);
            read.Source.RequireContext(read.SourceOwner, actor, read.Authority, tx, admission.Context, read.Line);
            await read.Source.FenceAsync(ct);
            read.Source.RequireContext(read.SourceOwner, actor, read.Authority, tx, admission.Context, read.Line);
            if (admission.Stock.Single().SourceAuthority != read.Source.Authority || admission.Stock.Single().Pool != read.Source.Pool)
                throw OrderAdmissionPolicy.Invalid();
            await read.Stock.RequireOriginAsync(data.Database, tx, ct);
            var actual = await new InventoryRepository(data.Database).ReadAsync(tx, read.Stock.Before.Id, ct);
            var reserve = admission.Stock.Single().Change;
            var written = read.Stock.Before with { Balance = StockBalancePolicy.Reserve(read.Stock.Before.Balance, reserve.ExpectedRevision, reserve.Packages) };
            if (actual != read.Stock.Before && actual != written) throw OrderAdmissionPolicy.Invalid();
        }
    }
    internal sealed class DispatchAdmission(GenuineFiniteDispatchTestData data) : IShipmentDispatchAdmissionReader {
        public IMongoDatabase Database => data.Database;
        internal Func<IClientSessionHandle, CancellationToken, Task>? BeforeComplete;
        private readonly ConcurrentDictionary<IClientSessionHandle, (ActorContext Actor, AuthorizationSnapshot Authority, OrganizationPostalSource Seller,
            ImmutableArray<ShipmentDispatchFiniteSource.Capture> Lots)> reads = new();
        public async Task<ShipmentDispatchAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, OrderDispatchSource source,
            ImmutableArray<ShipmentLotSelection> selected, CancellationToken ct) {
            var authority = await data.Base.Store.LoadAsync(actor, tx, ct);
            if (!ScopePolicy.Authorize(actor, authority, Permissions.ShipmentDispatch, source.Order.Resource, data.Clock.UtcNow).Allowed) throw ShipmentDispatchAdmissionPolicy.Invalid();
            var seller = await data.Addresses.ReadCurrentSourceAsync(actor, authority, tx, new(ScopeNodeKind.FulfillmentOrganization, 100), OrganizationAddressPurpose.LegalBilling,
                source.Order.Resource, Permissions.ShipmentDispatch, ct);
            var captures = ImmutableArray.CreateBuilder<ShipmentDispatchFiniteSource.Capture>(); var lots = ImmutableArray.CreateBuilder<ShipmentLotAuthority>();
            BsonDocument? policy = null;
            foreach (var chosen in selected) {
                var allocation = source.Reservations.Single(row => row.Fact.ReservationId == chosen.ReservationId).Allocation;
                var physical = await new InventoryRepository(data.Database).ReadAsync(tx, allocation.Change.Id, ct);
                var capture = await new ShipmentDispatchFiniteSource(data.Database, data.Unit, new(data.Database), data.Clock).ReadAsync(tx, physical, ct); captures.Add(capture);
                var pool = await data.Rows("inventoryPools").Find(tx, new BsonDocument("_id", physical.PhysicalPoolId)).SingleAsync(ct);
                await data.Stock.Valuation.Sources.RequireActivatedPoolAsync(tx, pool, ct);
                var approved = await data.Rows(StockSourcePolicyBson.Policies).Find(tx, new BsonDocument("poolId", physical.PhysicalPoolId)).Sort(new BsonDocument("policyRevision", -1)).FirstOrDefaultAsync(ct)
                    ?? throw ShipmentDispatchAdmissionPolicy.Invalid();
                if (policy is not null && policy != approved) throw ShipmentDispatchAdmissionPolicy.Invalid(); policy = approved;
                // Compatibility field comes from the actual one-package policy
                // allocation; the dispatch finite total is reviewed separately.
                var package = LotValuePolicy.TakeProportionalFloor(capture.Value, capture.Value.Revision, 1).TransferredValue;
                lots.Add(new(chosen.ReservationId.Value, allocation.SourceAuthority, new(physical.Id, capture.Value.Revision), package));
            }
            reads[tx] = (actor, authority, seller, captures.ToImmutable());
            return new(new(new(seller.AddressId, seller.Version), seller.Snapshot["source"]["profile"]["name"].AsString, Party(seller.Address)),
                new(policy!["_id"].AsInt64, policy["policyRevision"].AsInt64), lots.ToImmutable(), actor.FactorCompletedAt.Add(Permissions.Catalog[Permissions.ShipmentDispatch].Assurance.MaximumAge ?? SessionPolicy.FreshFactorLifetime));
        }
        public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, ShipmentDispatchAdmission admission, CancellationToken ct) {
            if (BeforeComplete is { } callback) await callback(tx, ct);
            if (!reads.TryGetValue(tx, out var read) || read.Actor != actor) throw ShipmentDispatchAdmissionPolicy.Invalid();
            read.Seller.RequireContext(actor, read.Authority, tx); await read.Seller.FenceAsync(ct);
            foreach (var lot in read.Lots) await lot.RequireOriginAsync(data.Database, tx, ct);
            ShipmentDispatchAdmissionPolicy.RequireCurrent(admission, data.Clock.UtcNow);
        }
    }
    internal async Task<ShipmentDispatchReview> PrepareDispatchAsync(int packages, string key, long revision = 0) {
        var source = await Orders.ReadFulfillmentOrderAsync(Dispatcher, SuborderId);
        await Preparations.SaveAsync(Dispatcher, SuborderId, revision, source.Review, [new(new(SkuId), packages)], Meta(key));
        var prepared = await Dispatch.ReadPreparationAsync(Dispatcher, SuborderId);
        return await Dispatch.PreviewAsync(Dispatcher, SuborderId, prepared.PreparationId.Value, prepared.PreparationRevision, Courier.UserId,
            [new(Assert.Single(prepared.Lots).Reservation.ReservationId, packages)]);
    }
    internal Task<BsonDocument> DispatchAsync(ShipmentDispatchReview review, string key) => Dispatch.DispatchAsync(Dispatcher, SuborderId,
        review.Preparation.PreparationId.Value, review.Preparation.PreparationRevision, Courier.UserId, review.Selected, review.Review, Meta(key));
    internal async Task<BsonDocument> BusinessAsync() {
        var snapshot = new BsonDocument();
        foreach (var name in new[] { "orders", "orderSuborders", "reservations", "shipments", "stockBalances", "movements", LotValueStore.HeldHeads,
            LotValueStore.AvailableHeads, LotValueStore.Transitions, "journalEntries", "accountingAuditEvents", "outboxEvents", "idempotencyRecords", "auditEvents",
            OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions, FulfillmentStockSourceRecords.Heads, FulfillmentStockSourceRecords.Transitions }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); snapshot.Add(name, new BsonArray(rows));
        }
        return snapshot;
    }
    public ValueTask DisposeAsync() => lifetime.DisposeAsync();
}
