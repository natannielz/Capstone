using System.Text;
using System.Buffers;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Identity;

/// <summary>Self-declared contact details. These fields never replace provider-verified identity facts.</summary>
public sealed record AccountContactInput(string Name, string? Phone, string? Position, string? Address)
{
    public AccountContactInput Validate() => new(
        Required(Name, 160), Optional(Phone, 40), Optional(Position, 160), Optional(Address, 1000));

    private static string Required(string? value, int maximum) => Optional(value, maximum) ??
        throw Invalid();
    private static string? Optional(string? value, int maximum)
    {
        if (value is null) return null;
        if (value.Length is < 1 or > 2_000 || !ValidUtf16(value) || value != value.Trim() ||
            !value.IsNormalized(NormalizationForm.FormC) || value.EnumerateRunes().Any(rune =>
                Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is
                    System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate))
            throw Invalid();
        var count = value.EnumerateRunes().Count();
        if (count is < 1 || count > maximum) throw Invalid();
        return value;
    }
    private static bool ValidUtf16(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
    private static DomainValidationException Invalid() => new("invalid_account_contact",
        "Enter bounded contact details without control characters or surrounding whitespace");
}

public sealed record AccountContactCurrent(long UserId, long Revision, string? Name, string? Phone,
    string? Position, string? Address, DateTimeOffset? UpdatedAt);
public sealed record AccountContactResult(long UserId, long Revision, long HistoryId, DateTimeOffset RecordedAt);
public sealed record AccountContactHistoryItem(long HistoryId, long Revision, AccountContactInput Contact,
    DateTimeOffset RecordedAt, string Reason);
public sealed record AccountContactHistoryPage(IReadOnlyList<AccountContactHistoryItem> Items, long? NextBeforeHistoryId);
