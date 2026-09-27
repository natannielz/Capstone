using Microsoft.AspNetCore.Http;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Authorization;

public sealed class HttpInputTests
{
    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("1")]
    [InlineData("W/\"1\"")]
    [InlineData("\"01\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1.0\"")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("\"1\",\"2\"")]
    public void Mutation_requires_one_canonical_expected_version(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = value;
        Assert.Throws<DomainValidationException>(() => AccessRequestMetadata.ExpectedVersion(context.Request));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(9007199254740993L)]
    [InlineData(long.MaxValue)]
    public void Versions_round_trip_without_javascript_number_conversion(long version)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = AccessRequestMetadata.VersionTag(version);
        Assert.Equal(version, AccessRequestMetadata.ExpectedVersion(context.Request));
    }

    [Fact]
    public void Idempotency_metadata_rejects_ambiguous_or_unbounded_headers()
    {
        var context = new DefaultHttpContext();
        Assert.Throws<DomainValidationException>(() => AccessRequestMetadata.IdempotencyKey(context.Request));
        foreach (var value in new[] { "one,two", "spaces not allowed", new string('a', 129), "key\n" })
        {
            context.Request.Headers["Idempotency-Key"] = value;
            Assert.Throws<DomainValidationException>(() => AccessRequestMetadata.IdempotencyKey(context.Request));
        }
        context.Request.Headers["Idempotency-Key"] = new[] { "one", "two" };
        Assert.Throws<DomainValidationException>(() => AccessRequestMetadata.IdempotencyKey(context.Request));
        context.Request.Headers["Idempotency-Key"] = "request:9e13972a-28e4-49b7";
        Assert.Equal("request:9e13972a-28e4-49b7", AccessRequestMetadata.IdempotencyKey(context.Request));
    }

}
