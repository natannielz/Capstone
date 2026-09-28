using System.Text.Json;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Catalog;

/// <summary>Validation of a supplied GTIN, not allocation, ownership or authenticity evidence.
/// Raw preserves the supplied packaging identifier; Canonical14 is the comparison key.</summary>
[JsonConverter(typeof(GtinJsonConverter))]
public sealed record Gtin
{
    public string Raw { get; }
    public string Canonical14 { get; }
    public int SuppliedLength => Raw.Length;
    private Gtin(string raw, string canonical14) { Raw = raw; Canonical14 = canonical14; }

    public static Gtin Parse(string raw)
    {
        if (raw is null || raw.Length is not (8 or 12 or 13 or 14) || raw.Any(character => character is < '0' or > '9'))
            throw Invalid();
        var sum = 0;
        var weight = 3;
        for (var index = raw.Length - 2; index >= 0; index--)
        {
            sum += (raw[index] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        if ((10 - sum % 10) % 10 != raw[^1] - '0') throw Invalid();
        return new(raw, raw.PadLeft(14, '0'));
    }

    private static DomainValidationException Invalid() => new("invalid_gtin", "GTIN must be an ASCII digit string with a valid length and check digit");
}

public sealed class GtinJsonConverter : JsonConverter<Gtin>
{
    public override bool HandleNull => true;
    public override Gtin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("GTIN must be a string");
        try { return Gtin.Parse(reader.GetString()!); }
        catch (DomainValidationException exception) { throw new JsonException("GTIN format is invalid", exception); }
    }

    public override void Write(Utf8JsonWriter writer, Gtin value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("GTIN must be a string");
        writer.WriteStringValue(value.Raw);
    }
}
