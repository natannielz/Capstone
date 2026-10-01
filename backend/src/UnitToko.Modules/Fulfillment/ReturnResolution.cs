using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Modules.Fulfillment;
public sealed record ReturnResolutionInput(string Action, string PublicNote);
public static class ReturnResolutionPolicy
{
    public static ReturnResolutionInput Copy(ReturnResolutionInput input) {
        if (input is null || input.Action is not ("replacement" or "credit" or "refund") || !ReceivingAccountPolicy.Text(input.PublicNote, 1000)) throw Invalid();
        return input with { };
    }
    public static string Permission(string action) => action switch { "replacement" => Permissions.CaseManage, "credit" => Permissions.CreditRequest, "refund" => Permissions.RefundRequest, _ => throw Invalid() };
    public static DomainValidationException Invalid() => new("invalid_return_resolution", "Request a resolution for the exact inspected receipt; stock and financial outcomes require separate approval");
}
