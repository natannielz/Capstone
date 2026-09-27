using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Accounting;

[JsonConverter(typeof(MoneyJsonConverter))]
public readonly record struct Money
{
    public decimal Value { get; }
    public Money(decimal value)
    {
        Validate(value, signed: false);
        Value = value;
    }
    internal static void Validate(decimal value, bool signed)
    {
        var scale = (decimal.GetBits(value)[3] >> 16) & 0xff;
        if (value > DomainValues.MaximumMoney || value < -DomainValues.MaximumMoney ||
            (!signed && value < 0) || scale > 2)
            throw new DomainValidationException("invalid_money", "Money is outside the supported range or precision");
    }
    public static Money Parse(string value) => new(DomainValues.ParseMoney(value));
    public override string ToString() => DomainValues.FormatMoney(Value);
    public static Money Add(Money left, Money right)
    {
        try { return new Money(checked(left.Value + right.Value)); }
        catch (OverflowException) { throw new DomainValidationException("invalid_money", "Document total exceeds the supported range"); }
    }
    public static Money Multiply(Money unitPrice, int quantity)
    {
        DomainValues.ParseQuantity(quantity);
        try { return new Money(checked(unitPrice.Value * quantity)); }
        catch (OverflowException) { throw new DomainValidationException("invalid_money", "Line total exceeds the supported range"); }
    }
}

public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? Money.Parse(reader.GetString()!) : throw new JsonException("Money must be a decimal string");
    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        Money.Validate(value.Value, signed: false);
        writer.WriteStringValue(value.ToString());
    }
}

public static class NumericContracts
{
    public static decimal ParseRate(string value)
    {
        if (string.IsNullOrEmpty(value) || !System.Text.RegularExpressions.Regex.IsMatch(value,
                @"^(?:0|[1-9][0-9]*)(?:\.[0-9]{1,4})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) ||
            rate is < 0 or > 100)
            throw new DomainValidationException("invalid_rate", "Rate must be between 0 and 100 with at most four fractional digits");
        return rate;
    }
}
