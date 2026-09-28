using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Authorization;

/// <summary>
/// Serializes current person authority in the caller's transaction. Every future governed binding/equivalence
/// writer must also change the shared group head, including insertions, to serialize membership phantoms.
/// Immutable evidence may only be appended; changing an existing evidence record is never a supported write.
/// </summary>
internal sealed class PersonAuthorizationFence(IMongoDatabase database, IClock clock)
{
    internal async Task TouchAsync(IClientSessionHandle session, PersonAuthorityProof proof, CancellationToken cancellationToken = default)
    {
        proof.RequireCurrent(session, clock.UtcNow);
        foreach (var row in proof.CopyRecords().OrderBy(row => row.Collection, StringComparer.Ordinal).ThenBy(row => row.Document["_id"].AsInt64))
        {
            proof.RequireCurrent(session, clock.UtcNow);
            var collection = database.GetCollection<BsonDocument>(row.Collection);
            var expected = PersonAuthorityBson.ExactFilter(row.Document, row.Collection != "personEvidenceVersions");
            if (row.Collection == "personEvidenceVersions")
            {
                if (await collection.CountDocumentsAsync(session, expected, cancellationToken: cancellationToken) != 1)
                    throw new AuthorizationDeniedException("person_authority_changed");
            }
            else
            {
                var result = await collection.UpdateOneAsync(session, expected,
                    Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: cancellationToken);
                if (result.ModifiedCount != 1) throw new AuthorizationDeniedException("person_authority_changed");
            }
            proof.RequireCurrent(session, clock.UtcNow);
        }
        proof.RequireCurrent(session, clock.UtcNow);
    }
}
