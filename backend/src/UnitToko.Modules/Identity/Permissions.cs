using System.Collections.Immutable;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Identity;

public sealed record PermissionDefinition(string Name, string Description,
    ImmutableArray<GrantScopeKind> AllowedScopes, AssuranceRequirement Assurance);

public static class Permissions
{
    public const string CatalogRead = "catalog.read", CatalogManage = "catalog.manage", PricingManage = "pricing.manage";
    public const string OrderRead = "order.read", OrderSubmit = "order.submit", OrderApprove = "order.approve", OrderSubstitute = "order.substitute", OrderCancel = "order.cancel";
    public const string BranchPic = "branch.pic", OrganizationWide = "organization.wide";
    public const string StockRead = "stock.read", StockReceive = "stock.receive", StockCount = "stock.count", StockAdjustPropose = "stock.adjust.propose", StockAdjustApprove = "stock.adjust.approve";
    public const string ShipmentRead = "shipment.read", ShipmentPrepare = "shipment.prepare", ShipmentDispatch = "shipment.dispatch", ShipmentAssign = "shipment.assign", DeliveryAttempt = "delivery.attempt", DeliveryEvidence = "delivery.evidence", DeliveryReturn = "delivery.return", ReceiptAccept = "receipt.accept";
    public const string CaseRead = "case.read", CaseRequest = "case.request", CaseManage = "case.manage";
    public const string ProcurementRead = "procurement.read", ProcurementPrepare = "procurement.prepare", ProcurementApprove = "procurement.approve", SupplierClaim = "supplier.claim";
    public const string SourcePrepare = "source.prepare", SourceReconcile = "source.reconcile", ReportPrepare = "report.prepare", PeriodSubmit = "period.submit", PeriodApprove = "period.approve", PeriodClose = "period.close", PeriodReopenRequest = "period.reopen.request", PeriodReopenApprove = "period.reopen.approve";
    public const string InvoiceRead = "invoice.read", InvoiceIssue = "invoice.issue", PaymentEvidence = "payment.evidence", PaymentRecord = "payment.record", PaymentAllocate = "payment.allocate", PaymentVerify = "payment.verify";
    public const string CreditRequest = "credit.request", CreditApprove = "credit.approve", RefundRequest = "refund.request", RefundApprove = "refund.approve", DisbursementRecord = "disbursement.record", DisbursementVerify = "disbursement.verify";
    public const string FundingRequest = "funding.request", FundingApprove = "funding.approve", ExpenseRequest = "expense.request", ExpenseApprove = "expense.approve";
    public const string ReconciliationVerify = "reconciliation.verify", JournalPost = "journal.post", JournalReverse = "journal.reverse";
    public const string InviteManage = "invite.manage", MembershipPropose = "membership.propose", MembershipApprove = "membership.approve", AccessPropose = "access.propose", AccessApprove = "access.approve", AccessRevoke = "access.revoke", RecoveryInitiate = "recovery.initiate", TechnicalConfigure = "technical.configure", AccessAuditRead = "access.audit.read";

    private static readonly ImmutableArray<GrantScopeKind> Buying = [GrantScopeKind.BuyerOwned, GrantScopeKind.BuyingDivision, GrantScopeKind.BuyingBranch, GrantScopeKind.BuyingOrganization];
    private static readonly ImmutableArray<GrantScopeKind> Operations = [GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse];
    private static readonly ImmutableArray<GrantScopeKind> Courier = [GrantScopeKind.CourierAssignment];
    private static readonly ImmutableArray<GrantScopeKind> Administrative = [GrantScopeKind.System];
    private static readonly ImmutableArray<GrantScopeKind> Delegation = [.. Buying, .. Operations, GrantScopeKind.CourierAssignment, GrantScopeKind.System];
    public static ImmutableDictionary<string, PermissionDefinition> Catalog { get; } = Build();

    public static PermissionDefinition? Find(string name) => name is not null && Catalog.TryGetValue(name, out var definition) ? definition : null;

    private static ImmutableDictionary<string, PermissionDefinition> Build()
    {
        var result = ImmutableDictionary.CreateBuilder<string, PermissionDefinition>(StringComparer.Ordinal);
        void Add(string name, ImmutableArray<GrantScopeKind> scopes, bool fresh = false) =>
            result.Add(name, new(name, name.Replace('.', ' '), scopes, new(true, fresh ? TimeSpan.FromMinutes(5) : null)));
        foreach (var name in new[] { CatalogRead, OrderRead, ShipmentRead, CaseRead, InvoiceRead }) Add(name, [.. Buying, .. Operations]);
        result[ShipmentRead] = result[ShipmentRead] with { AllowedScopes = [.. Buying, .. Operations, .. Courier] };
        foreach (var name in new[] { OrderSubmit, OrderApprove, OrderSubstitute, OrderCancel, ReceiptAccept, CaseRequest, PaymentEvidence }) Add(name, Buying);
        Add(BranchPic, [GrantScopeKind.BuyingBranch, GrantScopeKind.BuyingDivision]);
        Add(OrganizationWide, [GrantScopeKind.BuyingOrganization]);
        foreach (var name in new[] { CatalogManage, PricingManage, StockRead, StockReceive, StockCount, StockAdjustPropose, StockAdjustApprove,
            ShipmentPrepare, ShipmentDispatch, ShipmentAssign, CaseManage, ProcurementRead, ProcurementPrepare, ProcurementApprove, SupplierClaim,
            SourcePrepare, SourceReconcile, ReportPrepare, PeriodSubmit, PeriodApprove, PeriodClose, PeriodReopenRequest, PeriodReopenApprove,
            InvoiceIssue, PaymentRecord, PaymentAllocate, PaymentVerify, CreditRequest, CreditApprove, RefundRequest, DisbursementRecord,
            DisbursementVerify, FundingRequest, FundingApprove, ExpenseApprove, ReconciliationVerify, JournalPost, JournalReverse }) Add(name, Operations);
        Add(RefundApprove, Operations, true);
        Add(ExpenseRequest, [.. Operations, .. Courier]);
        Add(DeliveryAttempt, Courier);
        Add(DeliveryEvidence, Courier);
        Add(DeliveryReturn, Courier);
        foreach (var name in new[] { InviteManage, MembershipPropose, AccessPropose, TechnicalConfigure, AccessAuditRead }) Add(name, Administrative);
        Add(RecoveryInitiate, Administrative, true);
        foreach (var name in new[] { MembershipApprove, AccessApprove, AccessRevoke }) Add(name, Delegation, true);
        return result.ToImmutable();
    }
}
