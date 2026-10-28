using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Identity;

public sealed record RoleTemplateSnapshot(string Id, int Version, ImmutableArray<string> Permissions);
public sealed record LegacyRoleMapping(string TemplateId, ImmutableArray<string> FinancePacks, bool RequiresApprovedMembership = true);

public static class RoleTemplates
{
    public const int CurrentVersion = 1;
    public static ImmutableDictionary<string, RoleTemplateSnapshot> Templates { get; } = new Dictionary<string, RoleTemplateSnapshot>(StringComparer.Ordinal)
    {
        ["buyer"] = New("buyer", Permissions.CatalogRead, Permissions.OrderRead, Permissions.OrderSubmit, Permissions.OrderSubstitute,
            Permissions.OrderCancel, Permissions.ShipmentRead, Permissions.ReceiptAccept, Permissions.InvoiceRead, Permissions.CaseRead, Permissions.CaseRequest, Permissions.PaymentEvidence),
        ["store-staff"] = New("store-staff", Permissions.CatalogRead, Permissions.OrderRead, Permissions.StockRead, Permissions.StockReceive,
            Permissions.StockCount, Permissions.StockAdjustPropose, Permissions.ShipmentRead, Permissions.ShipmentPrepare, Permissions.ShipmentDispatch,
            Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.SourcePrepare, Permissions.ExpenseRequest),
        ["store-head"] = New("store-head", Permissions.CatalogRead, Permissions.CatalogManage, Permissions.PricingManage, Permissions.OrderRead,
            Permissions.StockRead, Permissions.StockAdjustApprove, Permissions.ShipmentRead, Permissions.ShipmentAssign, Permissions.CaseRead,
            Permissions.CaseManage, Permissions.ProcurementRead, Permissions.ProcurementApprove, Permissions.SupplierClaim, Permissions.FundingRequest, Permissions.ExpenseRequest),
        ["courier"] = New("courier", Permissions.ShipmentRead, Permissions.DeliveryAttempt, Permissions.DeliveryEvidence, Permissions.DeliveryReturn, Permissions.ExpenseRequest),
        ["finance"] = New("finance"),
        ["system-admin"] = New("system-admin", Permissions.InviteManage, Permissions.MembershipPropose, Permissions.AccessPropose,
            Permissions.RecoveryInitiate, Permissions.TechnicalConfigure, Permissions.AccessAuditRead),
        // Job templates (F-006). Each grants the routine work of one job; the decision authority that the explicit-grant rules keep out
        // of templates (fresh-MFA approvals, governance proposals, explicit reads) is offered as its suggestions. No template, with
        // its suggestions, holds both sides of a maker/checker pair. The six templates above keep their v1 contents unchanged.
        // branch.pic designates a branch person in charge; like every explicit designation it is never in a template. The branch job suggests it.
        ["branch-approver"] = New("branch-approver", Permissions.CatalogRead, Permissions.OrderRead, Permissions.ShipmentRead, Permissions.InvoiceRead,
            Permissions.CaseRead, Permissions.BranchOrderRequestRead),
        ["catalog-officer"] = New("catalog-officer", Permissions.CatalogRead, Permissions.CatalogManage, Permissions.PricingManage, Permissions.OrderRead,
            Permissions.StockRead),
        ["catalog-reviewer"] = New("catalog-reviewer", Permissions.CatalogRead, Permissions.OrderRead),
        ["dispatcher"] = New("dispatcher", Permissions.CatalogRead, Permissions.OrderRead, Permissions.StockRead, Permissions.ShipmentRead,
            Permissions.ShipmentPrepare, Permissions.ShipmentDispatch, Permissions.ShipmentAssign),
        ["finance-maker"] = New("finance-maker", Permissions.OrderRead, Permissions.InvoiceRead, Permissions.CaseRead, Permissions.InvoiceReleaseRead,
            Permissions.PaymentReviewRead),
        ["finance-checker"] = New("finance-checker", Permissions.OrderRead, Permissions.InvoiceRead, Permissions.CaseRead, Permissions.InvoiceReleaseRead,
            Permissions.PaymentReviewRead),
        ["procurement-officer"] = New("procurement-officer", Permissions.CatalogRead, Permissions.OrderRead, Permissions.StockRead, Permissions.ProcurementRead),
        ["procurement-approver"] = New("procurement-approver", Permissions.CatalogRead, Permissions.OrderRead, Permissions.StockRead, Permissions.ProcurementRead),
        ["warehouse-receiver"] = New("warehouse-receiver", Permissions.CatalogRead, Permissions.OrderRead, Permissions.StockRead, Permissions.StockCount,
            Permissions.ProcurementRead)
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public static ImmutableDictionary<string, ImmutableArray<string>> FinancePacks { get; } = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
    {
        ["reporting"] = [Permissions.SourceReconcile, Permissions.ReportPrepare, Permissions.PeriodSubmit, Permissions.PeriodReopenRequest],
        // F-007: only permissions a billing page can use. Payment recording has no page or command; credit and refund requests are
        // made from a case, which store heads run (offered with the store-head template).
        ["billing"] = [Permissions.InvoiceRead, Permissions.InvoiceIssue, Permissions.PaymentAllocate, Permissions.DisbursementRecord],
        ["approval"] = [Permissions.CreditApprove, Permissions.RefundApprove, Permissions.FundingApprove, Permissions.ExpenseApprove,
            Permissions.PeriodApprove, Permissions.PeriodReopenApprove],
        ["accounting"] = [Permissions.ReconciliationVerify, Permissions.JournalPost, Permissions.JournalReverse, Permissions.PeriodClose, Permissions.DisbursementVerify]
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Optional grant-review hints. They never enter a materialized v1 role unless selected explicitly.</summary>
    public static ImmutableDictionary<string, ImmutableArray<string>> Suggestions { get; } =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["buyer"] = [Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit, Permissions.SubstitutionAccept, Permissions.OrganizationAddressRead],
            ["store-staff"] = [Permissions.OrganizationAddressRead, Permissions.SubstitutionPropose, Permissions.StockConvert,
                Permissions.ReservationRelease, Permissions.SupplierChangePropose, Permissions.StockReleasePropose],
            ["store-head"] = [Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestDecide, Permissions.OrderAccept, Permissions.SupplierChangeApprove, Permissions.OrganizationAddressRead, Permissions.FundingRead,
                Permissions.CreditRequest, Permissions.RefundRequest, Permissions.InvoiceRead],
            ["courier"] = [],
            // The generic finance template offers the proposing side only; the deciding side is offered by finance-checker. Statement staging
        // (the importer cannot also confirm the receipt or verify the disbursement) is offered by finance-maker alone.
            ["finance"] = [Permissions.OrganizationAddressRead, Permissions.BankReceiptRead, Permissions.BankReceiptConfirm, Permissions.BankStatementRead, Permissions.PaymentReviewRead, Permissions.PaymentReviewCorrect, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead,
                Permissions.ReceivingAccountPropose, Permissions.FundingRead,
                Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose,
                Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead,
                Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose,
                Permissions.StockReleaseApprove],
            ["system-admin"] = [],
            ["branch-approver"] = [Permissions.BranchPic, Permissions.OrderSpendApprove, Permissions.OrderSpendingPolicyRead, Permissions.OrganizationAddressRead],
            ["catalog-officer"] = [Permissions.CatalogPublicPropose, Permissions.OrganizationAddressRead],
            ["catalog-reviewer"] = [Permissions.PricingApprove, Permissions.CatalogPublicApprove],
            ["dispatcher"] = [Permissions.OrganizationAddressRead, Permissions.StockReleasePropose, Permissions.ReservationRelease, Permissions.SubstitutionPropose],
            ["finance-maker"] = [Permissions.InvoiceReleasePropose, Permissions.InvoiceIssue, Permissions.PaymentAllocate, Permissions.DisbursementRecord,
                Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.ReceivingAccountRead,
                Permissions.ReceivingAccountDetailsRead, Permissions.ReceivingAccountPropose, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.BankStatementStage,
                Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose, Permissions.OrganizationAddressRead],
            ["finance-checker"] = [Permissions.InvoiceReleaseApprove, Permissions.PaymentVerify, Permissions.PaymentReviewCorrect, Permissions.DisbursementVerify,
                Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.ReceivingAccountRead,
                Permissions.ReceivingAccountDetailsRead, Permissions.ReceivingAccountVerify, Permissions.ReceivingAccountApprove, Permissions.BankReceiptRead,
                Permissions.BankReceiptConfirm, Permissions.BankStatementRead,
                Permissions.SupplierBankRead, Permissions.SupplierBankApprove, Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationDecide,
                Permissions.StockReleaseApprove, Permissions.FundingRead, Permissions.FundingApprove, Permissions.OrganizationAddressRead],
            ["procurement-officer"] = [Permissions.ProcurementPrepare, Permissions.SupplierChangePropose, Permissions.ProcurementSupplierDocumentRecord,
                Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.SupplierClaim,
                Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementPayableRead,
                Permissions.ProcurementPayablePropose, Permissions.ProcurementAdvanceReturnRead, Permissions.ProcurementAdvanceReturnPropose,
                Permissions.FundingRead, Permissions.FundingRequest, Permissions.SupplierBankRead, Permissions.SupplierBankPropose, Permissions.OrganizationAddressRead],
            ["procurement-approver"] = [Permissions.ProcurementApprove, Permissions.SupplierChangeApprove, Permissions.SupplierClaimApprove,
                Permissions.ProcurementSupplierDocumentEvidenceRead, Permissions.ProcurementInspectionEvidenceRead, Permissions.ProcurementQualityAcceptanceRead,
                Permissions.ProcurementQualityAcceptanceDecide, Permissions.ProcurementPayableRead, Permissions.ProcurementPayableDecide,
                Permissions.ProcurementAdvanceReturnRead, Permissions.ProcurementAdvanceReturnDecide, Permissions.FundingRead, Permissions.SupplierBankRead,
                Permissions.OrganizationAddressRead],
            ["warehouse-receiver"] = [Permissions.StockReceive, Permissions.ProcurementContainerReceive, Permissions.ProcurementInspect,
                Permissions.ProcurementInspectionEvidenceRead, Permissions.StockConvert, Permissions.OrganizationAddressRead]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public static RoleTemplateSnapshot Materialize(string templateId, IEnumerable<string> financePacks, IEnumerable<string> additionalPermissions)
    {
        if (!Templates.TryGetValue(templateId, out var template))
            throw new DomainValidationException("invalid_template", "Unknown role template");
        var packs = financePacks.ToArray();
        if (packs.Length > 0 && templateId != "finance" || packs.Any(pack => !FinancePacks.ContainsKey(pack)))
            throw new DomainValidationException("invalid_finance_pack", "Finance packs require the Finance template and a known pack");
        var additions = additionalPermissions.ToArray();
        if (additions.Any(permission => Permissions.Find(permission) is null))
            throw new DomainValidationException("invalid_permission", "Unknown permission");
        return template with { Permissions = template.Permissions.Concat(packs.SelectMany(pack => FinancePacks[pack]))
            .Concat(additions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray() };
    }

    /// <summary>Proposal mapping only; existing demo role never activates new membership or grants.</summary>
    public static LegacyRoleMapping? MapLegacy(string legacyRole) => legacyRole switch
    {
        "pic" => new("buyer", []),
        "staf" or "staf_toko" or "staff" or "kasir" => new("store-staff", []),
        "kepala" or "kepala_toko" => new("store-head", []),
        "kurir" => new("courier", []),
        "laporan" => new("finance", ["reporting"]),
        "penagihan" => new("finance", ["billing"]),
        "pimpinan" => new("finance", ["approval"]),
        "akuntansi" => new("finance", ["accounting"]),
        "admin" => new("system-admin", []),
        _ => null
    };

    private static RoleTemplateSnapshot New(string id, params string[] permissions) => new(id, CurrentVersion, [.. permissions]);
}
