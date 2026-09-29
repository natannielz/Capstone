using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Tests.Ordering;
public sealed class InvoiceTermsTests
{
    private static InvoiceTerms Terms(int days, string origin = "issue_date") => new(new(1, 1), new(2, 1), new(3, 1), new(4, 1), new(5, 1), new(6, 1),
        "buying_org_period_channel", new(2026, 1, 1), new(2026, 12, 31), origin, days, "calendar_days",
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], [], "half_even", "ar", "sales", "tax", "charges", "Synthetic terms", DateTimeOffset.MaxValue, []);
    private static ImmutableArray<InvoiceReceiptSource> Sources(params DateTimeOffset[] dates) => [.. dates.Select(date => new InvoiceReceiptSource(new(),
        new BsonDocument("receipt", new BsonDocument("recordedAt", date.UtcDateTime))))];
    [Theory]
    [InlineData(2026, 1, 31, 2026, 3, 2)]
    [InlineData(2028, 1, 31, 2028, 3, 1)]
    [InlineData(2026, 12, 15, 2027, 1, 14)]
    public void Thirty_calendar_days_cross_month_leap_and_year_boundaries(int year, int month, int day, int dueYear, int dueMonth, int dueDay) =>
        Assert.Equal(new DateOnly(dueYear, dueMonth, dueDay), InvoiceAdmissionPolicy.DueDate([], Terms(30), new(year, month, day)));
    [Fact]
    public void Approved_working_calendar_excludes_weekends_and_listed_holidays() {
        var terms = Terms(1) with { DayRule = "working_days", Holidays = [new(2026, 11, 9)] };
        Assert.Equal(new DateOnly(2026, 11, 10), InvoiceAdmissionPolicy.DueDate([], terms, new(2026, 11, 6)));
        Assert.Equal(new DateOnly(2026, 11, 6), InvoiceAdmissionPolicy.DueDate([], terms with { TermDays = 0 }, new(2026, 11, 6)));
    }
    [Fact]
    public void Receipt_term_origin_uses_Jakarta_business_dates_and_explicit_first_or_last_rule() {
        var sources = Sources(new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 9, 29), InvoiceAdmissionPolicy.DueDate(sources, Terms(0, "earliest_receipt"), new(2026, 10, 2)));
        Assert.Equal(new DateOnly(2026, 10, 1), InvoiceAdmissionPolicy.DueDate(sources, Terms(0, "latest_receipt"), new(2026, 10, 2)));
    }
    [Fact]
    public void Unrepresentable_due_date_is_rejected() => Assert.Throws<DomainValidationException>(() => InvoiceAdmissionPolicy.DueDate([], Terms(1), DateOnly.MaxValue));
}
