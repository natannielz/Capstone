using System.Text.Json;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests;

public sealed class NumericContractTests
{
    [Theory]
    [InlineData("0001")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("9223372036854775808")]
    [InlineData("10000000000000000000")]
    public void Id_input_matches_canonical_decimal_string_contract(string value)
    {
        Assert.Throws<DomainValidationException>(() => DomainValues.ParseId(value));
        Assert.Throws<DomainValidationException>(() => JsonSerializer.Deserialize<LongId>(JsonSerializer.Serialize(value)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    public void Canonical_ids_round_trip_as_strings(string value)
    {
        var id = JsonSerializer.Deserialize<LongId>(JsonSerializer.Serialize(value));
        Assert.Equal(value, id.ToString());
        Assert.Equal(JsonSerializer.Serialize(value), JsonSerializer.Serialize(id));
    }
}
