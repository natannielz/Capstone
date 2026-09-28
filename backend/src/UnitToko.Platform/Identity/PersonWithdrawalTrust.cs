using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

internal sealed record PersonWithdrawalIntent(long ProposalId, string RequestDigest, BsonDocument Target);
internal interface IPersonWithdrawalTrust
{
    // App permissions never appoint an adjudicator. Only an approved server adapter may supply this proof.
    // Resolve appointments from the immutable manifest pinned by the policy head. Changing or revoking
    // an appointment must advance that fenced head; unfenced configuration cannot supply authority.
    Task<PersonWithdrawalPermit> RequireAsync(IClientSessionHandle tx, long userId, string action,
        PersonWithdrawalIntent intent, CancellationToken ct);
}
internal sealed class UnavailablePersonWithdrawalTrust : IPersonWithdrawalTrust
{
    public Task<PersonWithdrawalPermit> RequireAsync(IClientSessionHandle tx, long userId, string action,
        PersonWithdrawalIntent intent, CancellationToken ct) =>
        throw new AuthorizationDeniedException("person_governance_unavailable");
}
internal sealed class PersonWithdrawalPermit
{
    private readonly IClientSessionHandle owner;
    private readonly IMongoClient client;
    private readonly string databaseName;
    private readonly long transactionNumber, userId, proposalId;
    private readonly string action, requestDigest, targetDigest;
    private readonly BsonDocument policy;
    internal BsonDocument Pin => new() { { "id", policy["_id"] }, { "version", policy["version"] },
        { "digest", PersonWithdrawalTarget.Digest(policy, true) } };
    internal PersonWithdrawalPermit(IMongoDatabase database, IClientSessionHandle tx, long userId, string action,
        PersonWithdrawalIntent intent, BsonDocument policy)
    {
        if (!tx.IsInTransaction || userId <= 0 || intent.ProposalId < 0 ||
            intent.ProposalId == 0 && action != "receiptRead" ||
            action is not ("propose" or "approve" or "reject" or "withdraw" or "read" or "receiptRead") ||
            !PersonGovernanceSchemas.IsValid("personGovernancePolicies", policy)) throw Denied();
        client = database.Client; databaseName = database.DatabaseNamespace.DatabaseName;
        owner = tx; transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.userId = userId; this.action = action; proposalId = intent.ProposalId; requestDigest = intent.RequestDigest;
        targetDigest = PersonWithdrawalTarget.Digest(intent.Target, false); this.policy = policy.DeepClone().AsBsonDocument;
    }
    internal void RequireCurrent(IClientSessionHandle tx, long actorId, string requiredAction,
        PersonWithdrawalIntent intent, DateTimeOffset now)
    {
        if (!ReferenceEquals(tx, owner) || !tx.IsInTransaction ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber ||
            actorId != userId || requiredAction != action || proposalId != intent.ProposalId ||
            requestDigest != intent.RequestDigest || targetDigest != PersonWithdrawalTarget.Digest(intent.Target, false) ||
            policy["status"] != "active" || PersonAuthorityBson.At(policy, "effectiveAt") > now ||
            policy.TryGetValue("expiresAt", out var end) && !end.IsBsonNull && PersonAuthorityBson.At(policy, "expiresAt") <= now) throw Denied();
    }
    internal async Task TouchAsync(IMongoDatabase database, IClientSessionHandle tx, long actorId, string requiredAction,
        PersonWithdrawalIntent intent, IClock clock, CancellationToken ct)
    {
        if (!ReferenceEquals(database.Client, client) || database.DatabaseNamespace.DatabaseName != databaseName) throw Denied();
        RequireCurrent(tx, actorId, requiredAction, intent, clock.UtcNow);
        var changed = await database.GetCollection<BsonDocument>("personGovernancePolicies").UpdateOneAsync(tx,
            PersonAuthorityBson.ExactFilter(policy, true), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
        if (changed.ModifiedCount != 1) throw Denied();
        RequireCurrent(tx, actorId, requiredAction, intent, clock.UtcNow);
    }
    private static AuthorizationDeniedException Denied() => new("person_governance_unavailable");
}
