using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

/// <summary>Every maker/checker pair in Permissions.cs: the proposing (or recording) side, then the deciding side. The same list the
/// local pilot's persona test uses (backend/tools/UnitToko.LocalPilot.Tests/PilotSessionsTests.cs); this is the canonical copy.</summary>
internal static class MakerCheckerPairs
{
    internal static readonly (string Maker, string Checker)[] All = [
        (Permissions.OrderSubmit, Permissions.OrderSpendApprove), (Permissions.OrderSubmit, Permissions.OrderApprove),
        (Permissions.BranchOrderRequestSubmit, Permissions.BranchOrderRequestDecide), (Permissions.SubstitutionPropose, Permissions.SubstitutionAccept),
        (Permissions.OrderSpendingPolicyPropose, Permissions.OrderSpendingPolicyApprove), (Permissions.OrderCommercialPolicyPropose, Permissions.OrderCommercialPolicyApprove),
        (Permissions.PricingManage, Permissions.PricingApprove), (Permissions.CatalogPublicPropose, Permissions.CatalogPublicApprove),
        (Permissions.ProcurementPrepare, Permissions.ProcurementApprove), (Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove),
        (Permissions.SupplierBankPropose, Permissions.SupplierBankApprove), (Permissions.SupplierClaim, Permissions.SupplierClaimApprove),
        (Permissions.FundingRequest, Permissions.FundingApprove), (Permissions.ExpenseRequest, Permissions.ExpenseApprove),
        (Permissions.ProcurementPayablePropose, Permissions.ProcurementPayableDecide), (Permissions.ProcurementAdvanceReturnPropose, Permissions.ProcurementAdvanceReturnDecide),
        (Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementQualityAcceptanceDecide),
        (Permissions.ProcurementStockValuationPropose, Permissions.ProcurementStockValuationDecide),
        (Permissions.StockAdjustPropose, Permissions.StockAdjustApprove), (Permissions.StockReleasePropose, Permissions.StockReleaseApprove),
        (Permissions.StockSourcePropose, Permissions.StockSourceApprove), (Permissions.StockSourcePolicyPropose, Permissions.StockSourcePolicyApprove),
        (Permissions.StockSourceReconciliationPropose, Permissions.StockSourceReconciliationDecide),
        (Permissions.InvoiceReleasePropose, Permissions.InvoiceReleaseApprove), (Permissions.PaymentRecord, Permissions.PaymentVerify),
        (Permissions.PaymentAllocate, Permissions.PaymentVerify), (Permissions.DisbursementRecord, Permissions.DisbursementVerify),
        (Permissions.CreditRequest, Permissions.CreditApprove), (Permissions.RefundRequest, Permissions.RefundApprove),
        (Permissions.ReceivingAccountPropose, Permissions.ReceivingAccountVerify), (Permissions.ReceivingAccountPropose, Permissions.ReceivingAccountApprove),
        (Permissions.PeriodSubmit, Permissions.PeriodApprove), (Permissions.PeriodReopenRequest, Permissions.PeriodReopenApprove),
        (Permissions.AccountingBookPropose, Permissions.AccountingBookApprove), (Permissions.AccessPropose, Permissions.AccessApprove),
        (Permissions.MembershipPropose, Permissions.MembershipApprove), (Permissions.PersonReviewPropose, Permissions.PersonReviewApprove),
        (Permissions.TechnicalConfigure, Permissions.OrganizationApprove),
        // Independence the services and ApprovalPolicy enforce from recorded history (RequestedBy / RecordedBy / ApprovedBy / SubmittedBy ...):
        (Permissions.BankStatementStage, Permissions.BankReceiptConfirm),          // BankReceiptService.PeopleAsync: the statement importer
        (Permissions.BankStatementStage, Permissions.DisbursementVerify),          // ProcurementSettlementService, RefundPayoutService.Settlement: the importer
        (Permissions.FundingApprove, Permissions.DisbursementRecord),              // ProcurementDisbursementService, ApprovalPolicy: the funding approver
        (Permissions.RefundApprove, Permissions.DisbursementRecord),               // RefundPayoutService, ApprovalPolicy: the refund approver
        (Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementStockValuationDecide), // ProcurementStockValuationService: the matcher
        (Permissions.CaseRequest, Permissions.CaseManage),                         // ApprovalPolicy (AfterSalesCaseService): the case requester
        (Permissions.CaseRequest, Permissions.CreditRequest), (Permissions.CaseRequest, Permissions.RefundRequest), // ReturnEntitlementService: the case requester
        (Permissions.CaseRequest, Permissions.CreditApprove), (Permissions.CaseRequest, Permissions.RefundApprove), // ReturnFinancialApprovalService: every maker so far
        (Permissions.PaymentEvidence, Permissions.PaymentVerify),                  // PaymentVerificationService: the payment submitter
        (Permissions.PaymentEvidence, Permissions.PaymentAllocate),                // PaymentAllocationService (RecordedBy): the payment submitter
        (Permissions.PaymentEvidence, Permissions.PaymentReviewManual),            // manual payment review: the payment submitter
        (Permissions.PaymentEvidence, Permissions.PaymentReviewManage),            // payment review actions (RecordedBy): the payment submitter
        (Permissions.PaymentEvidence, Permissions.CreditApprove), (Permissions.PaymentEvidence, Permissions.RefundApprove), // ReturnFinancialApprovalService
        (Permissions.PaymentAllocate, Permissions.CreditApprove), (Permissions.PaymentAllocate, Permissions.RefundApprove), // ReturnFinancialApprovalService
        (Permissions.PaymentVerify, Permissions.CreditApprove), (Permissions.PaymentVerify, Permissions.RefundApprove),     // ReturnFinancialApprovalService
        (Permissions.StockCount, Permissions.StockAdjustApprove),                  // ApprovalPolicy: the counter (CountedBy)
        (Permissions.PeriodSubmit, Permissions.PeriodClose), (Permissions.PeriodApprove, Permissions.PeriodClose), // ApprovalPolicy: submitter and approver
        (Permissions.PeriodClose, Permissions.PeriodReopenApprove),                // ApprovalPolicy: ClosedBy
    ];
}

/// <summary>Role templates describe jobs (F-006, F-007). A template grants a job's routine work; the decision authority that earlier
/// rules keep explicit (fresh-MFA approvals, pricing and supplier governance, explicit reads) is offered as the template's suggestions,
/// which the granting administrator selects. No template, with its suggestions or with any one finance pack, holds both sides of a
/// maker/checker pair, so a granting administrator is never steered into giving one person both.</summary>
public sealed class RoleTemplateJobTests
{
    private static string[] Offered(string template) =>
        [.. RoleTemplates.Templates[template].Permissions, .. RoleTemplates.Suggestions.GetValueOrDefault(template, [])];

    [Fact]
    public void The_maker_checker_list_names_real_permissions_once()
    {
        Assert.Equal(62, MakerCheckerPairs.All.Length);
        Assert.Equal(MakerCheckerPairs.All.Length, MakerCheckerPairs.All.Distinct().Count());
        Assert.All(MakerCheckerPairs.All, pair => { Assert.NotNull(Permissions.Find(pair.Maker)); Assert.NotNull(Permissions.Find(pair.Checker)); });
    }

    [Fact]
    public void No_template_offers_both_sides_of_a_maker_checker_pair()
    {
        foreach (var template in RoleTemplates.Templates.Keys)
        {
            var offers = new List<(string Name, string[] Permissions)> { (template, Offered(template)) };
            if (template == "finance")
                offers.AddRange(RoleTemplates.FinancePacks.Select(pack => (template + "+" + pack.Key, (string[])[.. Offered(template), .. pack.Value])));
            foreach (var (name, permissions) in offers)
                foreach (var (maker, checker) in MakerCheckerPairs.All)
                    Assert.False(permissions.Contains(maker) && permissions.Contains(checker), name + " offers both " + maker + " and " + checker);
        }
        // Suggestions never repeat what the template already grants.
        foreach (var (template, suggestions) in RoleTemplates.Suggestions)
            Assert.Empty(suggestions.Intersect(RoleTemplates.Templates[template].Permissions));
    }

    [Theory]
    [InlineData("branch-approver", GrantScopeKind.BuyingBranch, new[] { Permissions.OrderRead, Permissions.OrderSpendApprove, Permissions.BranchPic, Permissions.CatalogRead })]
    [InlineData("catalog-officer", GrantScopeKind.FulfillmentShop, new[] { Permissions.CatalogManage, Permissions.PricingManage, Permissions.CatalogPublicPropose })]
    [InlineData("catalog-reviewer", GrantScopeKind.FulfillmentShop, new[] { Permissions.CatalogRead, Permissions.PricingApprove, Permissions.CatalogPublicApprove })]
    [InlineData("dispatcher", GrantScopeKind.FulfillmentWarehouse, new[] { Permissions.ShipmentRead, Permissions.ShipmentPrepare, Permissions.ShipmentDispatch, Permissions.ShipmentAssign })]
    [InlineData("finance-maker", GrantScopeKind.FulfillmentShop, new[] { Permissions.InvoiceRead, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleasePropose,
        Permissions.InvoiceIssue, Permissions.PaymentReviewRead, Permissions.PaymentAllocate, Permissions.DisbursementRecord, Permissions.ProcurementDisbursementRead })]
    [InlineData("finance-checker", GrantScopeKind.FulfillmentShop, new[] { Permissions.InvoiceRead, Permissions.InvoiceReleaseRead, Permissions.InvoiceReleaseApprove,
        Permissions.PaymentReviewRead, Permissions.PaymentVerify, Permissions.DisbursementVerify, Permissions.ProcurementDisbursementRead })]
    [InlineData("procurement-officer", GrantScopeKind.FulfillmentShop, new[] { Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.SupplierClaim,
        Permissions.ProcurementSupplierDocumentRecord, Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementQualityAcceptancePropose })]
    [InlineData("procurement-approver", GrantScopeKind.FulfillmentShop, new[] { Permissions.ProcurementRead, Permissions.ProcurementApprove, Permissions.SupplierClaimApprove,
        Permissions.ProcurementQualityAcceptanceDecide })]
    [InlineData("warehouse-receiver", GrantScopeKind.FulfillmentWarehouse, new[] { Permissions.ProcurementRead, Permissions.StockReceive,
        Permissions.ProcurementContainerReceive, Permissions.ProcurementInspect })]
    public void Each_job_template_offers_what_its_pages_need_in_one_scope(string template, GrantScopeKind scope, string[] needed)
    {
        Assert.True(RoleTemplates.Templates.ContainsKey(template), template);
        Assert.Empty(needed.Except(Offered(template)));
        // The template and its suggestions can be granted together in the job's scope.
        Assert.All(Offered(template), permission => Assert.Contains(scope, Permissions.Find(permission)!.AllowedScopes));
        Assert.Equal(1, RoleTemplates.Templates[template].Version);
    }

    /// <summary>G-035: a payment checker verifies transfers against bank records, so the finance-checker job offers every read the bank
    /// receipt and bank statement pages require (and the confirmation), and the finance maker offers what statement staging requires.</summary>
    [Fact]
    public void Finance_jobs_offer_what_the_bank_pages_require()
    {
        var checker = Offered("finance-checker");
        foreach (var permission in UnitToko.Platform.Billing.BankReceiptService.Required(false).Concat(UnitToko.Platform.Billing.BankReceiptService.Required(true))
            .Concat(UnitToko.Platform.Billing.BankStatementService.PermissionsFor(false)))
            Assert.Contains(permission, checker);
        var maker = Offered("finance-maker");
        foreach (var permission in UnitToko.Platform.Billing.BankStatementService.PermissionsFor(true)) Assert.Contains(permission, maker);
    }

    /// <summary>branch.pic designates a branch person in charge (a shipment-recipient candidate); it is never added implicitly
    /// (AccessGrantCommands, OnboardingPolicy), so the branch approver job suggests it for explicit selection.</summary>
    [Fact]
    public void Branch_designation_is_suggested_never_granted_by_a_template()
    {
        Assert.All(RoleTemplates.Templates.Values, template => Assert.DoesNotContain(Permissions.BranchPic, template.Permissions));
        Assert.Contains(Permissions.BranchPic, RoleTemplates.Suggestions["branch-approver"]);
    }

    [Fact]
    public void The_dispatch_page_works_from_the_dispatcher_template_alone()
    {
        var permissions = RoleTemplates.Materialize("dispatcher", [], []).Permissions;
        Assert.Contains(Permissions.ShipmentDispatch, permissions); Assert.Contains(Permissions.ShipmentAssign, permissions);
    }

    [Fact]
    public void Every_template_can_be_granted_in_one_scope_with_its_suggestions()
    {
        foreach (var template in RoleTemplates.Templates.Keys)
            Assert.True(Enum.GetValues<GrantScopeKind>().Any(kind => Offered(template).All(permission => Permissions.Find(permission)!.AllowedScopes.Contains(kind))),
                template + " mixes permissions no single scope can carry");
    }

    /// <summary>Templates are copied into a grant when it is made. The six v1 templates keep their exact contents, so no existing
    /// grant, invitation or persona built from them changes meaning.</summary>
    [Fact]
    public void The_original_six_templates_keep_their_v1_contents()
    {
        Assert.Equal(1, RoleTemplates.CurrentVersion);
        Assert.Equal(new[] { Permissions.CatalogRead, Permissions.OrderRead, Permissions.OrderSubmit, Permissions.OrderSubstitute, Permissions.OrderCancel,
            Permissions.ShipmentRead, Permissions.ReceiptAccept, Permissions.InvoiceRead, Permissions.CaseRead, Permissions.CaseRequest, Permissions.PaymentEvidence },
            RoleTemplates.Templates["buyer"].Permissions);
        Assert.Equal(new[] { Permissions.CatalogRead, Permissions.OrderRead, Permissions.StockRead, Permissions.StockReceive, Permissions.StockCount,
            Permissions.StockAdjustPropose, Permissions.ShipmentRead, Permissions.ShipmentPrepare, Permissions.ShipmentDispatch, Permissions.ProcurementRead,
            Permissions.ProcurementPrepare, Permissions.SourcePrepare, Permissions.ExpenseRequest }, RoleTemplates.Templates["store-staff"].Permissions);
        Assert.Equal(new[] { Permissions.CatalogRead, Permissions.CatalogManage, Permissions.PricingManage, Permissions.OrderRead, Permissions.StockRead,
            Permissions.StockAdjustApprove, Permissions.ShipmentRead, Permissions.ShipmentAssign, Permissions.CaseRead, Permissions.CaseManage, Permissions.ProcurementRead,
            Permissions.ProcurementApprove, Permissions.SupplierClaim, Permissions.FundingRequest, Permissions.ExpenseRequest }, RoleTemplates.Templates["store-head"].Permissions);
        Assert.Equal(new[] { Permissions.ShipmentRead, Permissions.DeliveryAttempt, Permissions.DeliveryEvidence, Permissions.DeliveryReturn, Permissions.ExpenseRequest },
            RoleTemplates.Templates["courier"].Permissions);
        Assert.Empty(RoleTemplates.Templates["finance"].Permissions);
        Assert.Equal(new[] { Permissions.InviteManage, Permissions.MembershipPropose, Permissions.AccessPropose, Permissions.RecoveryInitiate,
            Permissions.TechnicalConfigure, Permissions.AccessAuditRead }, RoleTemplates.Templates["system-admin"].Permissions);
    }

    /// <summary>F-007: each billing pack permission leads to a page where it can be used, with the reads that page needs offered
    /// beside it. Credit and refund requests are made from a case, which store heads run, so they are offered there.</summary>
    [Fact]
    public void Billing_pack_permissions_each_reach_a_page()
    {
        var billing = RoleTemplates.FinancePacks["billing"];
        Assert.DoesNotContain(Permissions.PaymentRecord, billing);
        Assert.DoesNotContain(Permissions.CreditRequest, billing); Assert.DoesNotContain(Permissions.RefundRequest, billing);
        var finance = RoleTemplates.Suggestions["finance"];
        Assert.Contains(Permissions.InvoiceRead, billing);
        if (billing.Contains(Permissions.PaymentAllocate)) Assert.Contains(Permissions.PaymentReviewRead, finance);
        if (billing.Contains(Permissions.DisbursementRecord)) Assert.Contains(Permissions.ProcurementDisbursementRead, finance);
        foreach (var request in new[] { Permissions.CreditRequest, Permissions.RefundRequest })
            Assert.Contains(request, Offered("store-head"));
        Assert.Contains(Permissions.CaseManage, Offered("store-head")); Assert.Contains(Permissions.CaseRead, Offered("store-head"));
        // The entitlement that follows a credit or refund decision reads the invoice (ReturnEntitlementService, CaseResolutionPanel).
        Assert.Contains(Permissions.InvoiceRead, Offered("store-head"));
    }
}
