using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitToko.Modules.Contracts;

[JsonConverter(typeof(LongIdJsonConverter))]
public readonly record struct LongId
{
    public long Value { get; }
    public LongId(long value) => Value = value > 0 ? value : throw new DomainValidationException("invalid_id", "ID must be a positive Int64");
    public static LongId Parse(string value) => new(DomainValues.ParseId(value));
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class LongIdJsonConverter : JsonConverter<LongId>
{
    public override LongId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? LongId.Parse(reader.GetString()!) : throw new JsonException("ID must be an Int64 decimal string");
    public override void Write(Utf8JsonWriter writer, LongId value, JsonSerializerOptions options)
    {
        if (value.Value <= 0) throw new DomainValidationException("invalid_id", "ID must be a positive Int64");
        writer.WriteStringValue(value.ToString());
    }
}
