using System.Collections.Immutable;
using UnitToko.Modules.Identity;

namespace UnitToko.Tests.Authorization;

public sealed class PersonIndependenceTests
{
    private static PersonEvidence Person(long user, long person) => new(user, person, PersonVerification.Verified, 1);

    [Fact]
    public void Different_accounts_of_same_person_are_not_independent()
    {
        Assert.False(PersonIndependencePolicy.AreIndependent(Person(101, 900), Person(102, 900)));
        Assert.False(PersonIndependencePolicy.AreIndependent(Person(101, 900), Person(101, 901)));
        Assert.True(PersonIndependencePolicy.AreIndependent(Person(101, 900), Person(103, 901)));
    }

    public static IEnumerable<object?[]> InvalidPeople()
    {
        yield return [null];
        yield return [new PersonEvidence(0, 900, PersonVerification.Verified, 1)];
        yield return [new PersonEvidence(-1, 900, PersonVerification.Verified, 1)];
        yield return [new PersonEvidence(101, null, PersonVerification.Verified, 1)];
        yield return [new PersonEvidence(101, 0, PersonVerification.Verified, 1)];
        yield return [new PersonEvidence(101, -1, PersonVerification.Verified, 1)];
        yield return [new PersonEvidence(101, 900, PersonVerification.Unknown, 1)];
        yield return [new PersonEvidence(101, 900, PersonVerification.Disputed, 1)];
        yield return [new PersonEvidence(101, 900, (PersonVerification)999, 1)];
        yield return [new PersonEvidence(101, 900, PersonVerification.Verified, 0)];
        yield return [new PersonEvidence(101, 900, PersonVerification.Verified, -1)];
    }

    [Theory]
    [MemberData(nameof(InvalidPeople))]
    public void Invalid_evidence_denies_on_either_side(PersonEvidence? invalid)
    {
        Assert.False(PersonIndependencePolicy.AreIndependent(invalid, Person(102, 901)));
        Assert.False(PersonIndependencePolicy.AreIndependent(Person(102, 901), invalid));
    }

    [Fact]
    public void History_is_required_and_every_participant_is_checked()
    {
        Assert.False(PersonIndependencePolicy.AreIndependent(Person(101, 900)));
        Assert.False(PersonIndependencePolicy.AreIndependent(Person(101, 900), (PersonEvidence?[]?)null));
        Assert.False(PersonIndependencePolicy.AreIndependent(Person(101, 900), Person(102, 901), Person(103, 900)));
        // Independence is actor versus each participant, not a blanket history-pair prohibition.
        Assert.True(PersonIndependencePolicy.AreIndependent(Person(101, 900), Person(102, 901), Person(103, 901)));
    }

    public static IEnumerable<object[]> ApprovalCases()
    {
        yield return [Permissions.OrderSpendApprove, new ApprovalRequest(3, 3, RequestedBy: 201), new long[] { 101, 201 }];
        yield return [Permissions.OrderSpendingPolicyApprove, new ApprovalRequest(3, 3, ProposedBy: 202), new long[] { 101, 202 }];
        yield return [Permissions.AccountingBookApprove, new ApprovalRequest(3, 3, ProposedBy: 202), new long[] { 101, 202 }];
        yield return [Permissions.SupplierChangeApprove, new ApprovalRequest(3, 3, ProposedBy: 202), new long[] { 101, 202 }];
        yield return [Permissions.ReceivingAccountVerify, new ApprovalRequest(3, 3, ProposedBy: 202), new long[] { 101, 202 }];
        yield return [Permissions.ReceivingAccountApprove, new ApprovalRequest(3, 3, ProposedBy: 202), new long[] { 101, 202 }];
        yield return [Permissions.BankReceiptConfirm, new ApprovalRequest(3, 3, RecordedBy: 203), new long[] { 101, 203 }];
        yield return [Permissions.PaymentVerify, new ApprovalRequest(3, 3, RecordedBy: 203), new long[] { 101, 203 }];
        yield return [Permissions.DisbursementVerify, new ApprovalRequest(3, 3, RecordedBy: 203), new long[] { 101, 203 }];
        yield return [Permissions.ReconciliationVerify, new ApprovalRequest(3, 3, RecordedBy: 203), new long[] { 101, 203 }];
        yield return [Permissions.CreditApprove, new ApprovalRequest(3, 3, RequestedBy: 201), new long[] { 101, 201 }];
        yield return [Permissions.RefundApprove, new ApprovalRequest(3, 3, RequestedBy: 201), new long[] { 101, 201 }];
        yield return [Permissions.FundingApprove, new ApprovalRequest(3, 3, RequestedBy: 201), new long[] { 101, 201 }];
        yield return [Permissions.ExpenseApprove, new ApprovalRequest(3, 3, RequestedBy: 201), new long[] { 101, 201 }];
        yield return [Permissions.StockAdjustApprove, new ApprovalRequest(3, 3, CountedBy: 205, ProposedBy: 202), new long[] { 101, 202, 205 }];
        yield return [Permissions.DisbursementRecord, new ApprovalRequest(3, 3, ApprovedBy: 204), new long[] { 101, 204 }];
        yield return [Permissions.PeriodApprove, new ApprovalRequest(3, 3, SubmittedBy: 206), new long[] { 101, 206 }];
        yield return [Permissions.PeriodClose, new ApprovalRequest(3, 3, SubmittedBy: 206, ApprovedBy: 204), new long[] { 101, 204, 206 }];
        yield return [Permissions.PeriodReopenApprove, new ApprovalRequest(3, 3, RequestedBy: 201, ClosedBy: 207, PreviousRevision: 2), new long[] { 101, 201, 207 }];
        yield return [Permissions.AccessApprove, new ApprovalRequest(3, 3, ProposedBy: 202, BeneficiaryId: 208), new long[] { 101, 202, 208 }];
        yield return [Permissions.MembershipApprove, new ApprovalRequest(3, 3, ProposedBy: 202, BeneficiaryId: 208), new long[] { 101, 202, 208 }];
    }

    [Theory]
    [MemberData(nameof(ApprovalCases))]
    public void Every_approval_binds_actor_and_complete_stored_participant_set(string permission, ApprovalRequest request, long[] required)
    {
        Assert.True(PersonApprovalPolicy.RequiresEvidence(permission));
        Assert.Equal(required, PersonApprovalPolicy.RequiredAccounts(101, permission, request));
        var people = required.Select(id => Person(id, id + 1000)).ToImmutableArray();
        Assert.True(PersonApprovalPolicy.Evaluate(101, permission, request, people.Reverse().ToImmutableArray()).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, default).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, []).Allowed);
        foreach (var participant in people)
        {
            Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, people.Remove(participant)).Allowed);
            Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request,
                people.Replace(participant, participant with { UserId = 999 })).Allowed);
            Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request,
                people.Replace(participant, participant with { Verification = PersonVerification.Unknown })).Allowed);
            Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request,
                people.Replace(participant, participant with { Verification = PersonVerification.Disputed })).Allowed);
            if (participant.UserId != 101)
                Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request,
                    people.Replace(participant, participant with { PersonId = 1101 })).Allowed);
        }
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, people.Add(people[0])).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, people.Add(Person(999, 1999))).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, people.Add(people[0] with { PersonId = 9999 })).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, people.SetItem(0, null!)).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request, people.SetItem(0, people[0] with { Version = 0 })).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, null, people).Allowed);
        Assert.True(PersonApprovalPolicy.RequiredAccounts(101, permission, null).IsDefault);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, request with { ExpectedRevision = 2 }, people).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission, new(3, 3), people).Allowed);
    }

    [Fact]
    public void Period_close_requires_three_people_even_when_three_accounts_are_distinct()
    {
        var request = new ApprovalRequest(3, 3, SubmittedBy: 201, ApprovedBy: 202);
        Assert.False(PersonApprovalPolicy.Evaluate(101, Permissions.PeriodClose, request,
            [Person(101, 900), Person(201, 901), Person(202, 901)]).Allowed);
        Assert.True(PersonApprovalPolicy.Evaluate(101, Permissions.PeriodClose, request,
            [Person(101, 900), Person(201, 901), Person(202, 902)]).Allowed);
    }

    [Theory]
    [InlineData(Permissions.StockAdjustApprove)]
    [InlineData(Permissions.AccessApprove)]
    [InlineData(Permissions.MembershipApprove)]
    [InlineData(Permissions.PeriodReopenApprove)]
    public void Other_multi_participant_workflows_preserve_actor_versus_history_semantics(string permission)
    {
        var request = new ApprovalRequest(3, 3, CountedBy: 201, ProposedBy: 202, BeneficiaryId: 201,
            RequestedBy: 201, ClosedBy: 202, PreviousRevision: 2);
        Assert.True(PersonApprovalPolicy.Evaluate(101, permission, request,
            [Person(101, 900), Person(201, 901), Person(202, 901)]).Allowed);
        var sameAccount = request with { CountedBy = 202, BeneficiaryId = 202, RequestedBy = 202 };
        Assert.Equal(new long[] { 101, 202 }, PersonApprovalPolicy.RequiredAccounts(101, permission, sameAccount));
        Assert.True(PersonApprovalPolicy.Evaluate(101, permission, sameAccount,
            [Person(101, 900), Person(202, 901)]).Allowed);
    }

    [Fact]
    public void Stored_account_checks_and_reopen_revision_cannot_be_replaced_by_person_proof()
    {
        Assert.False(PersonApprovalPolicy.Evaluate(101, Permissions.PaymentVerify, new(3, 3, RecordedBy: 101),
            [Person(101, 900), Person(201, 901)]).Allowed);
        Assert.True(PersonApprovalPolicy.RequiredAccounts(101, Permissions.PaymentVerify, new(3, 3, RecordedBy: 101)).IsDefault);
        Assert.False(PersonApprovalPolicy.Evaluate(101, Permissions.PeriodClose, new(3, 3, SubmittedBy: 201, ApprovedBy: 201),
            [Person(101, 900), Person(201, 901)]).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, Permissions.PeriodReopenApprove,
            new(3, 3, RequestedBy: 201, ClosedBy: 202, PreviousRevision: 3),
            [Person(101, 900), Person(201, 901), Person(202, 902)]).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(0, Permissions.PaymentVerify, new(3, 3, RecordedBy: 201),
            [Person(101, 900), Person(201, 901)]).Allowed);
    }

    [Theory]
    [InlineData(Permissions.OrderRead)]
    [InlineData(Permissions.OrderSubmit)]
    [InlineData(Permissions.CatalogRead)]
    [InlineData(Permissions.PaymentRecord)]
    public void Operational_permissions_do_not_require_person_proof(string permission)
    {
        Assert.False(PersonApprovalPolicy.RequiresEvidence(permission));
        var required = PersonApprovalPolicy.RequiredAccounts(101, permission, null);
        Assert.False(required.IsDefault);
        Assert.Empty(required);
        Assert.True(PersonApprovalPolicy.Evaluate(101, permission, null, default).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(0, permission, null, default).Allowed);
    }

    [Fact]
    public void Operational_consent_preserves_account_evidence_without_person_proof()
    {
        Assert.False(PersonApprovalPolicy.RequiresEvidence(Permissions.OrderAccept));
        Assert.False(PersonApprovalPolicy.RequiresEvidence(Permissions.SubstitutionAccept));
        Assert.True(PersonApprovalPolicy.Evaluate(101, Permissions.OrderAccept, new(3, 3, RequestedBy: 101), default).Allowed);
        Assert.True(PersonApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept,
            new(3, 3, RequestedBy: 101, ProposedBy: 101), default).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, Permissions.OrderAccept, null, default).Allowed);
        Assert.False(PersonApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept, new(3, 3, RequestedBy: 101), default).Allowed);
    }

    [Theory]
    [InlineData("missing.permission")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_permissions_fail_closed(string? permission)
    {
        Assert.False(PersonApprovalPolicy.RequiresEvidence(permission!));
        Assert.True(PersonApprovalPolicy.RequiredAccounts(101, permission!, null).IsDefault);
        Assert.False(PersonApprovalPolicy.Evaluate(101, permission!, null, default).Allowed);
    }
}
