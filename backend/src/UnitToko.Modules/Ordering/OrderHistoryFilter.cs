using System.Globalization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Ordering;

/// <summary>A query preference, never an authority or a copy of order data.</summary>
public sealed record OrderHistoryFilter(OrderBuyingContext? Context = null, string Sort = "newest")
{
    public void Validate() {
        Context?.Validate();
        if (Sort is not ("newest" or "oldest")) throw Invalid();
    }
    public string Cursor(long id) {
        Validate(); if (id <= 0) throw Invalid();
        return string.Join(':', "v1", Sort, Context?.OrganizationId.Value.ToString(CultureInfo.InvariantCulture) ?? "0",
            Context?.BranchId.Value.ToString(CultureInfo.InvariantCulture) ?? "0", Context?.DivisionId?.Value.ToString(CultureInfo.InvariantCulture) ?? "0",
            id.ToString(CultureInfo.InvariantCulture));
    }
    public long? ParseCursor(string? cursor) {
        Validate(); if (cursor is null) return null;
        if (cursor.Length > 110) throw Invalid();
        var parts = cursor.Split(':');
        if (parts.Length != 6 || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || Cursor(id) != cursor) throw Invalid();
        return id;
    }
    public static string Name(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value || value.Length > 80 || value.Any(char.IsControl)) throw Invalid();
        return value;
    }
    public static DomainValidationException Invalid() => new("invalid_order_view", "Choose a supported order filter and current page");
}
