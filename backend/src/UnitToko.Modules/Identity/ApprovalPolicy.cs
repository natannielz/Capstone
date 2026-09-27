namespace UnitToko.Modules.Identity;

/// <summary>Actors are immutable business-history IDs, never inferred from their current role.
/// Revision is the server-loaded aggregate or proposal revision being decided; ExpectedRevision
/// is the command's precondition for that same record. The owning command must load the exact
/// proposed version and requester/proposer from immutable history inside its fenced transaction.
/// For substitution consent it must also authorize the order's buyer scope, not merely this evidence.</summary>
public sealed record ApprovalRequest(
    long Revision, long ExpectedRevision, long? RequestedBy = null, long? RecordedBy = null,
    long? ApprovedBy = null, long? SubmittedBy = null, long? ClosedBy = null,
    long? CountedBy = null, long? ProposedBy = null, long? BeneficiaryId = null,
    long? PreviousRevision = null);

public static class ApprovalPolicy
{
    public static bool RequiresEvidence(string permission) => permission is
        Permissions.OrderSpendApprove or Permissions.OrderAccept or Permissions.SubstitutionAccept or
        Permissions.SupplierChangeApprove or
        Permissions.PaymentVerify or Permissions.CreditApprove or Permissions.RefundApprove or
        Permissions.FundingApprove or Permissions.ExpenseApprove or Permissions.StockAdjustApprove or
        Permissions.DisbursementRecord or Permissions.DisbursementVerify or Permissions.PeriodApprove or
        Permissions.PeriodClose or Permissions.PeriodReopenApprove or Permissions.AccessApprove or
        Permissions.MembershipApprove or Permissions.ReconciliationVerify;

    public static AuthorizationDecision Evaluate(long actorId, string permission, ApprovalRequest evidence)
    {
        if (Permissions.Find(permission) is null) return AuthorizationDecision.Deny("unknown_permission");
        if (actorId <= 0 || evidence.Revision <= 0 || evidence.Revision != evidence.ExpectedRevision)
            return AuthorizationDecision.Deny("approval_revision_invalid");
        bool Independent(params long?[] actors) => actors.All(id => id is > 0 && id != actorId);
        var valid = permission switch
        {
            Permissions.OrderSpendApprove => Independent(evidence.RequestedBy),
            Permissions.SupplierChangeApprove => Independent(evidence.ProposedBy),
            Permissions.OrderAccept => evidence.RequestedBy is > 0,
            Permissions.SubstitutionAccept => evidence.RequestedBy is > 0 && evidence.ProposedBy is > 0,
            Permissions.PaymentVerify or Permissions.DisbursementVerify or Permissions.ReconciliationVerify => Independent(evidence.RecordedBy),
            Permissions.CreditApprove or Permissions.RefundApprove or Permissions.FundingApprove or Permissions.ExpenseApprove => Independent(evidence.RequestedBy),
            Permissions.StockAdjustApprove => Independent(evidence.CountedBy, evidence.ProposedBy),
            Permissions.DisbursementRecord => Independent(evidence.ApprovedBy),
            Permissions.PeriodApprove => Independent(evidence.SubmittedBy),
            Permissions.PeriodClose => Independent(evidence.SubmittedBy, evidence.ApprovedBy) && evidence.SubmittedBy != evidence.ApprovedBy,
            Permissions.PeriodReopenApprove => Independent(evidence.RequestedBy, evidence.ClosedBy) &&
                evidence.PreviousRevision is > 0 && evidence.Revision > evidence.PreviousRevision,
            Permissions.AccessApprove or Permissions.MembershipApprove => Independent(evidence.ProposedBy, evidence.BeneficiaryId),
            _ => true
        };
        return valid ? new(true, "allowed", []) : AuthorizationDecision.Deny("independent_actor_required");
    }
}
