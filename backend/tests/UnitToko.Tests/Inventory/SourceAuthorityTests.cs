using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class SourceAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly StockLocation Location = InventoryQuantityPolicyTests.Location;
    private static readonly SourceAuthorityExpectation Expected = new(3, 2, 7);
    private static SourceStockPolicy Policy() => new(new(400), Location, new(401), 2, 3,
        StockAuthorityMode.WebLedger, TimeSpan.FromMinutes(5), 10, null);
    private static StockAuthorityReview Review() => new(new(402), new(400), 3, new(401), 2,
        StockPoolControl.ExclusiveWebAllocation, null);
    private static SourceStockObservation Observation() => new(new(400), 3, new(401), 2, 7,
        Now.AddMinutes(-1), Now.AddSeconds(-30), true, "synthetic-source-batch");
    private static SourceAuthorityDecision Evaluate(SourceStockPolicy? policy = null, StockAuthorityReview? review = null,
        SourceStockObservation? observation = null) =>
        StockAuthorityPolicy.Evaluate(policy ?? Policy(), review ?? Review(), observation ?? Observation(), Location, Expected, Now);

    [Fact]
    public void An_accepted_exclusive_web_pool_can_use_its_ledger_but_a_decision_is_not_a_quantity_or_reservation()
    {
        var result = Evaluate();
        Assert.Equal(SourceAuthorityDisposition.WebLedger, result.Disposition);
        Assert.Equal("web_ledger_authority", result.ReasonCode);
        Assert.Equal(3, result.PolicyRevision);
        Assert.Equal(2, result.PoolRevision);
        Assert.Equal(7, result.ObservationRevision);
        Assert.Equal(Observation().ObservedAt, result.ObservedAt);
        Assert.Equal(Observation().ReconciledAt, result.ReconciledAt);
    }

    [Theory]
    [InlineData("policy", "stock_authority_missing")]
    [InlineData("review", "stock_authority_unreviewed")]
    [InlineData("observation", "stock_observation_missing")]
    public void Missing_authority_evidence_never_implies_zero_or_live_stock(string missing, string code)
    {
        var result = StockAuthorityPolicy.Evaluate(missing == "policy" ? null : Policy(), missing == "review" ? null : Review(),
            missing == "observation" ? null : Observation(), Location, Expected, Now);
        Denied(result, code);
        Assert.Null(result.ObservedAt);
    }

    [Theory]
    [InlineData("unconfirmed", "stock_authority_unconfirmed")]
    [InlineData("policyRevision", "stock_policy_changed")]
    [InlineData("poolRevision", "stock_pool_changed")]
    [InlineData("reviewPolicy", "stock_authority_review_stale")]
    [InlineData("reviewPolicyRevision", "stock_authority_review_stale")]
    [InlineData("reviewPool", "stock_authority_review_stale")]
    [InlineData("reviewPoolRevision", "stock_authority_review_stale")]
    [InlineData("poolUnconfirmed", "stock_pool_control_unconfirmed")]
    [InlineData("observationPolicy", "stock_observation_mismatch")]
    [InlineData("observationPolicyRevision", "stock_observation_mismatch")]
    [InlineData("observationPool", "stock_observation_mismatch")]
    [InlineData("observationPoolRevision", "stock_observation_mismatch")]
    [InlineData("observationRevision", "stock_observation_changed")]
    [InlineData("unreconciled", "stock_unreconciled")]
    public void Stale_or_unconfirmed_authority_cannot_be_used(string change, string code)
    {
        var policy = Policy(); var review = Review(); var observation = Observation();
        switch (change)
        {
            case "unconfirmed": policy = policy with { Mode = StockAuthorityMode.Unconfirmed }; break;
            case "policyRevision": policy = policy with { Revision = 4 }; break;
            case "poolRevision": policy = policy with { PoolRevision = 3 }; break;
            case "reviewPolicy": review = review with { PolicyId = new(999) }; break;
            case "reviewPolicyRevision": review = review with { PolicyRevision = 2 }; break;
            case "reviewPool": review = review with { PhysicalPoolId = new(999) }; break;
            case "reviewPoolRevision": review = review with { PoolRevision = 1 }; break;
            case "poolUnconfirmed": review = review with { Control = StockPoolControl.Unconfirmed }; break;
            case "observationPolicy": observation = observation with { PolicyId = new(999) }; break;
            case "observationPolicyRevision": observation = observation with { PolicyRevision = 2 }; break;
            case "observationPool": observation = observation with { PhysicalPoolId = new(999) }; break;
            case "observationPoolRevision": observation = observation with { PoolRevision = 1 }; break;
            case "observationRevision": observation = observation with { Revision = 8 }; break;
            case "unreconciled": observation = observation with { Reconciled = false, ReconciledAt = null }; break;
            default: throw new InvalidOperationException();
        }
        Denied(Evaluate(policy, review, observation), code);
    }

    [Theory]
    [InlineData("organization")] [InlineData("shop")] [InlineData("warehouse")] [InlineData("channel")]
    public void The_entire_requested_location_must_match_before_exposing_observation_metadata(string changed)
    {
        var other = changed switch
        {
            "organization" => Location with { OrganizationId = new(999) },
            "shop" => Location with { ShopId = new(999) },
            "warehouse" => Location with { WarehouseId = new(999) },
            _ => Location with { ChannelId = new(999) }
        };
        var result = StockAuthorityPolicy.Evaluate(Policy(), Review(), Observation(), other, Expected, Now);
        Denied(result, "stock_authority_scope_mismatch");
        Assert.Null(result.ObservedAt); Assert.Null(result.ObservationRevision);
    }

    [Fact]
    public void The_exact_freshness_deadline_blocks_and_reconciliation_does_not_refresh_an_old_observation()
    {
        var old = Observation() with { ObservedAt = Now.AddMinutes(-5).AddMilliseconds(1), ReconciledAt = Now };
        Assert.Equal(SourceAuthorityDisposition.WebLedger, Evaluate(observation: old).Disposition);
        old = old with { ObservedAt = Now.AddMinutes(-5) };
        var denied = Evaluate(observation: old);
        Denied(denied, "stock_observation_stale");
        Assert.Equal(old.ObservedAt, denied.ObservedAt);
        Assert.Equal(Now, denied.ReconciledAt);
        Denied(Evaluate(observation: old with { ObservedAt = Now.AddDays(-1) }), "stock_observation_stale");
    }

    [Fact]
    public void Future_or_inconsistent_observation_times_do_not_authorize_stock()
    {
        foreach (var observation in new[] { Observation() with { ObservedAt = Now.AddSeconds(1) },
            Observation() with { ReconciledAt = Now.AddSeconds(1) },
            Observation() with { ReconciledAt = Now.AddMinutes(-2) } })
            Denied(Evaluate(observation: observation), "stock_source_time_invalid");
        Denied(Evaluate(observation: Observation() with { ReconciledAt = null }), "stock_unreconciled");
        Denied(Evaluate(observation: Observation() with { Reconciled = false }), "stock_unreconciled");
    }

    [Fact]
    public void Shared_channels_require_the_same_reviewed_pool_control_not_merely_a_fresh_feed()
    {
        Assert.Equal(SourceAuthorityDisposition.WebLedger,
            Evaluate(review: Review() with { Control = StockPoolControl.SharedWebLedger }).Disposition);
        Denied(Evaluate(review: Review() with { Control = StockPoolControl.ExternalHold, ReservationProtocolReference = "hold-v1" }),
            "stock_authority_mode_mismatch");
    }

    [Fact]
    public void An_external_feed_never_grants_web_ledger_authority_even_with_a_reviewed_hold_protocol()
    {
        var external = Policy() with { Mode = StockAuthorityMode.ExternalLedger, ExternalNamespace = "omi:synthetic-tenant" };
        Denied(Evaluate(policy: external), "stock_authority_mode_mismatch");
        var holdReview = Review() with { Control = StockPoolControl.ExternalHold, ReservationProtocolReference = "synthetic-hold-v1" };
        var result = Evaluate(external, holdReview);
        Assert.Equal(SourceAuthorityDisposition.ExternalHoldRequired, result.Disposition);
        Assert.Equal("external_reservation_required", result.ReasonCode);
        Assert.NotEqual(SourceAuthorityDisposition.WebLedger, result.Disposition);
        Denied(Evaluate(external, holdReview with { ReservationProtocolReference = null }), "external_hold_protocol_missing");
    }

    [Fact]
    public void Malformed_policies_review_references_and_expectations_are_rejected()
    {
        foreach (var policy in new[] { Policy() with { Id = default }, Policy() with { PhysicalPoolId = default },
            Policy() with { PoolRevision = 0 }, Policy() with { Revision = 0 }, Policy() with { Location = null! },
            Policy() with { Mode = (StockAuthorityMode)99 }, Policy() with { MaximumAge = TimeSpan.Zero },
            Policy() with { MaximumAge = TimeSpan.FromTicks(1) }, Policy() with { SafetyBufferBaseUnits = -1 },
            Policy() with { ExternalNamespace = "unexpected-web-source" },
            Policy() with { Mode = StockAuthorityMode.ExternalLedger, ExternalNamespace = " bad" } })
            Invalid(() => Evaluate(policy: policy));
        foreach (var review in new[] { Review() with { ReviewId = default }, Review() with { PolicyId = default },
            Review() with { PolicyRevision = 0 }, Review() with { PhysicalPoolId = default },
            Review() with { PoolRevision = 0 }, Review() with { Control = (StockPoolControl)99 },
            Review() with { ReservationProtocolReference = "bad\nreference" } })
            Invalid(() => Evaluate(review: review));
        foreach (var expectation in new[] { Expected with { PolicyRevision = 0 }, Expected with { PoolRevision = 0 }, Expected with { ObservationRevision = 0 } })
            Invalid(() => StockAuthorityPolicy.Evaluate(Policy(), Review(), Observation(), Location, expectation, Now));
        Invalid(() => StockAuthorityPolicy.Evaluate(Policy(), Review(), Observation(), Location, Expected, default));
    }

    [Fact]
    public void Malformed_observation_identity_and_noncanonical_times_are_rejected()
    {
        foreach (var observation in new[] { Observation() with { PolicyId = default }, Observation() with { PhysicalPoolId = default },
            Observation() with { PolicyRevision = 0 }, Observation() with { PoolRevision = 0 }, Observation() with { Revision = 0 },
            Observation() with { ObservedAt = default }, Observation() with { ObservedAt = Now.ToOffset(TimeSpan.FromHours(7)) },
            Observation() with { ReconciledAt = default(DateTimeOffset) }, Observation() with { SourceBatchReference = "" } })
            Invalid(() => Evaluate(observation: observation));
    }

    private static void Denied(SourceAuthorityDecision result, string code)
    {
        Assert.Equal(SourceAuthorityDisposition.Unavailable, result.Disposition);
        Assert.Equal(code, result.ReasonCode);
    }
    private static void Invalid(Action action) => Assert.Equal("invalid_stock_authority", Assert.Throws<DomainValidationException>(action).Code);
}
