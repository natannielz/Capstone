using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Modules.Billing;

// Facts independently read from the approved bank channel. Customer screenshots and OCR are not this source.
public sealed record BankReceiptObservation(string AccountNumber, string TransactionId, DateOnly BookedDate, Money Amount);
public static class BankReceiptPolicy
{
    public static BankReceiptObservation Copy(BankReceiptObservation value) {
        if (value is null || value.AccountNumber is not { Length: >= 4 and <= 34 } || value.AccountNumber.Any(c => c is < '0' or > '9') ||
            !BankStatementParser.Text(value.TransactionId, 80) || value.Amount.Value <= 0 || value.BookedDate.Year < 2000) throw Invalid();
        return value with { };
    }
    public static void RequireMatch(BankReceiptObservation observation, string accountNumber, BankStatementRow row) {
        observation = Copy(observation);
        if (row.Direction != "credit" || row.IdentityCandidate is null || row.TransactionId is null) throw Invalid("bank_receipt_identity_unresolved");
        if (observation.AccountNumber != accountNumber || observation.TransactionId != row.TransactionId || observation.BookedDate != row.BookedDate || observation.Amount != row.Amount)
            throw Invalid("bank_receipt_observation_mismatch");
    }
    public static void Independent(PersonEvidence reviewer, PersonEvidence importer) {
        if (!PersonIndependencePolicy.AreIndependent(reviewer, importer)) throw Invalid("independent_person_required");
    }
    public static DomainValidationException Invalid(string code = "invalid_bank_receipt") => new(code, "Review an exact credit using an approved independent bank source");
}
