using System.Collections.Immutable;

namespace UnitToko.Modules.Identity;

/// <summary>Additional person-level approval predicate. Callers still authorize current scope,
/// assurance, business state and finance-conflict policy, and resolve/fence all returned accounts
/// inside the same transaction. ApprovalRequest participants must come from stored history.</summary>
public static class PersonApprovalPolicy
{
    public static bool RequiresEvidence(string permission) => permission is
        Permissions.ProcurementApprove or Permissions.SupplierClaimApprove or Permissions.ProcurementStockValuationDecide or Permissions.ProcurementPayableDecide or Permissions.ProcurementAdvanceReturnDecide or Permissions.OrderSpendApprove or Permissions.OrderSpendingPolicyApprove or Permissions.AccountingBookApprove or Permissions.ReceivingAccountApprove or Permissions.ReceivingAccountVerify or Permissions.PricingApprove or Permissions.SupplierChangeApprove or Permissions.SupplierBankApprove or Permissions.StockSourceApprove or Permissions.StockSourcePolicyApprove or Permissions.StockSourceReconciliationDecide or
        Permissions.BankReceiptConfirm or Permissions.PaymentReviewManual or Permissions.PaymentReviewManage or Permissions.PaymentVerify or Permissions.PaymentAllocate or Permissions.DisbursementVerify or Permissions.ReconciliationVerify or
        Permissions.CaseManage or Permissions.CreditApprove or Permissions.RefundApprove or Permissions.FundingApprove or
        Permissions.ExpenseApprove or Permissions.StockAdjustApprove or Permissions.DisbursementRecord or
        Permissions.PeriodApprove or Permissions.PeriodClose or Permissions.PeriodReopenApprove or
        Permissions.AccessApprove or Permissions.MembershipApprove;

    /// <summary>Sorted unique actor and required stored participant IDs. Default means denial
    /// (unknown permission, invalid actor/history/revision); initialized empty means the known
    /// permission requires no person proof. A caller must never interpret default as empty.</summary>
    public static ImmutableArray<long> RequiredAccounts(long actorId, string permission, ApprovalRequest? evidence)
    {
        if (!AccountDecision(actorId, permission, evidence).Allowed) return default;
        if (!RequiresEvidence(permission)) return [];

        // AccountDecision has validated every required ID using the existing account policy.
        ImmutableArray<long?> participants = permission switch
        {
            Permissions.ProcurementApprove or Permissions.SupplierClaimApprove or Permissions.CaseManage or Permissions.OrderSpendApprove or Permissions.CreditApprove or Permissions.RefundApprove or
                Permissions.FundingApprove or Permissions.ExpenseApprove => [evidence!.RequestedBy],
            Permissions.AccountingBookApprove or Permissions.OrderSpendingPolicyApprove or Permissions.ReceivingAccountApprove or Permissions.ReceivingAccountVerify or Permissions.PricingApprove or Permissions.SupplierChangeApprove or Permissions.SupplierBankApprove or Permissions.StockSourceApprove or Permissions.StockSourcePolicyApprove or Permissions.StockSourceReconciliationDecide => [evidence!.ProposedBy],
            Permissions.BankReceiptConfirm or Permissions.PaymentReviewManual or Permissions.PaymentReviewManage or Permissions.PaymentVerify or Permissions.PaymentAllocate or Permissions.DisbursementVerify or
                Permissions.ReconciliationVerify => [evidence!.RecordedBy],
            Permissions.StockAdjustApprove => [evidence!.CountedBy, evidence.ProposedBy],
            Permissions.ProcurementStockValuationDecide => [evidence!.RequestedBy, evidence.RecordedBy],
            Permissions.ProcurementPayableDecide => [evidence!.RequestedBy],
            Permissions.ProcurementAdvanceReturnDecide => [evidence!.RequestedBy],
            Permissions.DisbursementRecord => [evidence!.ApprovedBy],
            Permissions.PeriodApprove => [evidence!.SubmittedBy],
            Permissions.PeriodClose => [evidence!.SubmittedBy, evidence.ApprovedBy],
            Permissions.PeriodReopenApprove => [evidence!.RequestedBy, evidence.ClosedBy],
            Permissions.AccessApprove or Permissions.MembershipApprove => [evidence!.ProposedBy, evidence.BeneficiaryId],
            _ => default
        };
        if (participants.IsDefaultOrEmpty || participants.Any(id => id is not > 0)) return default;
        return participants.Select(id => id!.Value).Append(actorId).Distinct().Order().ToImmutableArray();
    }

    public static AuthorizationDecision Evaluate(long actorId, string permission, ApprovalRequest? evidence,
        ImmutableArray<PersonEvidence> people)
    {
        var accountDecision = AccountDecision(actorId, permission, evidence);
        if (!accountDecision.Allowed || !RequiresEvidence(permission)) return accountDecision;

        var required = RequiredAccounts(actorId, permission, evidence);
        if (required.IsDefaultOrEmpty || people.IsDefaultOrEmpty || people.Length != required.Length)
            return AuthorizationDecision.Deny("person_evidence_invalid");

        var byUser = new Dictionary<long, PersonEvidence>();
        foreach (var person in people)
        {
            if (person is null || !required.Contains(person.UserId) || !byUser.TryAdd(person.UserId, person))
                return AuthorizationDecision.Deny("person_evidence_invalid");
        }

        var actor = byUser[actorId];
        if (!PersonIndependencePolicy.AreIndependent(actor,
                required.Where(id => id != actorId).Select(id => byUser[id]).ToArray()))
            return AuthorizationDecision.Deny("independent_person_required");

        // Closing a period requires three distinct people; the two historical stages must
        // remain independent even when a later reviewed equivalence links their accounts.
        if (permission == Permissions.PeriodClose &&
            !PersonIndependencePolicy.AreIndependent(byUser[evidence!.SubmittedBy!.Value],
                byUser[evidence.ApprovedBy!.Value]))
            return AuthorizationDecision.Deny("independent_person_required");

        return new(true, "allowed", []);
    }

    private static AuthorizationDecision AccountDecision(long actorId, string permission, ApprovalRequest? evidence)
    {
        if (Permissions.Find(permission) is null) return AuthorizationDecision.Deny("unknown_permission");
        if (actorId <= 0) return AuthorizationDecision.Deny("approval_revision_invalid");
        if (!ApprovalPolicy.RequiresEvidence(permission)) return new(true, "allowed", []);
        return evidence is null
            ? AuthorizationDecision.Deny("approval_evidence_required")
            : ApprovalPolicy.Evaluate(actorId, permission, evidence);
    }
}
