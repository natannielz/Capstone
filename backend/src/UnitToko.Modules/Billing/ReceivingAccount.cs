using System.Globalization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Modules.Billing;

// These are observed routing details, not a bank connection or a transfer instruction.
public sealed record ReceivingAccountDetails(string BankCode, string AccountNumber, string HolderName, string Currency);
public enum ReceivingAccountDecision { Approve, Reject, Withdraw }
public static class ReceivingAccountPolicy
{
    public static ReceivingAccountDetails Copy(ReceivingAccountDetails details) {
        if (details is null || !Text(details.BankCode, 32) || details.BankCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            details.BankCode != details.BankCode.ToUpperInvariant() || details.AccountNumber is not { Length: >= 4 and <= 34 } ||
            details.AccountNumber.Any(c => c is < '0' or > '9') || !Text(details.HolderName, 160) || details.Currency != "IDR") throw Invalid();
        return details with { };
    }
    public static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim() &&
        !value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
    public static void RequireMatch(ReceivingAccountDetails proposed, ReceivingAccountDetails observed) {
        // No fuzzy names, digit repair or account-number normalization. Discrepancies need a new proposal.
        if (Copy(proposed) != Copy(observed)) throw Invalid("receiving_account_observation_mismatch");
    }
    public static void RequireIndependent(PersonEvidence reviewer, PersonEvidence observer, PersonEvidence maker) {
        if (!PersonIndependencePolicy.AreIndependent(reviewer, maker) || !PersonIndependencePolicy.AreIndependent(observer, maker))
            throw new DomainValidationException("independent_person_required", "A currently verified person independent of the proposer is required");
    }
    public static string Mask(ReceivingAccountDetails details) { Copy(details); return details.AccountNumber.Length <= 4 ? new string('*', details.AccountNumber.Length) : new string('*', details.AccountNumber.Length - 4) + details.AccountNumber[^4..]; }
    public static DomainValidationException Invalid(string code = "invalid_receiving_account") => new(code, "Use exact bounded bank account details and an approved independent verification channel");
}
