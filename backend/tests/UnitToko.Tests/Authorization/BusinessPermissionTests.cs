using System.Collections.Immutable;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class BusinessPermissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
    private static readonly ActorContext Actor = new(101, "https://identity.example", "subject-101", 7,
        Now.AddMinutes(-1), FactorAssurance.MultiFactor);
    private static readonly GrantScope Buyer = new(GrantScopeKind.BuyerOwned, 1, 11, 111);
    private static readonly GrantScope Branch = new(GrantScopeKind.BuyingBranch, 1, 11);
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 2, ShopId: 21, ChannelId: 201);
    private static readonly ScopeDirectory Directory = new([
        new(1, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
        new(11, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 1),
        new(111, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1),
        new(2, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
        new(21, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1),
        new(201, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1)
    ]);
    private static readonly ResourceScope Order = new(1, 11, 111, 2, 21, null, 201, Actor.UserId, [Actor.UserId]);

    [Fact]
    public void Buyer_spending_and_store_acceptance_are_separate_explicit_authorities()
    {
        var buyer = Snapshot("buyer", Branch, [Permissions.BranchPic, Permissions.OrderSpendApprove]);
        Assert.True(ScopePolicy.Compile(Actor, buyer, Permissions.OrderSpendApprove, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, buyer, Permissions.OrderAccept, Now).IsAllowed);
        var store = Snapshot("store-head", Shop, [Permissions.OrderAccept]);
        Assert.True(ScopePolicy.Compile(Actor, store, Permissions.OrderAccept, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, store, Permissions.OrderSpendApprove, Now).IsAllowed);
        Assert.DoesNotContain(Permissions.OrderSpendApprove, RoleTemplates.Templates["buyer"].Permissions);
        Assert.DoesNotContain(Permissions.OrderAccept, RoleTemplates.Templates["store-head"].Permissions);
    }

    [Fact]
    public void Spending_policy_governance_is_scoped_explicit_and_separate_from_order_approval()
    {
        foreach (var permission in new[] { Permissions.OrderSpendingPolicyRead, Permissions.OrderSpendingPolicyPropose, Permissions.OrderSpendingPolicyApprove }) {
            Assert.Equal(new[] { GrantScopeKind.BuyingDivision, GrantScopeKind.BuyingBranch, GrantScopeKind.BuyingOrganization }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(permission, role.Permissions));
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Branch, [Permissions.OrderSpendApprove]), permission, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Branch, [permission]), permission, Now).IsAllowed);
            var policy = Snapshot("buyer", Branch, [permission, Permissions.BranchPic]);
            Assert.True(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, policy, Permissions.OrderSpendApprove, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, policy, Permissions.OrderAccept, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer, [permission]), permission, Now).IsAllowed);
            if (permission != Permissions.OrderSpendingPolicyRead) {
                Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
                Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, policy, permission, Now).IsAllowed);
            }
        }
        const string approval = Permissions.OrderSpendingPolicyApprove;
        Assert.True(ApprovalPolicy.RequiresEvidence(approval)); Assert.True(PersonApprovalPolicy.RequiresEvidence(approval));
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, ProposedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 2, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1)).Allowed);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(101, approval, new(1, 1, ProposedBy: 102)).ToArray());
    }

    [Fact]
    public void Commercial_policy_writes_require_explicit_exact_seller_shop_authority_and_fresh_MFA()
    {
        var directory = new ScopeDirectory([.. Directory.Nodes,
            new(22, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1),
            new(202, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1),
            new(211, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1)]);
        foreach (var permission in new[] { Permissions.OrderCommercialPolicyPropose, Permissions.OrderCommercialPolicyApprove }) {
            var definition = Permissions.Find(permission)!;
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop }, definition.AllowedScopes.ToArray());
            Assert.True(definition.Assurance.RequiresMfa);
            Assert.Equal(TimeSpan.FromMinutes(5), definition.Assurance.MaximumAge);
            Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(permission, role.Permissions));
            Assert.DoesNotContain(permission, RoleTemplates.Materialize("finance", RoleTemplates.FinancePacks.Keys, []).Permissions);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop), permission, Now).IsAllowed);
            foreach (var buyerAuthority in new[] { Permissions.OrderSpendApprove, Permissions.OrderSpendingPolicyApprove })
                Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Branch, [Permissions.BranchPic, buyerAuthority]), permission, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Branch, [Permissions.BranchPic, permission]), permission, Now).IsAllowed);
            var warehouse = Snapshot("finance", new(GrantScopeKind.FulfillmentWarehouse, 2, ShopId: 21, WarehouseId: 211, ChannelId: 201), [permission]) with { Directory = directory };
            Assert.False(ScopePolicy.Compile(Actor, warehouse, permission, Now).IsAllowed);

            var policy = Snapshot("finance", Shop, [permission]) with { Directory = directory };
            var query = ScopePolicy.Compile(Actor, policy, permission, Now);
            Assert.True(query.Matches(Order));
            Assert.False(query.Matches(Order with { ShopId = 22 }));
            Assert.False(query.Matches(Order with { ChannelId = 202 }));
            Assert.Equal("step_up_required", ScopePolicy.Compile(Actor with { Assurance = FactorAssurance.SingleFactor }, policy, permission, Now).DenialCode);
            Assert.True(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-5) }, policy, permission, Now).Matches(Order));
            Assert.Equal("step_up_required", ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-5).AddTicks(-1) }, policy, permission, Now).DenialCode);
        }
    }

    [Fact]
    public void Commercial_policy_reads_allow_explicit_buying_scope_and_seller_shop_without_write_authority()
    {
        const string read = Permissions.OrderCommercialPolicyRead;
        Assert.Equal(new[] { GrantScopeKind.BuyingDivision, GrantScopeKind.BuyingBranch, GrantScopeKind.BuyingOrganization, GrantScopeKind.FulfillmentShop },
            Permissions.Find(read)!.AllowedScopes.ToArray());
        Assert.Null(Permissions.Find(read)!.Assurance.MaximumAge);
        Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(read, role.Permissions));
        foreach (var scope in new[] { new GrantScope(GrantScopeKind.BuyingDivision, 1, 11, 111), Branch,
            new GrantScope(GrantScopeKind.BuyingOrganization, 1), Shop }) {
            string[] additions = scope.Kind switch {
                GrantScopeKind.BuyingOrganization => [read, Permissions.OrganizationWide],
                GrantScopeKind.FulfillmentShop => [read], _ => [read, Permissions.BranchPic] };
            var reader = Snapshot(scope == Shop ? "finance" : "buyer", scope, additions);
            Assert.True(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, reader, read, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, reader, Permissions.OrderCommercialPolicyPropose, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, reader, Permissions.OrderCommercialPolicyApprove, Now).IsAllowed);
        }
        Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer, [read]), read, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Branch, [read]), read, Now).IsAllowed);
    }

    [Fact]
    public void Commercial_policy_approval_requires_the_stored_independent_proposer_and_exact_revision()
    {
        const string approve = Permissions.OrderCommercialPolicyApprove;
        var checker = Snapshot("finance", Shop, [approve]);
        Assert.True(ApprovalPolicy.RequiresEvidence(approve));
        Assert.True(PersonApprovalPolicy.RequiresEvidence(approve));
        Assert.True(ScopePolicy.Authorize(Actor, checker, approve, Order, Now, new(3, 3, ProposedBy: 102)).Allowed);
        Assert.Equal("approval_evidence_required", ScopePolicy.Authorize(Actor, checker, approve, Order, Now).Code);
        Assert.Equal("approval_revision_invalid", ScopePolicy.Authorize(Actor, checker, approve, Order, Now, new(3, 2, ProposedBy: 102)).Code);
        Assert.Equal("independent_actor_required", ScopePolicy.Authorize(Actor, checker, approve, Order, Now, new(3, 3, ProposedBy: Actor.UserId)).Code);
        Assert.Equal("independent_actor_required", ScopePolicy.Authorize(Actor, checker, approve, Order, Now, new(3, 3, RequestedBy: 102)).Code);
    }

    [Fact]
    public void Accounting_book_setup_requires_exact_shop_authority_and_independent_approval_without_posting_authority()
    {
        foreach (var permission in new[] { Permissions.AccountingBookRead, Permissions.AccountingBookPropose, Permissions.AccountingBookApprove }) {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(permission, role.Permissions));
            var policy = Snapshot("finance", Shop, [permission]);
            Assert.True(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order with { ShopId = 22 }));
            Assert.False(ScopePolicy.Compile(Actor, policy, Permissions.JournalPost, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, policy, Permissions.PeriodClose, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop, [Permissions.JournalPost]), permission, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer, [permission]), permission, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", new(GrantScopeKind.FulfillmentWarehouse, 2, ShopId: 21, WarehouseId: 211, ChannelId: 201), [permission]), permission, Now).IsAllowed);
            if (permission != Permissions.AccountingBookRead) {
                Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
                Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, policy, permission, Now).IsAllowed);
            }
        }
        const string approval = Permissions.AccountingBookApprove;
        Assert.True(ApprovalPolicy.RequiresEvidence(approval)); Assert.True(PersonApprovalPolicy.RequiresEvidence(approval));
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, ProposedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 2, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1)).Allowed);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(101, approval, new(1, 1, ProposedBy: 102)).ToArray());
    }

    [Fact]
    public void Advance_funding_requires_private_scoped_authority_fresh_factor_and_requester_independence_without_payment_authority()
    {
        foreach (var permission in new[] { Permissions.FundingRead, Permissions.FundingRequest, Permissions.FundingApprove }) {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            var policy = Snapshot("finance", Shop, [permission]);
            Assert.True(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order with { ShopId = 22 }));
            foreach (var other in new[] { Permissions.StockReceive, Permissions.StockAdjustApprove, Permissions.PaymentRecord, Permissions.DisbursementRecord, Permissions.JournalPost })
                Assert.False(ScopePolicy.Compile(Actor, policy, other, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop, [Permissions.StockReceive]), permission, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Buyer, [permission]), permission, Now).IsAllowed);
            var warehouse = Snapshot("finance", new(GrantScopeKind.FulfillmentWarehouse, 2, ShopId: 21, WarehouseId: 211, ChannelId: 201), [permission]) with {
                Directory = new([.. Directory.Nodes, new(211, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1)]) };
            Assert.True(ScopePolicy.Compile(Actor, warehouse, permission, Now).Matches(Order with { WarehouseId = 211 }));
            Assert.False(ScopePolicy.Compile(Actor, warehouse, permission, Now).Matches(Order with { WarehouseId = 212 }));
            if (permission != Permissions.FundingRead) {
                Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
                Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, policy, permission, Now).IsAllowed);
            }
        }
        const string approval = Permissions.FundingApprove;
        Assert.True(ApprovalPolicy.RequiresEvidence(approval)); Assert.True(PersonApprovalPolicy.RequiresEvidence(approval));
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, RequestedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 2, RequestedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, ProposedBy: 102)).Allowed);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(101, approval, new(1, 1, RequestedBy: 102)).ToArray());
    }

    [Fact]
    public void Procurement_disbursement_reads_are_explicit_and_recording_keeps_approver_independence()
    {
        foreach (var permission in new[] { Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead }) {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(permission, role.Permissions));
            var reader = Snapshot("finance", Shop, [permission]);
            Assert.True(ScopePolicy.Compile(Actor, reader, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, reader, permission, Now).Matches(Order with { ShopId = 22 }));
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer, [permission]), permission, Now).IsAllowed);
            foreach (var other in new[] { Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead,
                Permissions.ProcurementRead, Permissions.FundingApprove, Permissions.SupplierBankDetailsRead,
                Permissions.DisbursementRecord, Permissions.DisbursementVerify, Permissions.JournalPost }.Where(value => value != permission))
                Assert.False(ScopePolicy.Compile(Actor, reader, other, Now).IsAllowed);
        }
        Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(Permissions.DisbursementRecord)!.Assurance.MaximumAge);
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.DisbursementRecord));
        Assert.True(PersonApprovalPolicy.RequiresEvidence(Permissions.DisbursementRecord));
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.DisbursementRecord, new(1, 1, ApprovedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.DisbursementRecord, new(1, 1, RequestedBy: 102)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.DisbursementRecord, new(1, 1, ApprovedBy: 102)).Allowed);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(101, Permissions.DisbursementRecord, new(1, 1, ApprovedBy: 102)).ToArray());
    }

    [Theory]
    [InlineData(Permissions.ProcurementPayableRead, Permissions.ProcurementPayablePropose, Permissions.ProcurementPayableDecide)]
    [InlineData(Permissions.ProcurementAdvanceReturnRead, Permissions.ProcurementAdvanceReturnPropose, Permissions.ProcurementAdvanceReturnDecide)]
    [InlineData(Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementQualityAcceptanceDecide)]
    public void Supplier_payable_and_return_authorities_are_explicit_scoped_and_independent_of_posting(string read, string propose, string decide)
    {
        foreach (var permission in new[] { read, propose, decide }) {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(permission, role.Permissions));
            Assert.DoesNotContain(permission, RoleTemplates.Materialize("finance", RoleTemplates.FinancePacks.Keys, []).Permissions);
            var policy = Snapshot("finance", Shop, [permission]);
            Assert.True(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order with { ShopId = 22 }));
            Assert.False(ScopePolicy.Compile(Actor, policy, Permissions.JournalPost, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop, [Permissions.JournalPost]), permission, Now).IsAllowed);
            foreach (var other in new[] { read, propose, decide }.Where(value => value != permission))
                Assert.False(ScopePolicy.Compile(Actor, policy, other, Now).IsAllowed);
            if (permission != read) {
                Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
                Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, policy, permission, Now).IsAllowed);
            }
        }
        var approval = decide;
        Assert.True(ApprovalPolicy.RequiresEvidence(approval)); Assert.True(PersonApprovalPolicy.RequiresEvidence(approval));
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, RequestedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 2, RequestedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1)).Allowed);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(101, approval, new(1, 1, RequestedBy: 102)).ToArray());
    }

    [Fact]
    public void Supplier_bank_authorities_are_shop_only_private_explicit_and_independent_without_transfer_rights()
    {
        foreach (var permission in new[] { Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose, Permissions.SupplierBankApprove }) {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(permission, role.Permissions));
            var policy = Snapshot("finance", Shop, [permission]);
            Assert.True(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order with { ShopId = 22 }));
            foreach (var other in new[] { Permissions.SupplierChangeApprove, Permissions.FundingApprove, Permissions.DisbursementRecord, Permissions.PaymentRecord, Permissions.JournalPost })
                Assert.False(ScopePolicy.Compile(Actor, policy, other, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop, [Permissions.SupplierChangeApprove]), permission, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", new(GrantScopeKind.FulfillmentWarehouse, 2, ShopId: 21, WarehouseId: 211, ChannelId: 201), [permission]), permission, Now).IsAllowed);
            foreach (var other in new[] { Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose, Permissions.SupplierBankApprove }.Where(value => value != permission))
                Assert.False(ScopePolicy.Compile(Actor, policy, other, Now).IsAllowed);
            if (permission is Permissions.SupplierBankPropose or Permissions.SupplierBankApprove) {
                Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
                Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, policy, permission, Now).IsAllowed);
            }
        }
        const string approval = Permissions.SupplierBankApprove;
        Assert.True(ApprovalPolicy.RequiresEvidence(approval)); Assert.True(PersonApprovalPolicy.RequiresEvidence(approval));
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, ProposedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 2, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, RequestedBy: 102)).Allowed);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(101, approval, new(1, 1, ProposedBy: 102)).ToArray());
    }

    [Fact]
    public void Procurement_requests_require_scoped_current_authority_and_requester_independence_without_receiving_or_payment_authority()
    {
        foreach (var permission in new[] { Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.ProcurementApprove }) {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            var policy = Snapshot("finance", Shop, [permission]);
            Assert.True(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order with { ShopId = 22 }));
            foreach (var other in new[] { Permissions.StockReceive, Permissions.StockAdjustApprove, Permissions.PaymentRecord, Permissions.DisbursementRecord, Permissions.JournalPost })
                Assert.False(ScopePolicy.Compile(Actor, policy, other, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop, [Permissions.StockReceive]), permission, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Buyer, [permission]), permission, Now).IsAllowed);
            var warehouse = Snapshot("finance", new(GrantScopeKind.FulfillmentWarehouse, 2, ShopId: 21, WarehouseId: 211, ChannelId: 201), [permission]) with {
                Directory = new([.. Directory.Nodes, new(211, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1)]) };
            Assert.True(ScopePolicy.Compile(Actor, warehouse, permission, Now).Matches(Order with { WarehouseId = 211 }));
            Assert.False(ScopePolicy.Compile(Actor, warehouse, permission, Now).Matches(Order with { WarehouseId = 212 }));
            if (permission != Permissions.ProcurementRead) {
                Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
                Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, policy, permission, Now).IsAllowed);
            }
        }
        const string approval = Permissions.ProcurementApprove;
        Assert.True(ApprovalPolicy.RequiresEvidence(approval)); Assert.True(PersonApprovalPolicy.RequiresEvidence(approval));
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, RequestedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 2, RequestedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, approval, new(1, 1, ProposedBy: 102)).Allowed);
        Assert.Equal(new long[] { 101, 102 }, PersonApprovalPolicy.RequiredAccounts(101, approval, new(1, 1, RequestedBy: 102)).ToArray());
    }

    [Fact]
    public void Supplier_governance_is_shop_only_fresh_and_separate_from_procurement_or_payment_authority()
    {
        foreach (var permission in new[] { Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove }) {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop }, Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
            var policy = Snapshot("finance", Shop, [permission]);
            Assert.True(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order));
            Assert.False(ScopePolicy.Compile(Actor, policy, permission, Now).Matches(Order with { ShopId = 22 }));
            Assert.False(ScopePolicy.Compile(Actor with { AuthenticatedAt = Now.AddMinutes(-6) }, policy, permission, Now).IsAllowed);
            foreach (var other in new[] { Permissions.ProcurementPrepare, Permissions.ProcurementApprove, Permissions.StockReceive, Permissions.PaymentRecord, Permissions.DisbursementRecord })
                Assert.False(ScopePolicy.Compile(Actor, policy, other, Now).IsAllowed);
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("finance", Shop, [Permissions.ProcurementRead]), permission, Now).IsAllowed);
            var warehouse = Snapshot("finance", new(GrantScopeKind.FulfillmentWarehouse, 2, ShopId: 21, WarehouseId: 211, ChannelId: 201), [permission]) with {
                Directory = new([.. Directory.Nodes, new(211, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1)]) };
            Assert.False(ScopePolicy.Compile(Actor, warehouse, permission, Now).IsAllowed);
            Assert.All(RoleTemplates.Templates.Values, role => Assert.DoesNotContain(permission, role.Permissions));
        }
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SupplierChangeApprove, new(1, 1, ProposedBy: 101)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SupplierChangeApprove, new(1, 1, RequestedBy: 102)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.SupplierChangeApprove, new(1, 1, ProposedBy: 102)).Allowed);
    }

    [Fact]
    public void Store_proposal_cannot_consent_and_buyer_consent_cannot_propose()
    {
        var staff = Snapshot("store-staff", Shop, [Permissions.SubstitutionPropose]);
        var buyer = Snapshot("buyer", Buyer, [Permissions.SubstitutionAccept]);
        Assert.True(ScopePolicy.Compile(Actor, staff, Permissions.SubstitutionPropose, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, staff, Permissions.SubstitutionAccept, Now).IsAllowed);
        Assert.True(ScopePolicy.Compile(Actor, buyer, Permissions.SubstitutionAccept, Now).Matches(Order));
        Assert.False(ScopePolicy.Compile(Actor, buyer, Permissions.SubstitutionPropose, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, buyer, Permissions.SubstitutionAccept, Now)
            .Matches(Order with { CreatedBy = 999, AssignedUserIds = [] }));
    }

    [Fact]
    public void Fulfillment_stock_supplier_and_read_only_scopes_are_explicit()
    {
        foreach (var permission in new[] { Permissions.StockConvert, Permissions.ReservationRelease })
        {
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse },
                Permissions.Find(permission)!.AllowedScopes.ToArray());
            Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer), permission, Now).IsAllowed);
        }
        foreach (var permission in new[] { Permissions.HistoryRead, Permissions.ReportRead })
        {
            Assert.Contains(GrantScopeKind.BuyerOwned, Permissions.Find(permission)!.AllowedScopes);
            Assert.Contains(GrantScopeKind.FulfillmentShop, Permissions.Find(permission)!.AllowedScopes);
            Assert.DoesNotContain(GrantScopeKind.System, Permissions.Find(permission)!.AllowedScopes);
            Assert.DoesNotContain(GrantScopeKind.CourierAssignment, Permissions.Find(permission)!.AllowedScopes);
            var baseGrant = Snapshot("buyer", Buyer);
            var auditor = baseGrant with
            {
                Grants = [baseGrant.Grants[0] with { Permissions = [permission] }]
            };
            Assert.True(ScopePolicy.Compile(Actor, auditor, permission, Now).Matches(Order));
            foreach (var other in Permissions.Catalog.Keys.Where(name => name != permission))
                Assert.False(ScopePolicy.Compile(Actor, auditor, other, Now).IsAllowed);
        }
    }

    [Fact]
    public void Suggestions_do_not_change_v1_materialization_or_legacy_snapshot()
    {
        Assert.Equal(1, RoleTemplates.CurrentVersion);
        // Branch requests (pilot) and held stock release added suggestions; none of them is granted by a v1 template.
        Assert.Equal(new[] { Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit, Permissions.SubstitutionAccept, Permissions.OrganizationAddressRead },
            RoleTemplates.Suggestions["buyer"].ToArray());
        Assert.Equal(new[] { Permissions.OrganizationAddressRead, Permissions.SubstitutionPropose, Permissions.StockConvert, Permissions.ReservationRelease,
            Permissions.SupplierChangePropose, Permissions.StockReleasePropose }, RoleTemplates.Suggestions["store-staff"].ToArray());
        Assert.Equal(new[] { Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestDecide, Permissions.OrderAccept, Permissions.SupplierChangeApprove,
            Permissions.OrganizationAddressRead, Permissions.FundingRead, Permissions.CreditRequest, Permissions.RefundRequest, Permissions.InvoiceRead }, RoleTemplates.Suggestions["store-head"].ToArray());
        foreach (var role in new[] { "buyer", "store-staff", "store-head" })
            foreach (var suggestion in RoleTemplates.Suggestions[role])
                Assert.DoesNotContain(suggestion, RoleTemplates.Materialize(role, [], []).Permissions);
        var financeSuggestions = new[] { Permissions.OrganizationAddressRead, Permissions.BankReceiptRead, Permissions.BankReceiptConfirm,
            Permissions.BankStatementRead,
            Permissions.PaymentReviewRead, Permissions.PaymentReviewCorrect,
            Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead,
            Permissions.ReceivingAccountPropose, Permissions.FundingRead,
            Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose,
            Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead,
            Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose,
            Permissions.StockReleaseApprove };
        Assert.Equal(financeSuggestions, RoleTemplates.Suggestions["finance"].ToArray());
        var allFinancePacks = RoleTemplates.Materialize("finance", RoleTemplates.FinancePacks.Keys, []).Permissions;
        foreach (var suggestion in financeSuggestions)
            Assert.DoesNotContain(suggestion, allFinancePacks);
        Assert.Empty(RoleTemplates.Suggestions["courier"]);
        Assert.Empty(RoleTemplates.Suggestions["system-admin"]);
        Assert.DoesNotContain(Permissions.SubstitutionAccept, RoleTemplates.Materialize("buyer", [], []).Permissions);
        Assert.DoesNotContain(Permissions.SubstitutionPropose, RoleTemplates.Materialize("store-staff", [], []).Permissions);
        Assert.NotNull(Permissions.Find(Permissions.OrderApprove));
        Assert.NotNull(Permissions.Find(Permissions.OrderSubstitute));
        var legacy = Snapshot("buyer", Buyer);
        Assert.True(ScopePolicy.Compile(Actor, legacy, Permissions.OrderSubstitute, Now).IsAllowed);
        Assert.False(ScopePolicy.Compile(Actor, legacy, Permissions.SubstitutionAccept, Now).IsAllowed);
    }

    [Fact]
    public void Submitted_payment_review_requires_an_explicit_scoped_grant_and_never_grants_financial_actions()
    {
        Assert.DoesNotContain(Permissions.PaymentReviewRead, RoleTemplates.Materialize("finance", ["billing"], []).Permissions);
        var review = Snapshot("finance", Shop, [Permissions.PaymentReviewRead]);
        Assert.True(ScopePolicy.Compile(Actor, review, Permissions.PaymentReviewRead, Now).Matches(Order));
        foreach (var permission in new[] { Permissions.PaymentEvidence, Permissions.PaymentRecord, Permissions.PaymentAllocate, Permissions.PaymentVerify })
            Assert.False(ScopePolicy.Compile(Actor, review, permission, Now).IsAllowed);
        Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, Permissions.Find(Permissions.PaymentReviewRead)!.AllowedScopes.ToArray());
        Assert.False(ScopePolicy.Compile(Actor, Snapshot("buyer", Buyer), Permissions.PaymentReviewRead, Now).IsAllowed);
    }

    [Fact]
    public void Approval_evidence_checks_origin_and_exact_revision()
    {
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.OrderSpendApprove));
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.OrderAccept));
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.SubstitutionAccept));
        Assert.True(ApprovalPolicy.RequiresEvidence(Permissions.SupplierChangeApprove));
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.OrderSpendApprove, new(2, 2, RequestedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.OrderSpendApprove, new(2, 2, RequestedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SupplierChangeApprove, new(2, 2, ProposedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.SupplierChangeApprove, new(2, 2, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept, new(2, 1, RequestedBy: 101, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept, new(2, 2, RequestedBy: 101)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.SubstitutionAccept, new(2, 2, RequestedBy: 101, ProposedBy: 102)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(101, Permissions.OrderAccept, new(2, 2)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(101, Permissions.OrderAccept, new(2, 2, RequestedBy: 101)).Allowed);
        Assert.NotNull(Permissions.Find(Permissions.OrderSpendApprove)!.Assurance.MaximumAge);
        Assert.NotNull(Permissions.Find(Permissions.SupplierChangeApprove)!.Assurance.MaximumAge);
        Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(Permissions.PaymentVerify)!.Assurance.MaximumAge);
    }

    private static AuthorizationSnapshot Snapshot(string template, GrantScope scope, string[]? additions = null)
    {
        var permissions = RoleTemplates.Materialize(template, [], additions ?? []).Permissions;
        return new(Actor.UserId, Actor.GrantVersion, AuthorizationStatus.Active,
            [new(1001, Actor.UserId, 501, template, 1, permissions, scope, AuthorizationStatus.Active,
                801, 802, "Reviewed business duties", Now.AddDays(-1))],
            [new(501, Actor.UserId, scope, AuthorizationStatus.Active, 802, Now.AddDays(-1))], Directory);
    }
}
