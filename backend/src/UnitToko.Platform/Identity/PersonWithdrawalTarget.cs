using System.Collections.Immutable;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

/// <summary>A structural withdrawal target, never positive person or reviewer authority.</summary>
internal sealed class PersonWithdrawalTarget
{
    private readonly IClientSessionHandle owner;
    private readonly long transactionNumber;
    private readonly IMongoClient client;
    private readonly string databaseName;
    private readonly ImmutableArray<(string Collection, BsonDocument Document)> records;
    private readonly BsonDocument snapshot;
    internal long GroupId { get; }
    internal long GroupVersion { get; }
    internal string GroupStatus { get; }
    internal long CanonicalPersonId { get; }
    internal ImmutableArray<long> PersonIds { get; }
    internal ImmutableArray<long> UserIds { get; }
    internal BsonDocument Snapshot => snapshot.DeepClone().AsBsonDocument;

    internal PersonWithdrawalTarget(IMongoDatabase database, IClientSessionHandle tx, BsonDocument group,
        IEnumerable<(string Collection, BsonDocument Document)> captured)
    {
        if (!tx.IsInTransaction) throw Unavailable();
        owner = tx;
        transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        client = database.Client; databaseName = database.DatabaseNamespace.DatabaseName;
        GroupId = group["_id"].AsInt64; GroupVersion = group["version"].AsInt64;
        GroupStatus = group["status"].AsString; CanonicalPersonId = group["canonicalPersonId"].AsInt64;
        PersonIds = group["memberPersonIds"].AsBsonArray.Select(value => value.AsInt64).ToImmutableArray();
        UserIds = group["memberUserIds"].AsBsonArray.Select(value => value.AsInt64).ToImmutableArray();
        records = captured.OrderBy(row => row.Collection, StringComparer.Ordinal).ThenBy(row => row.Document["_id"].AsInt64)
            .Select(row => (row.Collection, row.Document.DeepClone().AsBsonDocument)).ToImmutableArray();
        if (records.Length > 2048 || records.Select(row => (row.Collection, row.Document["_id"].AsInt64)).Distinct().Count() != records.Length)
            throw Unavailable();
        var heads = new BsonArray(records.Select(row => new BsonDocument
        {
            { "collection", row.Collection }, { "id", row.Document["_id"].AsInt64 },
            { "revision", row.Document[row.Collection == "identities" ? "grantVersion" : row.Collection == "personEvidenceVersions" ? "evidenceVersion" : "version"].AsInt64 },
            { "digest", Digest(row.Document, excludeFence: true) }
        }));
        snapshot = new BsonDocument
        {
            { "groupId", GroupId }, { "groupVersion", GroupVersion }, { "groupStatus", GroupStatus },
            { "canonicalPersonId", CanonicalPersonId }, { "personIds", new BsonArray(PersonIds) },
            { "userIds", new BsonArray(UserIds) }, { "heads", heads }
        };
    }

    internal async Task TouchAsync(IMongoDatabase database, IClientSessionHandle tx, CancellationToken cancellationToken = default)
    {
        RequireOwner(database, tx);
        foreach (var row in records)
        {
            RequireOwner(database, tx);
            var collection = database.GetCollection<BsonDocument>(row.Collection);
            var filter = row.Collection == "identities" ? IdentityFilter(row.Document) :
                PersonAuthorityBson.ExactFilter(row.Document, row.Collection != "personEvidenceVersions");
            if (row.Collection == "personEvidenceVersions")
            {
                // These records are append-only. A supported writer changes current pointers and
                // shared heads; an unsupported in-transaction evidence mutation is still detected.
                if (await collection.CountDocumentsAsync(tx, filter, cancellationToken: cancellationToken) != 1) throw Changed();
            }
            else
            {
                var updated = await collection.UpdateOneAsync(tx, filter, Builders<BsonDocument>.Update.Inc("fence", 1L),
                    cancellationToken: cancellationToken);
                if (updated.ModifiedCount != 1) throw Changed();
            }
            RequireOwner(database, tx);
        }
    }

    private void RequireOwner(IMongoDatabase database, IClientSessionHandle tx)
    {
        if (!ReferenceEquals(owner, tx) || !tx.IsInTransaction ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber ||
            !ReferenceEquals(database.Client, client) || database.DatabaseNamespace.DatabaseName != databaseName)
            throw Unavailable();
    }

    private static FilterDefinition<BsonDocument> IdentityFilter(BsonDocument row)
    {
        var filter = Builders<BsonDocument>.Filter;
        var properties = new BsonDocument();
        var conditions = new List<FilterDefinition<BsonDocument>>();
        foreach (var field in row)
        {
            if (field.Name == "fence")
            {
                properties.Add("fence", new BsonDocument { { "bsonType", "long" }, { "minimum", 0L }, { "maximum", long.MaxValue - 1 } });
                continue;
            }
            properties.Add(field.Name, new BsonDocument("bsonType", field.Value.BsonType switch
            {
                BsonType.Int64 => "long", BsonType.Int32 => "int", BsonType.String => "string", _ => throw Unavailable()
            }));
            conditions.Add(filter.Eq(field.Name, field.Value));
        }
        conditions.Add(new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray(row.Names) }, { "properties", properties }
        }));
        if (!row.Contains("securityVersion")) conditions.Add(filter.Exists("securityVersion", false));
        return filter.And(conditions);
    }

    /// <summary>Canonical field order preserves BSON types and array order; only the root operational fence may be omitted.</summary>
    internal static string Digest(BsonDocument document, bool excludeFence)
    {
        BsonValue Canonical(BsonValue value, bool root = false) => value switch
        {
            BsonDocument objectValue => new BsonDocument(objectValue
                .Where(field => !(root && excludeFence && field.Name == "fence"))
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .Select(field => new BsonElement(field.Name, Canonical(field.Value)))),
            BsonArray array => new BsonArray(array.Select(value => Canonical(value))),
            _ => value.DeepClone()
        };
        return Convert.ToHexString(SHA256.HashData(Canonical(document, root: true).AsBsonDocument.ToBson())).ToLowerInvariant();
    }

    private static AuthorizationDeniedException Unavailable() => new("person_withdrawal_target_unavailable");
    private static AuthorizationDeniedException Changed() => new("person_withdrawal_target_changed");
}
