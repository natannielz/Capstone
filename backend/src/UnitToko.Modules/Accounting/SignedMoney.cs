using System.Text.Json;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Accounting;

[JsonConverter(typeof(SignedMoneyJsonConverter))]
public readonly record struct SignedMoney
{
    public decimal Value { get; }
    public SignedMoney(decimal value)
    {
        Money.Validate(value, signed: true);
        Value = value;
    }
    public static SignedMoney Parse(string value) => new(DomainValues.ParseMoney(value, signed: true));
    public override string ToString() => DomainValues.FormatMoney(Value);
}

public sealed class SignedMoneyJsonConverter : JsonConverter<SignedMoney>
{
    public override SignedMoney Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? SignedMoney.Parse(reader.GetString()!)
            : throw new JsonException("Signed money must be a decimal string");
    public override void Write(Utf8JsonWriter writer, SignedMoney value, JsonSerializerOptions options)
    {
        Money.Validate(value.Value, signed: true);
        writer.WriteStringValue(value.ToString());
    }
}
