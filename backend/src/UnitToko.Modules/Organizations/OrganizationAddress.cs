using System.Globalization;
using System.Text;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Modules.Organizations;

public enum OrganizationAddressPurpose { LegalBilling, Delivery, Dispatch }
public enum OrganizationAddressDecision { Approve, Reject }
public sealed record OrganizationAddressTarget(ScopeNodeKind Kind, long Id);
/// <summary>Postal facts only. An approved address never verifies a recipient, contact or OTP channel.</summary>
public sealed record PostalAddress(string Line1, string? Line2, string City, string Region, string PostalCode, string CountryCode);
public sealed record OrganizationAddressInput(OrganizationAddressTarget Target, OrganizationAddressPurpose Purpose,
    long ExpectedScopeVersion, long ExpectedProfileVersion, long ExpectedAddressVersion, PostalAddress Address);

public static class OrganizationAddressPolicy
{
    public static OrganizationAddressInput Copy(OrganizationAddressInput input) {
        if (input is null || input.Target is null || input.Address is null || input.ExpectedScopeVersion is <= 0 or >= long.MaxValue ||
            input.ExpectedProfileVersion is <= 0 or >= long.MaxValue || input.ExpectedAddressVersion < 0 || input.ExpectedAddressVersion >= long.MaxValue - 1)
            throw Invalid();
        RequireTarget(input.Target, input.Purpose);
        return input with { Target = input.Target with { }, Address = Copy(input.Address) };
    }
    public static PostalAddress Copy(PostalAddress value) {
        if (value is null) throw Invalid();
        var country = Text(value.CountryCode, 2).ToUpperInvariant();
        if (country.Length != 2 || country.Any(c => c is < 'A' or > 'Z')) throw Invalid();
        return new(Text(value.Line1, 200), value.Line2 is null ? null : Text(value.Line2, 200),
            Text(value.City, 80), Text(value.Region, 80), Text(value.PostalCode, 20), country);
    }
    public static void RequireTarget(OrganizationAddressTarget target, OrganizationAddressPurpose purpose) {
        if (target is null || target.Id <= 0 || !Enum.IsDefined(target.Kind) || !Enum.IsDefined(purpose) || !(purpose switch {
            OrganizationAddressPurpose.LegalBilling => target.Kind is ScopeNodeKind.BuyingOrganization or ScopeNodeKind.FulfillmentOrganization,
            OrganizationAddressPurpose.Delivery => target.Kind is ScopeNodeKind.Branch or ScopeNodeKind.Division,
            OrganizationAddressPurpose.Dispatch => target.Kind == ScopeNodeKind.Warehouse,
            _ => false })) throw Invalid();
    }
    public static void RequireIndependent(PersonEvidence actor, PersonEvidence proposer) {
        if (!PersonIndependencePolicy.AreIndependent(actor, proposer))
            throw Invalid("independent_person_required");
    }
    private static string Text(string value, int maximum) {
        string normalized;
        try { normalized = (value ?? "").Normalize(NormalizationForm.FormKC).Trim(); }
        catch (ArgumentException) { throw Invalid(); }
        if (normalized.Length is < 1 || normalized.Length > maximum || normalized.Any(c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate)) throw Invalid();
        return normalized;
    }
    public static DomainValidationException Invalid(string code = "organization_address_invalid") =>
        new(code, "An explicit postal address, compatible organization target and supported source versions are required");
}
