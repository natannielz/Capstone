using System.Collections.Immutable;
using System.Text;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Ordering;

public sealed class BankStatementParserTests
{
    private static readonly ReceivingAccountDetails Account = new("FIXTUREBANK", "0012345678", "Synthetic shop", "IDR");
    internal static readonly BankStatementFormat Format = new(",", "yyyy-MM-dd", ".", "C", "D", BankStatementParser.Fields, "fixture-stable-id-v1");
    private static BankStatementPreview Parse(string rows, BankStatementFormat? format = null) => BankStatementParser.Parse(
        Encoding.UTF8.GetBytes(string.Join((format ?? Format).Delimiter, BankStatementParser.Fields) + "\n" + rows), format ?? Format, Account, new string('a', 64), new(2026, 9, 30));
    [Fact] public void Exact_rows_and_totals_preserve_identifiers_and_distinct_transfers_with_shared_references() {
        var result = Parse("0001,2026-09-29,C,1000.25,Buyer,REF,0012345678,IDR\r\n0002,2026-09-30,D,1.25,Buyer,REF,0012345678,IDR\r\n");
        Assert.True(result.CanStage); Assert.Equal("1000.25", result.Credits.ToString()); Assert.Equal("1.25", result.Debits.ToString());
        Assert.Equal("0001", result.Rows[0].TransactionId); Assert.NotEqual(result.Rows[0].IdentityCandidate, result.Rows[1].IdentityCandidate);
        Assert.Contains(result.Issues, issue => issue.Code == "reference_repeated");
    }
    [Fact] public void Repeated_and_unknown_identities_remain_explicit_candidates_not_cash() {
        var result = Parse("T,2026-09-30,C,1000.00,Buyer,R,0012345678,IDR\nT,2026-09-30,C,1000.00,Buyer,R,0012345678,IDR\n,2026-09-30,C,1.00,,,0012345678,IDR");
        Assert.True(result.CanStage); Assert.Equal(result.Rows[0].IdentityCandidate, result.Rows[1].IdentityCandidate); Assert.Null(result.Rows[2].IdentityCandidate);
        Assert.Contains(result.Issues, issue => issue.Code == "identity_repeated"); Assert.Contains(result.Issues, issue => issue.Code == "payer_unknown");
        Assert.Null(Parse("T,2026-09-30,C,1.00,Buyer,R,0012345678,IDR", Format with { IdentityNamespace = null }).Rows[0].IdentityCandidate);
    }
    [Theory]
    [InlineData("T,2026-10-01,C,1.00,Buyer,R,0012345678,IDR", "invalid_date")]
    [InlineData("T,2026-09-30,C,1.001,Buyer,R,0012345678,IDR", "invalid_amount")]
    [InlineData("T,2026-09-30,C,0.00,Buyer,R,0012345678,IDR", "invalid_amount")]
    [InlineData("T,2026-09-30,C,1.00,Buyer,R,12345678,IDR", "account_mismatch")]
    [InlineData("T,2026-09-30,C,1.00,Buyer,R,0012345678,USD", "currency_mismatch")]
    [InlineData("T,2026-09-30,X,1.00,Buyer,R,0012345678,IDR", "invalid_direction")]
    [InlineData("T,2026-09-30,C,1.00,Buyer,R,0012345678", "column_count")]
    [InlineData("T,2026-09-30,C,1.00,Buyer\u200b,R,0012345678,IDR", "invalid_text")]
    public void Invalid_rows_block_whole_import(string row, string code) { var result = Parse(row); Assert.False(result.CanStage); Assert.Contains(result.Issues, issue => issue.Code == code); }
    [Fact] public void Explicit_locale_does_not_guess_grouping_or_round_totals() {
        var format = Format with { Delimiter = ";", DecimalSeparator = ",", DateFormat = "dd/MM/yyyy" };
        Assert.Equal("1000.25", Parse("T;30/09/2026;C;1000,25;Buyer;R;0012345678;IDR", format).Credits.ToString());
        Assert.False(Parse("T;30/09/2026;C;1.000,25;Buyer;R;0012345678;IDR", format).CanStage);
        Assert.False(Parse("T,2026-09-30,C,999999999999999.99,Buyer,R,0012345678,IDR\nU,2026-09-30,C,0.01,Buyer,R,0012345678,IDR").CanStage);
    }
    [Fact] public void Csv_quotes_original_bytes_and_bounded_decoding_are_real() {
        var result = Parse("T,2026-09-30,C,1.00,\"Buyer, A\",\"REF\"\"01\",0012345678,IDR");
        Assert.Equal("Buyer, A", result.Rows.Single().PayerName); Assert.Equal("REF\"01", result.Rows.Single().Reference);
        Assert.Throws<DomainValidationException>(() => Parse("\"unfinished"));
        Assert.Throws<DomainValidationException>(() => Parse("\"T\"x,2026-09-30,C,1.00,Buyer,R,0012345678,IDR"));
        Assert.Throws<DomainValidationException>(() => Parse(new string('a', 513)));
        Assert.Throws<DomainValidationException>(() => Parse(string.Join('\n', Enumerable.Repeat("T,2026-09-30,C,1.00,Buyer,R,0012345678,IDR", 501))));
        Assert.Throws<DomainValidationException>(() => BankStatementParser.Parse([0xff], Format, Account, new string('a',64), new(2026,9,30)));
        Assert.Throws<DomainValidationException>(() => BankStatementParser.Parse(new byte[BankStatementParser.MaximumBytes+1], Format, Account, new string('a',64), new(2026,9,30)));
    }
    [Fact] public void Physical_account_and_bank_identity_namespace_bound_candidates_independent_of_filename_or_version() {
        Assert.Equal(BankStatementParser.Candidate("account", "bank-domain", "001"), BankStatementParser.Candidate("account", "bank-domain", "001"));
        Assert.NotEqual(BankStatementParser.Candidate("account", "bank-domain", "001"), BankStatementParser.Candidate("account", "other-domain", "001"));
        Assert.NotEqual(BankStatementParser.Candidate("account", "bank-domain", "001"), BankStatementParser.Candidate("other-account", "bank-domain", "001"));
        Assert.NotEqual(BankStatementParser.Candidate("account", "a", "bc"), BankStatementParser.Candidate("account", "ab", "c"));
    }
}
