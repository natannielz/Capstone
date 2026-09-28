using UnitToko.Platform.Authorization;
using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;

namespace UnitToko.Platform.Identity;

/// <summary>Server-owned transaction proof; private personnel material never leaves the platform.</summary>
internal sealed class PersonAuthorityProof
{
    private readonly ImmutableArray<(string Collection, BsonDocument Document)> records;
    private readonly IClientSessionHandle owner;
    internal ImmutableArray<PersonEvidence> Evidence { get; }
    internal DateTimeOffset ValidFrom { get; }
    internal DateTimeOffset? ValidUntil { get; }

    internal PersonAuthorityProof(IClientSessionHandle owner, ImmutableArray<PersonEvidence> evidence,
        IEnumerable<(string Collection, BsonDocument Document)> records, DateTimeOffset validFrom, DateTimeOffset? validUntil)
    {
        this.owner = owner;
        Evidence = evidence;
        this.records = records.Select(row => (row.Collection, (BsonDocument)row.Document.DeepClone())).ToImmutableArray();
        ValidFrom = validFrom;
        ValidUntil = validUntil;
    }

    internal IEnumerable<(string Collection, BsonDocument Document)> CopyRecords() =>
        records.Select(row => (row.Collection, (BsonDocument)row.Document.DeepClone()));

    internal void RequireCurrent(IClientSessionHandle session, DateTimeOffset now)
    {
        if (!ReferenceEquals(owner, session) || !session.IsInTransaction || now < ValidFrom ||
            ValidUntil is { } until && now >= until || Evidence.Any(row => row.Verification != PersonVerification.Verified))
            throw new AuthorizationDeniedException("person_authority_unavailable");
    }
}
