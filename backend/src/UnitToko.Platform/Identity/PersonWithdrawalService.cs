using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

/// <summary>Independently reviewed withdrawal, with no production adjudicator source configured by default.</summary>
public sealed partial class PersonWithdrawalService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly PersonAccountInvalidation invalidation;
    private readonly IPersonWithdrawalTrust trust;
    private readonly IPersonReviewSelectionTrust selectionTrust;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private static readonly ResourceScope Administrative = new(IsAdministrative: true);
    public PersonWithdrawalService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids,
        IClock clock, MongoIdentitySessionStore sessions) : this(database, unit, ids, clock, sessions, new UnavailablePersonWithdrawalTrust()) { }
    internal PersonWithdrawalService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids,
        IClock clock, MongoIdentitySessionStore sessions, IPersonWithdrawalTrust trust, IPersonReviewSelectionTrust? selectionTrust = null)
    {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.trust = trust;
        this.selectionTrust = selectionTrust ?? new UnavailablePersonReviewSelectionTrust();
        authorization = new(database, unit, clock); invalidation = new(database, sessions);
    }
    private IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);
    private sealed record Work(BsonDocument Proposal, long HistoryId);
    private sealed class Guards
    {
        internal readonly List<Action<DateTimeOffset>> Checks = [];
        internal void Check(DateTimeOffset now) { foreach (var check in Checks) check(now); }
    }
    private static string Permission(string action) => action switch
    {
        "approve" or "reject" => Permissions.PersonReviewApprove,
        "propose" or "withdraw" => Permissions.PersonReviewPropose,
        "read" or "receiptRead" => Permissions.PersonReviewRead,
        _ => throw Denied()
    };
    private static AuthorizationDeniedException Denied(string code = "person_review_unavailable") => new(code);
    private static void Version(long id, long version)
    {
        if (id <= 0 || version <= 0 || version == long.MaxValue) throw Denied("invalid_version");
    }
    private static BsonDocument Require(string collection, BsonDocument? row)
    {
        if (row is null || !PersonGovernanceSchemas.IsValid(collection, row)) throw Denied();
        return row;
    }
    private async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = Require("personReviewProposals", await Collection("personReviewProposals").Find(tx, F.Eq("_id", id)).FirstOrDefaultAsync(ct));
        if (RequestDigest(row) != row["requestDigest"].AsString) throw Denied();
        var original = Require("personReviewHistory", await Collection("personReviewHistory").Find(tx,
            F.Eq("proposalId", id) & F.Eq("transitionVersion", 1L)).FirstOrDefaultAsync(ct));
        var pending = row.DeepClone().AsBsonDocument;
        pending["version"] = 1L; pending["status"] = "pending"; pending.Remove("resolution");
        if (!Equal(pending, original["proposal"].AsBsonDocument)) throw Denied("person_history_invalid");
        if (row["version"].AsInt64 == 2)
        {
            var latest = Require("personReviewHistory", await Collection("personReviewHistory").Find(tx,
                F.Eq("proposalId", id) & F.Eq("transitionVersion", 2L)).FirstOrDefaultAsync(ct));
            if (!Equal(row, latest["proposal"].AsBsonDocument)) throw Denied("person_history_invalid");
        }
        return row;
    }
    private static PersonWithdrawalIntent Intent(BsonDocument proposal) =>
        new(proposal["_id"].AsInt64, proposal["requestDigest"].AsString, proposal["target"].DeepClone().AsBsonDocument);
    private async Task<PersonWithdrawalPermit> PermitAsync(IClientSessionHandle tx, long userId, string action,
        PersonWithdrawalIntent intent, Guards guards, CancellationToken ct)
    {
        var permit = await trust.RequireAsync(tx, userId, action, intent, ct) ?? throw Denied();
        await permit.TouchAsync(database, tx, userId, action, intent, clock, ct);
        guards.Checks.Add(now => permit.RequireCurrent(tx, userId, action, intent, now)); return permit;
    }
    private async Task<PersonAuthorityProof> PersonAsync(IClientSessionHandle tx, long userId, Guards guards, CancellationToken ct)
    {
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [userId], ct);
        proof.RequireCurrent(tx, clock.UtcNow);
        if (proof.Evidence.Length != 1 || proof.Evidence[0].PersonId is not > 0) throw Denied();
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, ct);
        guards.Checks.Add(now => proof.RequireCurrent(tx, now)); return proof;
    }
    private static BsonDocument PersonSnapshot(PersonAuthorityProof proof)
    {
        var evidence = proof.Evidence.Single();
        return new() { { "userId", evidence.UserId }, { "canonicalPersonId", evidence.PersonId!.Value },
            { "heads", new BsonArray(proof.CopyRecords().OrderBy(row => row.Collection, StringComparer.Ordinal)
                .ThenBy(row => row.Document["_id"].AsInt64).Select(row => new BsonDocument
                {
                    { "collection", row.Collection }, { "id", row.Document["_id"] },
                    { "revision", row.Document[row.Collection == "personEvidenceVersions" ? "evidenceVersion" : "version"] },
                    { "digest", PersonWithdrawalTarget.Digest(row.Document, row.Collection != "personEvidenceVersions") }
                })) } };
    }
    private static bool Equal(BsonDocument left, BsonDocument right) =>
        PersonWithdrawalTarget.Digest(left, false) == PersonWithdrawalTarget.Digest(right, false);
    private static long Security(BsonDocument identity)
    {
        if (!identity.TryGetValue("securityVersion", out var value)) return 1;
        return value.IsInt64 && value.AsInt64 > 0 ? value.AsInt64 : throw Denied();
    }
    private async Task<long> ActorSecurityAsync(IClientSessionHandle tx, ActorContext actor, CancellationToken ct)
    {
        var row = await Collection("identities").Find(tx, F.Eq("_id", actor.UserId)).FirstOrDefaultAsync(ct);
        if (row is null || !row.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 != actor.UserId ||
            !row.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !row.TryGetValue("grantVersion", out var version) || !version.IsInt64 || version.AsInt64 != actor.GrantVersion ||
            !row.TryGetValue("status", out var status) || !status.IsString || status.AsString != "active" ||
            !row.TryGetValue("issuer", out var issuer) || !issuer.IsString || issuer.AsString != actor.ProviderIssuer ||
            !row.TryGetValue("subject", out var subject) || !subject.IsString || subject.AsString != actor.ProviderSubject ||
            !row.TryGetValue("fence", out var fence) || !fence.IsInt64 || fence.AsInt64 is < 0 or long.MaxValue) throw Denied();
        return Security(row);
    }
    private async Task<AuthorizationSnapshot> AuthorizeAsync(ActorContext actor, string action,
        IClientSessionHandle tx, Guards guards, CancellationToken ct)
    {
        var snapshot = await authorization.LoadAsync(actor, tx, ct);
        await ActorSecurityAsync(tx, actor, ct);
        AuthorizationDecision Demand(DateTimeOffset now)
        {
            var decision = ScopePolicy.Authorize(actor, snapshot, Permission(action), Administrative, now);
            if (!decision.Allowed) throw Denied(decision.Code);
            return decision;
        }
        var decision = Demand(clock.UtcNow);
        var grants = snapshot.Grants.Where(row => decision.GrantIds.Contains(row.Id)).ToArray();
        var memberships = snapshot.Memberships.Where(row => grants.Any(grant => grant.MembershipId == row.Id)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, [], [], grants, memberships, tx, ct);
        guards.Checks.Add(now => Demand(now)); return snapshot;
    }
    private async Task CompleteAsync(ActorContext actor, IClientSessionHandle tx, Guards guards, CancellationToken ct)
    {
        guards.Check(clock.UtcNow);
        await authorization.FenceAsync(actor, [], tx, ct);
        if (actor.Session is { } reference)
        {
            var row = await Collection("identitySessions").Find(tx,
                SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).FirstOrDefaultAsync(ct);
            var now = new BsonDateTime(clock.UtcNow.UtcDateTime).MillisecondsSinceEpoch;
            if (row is null || !row.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !row.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw Denied("stale_or_inactive_session");
        }
        guards.Check(clock.UtcNow);
    }
    private static BsonDocument ResultDocument(BsonDocument proposal)
    {
        var target = proposal["target"].AsBsonDocument;
        var resolution = proposal.GetValue("resolution", BsonNull.Value);
        return new() { { "proposalId", proposal["_id"] }, { "version", proposal["version"] }, { "status", proposal["status"] },
            { "groupId", target["groupId"] },
            { "groupVersion", resolution.IsBsonNull ? target["groupVersion"] : resolution["resultGroupVersion"] },
            { "recordedAt", resolution.IsBsonNull ? proposal["createdAt"] : resolution["decidedAt"] } };
    }
    private static PersonWithdrawalResult Result(BsonDocument proposal)
    {
        var row = ResultDocument(proposal);
        return new(row["proposalId"].AsInt64, row["version"].AsInt64, row["status"].AsString,
            row["groupId"].AsInt64, row["groupVersion"].AsInt64, PersonAuthorityBson.At(row, "recordedAt"));
    }
    private async Task WriteHistoryAsync(long id, BsonDocument proposal, CancellationToken ct, IClientSessionHandle tx)
    {
        var terminal = proposal["version"].AsInt64 == 2;
        var source = terminal ? proposal["resolution"].AsBsonDocument : proposal;
        var history = new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "proposalId", proposal["_id"] },
            { "transitionVersion", proposal["version"] }, { "event", terminal ? proposal["status"] : new BsonString("proposed") },
            { "actorId", source[terminal ? "actorId" : "requestedBy"] },
            { "actorGrantVersion", source[terminal ? "actorGrantVersion" : "requesterGrantVersion"] },
            { "actorSecurityVersion", source[terminal ? "actorSecurityVersion" : "requesterSecurityVersion"] },
            { "createdAt", source[terminal ? "decidedAt" : "createdAt"] }, { "reason", source["reason"] },
            { "correlationId", source["correlationId"] }, { "proposal", proposal.DeepClone() }
        };
        Require("personReviewHistory", history);
        await Collection("personReviewHistory").InsertOneAsync(tx, history, cancellationToken: ct);
    }
    private async Task<PersonWithdrawalResult> ExecuteAsync(ActorContext actor, string action,
        AuthorizationCommandMetadata metadata, object requestBody,
        Func<IClientSessionHandle, Guards, CancellationToken, Task<Work>> write, CancellationToken ct)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        var operation = "person.review." + action;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Request = requestBody, metadata.Reason })))).ToLowerInvariant();
        var idempotency = IdempotencyScope.ForActor(actor, operation, metadata.IdempotencyKey, hash); idempotency.Validate();
        return await unit.ExecuteAsync(async (tx, token) =>
        {
            var guards = new Guards(); await AuthorizeAsync(actor, action, tx, guards, token);
            var filter = F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", metadata.IdempotencyKey);
            var old = await Collection("idempotencyRecords").Find(tx, filter).FirstOrDefaultAsync(token);
            BsonDocument proposal;
            if (old is not null)
            {
                RequireReceiptOwner(old, actor.UserId, operation, metadata.IdempotencyKey);
                if (old.GetValue("bodyHash", BsonNull.Value) != hash)
                    throw new PersistenceConflictException("idempotency_key_reused", "Request key was used with different details");
                proposal = await ReceiptProposalAsync(tx, actor.UserId, action, old, token);
                await PermitAsync(tx, actor.UserId, action, Intent(proposal), guards, token);
                await PersonAsync(tx, actor.UserId, guards, token);
            }
            else
            {
                var work = await write(tx, guards, token); proposal = work.Proposal;
                await Collection("idempotencyRecords").InsertOneAsync(tx, new BsonDocument
                {
                    { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "operation", operation }, { "key", metadata.IdempotencyKey },
                    { "bodyHash", hash }, { "createdAt", clock.UtcNow.UtcDateTime }, { "result", ResultDocument(proposal) },
                    { "personWithdrawal", new BsonDocument { { "schemaVersion", 1 }, { "action", action },
                        { "proposalId", proposal["_id"] }, { "transitionVersion", proposal["version"] }, { "historyId", work.HistoryId } } }
                }, cancellationToken: token);
            }
            await CompleteAsync(actor, tx, guards, token);
            return Result(proposal);
        }, ct);
    }
    private async Task<BsonDocument> ReceiptProposalAsync(IClientSessionHandle tx, long actorId, string action, BsonDocument receipt, CancellationToken ct)
    {
        if (!receipt.TryGetValue("personWithdrawal", out var value) || !value.IsBsonDocument) throw Denied("person_receipt_invalid");
        var link = value.AsBsonDocument;
        if (link.ElementCount != 5 || !link.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !link.TryGetValue("action", out var storedAction) || !storedAction.IsString || storedAction.AsString != action ||
            new[] { "proposalId", "transitionVersion", "historyId" }.Any(name => !link.TryGetValue(name, out var id) || !id.IsInt64 || id.AsInt64 <= 0))
            throw Denied("person_receipt_invalid");
        var history = Require("personReviewHistory", await Collection("personReviewHistory").Find(tx, F.Eq("_id", link["historyId"])).FirstOrDefaultAsync(ct));
        if (history["actorId"] != actorId || history["proposalId"] != link["proposalId"] || history["transitionVersion"] != link["transitionVersion"] ||
            history["event"] != (action switch { "propose" => "proposed", "approve" => "approved", "reject" => "rejected", "withdraw" => "withdrawn", _ => throw Denied() }))
            throw Denied("person_receipt_invalid");
        var proposal = history["proposal"].AsBsonDocument;
        if (!receipt.TryGetValue("result", out var result) || !result.IsBsonDocument || !Equal(result.AsBsonDocument, ResultDocument(proposal)))
            throw Denied("person_receipt_invalid");
        if (RequestDigest(proposal) != proposal["requestDigest"].AsString) throw Denied("person_receipt_invalid");
        var current = await ProposalAsync(tx, proposal["_id"].AsInt64, ct);
        if (current["requestDigest"] != proposal["requestDigest"]) throw Denied("person_receipt_invalid");
        if (proposal["version"].AsInt64 == 2 && !Equal(current, proposal)) throw Denied("person_receipt_invalid");
        return proposal.DeepClone().AsBsonDocument;
    }
}
