using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Identity;

public static class SessionPersistence
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;

    /// <summary>Use inside the same business transaction as the identity fence. Revocation
    /// changes version/status; touching only a fence would not invalidate a cached actor.</summary>
    public static FilterDefinition<BsonDocument> LiveFilter(SessionReference reference, long userId, string issuer,
        string subject, long grantVersion, DateTimeOffset now) =>
        F.Eq("_id", reference.Id) & F.Eq("userId", userId) & F.Eq("issuer", issuer) & F.Eq("subject", subject) &
        F.Eq("grantVersion", grantVersion) & F.Eq("securityVersion", reference.SecurityVersion) &
        F.Eq("version", reference.Version) & F.Eq("status", "active") &
        F.Gt("idleExpiresAt", now.UtcDateTime) & F.Gt("absoluteExpiresAt", now.UtcDateTime);

    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string? TokenHash(string? token) => token is not null &&
        Regex.IsMatch(token, "^[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant)
        ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant() : null;
}
