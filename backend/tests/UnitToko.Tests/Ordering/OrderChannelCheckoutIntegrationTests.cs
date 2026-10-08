using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
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
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Organizations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;
using Xunit.Abstractions;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using Q = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;

namespace UnitToko.Tests.Ordering;

/// <summary>One local Mongo database and actual independent business commands. Identity,
/// catalog assignments, scanner/model acceptance and external supplier/finance/quality
/// policy inputs are explicitly synthetic; this proves no production provider approval.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderChannelCheckoutIntegrationTests(MongoFixture fixture, ITestOutputHelper output)
{
    private static AuthorizationCommandMetadata Meta(string key) => new("channel-checkout-" + key,
        "Explicit local synthetic two-channel rehearsal through actual business owners", "channel-checkout-test");
    private sealed record SecondChannel(StockLocation Location, long BalanceId, ActorContext Maker, ActorContext Checker,
        OperationalBookKey Seller, OrderDraftInput Input, BsonDocument Commercial);

    [WindowsStorageFact]
    public async Task Independent_channel_roots_preserve_a_committed_group_when_required_capacity_refuses_another_and_recover_without_reserving_twice()
    {
        var timing = Stopwatch.StartNew();
        void Stage(string stage) { output.WriteLine($"channel-checkout {stage}: {timing.Elapsed.TotalSeconds:F3}s"); timing.Restart(); }
        await using var first = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, cogs: false, createOrder: false,
            reviewedSafetyBufferBaseUnits: 0);
        Stage("first-genuine-released-source");
        var second = await AppendSecondChannelAsync(first, Stage);
        var organizations = first.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
        var reader = new ApprovedOrderAdmissionReader(first.Database, first.Unit, new(first.Database), first.Clock, organizations);
        var orders = new OrderSubmissionService(first.Database, first.Unit, new(first.Database), first.Clock, reader);
        var input = first.Input with { Selections = first.Input.Selections.AddRange(second.Input.Selections) };
        var groups = await new OrderQuoteService(first.Database, first.Unit, first.Clock).CheckoutGroupsAsync(first.Buyer, input);
        Assert.Equal(2, groups.Items.Length);
        Assert.Equal(input.Selections.OrderBy(line => line.SkuId.Value), groups.Items.SelectMany(group => group.Input.Selections).OrderBy(line => line.SkuId.Value));
        var a = groups.Items.Single(group => group.Input.Selections.Any(line => line.SkuId == first.Input.Selections[0].SkuId));
        var b = groups.Items.Single(group => group.Seller == second.Seller);
        Assert.NotEqual(a.GroupKey, b.GroupKey); Assert.NotEqual(a.Seller.ChannelId, b.Seller.ChannelId);
        Assert.Equal(input.Context, a.Input.Context); Assert.Equal(input.Context, b.Input.Context);
        Assert.Equal(input.Delivery, a.Input.Delivery); Assert.Equal(input.Delivery, b.Input.Delivery);
        var money = await MoneyAndOriginsAsync(first);
        var originalA = InventoryBson.ReadBalance(await first.Rows("stockBalances").Find(new BsonDocument("_id", first.BalanceId)).SingleAsync());
        var originalB = InventoryBson.ReadBalance(await first.Rows("stockBalances").Find(new BsonDocument("_id", second.BalanceId)).SingleAsync());
        Assert.NotEqual(originalA.PhysicalPoolId, originalB.PhysicalPoolId);
        Assert.Equal(1L, StockBalancePolicy.AvailablePackages(originalA.Balance)); Assert.Equal(1L, StockBalancePolicy.AvailablePackages(originalB.Balance));

        var previewA = await orders.PreviewAsync(first.Buyer, a.Input);
        var resultA = await orders.SubmitAsync(first.Buyer, a.Input, previewA.Review, Meta("root-a"));
        Stage("first-independent-root-committed");
        var committedA = await first.Rows(B.Orders).Find(new BsonDocument("_id", resultA["orderId"])).SingleAsync();
        var afterA = await OrderEffectsAsync(first);
        Assert.Equal("order_capacity_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() => orders.PreviewAsync(first.Buyer, b.Input))).Code);
        Assert.Equal(afterA, await OrderEffectsAsync(first));
        Assert.Null(await orders.RecoverAsync(first.Buyer, Meta("root-b").IdempotencyKey));
        Assert.Equal(1L, await first.Rows(B.Orders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(money, await MoneyAndOriginsAsync(first));
        Stage("required-capacity-refusal-preserved-first-root");

        // A real subsequent approval explicitly marks capacity NotApplicable. It supplies
        // no credit amount and does not alter the already committed first group.
        var revised = await ApproveCommercialAsync(first, second.Maker, second.Checker, second.Seller, 1,
            budgetRequired: false, "second-capacity-not-applicable");
        Assert.Equal(2L, revised["policyVersion"].AsInt64);
        var previewB = await orders.PreviewAsync(first.Buyer, b.Input);
        var resultB = await orders.SubmitAsync(first.Buyer, b.Input, previewB.Review, Meta("root-b"));
        Stage("second-independent-root-committed");
        Assert.NotEqual(resultA["orderId"], resultB["orderId"]);
        var committedB = await first.Rows(B.Orders).Find(new BsonDocument("_id", resultB["orderId"])).SingleAsync();
        Assert.NotEqual(committedA["terms"]["policy"], committedB["terms"]["policy"]);
        Assert.Equal(revised["policyId"], committedB["terms"]["policy"]["id"]);
        Assert.Equal(2L, committedB["terms"]["policy"]["revision"].AsInt64);
        Assert.Equal(committedA["terms"], (await first.Rows(B.Orders).Find(new BsonDocument("_id", resultA["orderId"])).SingleAsync())["terms"]);
        Assert.Equal(2L, await first.Rows(B.Orders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await first.Rows(B.Suborders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await first.Rows(B.Reservations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(money, await MoneyAndOriginsAsync(first));
        var afterB = await OrderEffectsAsync(first);

        // Restart the production reader/service and look up both original actor keys.
        // Replaying the exact original commands also produces no extra reservation.
        var restarted = new OrderSubmissionService(first.Database, first.Unit, new(first.Database), first.Clock,
            new ApprovedOrderAdmissionReader(first.Database, first.Unit, new(first.Database), first.Clock, organizations));
        Assert.Equal(resultA, await restarted.RecoverAsync(first.Buyer, Meta("root-a").IdempotencyKey));
        Assert.Equal(resultB, await restarted.RecoverAsync(first.Buyer, Meta("root-b").IdempotencyKey));
        Assert.Equal(resultA, await restarted.SubmitAsync(first.Buyer, a.Input, previewA.Review, Meta("root-a")));
        Assert.Equal(resultB, await restarted.SubmitAsync(first.Buyer, b.Input, previewB.Review, Meta("root-b")));
        Assert.Equal(afterB, await OrderEffectsAsync(first)); Assert.Equal(money, await MoneyAndOriginsAsync(first));
        foreach (var (id, original) in new[] { (first.BalanceId, originalA), (second.BalanceId, originalB) }) {
            var current = InventoryBson.ReadBalance(await first.Rows("stockBalances").Find(new BsonDocument("_id", id)).SingleAsync());
            Assert.Equal(original.Balance.OnHand, current.Balance.OnHand); Assert.Equal(original.Balance.Held, current.Balance.Held);
            Assert.Equal(original.Balance.Expired, current.Balance.Expired); Assert.Equal(1L, current.Balance.Reserved);
            Assert.Equal(original.Balance.Revision + 1, current.Balance.Revision); Assert.Equal(0L, StockBalancePolicy.AvailablePackages(current.Balance));
            var capitalized = await first.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", id)).SingleAsync();
            Assert.Equal(1L, capitalized["remainingPackages"].AsInt64); Assert.Equal("11250.00", capitalized["remainingValue"].AsString);
        }
        Stage("original-keys-recovered-with-no-second-stock-or-money-effects");
    }

    private static async Task<SecondChannel> AppendSecondChannelAsync(GenuineFiniteDispatchTestData first, Action<string> stage)
    {
        var valuation = first.Stock.Valuation; var inspection = valuation.Inspection; var documents = valuation.Documents;
        var receiving = inspection.Receiving; var purchasing = receiving.Orders; var procurement = receiving.Procurement;
        var organizations = procurement.Organizations; var database = first.Database; var unit = first.Unit; var clock = first.Clock;
        var ids = new IdAllocator(database); var date = AccountingCalendar.BusinessDate(clock.UtcNow);
        var proposedChannel = await organizations.Organizations.ProposeAsync(organizations.ProfileMaker,
            new(OrganizationChangeKind.Create, ScopeNodeKind.Channel, 100, "SMART-LOCAL", "S-Mart local synthetic channel"), Meta("channel-profile"));
        var approvedChannel = await organizations.Organizations.ApproveAsync(organizations.ProfileChecker, proposedChannel.Id, 1, Meta("channel-profile-approved"));
        var location = new ProcurementLocation(100, procurement.ForeignShopId, procurement.ForeignWarehouseId, approvedChannel.ScopeId!.Value);
        var physicalLocation = new StockLocation(new(location.OrganizationId), new(location.ShopId), new(location.WarehouseId), new(location.ChannelId));
        var shop = new GrantScope(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId);
        var warehouse = new GrantScope(GrantScopeKind.FulfillmentWarehouse, location.OrganizationId, ShopId: location.ShopId, WarehouseId: location.WarehouseId, ChannelId: location.ChannelId);
        var maker = await first.Base.AddActorAsync(880101, warehouse, Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.StockRead,
            Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose);
        var checker = await first.Base.AddActorAsync(880102, warehouse, Permissions.ProcurementRead, Permissions.ProcurementApprove, Permissions.ProcurementContainerReceive,
            Permissions.StockRead, Permissions.StockSourceRead, Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove);
        await first.Base.AddGrantAsync(maker, 880111, shop, [Permissions.CatalogManage, Permissions.PricingManage, Permissions.SupplierChangePropose,
            Permissions.AccountingBookRead, Permissions.AccountingBookPropose, Permissions.OrderCommercialPolicyPropose]);
        await first.Base.AddGrantAsync(checker, 880112, shop, [Permissions.PricingApprove, Permissions.SupplierChangeApprove, Permissions.AccountingBookRead,
            Permissions.AccountingBookApprove, Permissions.OrderCommercialPolicyApprove]);
        var valueMaker = await first.Base.AddActorAsync(880103, warehouse, Permissions.ProcurementRead, Permissions.StockRead, Permissions.StockReceive,
            Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose);
        var valueChecker = await first.Base.AddActorAsync(880104, warehouse, Permissions.ProcurementRead, Permissions.StockRead,
            Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationDecide, Permissions.JournalPost);
        var qualityMaker = await first.Base.AddActorAsync(880105, warehouse, Permissions.ProcurementRead, Permissions.StockRead,
            Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptancePropose);
        var qualityChecker = await first.Base.AddActorAsync(880106, warehouse, Permissions.ProcurementRead, Permissions.StockRead,
            Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptanceDecide);
        var releaseMaker = await first.Base.AddActorAsync(880107, warehouse, Permissions.StockReleasePropose, Permissions.ProcurementStockValuationDecide);
        var releaseChecker = await first.Base.AddActorAsync(880108, warehouse, Permissions.StockReleaseApprove, Permissions.ProcurementStockValuationDecide);
        await PersonTestData.SeedDistinctPeopleAsync(database, clock, maker.UserId, checker.UserId, valueMaker.UserId, valueChecker.UserId,
            qualityMaker.UserId, qualityChecker.UserId, releaseMaker.UserId, releaseChecker.UserId);
        // Existing private workers remain alive. Only new exact scoped test assignments
        // are added; their original uploads, approvals and immutable pins stay untouched.
        await first.Base.AddGrantAsync(inspection.Actor, 880121, warehouse, [Permissions.ProcurementRead, Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead, Permissions.StockRead]);
        await first.Base.AddGrantAsync(documents.Actor, 880122, warehouse, [Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentRecord, Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.StockRead]);
        await first.Base.AddGrantAsync(documents.Matcher, 880123, warehouse, [Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.StockRead]);
        await first.Base.AddGrantAsync(documents.EvidenceReader, 880124, warehouse, [Permissions.ProcurementRead, Permissions.ProcurementSupplierDocumentEvidenceRead]);

        var category = await procurement.Catalog.SaveCategoryAsync(maker, new(shop, null, 0, "CHANNEL-ATK", "Local second-channel stationery"), Meta("category"));
        var family = await procurement.Catalog.CreateFamilyAsync(maker, new(shop, category.Id, "Local second-channel pencils", "Synthetic finite source", []), Meta("family"));
        var sku = await procurement.Catalog.CreateSkuAsync(maker, new(family.Id, family.Version, "CHANNEL-PENCIL-12", "channel-pencil", UnitToko.Tests.Catalog.CatalogAuthoringTestData.Definition()), Meta("sku"));
        var source = new StockSourceSetupService(database, unit, ids, clock);
        var sourceInput = new StockSourceSetupInput(physicalLocation, "local-second-channel", "Synthetic separate canonical channel source",
            StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger, 60001, 0, null, null, "Synthetic local receiving owner", "Synthetic separate empty handoff");
        var sourceProposal = await source.ProposeAsync(maker, sourceInput, Meta("source"));
        var sourceDecision = await source.DecideAsync(checker, sourceProposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic canonical source review", Meta("source-approved"));
        var poolId = sourceDecision["poolId"].AsInt64;
        var policyPreview = await source.PreviewOperatingPolicyAsync(maker, poolId);
        var operating = await source.ProposeOperatingPolicyAsync(maker, poolId, new(policyPreview["poolRevision"].AsInt64,
            policyPreview["suspensionRevision"].AsInt64, policyPreview["policyRevision"].AsInt64, policyPreview["configurationHash"].AsString),
            new(StockMovementWriter.UnitToko, null, 0, "Synthetic per-channel daily reconciliation", "Synthetic per-channel suspend on outage"), Meta("source-policy"));
        await source.DecideOperatingPolicyAsync(checker, operating["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
            "Independent synthetic operating-policy review", Meta("source-policy-approved"));
        var empty = await source.PreviewActivationAsync(maker, poolId);
        Assert.All(empty["obligations"].AsBsonArray, row => { Assert.True(row["complete"].AsBoolean); Assert.Equal(0L, row["openCount"].AsInt64); });
        var handoff = new StockSourceEmptyHandoff(sourceInput.HandoffReference, clock.UtcNow, 0, 0, true, true, true);
        var activation = await source.ProposeActivationAsync(maker, poolId, StockSourceActivationBson.ReadExpected(empty["expected"].AsBsonDocument), handoff, Meta("activate"));
        var activationReview = await source.PreviewActivationDecisionAsync(checker, activation["proposalId"].AsInt64, StockSourceSetupDecision.Approve, handoff);
        var activated = await source.DecideActivationAsync(checker, activation["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve, handoff,
            StockSourceActivationBson.ReadExpected(activationReview["expected"].AsBsonDocument), activationReview["binding"].AsString, Meta("activated"));
        Assert.Equal("activated", activated["status"].AsString); stage("second-canonical-source-activated-before-demand");

        var details = SupplierTestData.Details with { SubjectReference = "SYNTHETIC-SECOND-CHANNEL-SUPPLIER" };
        var supplierPolicyId = (await ids.NextAsync(SupplierTestData.Policies)).Value;
        await first.Rows(SupplierTestData.Policies).InsertOneAsync(new BsonDocument {
            { "_id", supplierPolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "active", true },
            { "scope", new BsonDocument { { "organizationId", location.OrganizationId }, { "shopId", location.ShopId }, { "channelId", location.ChannelId } } },
            { "subjectReference", details.SubjectReference }, { "legalName", details.LegalName },
            { "registrationNamespace", details.RegistrationNamespace is null ? BsonNull.Value : details.RegistrationNamespace },
            { "registrationValue", details.RegistrationValue is null ? BsonNull.Value : details.RegistrationValue },
            { "method", "authenticated-record" }, { "trustedChannelReference", "synthetic-second-channel-directory" }, { "operatingOwnerReference", "SYNTHETIC-EXTERNAL-POLICY-INPUT-NOT-PRODUCTION-APPROVAL" },
            { "approvalReference", "SYNTHETIC-LOCAL-SUPPLIER-POLICY-INPUT" }, { "approvedAt", clock.UtcNow.AddHours(-1).UtcDateTime }, { "expiresAt", clock.UtcNow.AddDays(31).UtcDateTime },
            { "maxObservationAgeSeconds", 900L }, { "allowReferenceOnly", true } });
        var supplierInput = new SupplierChangeInput(new(location.OrganizationId, location.ShopId, location.ChannelId), null, 0, supplierPolicyId, 1, details,
            [new(location, "supplier", "000001", "000012", sku.Id.Value, sku.Version, sku.CurrentDefinitionId!.Value.Value, sku.DefinitionVersion!.Value, clock.UtcNow.AddDays(-1), clock.UtcNow.AddDays(30))]);
        var supplierService = purchasing.Suppliers.Service;
        var supplierPreview = await supplierService.PreviewAsync(maker, supplierInput);
        var proposedSupplier = await supplierService.ProposeAsync(maker, supplierInput, supplierPreview["binding"].AsString, Meta("supplier"));
        var observation = await supplierService.ObserveAsync(checker, proposedSupplier["proposalId"].AsInt64, 1, details, supplierPolicyId, 1,
            "synthetic-second-channel-directory", "synthetic-second-channel-observation", true, Meta("supplier-observed"));
        var supplierReview = await supplierService.PreviewDecisionAsync(checker, proposedSupplier["proposalId"].AsInt64, SupplierDecision.Approve, observation["recordId"].AsInt64);
        var supplier = await supplierService.DecideAsync(checker, proposedSupplier["proposalId"].AsInt64, 1, SupplierDecision.Approve, observation["recordId"].AsInt64,
            supplierReview["binding"].AsString, Meta("supplier-approved"));
        var requestInput = new ProcurementRequestInput(location, ProcurementRoute.Ddo, ProcurementRequestKind.PurchaseRequest, date, null,
            "Synthetic separate-channel procurement demand", [ProcurementRequestTestData.Line(sku, 6) with { Specification = "Synthetic reviewed separate-channel specification" }]);
        var requestPreview = await procurement.Service.PreviewAsync(maker, requestInput);
        var request = await procurement.Service.SubmitAsync(maker, requestInput, requestPreview["binding"].AsString, Meta("request") with { Reason = requestInput.Reason });
        var requestReview = await procurement.Service.PreviewDecisionAsync(checker, request["requestId"].AsInt64, ProcurementRequestDecision.Approve);
        var requestDecision = await procurement.Service.DecideAsync(checker, request["requestId"].AsInt64, 1, ProcurementRequestDecision.Approve, requestReview["binding"].AsString, Meta("request-approved"));
        var purchaseInput = new PurchaseOrderInput(location, request["requestId"].AsInt64, 1, requestDecision["decisionId"].AsInt64,
            supplier["supplierId"].AsInt64, supplier["versionId"].AsInt64, supplier["version"].AsInt64, date, "IDR",
            new("quote", "SYNTHETIC-CHANNEL-QUOTE", date, clock.UtcNow.AddDays(1)), null,
            new("credit", "Synthetic independently reviewed due-date terms", new(0m), "due_date", date.AddDays(30)),
            [new(sku.Id.Value, 3, "supplier", "000001", "000012", sku.CurrentDefinitionId!.Value.Value, sku.DefinitionVersion!.Value, new(10000m), new(3300m), new(700m))]);
        var poolRevision = (await first.Rows("inventoryPools").Find(new BsonDocument("_id", poolId)).SingleAsync())["revision"].AsInt64;
        var inbound = new PurchaseOrderInboundPoolSelection([.. purchaseInput.Lines.Select(line =>
            new PurchaseOrderInboundAllocation(line.RequestSkuId, line.Packages, poolId, poolRevision))]);
        var purchasePreview = await purchasing.Service.PreviewInboundAsync(maker, purchaseInput, inbound);
        var purchaseProposal = await purchasing.Service.ProposeInboundAsync(maker, purchaseInput, inbound, purchasePreview["binding"].AsString, Meta("purchase"));
        var purchaseReview = await purchasing.Service.PreviewDecisionAsync(checker, purchaseProposal["proposalId"].AsInt64, PurchaseOrderDecision.Approve);
        var purchase = await purchasing.Service.DecideAsync(checker, purchaseProposal["proposalId"].AsInt64, 1, PurchaseOrderDecision.Approve, purchaseReview["binding"].AsString, Meta("purchase-approved"));
        var noticeInput = receiving.NoticeInput("SYNTHETIC-CHANNEL-DELIVERY") with { Location = location, OrderId = purchase["orderId"].AsInt64, OrderApprovalId = purchase["decisionId"].AsInt64 };
        var noticePreview = await receiving.Service.PreviewNoticeAsync(maker, noticeInput);
        var notice = await receiving.Service.RecordNoticeAsync(maker, noticeInput, noticePreview["binding"].AsString, Meta("notice"));
        var intakeInput = receiving.IntakeInput("SYNTHETIC-CHANNEL-CONTAINERS");
        var intakePreview = await receiving.Service.PreviewIntakeAsync(checker, notice["noticeId"].AsInt64, 0, intakeInput);
        var intake = await receiving.Service.ReceiveAsync(checker, notice["noticeId"].AsInt64, 0, intakeInput, intakePreview["binding"].AsString, Meta("intake"));
        stage("second-genuine-supplier-demand-purchase-and-custody");

        var expires = date.AddYears(1);
        var inspectionInput = new ProcurementInspectionInput(location, notice["noticeId"].AsInt64, intake["receiptId"].AsInt64, date,
            "SYNTHETIC-CHANNEL-INSPECTION", 2, [new(sku.Id.Value, sku.CurrentDefinitionId!.Value.Value, sku.DefinitionVersion!.Value,
                3, 3, 0, 0, 0, "SYNTHETIC-CHANNEL-LOT", expires, "SYNTHETIC-CHANNEL-HELD-BAY", null)], null);
        var inspectionPreview = await inspection.Service.PreviewAsync(inspection.Actor, inspectionInput);
        var inspectionDraft = await inspection.Service.DraftAsync(inspection.Actor, inspectionInput, inspectionPreview["binding"].AsString, Meta("inspection-draft"));
        var video = await inspection.UploadForAsync(inspectionDraft["draftId"].AsInt64, "channel-inspection-video");
        Assert.Equal("completed", await inspection.Runtime.RunOnceAsync());
        var submitInspection = await inspection.Service.PreviewSubmitAsync(inspection.Actor, inspectionDraft["draftId"].AsInt64, 1, video.Reservation.ReservationId, video.Reference);
        var observedInspection = await inspection.Evidence.SubmitAsync(inspection.Actor, inspectionDraft["draftId"].AsInt64, 1,
            video.Reservation.ReservationId, video.Reference, submitInspection["binding"].AsString, Meta("inspection-submitted"));
        var invoiceInput = documents.Input() with { Location = location, AnchorOrderId = purchase["orderId"].AsInt64, AnchorOrderApprovalId = purchase["decisionId"].AsInt64 };
        var invoicePreview = await documents.Service.PreviewAsync(documents.Actor, invoiceInput);
        var invoiceDraft = await documents.Service.DraftAsync(documents.Actor, invoiceInput, invoicePreview["binding"].AsString, Meta("invoice-draft"));
        var image = await documents.UploadForAsync(invoiceDraft["draftId"].AsInt64, "channel-invoice-image");
        Assert.Equal("completed", await documents.Runtime.RunOnceAsync());
        var submitInvoice = await documents.Service.PreviewSubmitAsync(documents.Actor, invoiceDraft["draftId"].AsInt64, 1, image.Reservation.ReservationId, image.Reference);
        var invoice = await documents.Evidence.SubmitAsync(documents.Actor, invoiceDraft["draftId"].AsInt64, 1, image.Reservation.ReservationId,
            image.Reference, submitInvoice["binding"].AsString, Meta("invoice-submitted"));
        // Reuse the exact live, already configured local OCR runtime/policy; no old
        // publication, extraction result, worker approval or private pin is copied.
        Assert.Equal("completed", await SupplierDocumentOcrTestData.CreateRuntime(documents).RunOnceAsync());
        var extraction = await documents.Evidence.ReadExtractionAsync(documents.EvidenceReader, invoiceDraft["draftId"].AsInt64, 1, image.Reference,
            new("supplier-document-ocr-view", "synthetic-second-channel-original-review"));
        Assert.Equal("succeeded", extraction.State); Assert.NotNull(extraction.ResultId);
        var matchInput = new SupplierDocumentMatchInput(invoice["documentId"].AsInt64, 1, invoiceInput, extraction.ResultId,
            [new(1, 2, observedInspection["inspectionId"].AsInt64, 1, null, null, new(20000m), new(2000m), new(500m), null, null)]);
        var matchPreview = await documents.Service.PreviewMatchAsync(documents.Matcher, matchInput);
        var match = await documents.Service.MatchAsync(documents.Matcher, matchInput, matchPreview["binding"].AsString, Meta("invoice-human-match"));
        var receiptInput = new ProcurementStockReceiptInput(location, purchase["orderId"].AsInt64, 1, purchase["decisionId"].AsInt64,
            observedInspection["inspectionId"].AsInt64, 1, 3, poolId, poolRevision, "SYNTHETIC-CHANNEL-HELD-RECEIPT", "SYNTHETIC-CHANNEL-LOT", expires,
            "Synthetic actual held receipt after independent procurement and private inspection");
        var receiptPreview = await valuation.Receipts.PreviewAsync(valueMaker, receiptInput);
        var receipt = await valuation.Receipts.ReceiveAsync(valueMaker, receiptInput, receiptPreview["binding"].AsString, Meta("held-receipt") with { Reason = receiptInput.Reason });
        stage("second-native-private-inspection-document-ocr-match-and-held-receipt");

        var seller = new OperationalBookKey(location.OrganizationId, location.ShopId, location.ChannelId, "IDR");
        var bookService = new AccountingBookSetupService(database, unit, ids, clock);
        var bookInput = new AccountingBookSetupInput(seller, date.ToString("yyyy-MM", CultureInfo.InvariantCulture), [new("inventory", "A1200"), new("receipt_accrual", "L2100")]);
        var bookPreview = await bookService.PreviewAsync(maker, bookInput);
        var bookProposal = await bookService.ProposeAsync(maker, bookInput, bookPreview["binding"].AsString, Meta("book"));
        var bookReview = await bookService.PreviewDecisionAsync(checker, bookProposal["proposalId"].AsInt64, AccountingBookSetupDecision.Approve);
        var book = await bookService.DecideAsync(checker, bookProposal["proposalId"].AsInt64, 1, AccountingBookSetupDecision.Approve, bookReview["binding"].AsString, Meta("book-approved"));
        var chart = await first.Rows("accountingChartVersions").Find(new BsonDocument("_id", book["chartId"])).SingleAsync();
        var inventoryAccount = chart["mappings"].AsBsonArray.Single(row => row["mappingKey"] == "inventory");
        var accrualAccount = chart["mappings"].AsBsonArray.Single(row => row["mappingKey"] == "receipt_accrual");
        var people = await unit.ExecuteAsync((tx, ct) => new PersonRegistryService(database, clock).ResolveAsync(tx, [maker.UserId, checker.UserId], ct));
        var financePolicyId = (await ids.NextAsync(ProcurementStockValuationTestData.Policies)).Value;
        await first.Rows(ProcurementStockValuationTestData.Policies).InsertOneAsync(new BsonDocument {
            { "_id", financePolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "active", true }, { "location", ProcurementRequestBson.Location(location) }, { "currency", "IDR" },
            { "approvedAt", clock.UtcNow.UtcDateTime }, { "expiresAt", clock.UtcNow.AddDays(1).UtcDateTime }, { "proposedBy", maker.UserId }, { "approvedBy", checker.UserId },
            { "people", new BsonArray(people.Select(ProcurementRequestBson.Person)) }, { "sourceRecognition", "receipt_accrual" }, { "bookId", book["bookId"] }, { "chartVersionId", book["chartId"] },
            { "inventoryMapping", "inventory" }, { "inventoryAccountId", inventoryAccount["accountId"] }, { "inventoryAccountCode", inventoryAccount["accountCode"] },
            { "receiptAccrualMapping", "receipt_accrual" }, { "receiptAccrualAccountId", accrualAccount["accountId"] }, { "receiptAccrualAccountCode", accrualAccount["accountCode"] },
            { "capitalizeTax", true }, { "capitalizeCharge", true }, { "operatingOwnerReference", "SYNTHETIC-EXTERNAL-FINANCE-POLICY-INPUT" },
            { "approvalReference", "SYNTHETIC-LOCAL-FINANCE-INPUT-NOT-PRODUCTION-APPROVAL" } });
        var valueInput = new ProcurementStockValuationInput(location, receipt["receiptId"].AsInt64, 1, invoice["documentId"].AsInt64, 1, match["matchId"].AsInt64, 1, 2,
            new(20000m), new(2000m), new(500m), new(22500m), financePolicyId, 1, date, "Synthetic second-channel actual matched valuation");
        var valuePreview = await valuation.Service.PreviewAsync(valueMaker, valueInput);
        var valueProposal = await valuation.Service.ProposeAsync(valueMaker, valueInput, valuePreview["binding"].AsString, Meta("value") with { Reason = valueInput.Reason });
        var valueReview = await valuation.Service.PreviewDecisionAsync(valueChecker, valueProposal["proposalId"].AsInt64, ProcurementStockValuationDecision.Approve);
        var valued = await valuation.Service.DecideAsync(valueChecker, valueProposal["proposalId"].AsInt64, 1, ProcurementStockValuationDecision.Approve, valueReview["binding"].AsString, Meta("value-approved"));
        var qualityPolicyId = (await ids.NextAsync(Q.Policies)).Value;
        await first.Rows(Q.Policies).InsertOneAsync(new BsonDocument {
            { "_id", qualityPolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "active", true }, { "location", ProcurementRequestBson.Location(location) },
            { "qualityProcedureReference", "SYNTHETIC-SECOND-CHANNEL-CLEAN-OBSERVATION" }, { "putawayProcedureReference", "SYNTHETIC-SECOND-CHANNEL-SEGREGATION" },
            { "allowNonExpiring", false }, { "nonExpiringProcedureReference", BsonNull.Value }, { "maximumObservationAgeMilliseconds", 900000L },
            { "approvalReference", "SYNTHETIC-LOCAL-QUALITY-INPUT-NOT-PRODUCTION-APPROVAL" }, { "proposedBy", maker.UserId }, { "approvedBy", checker.UserId },
            { "people", new BsonArray(people.Select(ProcurementRequestBson.Person)) }, { "approvedAt", clock.UtcNow.UtcDateTime }, { "expiresAt", clock.UtcNow.AddDays(1).UtcDateTime }, { "fence", 0L } });
        var quality = new ProcurementQualityAcceptanceService(database, unit, ids, clock, procurement.Catalog, purchasing.Service, receiving.Service, source);
        var qualityInput = new ProcurementQualityAcceptanceInput(location, receipt["receiptId"].AsInt64, 1, 2, qualityPolicyId, 1, clock.UtcNow,
            "SYNTHETIC-SECOND-CHANNEL-MAKER-OBSERVATION", "SYNTHETIC-CHANNEL-HELD-BAY", "SYNTHETIC-CHANNEL-CLEAN-SUBSET", "sealed_undamaged", "SYNTHETIC-CHANNEL-LOT", expires, null,
            "Synthetic explicit quality observation; no stock release authority");
        var qualityPreview = await quality.PreviewAsync(qualityMaker, qualityInput);
        var qualityProposal = await quality.ProposeAsync(qualityMaker, qualityInput, qualityPreview["binding"].AsString, Meta("quality") with { Reason = qualityInput.Reason });
        var independent = new ProcurementQualityAcceptanceObservation(clock.UtcNow, 2, "SYNTHETIC-SECOND-CHANNEL-CHECKER-OBSERVATION", qualityInput.PutawayReference,
            qualityInput.SegregationReference, qualityInput.AcceptedCondition, qualityInput.LotReference, expires, null);
        var qualityReview = await quality.PreviewDecisionAsync(qualityChecker, qualityProposal["proposalId"].AsInt64, ProcurementQualityAcceptanceDecision.Approve, independent);
        var accepted = await quality.DecideAsync(qualityChecker, qualityProposal["proposalId"].AsInt64, 1, ProcurementQualityAcceptanceDecision.Approve, independent, qualityReview["binding"].AsString, Meta("quality-approved"));
        var release = first.Stock.NewService();
        var releaseInput = new StockReleaseInput(physicalLocation, receipt["receiptId"].AsInt64, accepted["allocationId"].AsInt64, valued["allocationId"].AsInt64, 1, LotValuePolicy.ProportionalFloorPolicy);
        var releasePreview = await release.PreviewAsync(releaseMaker, releaseInput);
        var releaseProposal = await release.ProposeAsync(releaseMaker, releaseInput, StockReleaseBson.Expected(releasePreview["expected"]), releasePreview["binding"].AsString, Meta("release"));
        var releaseReview = await release.PreviewDecisionAsync(releaseChecker, releaseProposal["proposalId"].AsInt64, StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy);
        var released = await release.DecideAsync(releaseChecker, releaseProposal["proposalId"].AsInt64, 1, StockReleaseDecision.Approve, LotValuePolicy.ProportionalFloorPolicy,
            StockReleaseBson.Expected(releaseReview["expected"]), releaseReview["binding"].AsString, Meta("release-approved"));
        Assert.Equal("11250.00", released["transferredValue"].AsString); stage("second-independent-quality-value-and-release");

        // Assortment/entitlement assignments are labelled local catalog bootstrap,
        // as in the first fixture. Publication and price approvals remain real commands.
        var assortmentId = (await ids.NextAsync("catalogAssortments")).Value;
        await first.Rows("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(new CatalogAssortment(assortmentId, shop, AuthorizationStatus.Active, 1)));
        var entitlementId = (await ids.NextAsync("catalogEntitlements")).Value;
        await first.Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(entitlementId, first.Buyer.UserId, first.Buyer.UserId, assortmentId,
            UnitToko.Tests.Catalog.CatalogAuthoringTestData.Audience, AuthorizationStatus.Active, clock.UtcNow, null, null, 1, 998, 999, 880125, 1, clock.UtcNow)));
        await procurement.Catalog.SetPublicationsAsync(maker, new(sku.Id, sku.Version, [new(assortmentId)], [new(new(assortmentId), 1)]), Meta("published"));
        var prices = new CatalogPriceService(database, unit, ids, clock);
        var sellingPricePreview = await prices.PreviewAsync(maker, sku.Id.Value, CatalogPriceAccess.Prepare);
        var sellingPrice = await prices.ProposeAsync(maker, sku.Id.Value, sellingPricePreview.Expected, new("IDR", new(30000m)), Meta("price"));
        await prices.DecideAsync(checker, sellingPrice["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve, "Independent synthetic second-channel selling-price review", Meta("price-approved"));
        var commercial = await ApproveCommercialAsync(first, maker, checker, seller, 0, budgetRequired: true, "second-required-capacity");
        var catalogItem = (await first.Catalog.BuyerService.QueryAsync(first.Buyer, 0, 25, null)).Items.Single(item => item.Sku.Id == sku.Id);
        var input = first.Input with { Selections = [new(catalogItem.Sku.Id, catalogItem.Definition.Id, catalogItem.Definition.Version,
            catalogItem.CurrentPrice!.Id, catalogItem.CurrentPrice.Version, 1)] };
        stage("second-genuine-publication-price-and-commercial-bundle");
        return new(physicalLocation, receipt["balanceId"].AsInt64, maker, checker, seller, input, commercial);
    }

    private static async Task<BsonDocument> ApproveCommercialAsync(GenuineFiniteDispatchTestData first, ActorContext maker, ActorContext checker,
        OperationalBookKey seller, long expectedVersion, bool budgetRequired, string key)
    {
        var organizations = first.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
        var profiles = await first.Unit.ExecuteAsync(async (tx, ct) => (
            Branch: await organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Branch, first.Input.Context.BranchId.Value), ct),
            Organization: await organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.FulfillmentOrganization, seller.OrganizationId), ct),
            Shop: await organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Shop, seller.ShopId), ct),
            Channel: await organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Channel, seller.ChannelId), ct)));
        var rule = OrderCommercialPolicyRuleTests.Rule with { Charges = Money.Parse("2.50"), Applicability = new(
            budgetRequired ? OrderCommercialApplicability.Required : OrderCommercialApplicability.NotApplicable,
            OrderCommercialApplicability.NotApplicable, OrderCommercialApplicability.Required, OrderCommercialApplicability.NotApplicable) };
        var input = new OrderCommercialPolicyInput(new(ScopeNodeKind.Branch, first.Input.Context.BranchId.Value, seller),
            profiles.Branch["target"]["version"].AsInt64, profiles.Branch["profile"]["version"].AsInt64,
            new(profiles.Organization["target"]["version"].AsInt64, profiles.Organization["profile"]["version"].AsInt64,
                profiles.Shop["target"]["version"].AsInt64, profiles.Shop["profile"]["version"].AsInt64,
                profiles.Channel["target"]["version"].AsInt64, profiles.Channel["profile"]["version"].AsInt64),
            expectedVersion, rule, first.Clock.UtcNow.AddMinutes(-1), first.Clock.UtcNow.AddDays(1));
        var service = new OrderCommercialPolicyService(first.Database, first.Unit, new(first.Database), first.Clock, organizations);
        var preview = await service.PreviewAsync(maker, input);
        var proposed = await service.ProposeAsync(maker, input, preview.Binding, Meta(key));
        var review = await service.PreviewDecisionAsync(checker, proposed["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve);
        return await service.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve, review.Binding, Meta(key + "-approved"));
    }
    private static async Task<BsonDocument> MoneyAndOriginsAsync(GenuineFiniteDispatchTestData first)
    {
        var result = new BsonDocument { { "accounting", await first.Stock.Valuation.StateAsync() }, { "documents", await first.Stock.Valuation.Documents.StateAsync() } };
        foreach (var table in new[] { LotValueStore.AvailableHeads, LotValueStore.HeldHeads, LotValueStore.Transitions }) {
            var rows = await first.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[table] = new BsonArray(rows);
        }
        return result;
    }
    private static async Task<BsonDocument> OrderEffectsAsync(GenuineFiniteDispatchTestData first)
    {
        var result = new BsonDocument();
        foreach (var table in new[] { B.Orders, B.Suborders, B.Reservations, "stockBalances", "movements", OrderStockSourceRecords.Heads, OrderStockSourceRecords.Transitions }) {
            var rows = await first.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            result[table] = new BsonArray(await first.Rows(table).Find(new BsonDocument(field, new BsonRegularExpression("^ordering\\.order(?:[.:]|$)"))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
