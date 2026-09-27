using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

public sealed class NumericStringSchemaTransformer : IOpenApiSchemaTransformer
{
    private static readonly string LongIdPattern = BuildPositiveInt64Pattern();

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var nullableType = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type);
        var type = nullableType ?? context.JsonTypeInfo.Type;
        if (type == typeof(LongId))
            SetString(schema, LongIdPattern, 19,
                "Positive decimal Int64 string, from 1 through 9223372036854775807. JSON numbers are invalid.");
        else if (type == typeof(Money))
            SetString(schema, @"^(?:0|[1-9][0-9]{0,14})(?:\.[0-9]{1,2})?$(?![\s\S])", 18,
                "Nonnegative decimal string from 0.00 through 999999999999999.99, with at most two fractional digits; responses use exactly two. JSON numbers are invalid.");
        else if (type == typeof(SignedMoney))
            SetString(schema, @"^-?(?:0|[1-9][0-9]{0,14})(?:\.[0-9]{1,2})?$(?![\s\S])", 19,
                "Signed decimal string from -999999999999999.99 through 999999999999999.99, with at most two fractional digits; responses use exactly two. JSON numbers are invalid.");
        if (nullableType is not null && (type == typeof(LongId) || type == typeof(Money) || type == typeof(SignedMoney)))
            schema.Type = JsonSchemaType.String | JsonSchemaType.Null;
        return Task.CompletedTask;
    }

    private static void SetString(OpenApiSchema schema, string pattern, int maxLength, string description)
    {
        schema.Type = JsonSchemaType.String;
        schema.Format = null;
        schema.Pattern = pattern;
        schema.MaxLength = maxLength;
        schema.Description = description;
        schema.Properties?.Clear();
    }

    private static string BuildPositiveInt64Pattern()
    {
        var maximum = long.MaxValue.ToString(CultureInfo.InvariantCulture);
        var branches = new List<string> { "[1-9][0-9]{0,17}" };
        // Each 19-digit branch first differs from Int64.MaxValue at one position.
        for (var index = 0; index < maximum.Length; index++)
        {
            var lower = index == 0 ? '1' : '0';
            var upper = (char)(maximum[index] - 1);
            if (upper < lower) continue;
            var digit = lower == upper ? lower.ToString() : $"[{lower}-{upper}]";
            var suffixLength = maximum.Length - index - 1;
            var suffix = suffixLength == 0 ? "" : $"[0-9]{{{suffixLength}}}";
            branches.Add(Regex.Escape(maximum[..index]) + digit + suffix);
        }
        branches.Add(maximum);
        return "^(?:" + string.Join('|', branches) + ")$(?![\\s\\S])";
    }
}
