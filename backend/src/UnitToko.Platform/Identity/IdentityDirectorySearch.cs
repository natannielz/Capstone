using System.Text.RegularExpressions;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

public sealed partial class IdentityOnboardingService
{
    public Task<AccountDirectoryPage> SearchProfilesAsync(ActorContext actor, string term, int offset, int limit,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length > 80 || term.Any(char.IsControl))
            throw new DomainValidationException("invalid_directory_search", "A bounded search term is required");
        var normalized = term.Trim();
        if (normalized.Length < 2) throw new DomainValidationException("invalid_directory_search", "Search at least two characters");
        return ListProfilesCoreAsync(actor, offset, limit, audit, null, normalized, cancellationToken);
    }

    private static BsonDocument SearchMatch(string term)
    {
        var escaped = new BsonRegularExpression(Regex.Escape(term), "i");
        var choices = new BsonArray
        {
            new BsonDocument("displayName", escaped),
            new BsonDocument("verifiedEmail", escaped)
        };
        if (long.TryParse(term, out var userId) && userId > 0) choices.Add(new BsonDocument("_id", userId));
        return new BsonDocument("$match", new BsonDocument("$or", choices));
    }
}
