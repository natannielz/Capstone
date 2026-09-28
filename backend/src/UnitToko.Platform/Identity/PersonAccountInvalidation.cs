using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

/// <summary>Safe authority revisions for the governed change's audit; contains no provider or session secrets.</summary>
public sealed record PersonAccountAuthorityRevision(long UserId, long PreviousGrantVersion, long GrantVersion,
    long PreviousSecurityVersion, long SecurityVersion);

/// <summary>
/// Invalidates every alias selected by a governed person change in the caller's transaction.
/// The caller must discover and fence the complete equivalence graph, including inactive accounts.
/// This helper neither establishes person equivalence nor chooses who may approve it.
/// </summary>
public sealed class PersonAccountInvalidation(IMongoDatabase database, MongoIdentitySessionStore sessions)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private sealed record Account(BsonDocument Expected, long UserId, long GrantVersion, long SecurityVersion, bool LegacySecurity);
    private const int MaximumAccounts = 256;

    public async Task<ImmutableArray<PersonAccountAuthorityRevision>> InvalidateAsync(IClientSessionHandle tx,
        ImmutableArray<long> affectedUserIds, CancellationToken cancellationToken = default)
    {
        if (!tx.IsInTransaction) throw new InvalidOperationException("Person changes require an active transaction");
        if (affectedUserIds.IsDefaultOrEmpty || affectedUserIds.Length > MaximumAccounts ||
            affectedUserIds[0] <= 0 || affectedUserIds.Zip(affectedUserIds.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new ArgumentException("A complete sorted unique positive account selection is required", nameof(affectedUserIds));
        var identities = database.GetCollection<BsonDocument>("identities");
        // Read all identities before the first write; a bad later alias cannot be partly invalidated
        // even if a caller catches the validation error within its transaction.
        var rows = await identities.Find(tx, F.In("_id", affectedUserIds))
            .Limit(MaximumAccounts + 1).ToListAsync(cancellationToken);
        if (rows.Count != affectedUserIds.Length) throw Unavailable();
        var accounts = rows.Select(ParseAccount).OrderBy(account => account.UserId).ToArray();
        if (!accounts.Select(account => account.UserId).SequenceEqual(affectedUserIds)) throw Unavailable();

        // Reuse the established session revocation operation, but preflight its counters. Include
        // revoked rows in this read: Mongo equality treats arrays as matches, so excluding "revoked"
        // before checking the scalar status could hide a malformed ["active", "revoked"] row.
        var projection = Builders<BsonDocument>.Projection.Include("_id").Include("schemaVersion")
            .Include("userId").Include("status").Include("version").Include("fence");
        using (var cursor = await database.GetCollection<BsonDocument>("identitySessions")
            .Find(tx, F.In("userId", affectedUserIds)).Project<BsonDocument>(projection).ToCursorAsync(cancellationToken))
        {
            while (await cursor.MoveNextAsync(cancellationToken))
                foreach (var row in cursor.Current) RequireRevocableSession(row, affectedUserIds);
        }

        var revisions = ImmutableArray.CreateBuilder<PersonAccountAuthorityRevision>(accounts.Length);
        foreach (var account in accounts)
        {
            var update = U.Inc("grantVersion", 1L).Inc("fence", 1L);
            update = account.LegacySecurity ? update.Set("securityVersion", 2L) : update.Inc("securityVersion", 1L);
            var changed = await identities.UpdateOneAsync(tx, ExactAccount(account.Expected), update, cancellationToken: cancellationToken);
            if (changed.ModifiedCount != 1)
                throw new PersistenceConflictException("version_conflict", "Account authority changed; refresh and try again");
            await sessions.RevokeUserSessionsAsync(tx, account.UserId, "person_authority_changed", cancellationToken);
            revisions.Add(new(account.UserId, account.GrantVersion, account.GrantVersion + 1,
                account.SecurityVersion, account.SecurityVersion + 1));
        }
        return revisions.MoveToImmutable();
    }

    private static Account ParseAccount(BsonDocument row)
    {
        var id = Number(row, "_id", positive: true, incremented: false);
        var grant = Number(row, "grantVersion", positive: true, incremented: true);
        _ = Number(row, "fence", positive: false, incremented: true);
        var legacy = !row.Contains("securityVersion");
        var security = legacy ? 1L : Number(row, "securityVersion", positive: true, incremented: true);
        RequireSchema(row);
        RequireText(row, "issuer", 2048);
        RequireText(row, "subject", 512);
        if (!row.TryGetValue("status", out var status) || !status.IsString ||
            status.AsString is not ("pending" or "active" or "suspended" or "revoked")) throw Unavailable();
        var expected = new BsonDocument();
        foreach (var field in new[] { "_id", "schemaVersion", "issuer", "subject", "status", "grantVersion", "fence", "securityVersion" })
            if (row.TryGetValue(field, out var value)) expected.Add(field, value);
        return new(expected, id, grant, security, legacy);
    }

    private static void RequireRevocableSession(BsonDocument row, ImmutableArray<long> affectedUserIds)
    {
        RequireSchema(row);
        var id = RequireText(row, "_id", 64);
        if (id.Length != 64 || id.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Unavailable();
        var userId = Number(row, "userId", positive: true, incremented: false);
        if (!affectedUserIds.Contains(userId) || !row.TryGetValue("status", out var status) || !status.IsString ||
            status.AsString is not ("active" or "rotated" or "revoked")) throw Unavailable();
        // Already revoked rows remain byte-for-byte unchanged; only counters that will advance
        // must have room. Their positive/nonnegative BSON types are still required.
        var incremented = status.AsString != "revoked";
        _ = Number(row, "version", positive: true, incremented);
        _ = Number(row, "fence", positive: false, incremented);
    }

    private static FilterDefinition<BsonDocument> ExactAccount(BsonDocument expected)
    {
        var properties = new BsonDocument();
        var filters = new List<FilterDefinition<BsonDocument>>();
        foreach (var field in expected)
        {
            properties.Add(field.Name, new BsonDocument("bsonType", field.Value.BsonType switch
            {
                BsonType.Int64 => "long", BsonType.Int32 => "int", BsonType.String => "string",
                _ => throw Unavailable()
            }));
            filters.Add(F.Eq(field.Name, field.Value));
        }
        filters.Add(new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray(expected.Names) }, { "properties", properties }
        }));
        if (!expected.Contains("securityVersion")) filters.Add(F.Exists("securityVersion", false));
        return F.And(filters);
    }

    private static void RequireSchema(BsonDocument row)
    {
        if (!row.TryGetValue("schemaVersion", out var value) || !value.IsInt32 || value.AsInt32 != 1) throw Unavailable();
    }

    private static string RequireText(BsonDocument row, string field, int maximum)
    {
        if (!row.TryGetValue(field, out var value) || !value.IsString || string.IsNullOrWhiteSpace(value.AsString) ||
            value.AsString.Length > maximum || value.AsString.Any(char.IsControl)) throw Unavailable();
        return value.AsString;
    }

    private static long Number(BsonDocument row, string field, bool positive, bool incremented)
    {
        if (!row.TryGetValue(field, out var value) || !value.IsInt64 ||
            value.AsInt64 < (positive ? 1 : 0) || incremented && value.AsInt64 == long.MaxValue) throw Unavailable();
        return value.AsInt64;
    }

    private static PersistenceConflictException Unavailable() =>
        new("person_account_authority_unavailable", "Account authority is unavailable for the person change");
}
