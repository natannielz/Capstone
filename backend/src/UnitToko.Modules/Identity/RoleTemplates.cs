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
            Permissions.RecoveryInitiate, Permissions.TechnicalConfigure, Permissions.AccessAuditRead)
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public static ImmutableDictionary<string, ImmutableArray<string>> FinancePacks { get; } = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
    {
        ["reporting"] = [Permissions.SourceReconcile, Permissions.ReportPrepare, Permissions.PeriodSubmit, Permissions.PeriodReopenRequest],
        ["billing"] = [Permissions.InvoiceRead, Permissions.InvoiceIssue, Permissions.PaymentRecord, Permissions.PaymentAllocate,
            Permissions.CreditRequest, Permissions.RefundRequest, Permissions.DisbursementRecord],
        ["approval"] = [Permissions.CreditApprove, Permissions.RefundApprove, Permissions.FundingApprove, Permissions.ExpenseApprove,
            Permissions.PeriodApprove, Permissions.PeriodReopenApprove],
        ["accounting"] = [Permissions.ReconciliationVerify, Permissions.JournalPost, Permissions.JournalReverse, Permissions.PeriodClose, Permissions.DisbursementVerify]
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
