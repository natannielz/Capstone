using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Mongo;

public sealed class OutboxStore(IMongoDatabase database, IClock clock)
{
    private readonly IMongoCollection<BsonDocument> events = database.GetCollection<BsonDocument>("outboxEvents");
    private readonly IMongoCollection<BsonDocument> durableEvents =
        database.GetCollection<BsonDocument>("outboxEvents").WithWriteConcern(WriteConcern.WMajority);

    public async Task EnqueueAsync(IClientSessionHandle session, string sourceKey, string kind,
        BsonDocument payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 200 ||
            string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || payload.ToBson().Length > 32_768)
            throw new DomainValidationException("invalid_outbox", "Outbox event is invalid or too large");
        await events.InsertOneAsync(session, new BsonDocument
        {
            { "schemaVersion", CollectionMigrations.FoundationDocumentSchemaVersion }, { "sourceKey", sourceKey }, { "kind", kind }, { "payload", payload },
            { "status", "pending" }, { "attempts", 0 },
            { "dueAt", clock.UtcNow.UtcDateTime }, { "createdAt", clock.UtcNow.UtcDateTime }
        }, cancellationToken: cancellationToken);
    }

    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private const int MaximumTerminalizationsPerClaim = 16;
    private static readonly string[] FailureCodes = ["processing_retry", "processing_error", "scanner_unavailable", "decoder_unavailable", "decode_timeout", "invalid_media", "infected_media", "integrity_mismatch", "unsupported_kind"];

    /// <summary>Claim only registered kinds. A call terminalizes at most 16 exhausted jobs before yielding.</summary>
    public async Task<BsonDocument?> ClaimAsync(string consumerId, OutboxClaimPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId) || consumerId.Length > 128 || consumerId != consumerId.Trim() || consumerId.Any(char.IsControl))
            throw Invalid("invalid_lease");
        ArgumentNullException.ThrowIfNull(policy);
        for (var scanned = 0; scanned < MaximumTerminalizationsPerClaim; scanned++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.UtcNow.UtcDateTime;
            var filter = F.In("kind", policy.Kinds) & F.Gte("attempts", 0) &
                (F.Exists("attemptLimit", false) | (F.Type("attemptLimit", BsonType.Int32) & F.Gte("attemptLimit", 1) & F.Lte("attemptLimit", 1000))) &
                ((F.In("status", new[] { "pending", "retry" }) & F.Lte("dueAt", now)) |
                    (F.Eq("status", "leased") & F.Lte("leaseUntil", now)));
            var exhausted = new BsonDocument("$gte", new BsonArray { "$attempts", "$attemptLimit" });
            var hardDeadline = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$status", "leased" }),
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$claimDeadline"), "date" }),
                new BsonDocument("$lte", new BsonArray { "$claimDeadline", now })
            });
            var terminal = new BsonDocument("$or", new BsonArray { exhausted, hardDeadline });
            var update = Pipeline(
                new("$set", new BsonDocument("attemptLimit", new BsonDocument("$min", new BsonArray
                { new BsonDocument("$ifNull", new BsonArray { "$attemptLimit", policy.MaximumAttempts }), policy.MaximumAttempts }))),
                new("$set", new BsonDocument
                {
                    { "status", Choose(terminal, "dead", "leased") },
                    { "attempts", Choose(terminal, "$attempts", new BsonDocument("$add", new BsonArray { "$attempts", 1 })) },
                    { "leaseOwner", Choose(terminal, "$$REMOVE", Literal(consumerId)) },
                    { "leaseToken", Choose(terminal, "$$REMOVE", Literal(Guid.NewGuid().ToString("N"))) },
                    { "leaseUntil", Choose(terminal, "$$REMOVE", new BsonDateTime(now.Add(policy.Lease))) },
                    { "claimStartedAt", Choose(terminal, "$$REMOVE", new BsonDateTime(now)) },
                    { "claimDeadline", Choose(terminal, "$$REMOVE", new BsonDateTime(now.Add(policy.MaximumProcessingTime))) },
                    { "deadAt", Choose(terminal, new BsonDateTime(now), "$$REMOVE") },
                    { "lastFailureCode", Choose(terminal, Choose(hardDeadline, "processing_deadline_exceeded", "attempt_limit_reached"), new BsonDocument("$ifNull", new BsonArray { "$lastFailureCode", "$$REMOVE" })) }
                }));
            var result = await durableEvents.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Ascending("dueAt").Ascending("_id"), ReturnDocument = ReturnDocument.After }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null || result["status"] == "leased") return FreshClaim(result, cancellationToken);
        }
        return null;
    }

    public async Task<BsonDocument?> RenewAsync(BsonDocument claim, TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        if (extension < TimeSpan.FromSeconds(1) || extension > TimeSpan.FromHours(1)) throw Invalid("invalid_lease");
        var now = clock.UtcNow.UtcDateTime;
        // Persisted deadline and lease are authoritative; returned claim documents are mutable snapshots.
        var renewed = await durableEvents.FindOneAndUpdateAsync(LiveClaim(claim, now), Pipeline(new BsonDocument("$set", new BsonDocument("leaseUntil",
            new BsonDocument("$min", new BsonArray { "$claimDeadline", new BsonDocument("$max", new BsonArray { "$leaseUntil", now.Add(extension) }) })))),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        return FreshClaim(renewed, cancellationToken);
    }

    public async Task<bool> CompleteAsync(BsonDocument claim, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow.UtcDateTime;
        var result = await durableEvents.UpdateOneAsync(LiveClaim(claim, now), ClearLease(Builders<BsonDocument>.Update
            .Set("status", "completed").Set("completedAt", now)), cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    public Task<bool> RetryAsync(BsonDocument claim, TimeSpan delay, CancellationToken cancellationToken = default) =>
        FailAsync(claim, "processing_retry", true, delay, cancellationToken);

    public async Task<bool> FailAsync(BsonDocument claim, string failureCode, bool retryable, TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (!FailureCodes.Contains(failureCode, StringComparer.Ordinal)) throw Invalid("invalid_failure_code");
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1)) throw Invalid("invalid_retry");
        var now = clock.UtcNow.UtcDateTime;
        var terminal = new BsonDocument("$or", new BsonArray { !retryable, new BsonDocument("$gte", new BsonArray { "$attempts", "$attemptLimit" }) });
        var result = await durableEvents.UpdateOneAsync(LiveClaim(claim, now), Pipeline(
            new("$set", new BsonDocument
            {
                { "status", Choose(terminal, "dead", "retry") },
                { "dueAt", Choose(terminal, "$dueAt", new BsonDateTime(now.Add(delay))) },
                { "deadAt", Choose(terminal, new BsonDateTime(now), "$$REMOVE") },
                { "lastFailureCode", Literal(failureCode) }
            }), new("$unset", new BsonArray { "leaseOwner", "leaseToken", "leaseUntil", "claimStartedAt", "claimDeadline" })), cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    private BsonDocument? FreshClaim(BsonDocument? claim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (claim is null) return null;
        var now = new BsonDateTime(clock.UtcNow.UtcDateTime).MillisecondsSinceEpoch;
        // The write may already be durable. Withhold an expired response, never pretend to undo it.
        return claim.TryGetValue("leaseUntil", out var lease) && lease.IsBsonDateTime && lease.AsBsonDateTime.MillisecondsSinceEpoch > now &&
            claim.TryGetValue("claimDeadline", out var deadline) && deadline.IsBsonDateTime && deadline.AsBsonDateTime.MillisecondsSinceEpoch > now ? claim : null;
    }
    private static FilterDefinition<BsonDocument> LiveClaim(BsonDocument claim, DateTime now)
    {
        if (claim is null || !claim.TryGetValue("_id", out var id) || id.IsBsonNull ||
            !claim.TryGetValue("kind", out var kind) || !kind.IsString ||
            !claim.TryGetValue("leaseOwner", out var owner) || !owner.IsString ||
            !claim.TryGetValue("leaseToken", out var token) || !token.IsString)
            throw Invalid("invalid_claim");
        return F.Eq("_id", id) & F.Eq("kind", kind) & F.Eq("status", "leased") &
            F.Eq("leaseOwner", owner) & F.Eq("leaseToken", token) &
            F.Gt("leaseUntil", now) & F.Gt("claimDeadline", now) &
            F.Type("attemptLimit", BsonType.Int32) & F.Gte("attemptLimit", 1) & F.Lte("attemptLimit", 1000);
    }
    private static UpdateDefinition<BsonDocument> ClearLease(UpdateDefinition<BsonDocument> update) =>
        update.Unset("leaseOwner").Unset("leaseToken").Unset("leaseUntil").Unset("claimStartedAt").Unset("claimDeadline");
    private static BsonDocument Literal(string value) => new("$literal", value);
    private static BsonDocument Choose(BsonValue condition, BsonValue yes, BsonValue no) => new("$cond", new BsonArray { condition, yes, no });
    private static UpdateDefinition<BsonDocument> Pipeline(params BsonDocument[] stages) =>
        new PipelineUpdateDefinition<BsonDocument>(stages);
    private static DomainValidationException Invalid(string code) => new(code, "Outbox consumer input is invalid");
}
