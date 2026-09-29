using System.Collections.Immutable;
using System.Text.Json;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Catalog;

public sealed class CatalogPricePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly Sku Sku = new(new(9007199254740993), new(100), new(200),
        Guid.Parse("eaa8d217-47d6-4c26-8f25-6116e7f81c86"), "PENCIL-12", "pencil-blue", true, new(300), 4);
    private static readonly VariantDefinitionVersion Definition = new(new(300), Sku.Id, 2,
        "Dus", "sealed", "piece", 12, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1, Now.AddDays(-1));
    private static CatalogPriceExpectation Expected(long price = 0) => new(4, new(300), 2, price);
    private static CatalogPriceInput Input(string value = "12500.25") => new("IDR", Money.Parse(value));
    private static PriceVersion Current(long revision = 1) => new(new(500), Sku.Id, revision, "IDR", Money.Parse("12000.00"), Now.AddHours(-1));

    [Fact]
    public void First_price_is_explicit_per_package_and_does_not_change_the_package()
    {
        var price = CatalogPricePolicy.Approve(Sku, Definition, null, Expected(), Input(), new(501), Now);
        Assert.Equal(new(501), price.Id); Assert.Equal(9007199254740993, price.SkuId.Value);
        Assert.Equal(1, price.Version); Assert.Equal(12500.25m, price.UnitPrice.Value);
        Assert.Equal("IDR", price.Currency); Assert.Equal(Now, price.EffectiveAt);
        Assert.Equal(12, Definition.BaseUnitsPerPackage); Assert.Equal(4, Sku.Revision);
        Assert.Equal("25000.50", Money.Multiply(price.UnitPrice, 2).ToString());
    }

    [Fact]
    public void Later_price_keeps_the_preceding_value_and_uses_a_new_immutable_identity()
    {
        var original = Current(); var before = original;
        var next = CatalogPricePolicy.Approve(Sku, Definition, original, Expected(1), Input(), new(501), Now);
        Assert.Equal(2, next.Version); Assert.NotEqual(original.Id, next.Id);
        Assert.Equal(12000m, original.UnitPrice.Value); Assert.Equal(before, original);
        Assert.Equal(original.SkuId, next.SkuId);
    }

    [Theory]
    [InlineData("0.00")] [InlineData("0.01")] [InlineData("999999999999999.99")]
    public void Explicit_amounts_preserve_the_existing_exact_money_contract(string value)
    {
        var price = CatalogPricePolicy.Approve(Sku, Definition, null, Expected(), Input(value), new(501), Now);
        Assert.Equal(value, price.UnitPrice.ToString());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(price));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("UnitPrice").ValueKind);
        Assert.Equal(value, json.RootElement.GetProperty("UnitPrice").GetString());
    }

    [Fact]
    public void Missing_price_input_is_not_an_approved_zero_price() =>
        Assert.Throws<DomainValidationException>(() => CatalogPricePolicy.Approve(Sku, Definition, null, Expected(), null!, new(501), Now));

    [Theory]
    [InlineData("")] [InlineData("idr")] [InlineData(" IDR")] [InlineData("IDR ")]
    [InlineData("IDR\n")] [InlineData("ID")] [InlineData("IDRR")] [InlineData("ＩＤＲ")] [InlineData(null)]
    public void Currency_must_be_explicit_canonical_data(string? currency) =>
        Assert.Throws<DomainValidationException>(() => CatalogPricePolicy.Validate(new CatalogPriceInput(currency!, Money.Parse("1.00"))));

    [Theory]
    [InlineData("sku")] [InlineData("definition")] [InlineData("definition-version")]
    [InlineData("price")] [InlineData("missing-price")] [InlineData("unexpected-price")]
    public void Stale_review_cannot_approve_a_price_for_changed_package_or_price_state(string fault)
    {
        var current = fault == "missing-price" ? null : Current();
        var expected = fault switch
        {
            "sku" => Expected(1) with { SkuRevision = 3 },
            "definition" => Expected(1) with { DefinitionId = new(301) },
            "definition-version" => Expected(1) with { DefinitionVersion = 1 },
            "price" => Expected(2),
            "unexpected-price" => Expected(),
            _ => Expected(1)
        };
        var error = Assert.Throws<DomainValidationException>(() =>
            CatalogPricePolicy.Approve(Sku, Definition, current, expected, Input(), new(501), Now));
        Assert.Equal("catalog_price_changed", error.Code);
    }

    [Theory]
    [InlineData("inactive")] [InlineData("foreign-current")] [InlineData("foreign-definition")]
    [InlineData("wrong-current-definition")] [InlineData("reused-id")] [InlineData("no-id")]
    [InlineData("exhausted")] [InlineData("invalid-history")]
    public void Invalid_or_unrelated_catalog_and_price_facts_cannot_approve(string fault)
    {
        var sku = fault == "inactive" ? Sku with { Active = false } : Sku;
        var definition = fault switch
        {
            "foreign-definition" => Definition with { SkuId = new(999) },
            "wrong-current-definition" => Definition with { Id = new(301) },
            _ => Definition
        };
        var current = fault switch
        {
            "foreign-current" => Current() with { SkuId = new(999) },
            "exhausted" => Current(long.MaxValue),
            "invalid-history" => Current() with { Version = 0 },
            _ => Current()
        };
        var id = fault == "reused-id" ? new LongId(500) : fault == "no-id" ? default : new LongId(501);
        Assert.Throws<DomainValidationException>(() => CatalogPricePolicy.Approve(sku, definition, current,
            Expected(current.Version), Input(), id, Now));
    }

    [Theory]
    [InlineData("local-offset")] [InlineData("default")] [InlineData("sub-millisecond")]
    [InlineData("before-history")] [InlineData("before-definition")]
    public void Approval_time_is_server_utc_at_millisecond_precision_and_never_backdated(string fault)
    {
        var now = fault switch
        {
            "local-offset" => Now.ToOffset(TimeSpan.FromHours(7)),
            "default" => default,
            "sub-millisecond" => Now.AddTicks(1),
            "before-history" => Now.AddHours(-2),
            "before-definition" => Now.AddDays(-2),
            _ => Now
        };
        Assert.Throws<DomainValidationException>(() => CatalogPricePolicy.Approve(Sku, Definition, Current(), Expected(1), Input(), new(501), now));
    }

    [Fact]
    public void Equal_database_timestamps_do_not_erase_revision_order()
    {
        var current = Current() with { EffectiveAt = Now };
        var next = CatalogPricePolicy.Approve(Sku, Definition, current, Expected(1), Input(), new(501), Now);
        Assert.Equal(2, next.Version); Assert.Equal(current.EffectiveAt, next.EffectiveAt);
    }

    [Fact]
    public void Unchanged_price_cannot_create_another_logical_price_version()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            CatalogPricePolicy.Approve(Sku, Definition, Current(), Expected(1), Input("12000.00"), new(501), Now));
        Assert.Equal("catalog_price_unchanged", error.Code);
    }

    [Fact]
    public void Currency_change_is_explicit_and_does_not_convert_the_original_price()
    {
        var current = Current();
        var next = CatalogPricePolicy.Approve(Sku, Definition, current, Expected(1), new("USD", Money.Parse("12000.00")), new(501), Now);
        Assert.Equal("USD", next.Currency); Assert.Equal(12000m, next.UnitPrice.Value);
        Assert.Equal("IDR", current.Currency); Assert.Equal(12000m, current.UnitPrice.Value);
    }

    [Fact]
    public void Large_current_revision_remains_exact()
    {
        var next = CatalogPricePolicy.Approve(Sku, Definition, Current(9007199254740993),
            Expected(9007199254740993), Input(), new(501), Now);
        Assert.Equal(9007199254740994, next.Version);
    }

    [Theory]
    [InlineData("sku-zero")] [InlineData("definition-zero")] [InlineData("version-zero")] [InlineData("price-negative")] [InlineData("null")]
    public void Invalid_review_expectations_are_rejected(string fault)
    {
        var expected = fault switch
        {
            "sku-zero" => Expected() with { SkuRevision = 0 },
            "definition-zero" => Expected() with { DefinitionId = default },
            "version-zero" => Expected() with { DefinitionVersion = 0 },
            "price-negative" => Expected() with { PriceVersion = -1 },
            _ => null
        };
        Assert.Throws<DomainValidationException>(() => CatalogPricePolicy.Approve(Sku, Definition, null, expected!, Input(), new(501), Now));
    }

    [Fact]
    public void Price_approval_is_separate_scoped_fresh_and_independent_at_account_and_person_levels()
    {
        const string approve = "pricing.approve";
        var permission = Permissions.Find(approve); Assert.NotNull(permission);
        Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, permission.AllowedScopes.ToArray());
        Assert.Equal(TimeSpan.FromMinutes(5), permission.Assurance.MaximumAge);
        foreach (var role in RoleTemplates.Templates.Values) Assert.DoesNotContain(approve, role.Permissions);
        Assert.True(ApprovalPolicy.RequiresEvidence(approve)); Assert.True(PersonApprovalPolicy.RequiresEvidence(approve));
        Assert.False(ApprovalPolicy.Evaluate(41, approve, new(1, 1, ProposedBy: 41)).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(42, approve, new(1, 1, ProposedBy: 41)).Allowed);
        Assert.True(PersonApprovalPolicy.RequiredAccounts(42, approve, null).IsDefault);
        Assert.Equal(new long[] { 41, 42 }, PersonApprovalPolicy.RequiredAccounts(42, approve, new(1, 1, ProposedBy: 41)).ToArray());
        Assert.False(ApprovalPolicy.RequiresEvidence(Permissions.PricingManage));
    }

    [Theory]
    [InlineData("null")] [InlineData("id-zero")] [InlineData("sku-zero")] [InlineData("version-zero")]
    [InlineData("version-negative")] [InlineData("currency")] [InlineData("time-default")]
    [InlineData("time-offset")] [InlineData("time-precision")]
    public void Malformed_history_is_not_silently_repaired(string fault)
    {
        var price = fault switch
        {
            "null" => null,
            "id-zero" => Current() with { Id = default },
            "sku-zero" => Current() with { SkuId = default },
            "version-zero" => Current() with { Version = 0 },
            "version-negative" => Current() with { Version = -1 },
            "currency" => Current() with { Currency = "idr" },
            "time-default" => Current() with { EffectiveAt = default },
            "time-offset" => Current() with { EffectiveAt = Now.ToOffset(TimeSpan.FromHours(7)) },
            _ => Current() with { EffectiveAt = Now.AddTicks(1) }
        };
        Assert.Throws<DomainValidationException>(() => CatalogPricePolicy.Validate(price!));
    }

    [Theory]
    [InlineData("same-person")] [InlineData("missing-person")] [InlineData("unverified")]
    [InlineData("wrong-account")] [InlineData("duplicate-account")] [InlineData("stale-review")]
    public void Separate_accounts_alone_are_not_price_approval_authority(string fault)
    {
        var maker = new PersonEvidence(41, 141, PersonVerification.Verified, 1);
        var checker = new PersonEvidence(42, 142, PersonVerification.Verified, 1);
        var evidence = fault == "stale-review" ? new ApprovalRequest(1, 2, ProposedBy: 41) : new(1, 1, ProposedBy: 41);
        ImmutableArray<PersonEvidence> people = fault switch
        {
            "same-person" => [maker, checker with { PersonId = maker.PersonId }],
            "missing-person" => [checker],
            "unverified" => [maker with { Verification = PersonVerification.Unknown }, checker],
            "wrong-account" => [maker with { UserId = 99 }, checker],
            "duplicate-account" => [maker, maker],
            _ => [maker, checker]
        };
        Assert.False(PersonApprovalPolicy.Evaluate(42, Permissions.PricingApprove, evidence, people).Allowed);
        Assert.True(PersonApprovalPolicy.Evaluate(42, Permissions.PricingApprove, new(1, 1, ProposedBy: 41), [maker, checker]).Allowed);
    }

}
