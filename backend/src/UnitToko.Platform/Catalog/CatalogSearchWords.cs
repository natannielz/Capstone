using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace UnitToko.Platform.Catalog;

/// <summary>Search the way shoppers type: every word must appear in one of the fields, in any order, so "air 600"
/// finds "Air mineral 600 ml". Words are matched literally and case-insensitively; at most eight count.</summary>
internal static class CatalogSearchWords
{
    private const int MaximumWords = 8;

    internal static BsonDocument Match(string search, params string[] fields)
    {
        var words = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumWords).ToArray();
        return new BsonDocument("$and", new BsonArray(words.Select(word => new BsonDocument("$or",
            new BsonArray(fields.Select(field => new BsonDocument(field, new BsonRegularExpression(Regex.Escape(word), "i"))))))));
    }
}
