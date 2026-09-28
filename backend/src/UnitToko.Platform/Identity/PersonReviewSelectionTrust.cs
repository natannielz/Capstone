using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

internal enum PersonReviewSelectionKind { ProposalList, TargetSearch, TargetPreview, ProposalDetail }

internal sealed record PersonReviewSelectionRequest(PersonReviewSelectionKind Kind, string? Query = null, string? Status = null,
    long? ReferenceId = null, long? ExpectedVersion = null, int Offset = 0, int Limit = 25)
{
    internal PersonReviewSelectionRequest Normalize()
    {
        if (!Enum.IsDefined(Kind) || Offset is < 0 or > 10_000 || Limit is < 1 or > 100 ||
            Query?.Any(char.IsControl) == true || Status?.Any(char.IsControl) == true) throw Denied();
        var query = Query?.Trim();
        var status = Status?.Trim().ToLowerInvariant();
        var normalized = this with { Query = query, Status = status };
        var valid = Kind switch
        {
            PersonReviewSelectionKind.ProposalList => query is null && ReferenceId is null && ExpectedVersion is null &&
                status is null or "pending" or "approved" or "rejected" or "withdrawn",
            PersonReviewSelectionKind.TargetSearch => query is { Length: >= 2 and <= 80 } && status is null &&
                ReferenceId is null && ExpectedVersion is null,
            // Point selections cannot hide ignored query or pagination fields in an otherwise valid proof.
            PersonReviewSelectionKind.TargetPreview => query is null && status is null && ReferenceId is > 0 &&
                ExpectedVersion is > 0 && Offset == 0 && Limit == 25,
            PersonReviewSelectionKind.ProposalDetail => query is null && status is null && ReferenceId is > 0 &&
                (ExpectedVersion is null or > 0) && Offset == 0 && Limit == 25,
            _ => false
        };
        return valid ? normalized : throw Denied();
    }

    internal string Digest
    {
        get
        {
            var request = Normalize();
            return PersonWithdrawalTarget.Digest(new BsonDocument
            {
                { "kind", (int)request.Kind }, { "query", request.Query is null ? BsonNull.Value : request.Query },
                { "status", request.Status is null ? BsonNull.Value : request.Status },
                { "referenceId", request.ReferenceId.HasValue ? new BsonInt64(request.ReferenceId.Value) : BsonNull.Value },
                { "expectedVersion", request.ExpectedVersion.HasValue ? new BsonInt64(request.ExpectedVersion.Value) : BsonNull.Value },
                { "offset", request.Offset }, { "limit", request.Limit }
            }, false);
        }
    }
    private static AuthorizationDeniedException Denied() => new("person_selection_unavailable");
}

/// <summary>Private adapter input. Only supported mutable authority heads can serialize eligibility changes.</summary>
internal sealed record PersonReviewEligibilityHead(string Collection, long Id, long Revision, string Digest, BsonDocument Document);

internal interface IPersonReviewSelectionTrust
{
    // Explicit finite sets must come from the immutable manifest pinned by the policy head.
    // Any mutable eligibility dependency, including admission of new matching rows, must share
    // a supplied supported head. Its time-dependent validity must also bound the decision.
    // Roles, matching names and ordinary directory access never appoint this authority.
    Task<PersonReviewSelectionPermit> RequireAsync(IClientSessionHandle tx, ActorContext actor,
        PersonReviewSelectionRequest request, CancellationToken ct);
}

internal sealed class UnavailablePersonReviewSelectionTrust : IPersonReviewSelectionTrust
{
    public Task<PersonReviewSelectionPermit> RequireAsync(IClientSessionHandle tx, ActorContext actor,
        PersonReviewSelectionRequest request, CancellationToken ct) =>
        throw new AuthorizationDeniedException("person_governance_unavailable");
}

/// <summary>
/// Selection authority only. The owner still checks live session, permission/MFA and person
/// authority after its final await. These sets are conjunctive: proposal visibility uses
/// ProposalIds, target visibility uses GroupIds, and every profile also needs DisclosureUserIds.
/// Binding a session reference does not prove its current idle or absolute lifetime.
/// </summary>
internal sealed class PersonReviewSelectionPermit
{
    private readonly IClientSessionHandle owner;
    private readonly long transactionNumber;
    private readonly IMongoClient client;
    private readonly string databaseName, requestDigest;
    private readonly ActorContext actor;
    private readonly BsonDocument policy;
    private readonly ImmutableArray<PersonReviewEligibilityHead> heads;
    private readonly DateTimeOffset validFrom;
    internal ImmutableArray<long> ProposalIds { get; }
    internal ImmutableArray<long> GroupIds { get; }
    internal ImmutableArray<long> DisclosureUserIds { get; }
    internal DateTimeOffset? PolicyExpiresAt { get; }
    internal DateTimeOffset? ValidUntil { get; }
    internal BsonDocument PolicyPin => new()
    {
        { "id", policy["_id"].DeepClone() }, { "version", policy["version"].DeepClone() },
        { "digest", PersonWithdrawalTarget.Digest(policy, true) }
    };

    internal PersonReviewSelectionPermit(IMongoDatabase database, IClientSessionHandle tx, ActorContext actor,
        PersonReviewSelectionRequest request, BsonDocument policy, ImmutableArray<long> proposalIds,
        ImmutableArray<long> groupIds, ImmutableArray<long> disclosureUserIds,
        ImmutableArray<PersonReviewEligibilityHead> heads, DateTimeOffset? decisionValidFrom = null,
        DateTimeOffset? decisionValidUntil = null)
    {
        if (database is null || tx is null || !tx.IsInTransaction || !ValidActor(actor) || request is null ||
            policy is null || !PersonGovernanceSchemas.IsValid("personGovernancePolicies", policy) ||
            policy["fence"].AsInt64 == long.MaxValue || !ValidIds(proposalIds, 256) ||
            !ValidIds(groupIds, 256) || !ValidIds(disclosureUserIds, 258) || heads.IsDefault || heads.Length > 2048 ||
            decisionValidFrom.HasValue && decisionValidUntil.HasValue && decisionValidUntil <= decisionValidFrom) throw Denied();
        requestDigest = request.Digest;
        this.policy = policy.DeepClone().AsBsonDocument;
        var copied = ImmutableArray.CreateBuilder<PersonReviewEligibilityHead>(heads.Length);
        string? previousCollection = null; long previousId = 0;
        foreach (var head in heads)
        {
            if (!ValidHead(head) || previousCollection is not null &&
                (StringComparer.Ordinal.Compare(head.Collection, previousCollection) < 0 ||
                 head.Collection == previousCollection && head.Id <= previousId)) throw Denied();
            copied.Add(head with { Document = head.Document.DeepClone().AsBsonDocument });
            previousCollection = head.Collection; previousId = head.Id;
        }
        this.heads = copied.MoveToImmutable();
        owner = tx; transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        client = database.Client; databaseName = database.DatabaseNamespace.DatabaseName;
        this.actor = actor with { Session = actor.Session! with { } };
        ProposalIds = [.. proposalIds]; GroupIds = [.. groupIds]; DisclosureUserIds = [.. disclosureUserIds];
        PolicyExpiresAt = this.policy.TryGetValue("expiresAt", out var expiry) && !expiry.IsBsonNull
            ? PersonAuthorityBson.At(this.policy, "expiresAt") : null;
        validFrom = PersonAuthorityBson.At(this.policy, "effectiveAt");
        if (decisionValidFrom is { } from && from > validFrom) validFrom = from;
        ValidUntil = PolicyExpiresAt;
        if (decisionValidUntil is { } until && (ValidUntil is null || until < ValidUntil)) ValidUntil = until;
        if (ValidUntil is { } end && end <= validFrom) throw Denied();
    }

    internal void RequireCurrent(IMongoDatabase database, IClientSessionHandle tx, ActorContext actor,
        PersonReviewSelectionRequest request, DateTimeOffset now)
    {
        if (database is null || tx is null || !ReferenceEquals(owner, tx) || !tx.IsInTransaction ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber ||
            !ReferenceEquals(database.Client, client) || database.DatabaseNamespace.DatabaseName != databaseName ||
            actor != this.actor || request is null || request.Digest != requestDigest ||
            policy["status"] != "active" || now < validFrom || ValidUntil is { } end && now >= end) throw Denied();
    }

    internal async Task TouchAsync(IMongoDatabase database, IClientSessionHandle tx, ActorContext actor,
        PersonReviewSelectionRequest request, IClock clock, CancellationToken ct = default)
    {
        if (clock is null) throw Denied();
        RequireCurrent(database, tx, actor, request, clock.UtcNow);
        await Touch("personGovernancePolicies", policy);
        foreach (var head in heads) await Touch(head.Collection, head.Document);
        RequireCurrent(database, tx, actor, request, clock.UtcNow);

        async Task Touch(string collection, BsonDocument expected)
        {
            RequireCurrent(database, tx, actor, request, clock.UtcNow);
            var changed = await database.GetCollection<BsonDocument>(collection).UpdateOneAsync(tx,
                PersonAuthorityBson.ExactFilter(expected, true), Builders<BsonDocument>.Update.Inc("fence", 1L),
                cancellationToken: ct);
            if (changed.ModifiedCount != 1) throw Denied();
            RequireCurrent(database, tx, actor, request, clock.UtcNow);
        }
    }

    private static bool ValidIds(ImmutableArray<long> ids, int maximum)
    {
        if (ids.IsDefault || ids.Length > maximum) return false;
        long previous = 0;
        foreach (var id in ids) { if (id <= previous) return false; previous = id; }
        return true;
    }

    private static bool ValidHead(PersonReviewEligibilityHead? head)
    {
        if (head is null || head.Id <= 0 || head.Revision <= 0 || head.Document is null ||
            head.Digest is not { Length: 64 } || head.Digest.Any(character => !char.IsAsciiHexDigitLower(character))) return false;
        var row = head.Document;
        var valid = head.Collection switch
        {
            "personEvidenceSources" or "personRecords" or "personAccountBindings" or "personEquivalenceGroups"
                => PersonAuthoritySchemas.IsValid(head.Collection, row),
            "identities" => ValidIdentityHead(row),
            _ => false
        };
        return valid && row["_id"].AsInt64 == head.Id &&
            row[head.Collection == "identities" ? "grantVersion" : "version"].AsInt64 == head.Revision &&
            row["fence"].AsInt64 < long.MaxValue && PersonWithdrawalTarget.Digest(row, true) == head.Digest;
    }

    private static bool ValidIdentityHead(BsonDocument row)
    {
        string[] fields = ["_id", "schemaVersion", "issuer", "subject", "status", "grantVersion", "securityVersion", "fence"];
        return row.Names.Distinct(StringComparer.Ordinal).Count() == row.ElementCount &&
            row.Names.All(fields.Contains) && fields.Where(name => name != "securityVersion").All(row.Contains) &&
            PositiveLong(row, "_id") && PositiveLong(row, "grantVersion") &&
            (!row.Contains("securityVersion") || PositiveLong(row, "securityVersion")) &&
            row["schemaVersion"].IsInt32 && row["schemaVersion"].AsInt32 == 1 &&
            row["fence"].IsInt64 && row["fence"].AsInt64 >= 0 &&
            row["issuer"].IsString && Text(row["issuer"].AsString, 2048) &&
            row["subject"].IsString && Text(row["subject"].AsString, 512) &&
            row["status"].IsString && row["status"].AsString is "pending" or "active" or "suspended" or "revoked";
    }

    private static bool ValidActor(ActorContext? actor) => actor is not null &&
        actor.UserId > 0 && actor.GrantVersion > 0 && Text(actor.ProviderIssuer, 2048) && Text(actor.ProviderSubject, 512) &&
        actor.AuthenticatedAt != default && Enum.IsDefined(actor.Assurance) &&
        actor.Session is { Id.Length: 64, Version: > 0, SecurityVersion: > 0 } session &&
        session.Id.All(char.IsAsciiHexDigitLower);

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    private static bool PositiveLong(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 > 0;
    private static AuthorizationDeniedException Denied() => new("person_selection_unavailable");
}
