using System.Globalization;

namespace UnitToko.Modules.Contracts;

public sealed class DomainValidationException(string code, string title) : ArgumentException(title)
{
    public string Code { get; } = code;
    public string SafeTitle { get; } = title;
}

public static class DomainValues
{
    public const decimal MaximumMoney = 999_999_999_999_999.99m;
    public const int MaximumLineQuantity = 1_000_000;

    public static long ParseId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 19 || value[0] is < '1' or > '9' ||
            value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new DomainValidationException("invalid_id", "ID must be a positive Int64 decimal string");
        return id;
    }

    public static decimal ParseMoney(string value, bool signed = false)
    {
        if (string.IsNullOrEmpty(value) || !System.Text.RegularExpressions.Regex.IsMatch(value,
                signed ? @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?$" : @"^(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount) ||
            amount > MaximumMoney || amount < -MaximumMoney || (!signed && amount < 0) ||
            decimal.Round(amount, 2) != amount)
            throw new DomainValidationException("invalid_money", "Money must be a bounded decimal string with at most two fractional digits");
        return amount;
    }

    public static string FormatMoney(decimal value)
    {
        if (value > MaximumMoney || value < -MaximumMoney || decimal.Round(value, 2) != value)
            throw new DomainValidationException("invalid_money", "Money is outside the supported range or precision");
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static int ParseQuantity(int value) => value is >= 1 and <= MaximumLineQuantity
        ? value : throw new DomainValidationException("invalid_quantity", "Quantity must be between 1 and 1000000");
}
