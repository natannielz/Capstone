using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Billing;

public sealed class InvoiceReleaseRuleTests
{
    private static InvoiceReleaseInput Input => new([new(new(1), new(2))], 0, new(2026, 10, 9), new(2026, 10, 9),
        DateTimeOffset.Parse("2026-10-09T00:00:00Z"), DateTimeOffset.Parse("2026-10-10T00:00:00Z"), "Reviewed template", "Reviewed numbering", "Reviewed eligibility",
        new("invoice_ar", "invoice_revenue", "invoice_tax", "invoice_charges"), [new(new(2), new(3), 0, new(100m), new(0m))]);
    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(103)]
    public void Explicit_original_or_accepted_amendment_revision_retains_exact_minor_unit_values(long revision) {
        var input = Input with { Allocations = [Input.Allocations[0] with { LineRevision = revision, Tax = Money.Parse("0.01") }] };
        var copied = InvoiceReleaseRules.Copy(input); Assert.Equal(revision, Assert.Single(copied.Allocations).LineRevision);
        Assert.Equal("0.01", copied.Allocations[0].Tax.ToString()); Assert.Equal<InvoiceReceiptSelection>(input.Receipts, copied.Receipts);
    }
    [Theory]
    [InlineData(-1)] [InlineData(1)] [InlineData(104)]
    public void Missing_or_unaccepted_line_identity_cannot_become_an_allocation(long revision) =>
        Assert.Throws<DomainValidationException>(() => InvoiceReleaseRules.Copy(Input with { Allocations = [Input.Allocations[0] with { LineRevision = revision }] }));
    [Fact]
    public void Ambiguous_receipt_duplicate_line_missing_reference_and_unbounded_validity_are_refused() {
        Assert.Throws<DomainValidationException>(() => InvoiceReleaseRules.Copy(Input with { Allocations = [Input.Allocations[0], Input.Allocations[0]] }));
        Assert.Throws<DomainValidationException>(() => InvoiceReleaseRules.Copy(Input with { Allocations = [Input.Allocations[0] with { ReceiptId = new(99) }] }));
        Assert.Throws<DomainValidationException>(() => InvoiceReleaseRules.Copy(Input with { TemplateApprovalReference = " " }));
        Assert.Throws<DomainValidationException>(() => InvoiceReleaseRules.Copy(Input with { ValidUntil = Input.ValidFrom }));
        Assert.Throws<DomainValidationException>(() => InvoiceReleaseRules.Copy(Input with { Mappings = Input.Mappings with { Revenue = Input.Mappings.Receivable } }));
    }
}
