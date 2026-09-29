using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Inventory;

public sealed class StockSourceOperatingPolicyTests
{
    private const string Propose = "stock.source.policy.propose";
    private const string Approve = "stock.source.policy.approve";

    [Fact]
    public void Operating_policy_requires_explicit_scoped_grants_and_independent_fresh_review()
    {
        foreach (var name in new[] { Propose, Approve })
        {
            var permission = Permissions.Find(name);
            Assert.NotNull(permission);
            Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, permission.AllowedScopes.ToArray());
            foreach (var role in RoleTemplates.Templates.Values) Assert.DoesNotContain(name, role.Permissions);
        }
        Assert.Null(Permissions.Find(Propose)!.Assurance.MaximumAge);
        Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(Approve)!.Assurance.MaximumAge);
        Assert.True(ApprovalPolicy.RequiresEvidence(Approve));
        Assert.True(PersonApprovalPolicy.RequiresEvidence(Approve));
        Assert.False(ApprovalPolicy.Evaluate(11, Approve, new(1, 1, ProposedBy: 11)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(12, Approve, new(1, 1, ProposedBy: 11)).Allowed);
        Assert.Equal(new long[] { 11, 12 }, PersonApprovalPolicy.RequiredAccounts(12, Approve, new(1, 1, ProposedBy: 11)).ToArray());
        Assert.True(PersonApprovalPolicy.RequiredAccounts(12, Approve, null).IsDefault);
        Assert.False(ApprovalPolicy.RequiresEvidence(Propose));
    }
    private static StockSourceSetupInput Source(bool external = false) => new(new(new(100), new(101), new(102), new(103)),
        "physical-pool", "Synthetic source", external ? StockAuthorityMode.ExternalLedger : StockAuthorityMode.WebLedger,
        external ? StockPoolControl.ExternalHold : StockPoolControl.SharedWebLedger, 60001, 9007199254740993,
        external ? "synthetic-ledger" : null, external ? "synthetic-protocol" : null, "Synthetic owner", "Synthetic handoff");
    private static StockSourceOperatingPolicyInput Input(bool external = false) => new(
        external ? StockMovementWriter.ExternalSystem : StockMovementWriter.UnitToko,
        external ? "Tenant-A/02" : null, external ? 120000 : 0, "Reconciliation procedure v3", "Outage procedure v2");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Writer_policy_keeps_explicit_case_sensitive_tenant_and_exact_source_buffer(bool external)
    {
        var input = Input(external); var source = Source(external);
        StockSourceOperatingPolicyRules.Validate(input, source);
        Assert.Equal(9007199254740993L, source.SafetyBufferBaseUnits);
        Assert.Equal(external ? "Tenant-A/02" : null, input.ExternalTenant);
        // Feed intervals are facts, not invented freshness guarantees. A long interval can mean
        // that the source is unavailable between feeds under its shorter approved maximum age.
        if (external) Assert.True(input.FeedIntervalMilliseconds > source.MaximumAgeMilliseconds);
    }

    [Theory]
    [InlineData("null")] [InlineData("writer")] [InlineData("tenant")] [InlineData("feed")]
    [InlineData("procedure")] [InlineData("outage")] [InlineData("control")]
    public void Web_policy_rejects_missing_or_inconsistent_writer_facts(string kind)
    {
        var input = kind switch
        {
            "null" => null,
            "writer" => Input() with { Writer = StockMovementWriter.ExternalSystem },
            "tenant" => Input() with { ExternalTenant = "Tenant-A" },
            "feed" => Input() with { FeedIntervalMilliseconds = 1 },
            "procedure" => Input() with { ReconciliationProcedureReference = "" },
            "outage" => Input() with { OutageProcedureReference = " space " },
            _ => Input() with { OutageProcedureReference = "control\ncharacter" }
        };
        Assert.Throws<DomainValidationException>(() => StockSourceOperatingPolicyRules.Validate(input!, Source()));
    }

    [Theory]
    [InlineData("writer")] [InlineData("enum")] [InlineData("tenant")] [InlineData("blank")]
    [InlineData("feed")] [InlineData("negative")] [InlineData("overflow")]
    public void External_policy_requires_exact_tenant_and_bounded_positive_feed_interval(string kind)
    {
        var input = kind switch
        {
            "writer" => Input(true) with { Writer = StockMovementWriter.UnitToko },
            "enum" => Input(true) with { Writer = (StockMovementWriter)42 },
            "tenant" => Input(true) with { ExternalTenant = null },
            "blank" => Input(true) with { ExternalTenant = " " },
            "feed" => Input(true) with { FeedIntervalMilliseconds = 0 },
            "negative" => Input(true) with { FeedIntervalMilliseconds = -1 },
            _ => Input(true) with { FeedIntervalMilliseconds = long.MaxValue }
        };
        Assert.Throws<DomainValidationException>(() => StockSourceOperatingPolicyRules.Validate(input, Source(true)));
    }

    [Theory]
    [InlineData(0, 0, 0)] [InlineData(1, -1, 0)] [InlineData(1, 0, -1)] [InlineData(1, 0, long.MaxValue)]
    public void Policy_expectation_rejects_invalid_versions(long pool, long suspension, long policy) =>
        Assert.Throws<DomainValidationException>(() => new StockSourcePolicyExpectation(pool, suspension, policy, new('a', 64)).Validate());

    [Fact]
    public void Policy_expectation_preserves_exact_large_versions_and_rejects_noncanonical_hash()
    {
        new StockSourcePolicyExpectation(9007199254740993, 9007199254740993, 9007199254740993, new('a', 64)).Validate();
        Assert.Throws<DomainValidationException>(() => new StockSourcePolicyExpectation(1, 0, 0, new('A', 64)).Validate());
        Assert.Throws<DomainValidationException>(() => new StockSourcePolicyExpectation(1, 0, 0, "short").Validate());
    }
}
