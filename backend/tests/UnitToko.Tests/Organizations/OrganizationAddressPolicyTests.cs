using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Organizations;

public sealed class OrganizationAddressPolicyTests
{
    private static PostalAddress Address => new("Jalan Tebet Raya 12", null, "Jakarta Selatan", "DKI Jakarta", "12810", "ID");
    private static OrganizationAddressInput Input => new(new(ScopeNodeKind.Branch, 11), OrganizationAddressPurpose.Delivery, 2, 1, 0, Address);

    [Fact]
    public void Postal_facts_are_canonical_bounded_and_explicit_without_hidden_controls() {
        var normalized = OrganizationAddressPolicy.Copy(Input with { Address = Address with {
            Line1 = "  Jalan Tebet Ｒａｙａ 12  ", Line2 = "  Lantai 2  ", CountryCode = " id " } });
        Assert.Equal("Jalan Tebet Raya 12", normalized.Address.Line1); Assert.Equal("Lantai 2", normalized.Address.Line2);
        Assert.Equal("ID", normalized.Address.CountryCode); Assert.Null(OrganizationAddressPolicy.Copy(Input).Address.Line2);
        foreach (var value in new[] { "", "  ", "line\nbreak", "\u202eHidden", "\ue000Private", new string('x', 201), "\ud800" })
            Assert.Equal("organization_address_invalid", Assert.Throws<DomainValidationException>(() =>
                OrganizationAddressPolicy.Copy(Input with { Address = Address with { Line1 = value } })).Code);
        foreach (var value in new[] { "", "IDN", "I1", "Indonesia" })
            Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { Address = Address with { CountryCode = value } }));
        Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { Address = Address with { Line2 = " " } }));
        Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { Address = Address with { City = new string('x', 81) } }));
        Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { Address = Address with { PostalCode = new string('x', 21) } }));
    }

    [Fact]
    public void A_postal_purpose_selects_only_its_exact_organization_kind() {
        foreach (var purpose in Enum.GetValues<OrganizationAddressPurpose>()) foreach (var kind in Enum.GetValues<ScopeNodeKind>()) {
            var compatible = purpose switch {
                OrganizationAddressPurpose.LegalBilling => kind is ScopeNodeKind.BuyingOrganization or ScopeNodeKind.FulfillmentOrganization,
                OrganizationAddressPurpose.Delivery => kind is ScopeNodeKind.Branch or ScopeNodeKind.Division,
                OrganizationAddressPurpose.Dispatch => kind == ScopeNodeKind.Warehouse,
                _ => false };
            var target = new OrganizationAddressTarget(kind, 11);
            if (compatible) OrganizationAddressPolicy.RequireTarget(target, purpose);
            else Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.RequireTarget(target, purpose));
        }
        Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { Target = Input.Target with { Id = 0 } }));
        Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { Purpose = (OrganizationAddressPurpose)99 }));
        Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { Target = new((ScopeNodeKind)99, 11) }));
    }

    [Fact]
    public void Missing_source_versions_and_exhausted_successors_cannot_be_proposals() {
        foreach (var version in new[] { 0L, -1L, long.MaxValue }) {
            Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { ExpectedScopeVersion = version }));
            Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { ExpectedProfileVersion = version }));
        }
        foreach (var version in new[] { -1L, long.MaxValue - 1, long.MaxValue })
            Assert.Throws<DomainValidationException>(() => OrganizationAddressPolicy.Copy(Input with { ExpectedAddressVersion = version }));
        Assert.Equal(long.MaxValue - 2, OrganizationAddressPolicy.Copy(Input with { ExpectedAddressVersion = long.MaxValue - 2 }).ExpectedAddressVersion);
    }

    [Fact]
    public void Review_requires_distinct_verified_humans_including_equivalent_accounts() {
        var maker = new PersonEvidence(301, 1301, PersonVerification.Verified, 1);
        var checker = new PersonEvidence(302, 1302, PersonVerification.Verified, 1);
        OrganizationAddressPolicy.RequireIndependent(checker, maker);
        foreach (var invalid in new[] { maker, checker with { PersonId = maker.PersonId },
            checker with { Verification = PersonVerification.Unknown, PersonId = null, Version = 0 },
            checker with { Verification = PersonVerification.Disputed } })
            Assert.Equal("independent_person_required", Assert.Throws<DomainValidationException>(() =>
                OrganizationAddressPolicy.RequireIndependent(invalid, maker)).Code);
    }
}
