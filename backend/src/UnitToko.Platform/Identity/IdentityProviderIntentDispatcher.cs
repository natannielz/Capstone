using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

public sealed class IdentityProviderIntentDispatcherOptions
{
    public bool Enabled { get; init; }
    public string Issuer { get; init; } = "";
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed record IdentityProviderIntentDispatchResult(string Status, string Code)
{
    public bool CanAutomaticallyRetry => false;
    public bool DeliveryVerified => false;
    public bool EmailVerified => false;
    public bool RecoveryCompleted => false;
}

/// <summary>Owns one password-email attempt on the existing protected intent. Disabled by default;
/// requires the exact migrated validator, current requester session and current owner authority.
/// All stores must be composed over the same Mongo client/database. No token or provider email is stored.
/// A persisted dispatched marker precedes POST; abandoned claims and ambiguous sends are never reclaimed.
/// Provider acceptance is not mailbox delivery or recovery. Auth0's email endpoint is not atomically subject-bound.</summary>
public sealed class IdentityProviderIntentDispatcher
{
    internal delegate Task<Auth0ProviderDeliveryResult> DeliveryAttempt(BsonDocument original,
        Func<CancellationToken, Task<bool>> current, Func<CancellationToken, Task<bool>> beforeMessage, CancellationToken ct);
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly AuthorizationStore authority;
    private readonly MongoIdentitySessionStore sessions;
    private readonly SessionPayloadProtector protector;
    private readonly IClock clock;
    private readonly IdentityProviderIntentDispatcherOptions options;
    private readonly DeliveryAttempt send;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Intents => database.GetCollection<BsonDocument>("identityProviderIntents");

    public IdentityProviderIntentDispatcher(IMongoDatabase database, MongoUnitOfWork unit, AuthorizationStore authority,
        MongoIdentitySessionStore sessions, SessionPayloadProtector protector, IClock clock,
        IdentityProviderIntentDispatcherOptions options, Auth0ProviderDelivery delivery)
        : this(database, unit, authority, sessions, protector, clock, options, delivery.DeliverClaimedAsync) { }

    // Fake transport seam only; production cannot supply a boolean in place of persisted/session authority.
    internal IdentityProviderIntentDispatcher(IMongoDatabase database, MongoUnitOfWork unit, AuthorizationStore authority,
        MongoIdentitySessionStore sessions, SessionPayloadProtector protector, IClock clock,
        IdentityProviderIntentDispatcherOptions options, DeliveryAttempt send)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(authority); ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(protector); ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(send);
        if (options.Enabled && (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != "https" || issuer.UserInfo.Length != 0 || issuer.AbsolutePath != "/" ||
            issuer.Query.Length != 0 || issuer.Fragment.Length != 0 || !issuer.IsDefaultPort || issuer.AbsoluteUri != options.Issuer ||
            options.LeaseDuration < TimeSpan.FromSeconds(30) || options.LeaseDuration > TimeSpan.FromMinutes(2)))
            throw new ArgumentException("Provider dispatch requires reviewed issuer and bounded lease settings");
        this.database = database; this.unit = unit; this.authority = authority; this.sessions = sessions;
        this.protector = protector; this.clock = clock; this.options = options; this.send = send;
    }

    public async Task<IdentityProviderIntentDispatchResult> DispatchAsync(long intentId, string requesterSessionToken,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return Blocked("delivery_disabled");
        if (intentId <= 0) return Blocked("invalid_intent");
        if (!await StorageReadyAsync(cancellationToken)) return Blocked("storage_not_ready");
        BsonDocument? original = null; BsonDocument? claim = null; Binding? binding = null; ActorContext? actor = null;
        var claimId = Guid.NewGuid().ToString("N");
        try {
            var validated = await sessions.ReadAsync(requesterSessionToken, cancellationToken);
            if (validated is null) return Blocked("authority_changed");
            actor = new(validated.UserId, validated.Proof.Issuer, validated.Proof.Subject, validated.GrantVersion,
                validated.Proof.FactorCompletedAt, FactorAssurance.MultiFactor, validated.Reference);
            var previous = await unit.ExecuteAsync(async (tx, ct) => {
                var row = await Intents.Find(tx, F.Eq("_id", intentId)).FirstOrDefaultAsync(ct)
                    ?? throw new AuthorizationDeniedException("intent_unavailable");
                binding = ReadBinding(row);
                await RequireAuthorityAsync(row, binding, actor, tx, ct);
                var status = row["status"].AsString;
                if (status != "blocked") return Existing(row, clock.UtcNow);
                if (row.Contains("delivery") || !Long(row, "version", out var version) || version != 1)
                    throw new AuthorizationDeniedException("invalid_intent");
                original = row.DeepClone().AsBsonDocument;
                var claimed = new BsonDocument {
                    { "claimId", claimId }, { "bindingHash", Hash(row) }, { "actorSessionId", actor.Session!.Id },
                    { "actorSessionVersion", actor.Session.Version }, { "actorSecurityVersion", actor.Session.SecurityVersion },
                    { "actorGrantVersion", actor.GrantVersion }, { "targetSecurityVersion", binding.TargetEpoch },
                    { "claimedAt", clock.UtcNow.UtcDateTime }, { "leaseUntil", clock.UtcNow.Add(options.LeaseDuration).UtcDateTime },
                    { "dispatchedAt", BsonNull.Value }, { "completedAt", BsonNull.Value }, { "outcomeCode", BsonNull.Value }
                };
                await ChangeAsync(row, tx, Builders<BsonDocument>.Update.Set("status", "claimed").Set("delivery", claimed).Inc("version", 1L), ct);
                claim = claimed.DeepClone().AsBsonDocument;
                RequireTime(actor, claimed);
                return (IdentityProviderIntentDispatchResult?)null;
            }, cancellationToken);
            if (previous is not null) return previous;
        } catch (Exception error) when (Unavailable(error)) { return Blocked("authority_changed"); }

        // Each callback re-reads and fences the exact claim and its current authorization in a transaction.
        // The irreversible marker must commit successfully before the transport can start one POST.
        async Task<bool> Check(CancellationToken ct, bool mark) {
            try {
                return await unit.ExecuteAsync(async (tx, token) => {
                    var row = await Intents.Find(tx, F.Eq("_id", intentId)).FirstOrDefaultAsync(token);
                    if (row is null || !Owns(row, original!, claim!, actor!, "claimed")) return false;
                    await RequireAuthorityAsync(row, binding!, actor!, tx, token);
                    RequireTime(actor!, row["delivery"].AsBsonDocument);
                    if (mark) await ChangeAsync(row, tx, Builders<BsonDocument>.Update.Set("status", "dispatched")
                        .Set("delivery.dispatchedAt", clock.UtcNow.UtcDateTime).Inc("version", 1L), token);
                    RequireTime(actor!, row["delivery"].AsBsonDocument);
                    return true;
                }, ct);
            } catch (Exception error) when (Unavailable(error)) { return false; }
        }
        Auth0ProviderDeliveryResult result;
        try {
            // External work is deliberately outside MongoUnitOfWork's retryable transaction callback.
            result = await send(original!, ct => Check(ct, false), ct => Check(ct, true), cancellationToken);
        } catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) {
            return Unknown();
        }
        try {
            return await unit.ExecuteAsync(async (tx, ct) => {
                var row = await Intents.Find(tx, F.Eq("_id", intentId)).FirstOrDefaultAsync(ct);
                var expected = result.MessageRequestStarted ? "dispatched" : "claimed";
                if (row is null || !Owns(row, original!, claim!, actor!, expected)) return Unknown();
                await RequireAuthorityAsync(row, binding!, actor!, tx, ct);
                RequireTime(actor!, row["delivery"].AsBsonDocument);
                var status = result.State switch {
                    Auth0ProviderDeliveryState.ProviderAccepted when result.MessageRequestStarted => "providerAccepted",
                    Auth0ProviderDeliveryState.ProviderRejected when result.MessageRequestStarted => "providerRejected",
                    Auth0ProviderDeliveryState.Blocked when !result.MessageRequestStarted => "notSent",
                    _ => "outcomeUnknown"
                };
                // Persist bounded codes only, never provider bodies/URLs/tickets/emails or exception text.
                var code = status switch { "providerAccepted" => "provider_request_accepted", "providerRejected" => "provider_request_rejected",
                    "notSent" => "provider_request_not_sent", _ => "provider_outcome_unknown" };
                await ChangeAsync(row, tx, Builders<BsonDocument>.Update.Set("status", status).Set("delivery.outcomeCode", code)
                    .Set("delivery.completedAt", clock.UtcNow.UtcDateTime).Inc("version", 1L), ct);
                RequireTime(actor!, row["delivery"].AsBsonDocument);
                return new IdentityProviderIntentDispatchResult(status, code);
            }, cancellationToken);
        } catch (Exception error) when (Unavailable(error)) { return Unknown(); }
    }

    private sealed record Binding(string Issuer, string Subject, long TargetId, long TargetEpoch, long ActorId,
        long ActorGrant, long ActorEpoch);

    private Binding ReadBinding(BsonDocument row)
    {
        if (row.GetValue("kind", "") != "passwordRecovery" || !Long(row, "_id", out _) ||
            !Long(row, "userId", out var target) || !Long(row, "securityVersion", out var targetEpoch) ||
            !Long(row, "requestedBy", out var requester) || !row.GetValue("invitationId", "").IsBsonNull ||
            !row.GetValue("approvedBy", "").IsBsonNull || !row.TryGetValue("protectedPayload", out var encrypted) ||
            !encrypted.IsString || encrypted.AsString.Length is < 1 or > 262144)
            throw new AuthorizationDeniedException("unsupported_intent");
        var payload = BsonDocument.Parse(protector.Unprotect("account-recovery", encrypted.AsString));
        string[] names = ["issuer", "subject", "kind", "actorGrantVersion", "actorSecurityVersion"];
        if (payload.ElementCount != 5 || payload.Names.Distinct(StringComparer.Ordinal).Count() != 5 ||
            payload.Names.Any(name => !names.Contains(name, StringComparer.Ordinal)) || payload.GetValue("kind", "") != "password" ||
            payload.GetValue("issuer", "") != options.Issuer || !payload.TryGetValue("subject", out var subject) ||
            !subject.IsString || subject.AsString.Length is < 1 or > 256 ||
            !PayloadLong(payload, "actorGrantVersion", out var grant) || !PayloadLong(payload, "actorSecurityVersion", out var epoch))
            throw new AuthorizationDeniedException("invalid_intent_binding");
        // The real producer records the self actor BEFORE incrementing/revoking its epoch.
        if (target == requester && (epoch == long.MaxValue || epoch + 1 != targetEpoch))
            throw new AuthorizationDeniedException("invalid_self_recovery_epoch");
        return new(options.Issuer, subject.AsString, target, targetEpoch, requester, grant, target == requester ? targetEpoch : epoch);
    }

    private async Task RequireAuthorityAsync(BsonDocument row, Binding binding, ActorContext actor, IClientSessionHandle tx, CancellationToken ct)
    {
        if (actor.UserId != binding.ActorId || actor.GrantVersion != binding.ActorGrant || actor.Session?.SecurityVersion != binding.ActorEpoch ||
            actor.ProviderIssuer != binding.Issuer) throw new AuthorizationDeniedException("authority_changed");
        RequireTime(actor);
        var snapshot = await authority.LoadAsync(actor, tx, ct);
        var nodes = Array.Empty<ScopeNode>();
        if (binding.TargetId != actor.UserId) {
            var query = ScopePolicy.Compile(actor, snapshot, Permissions.RecoveryInitiate, clock.UtcNow);
            if (!query.Matches(new(IsAdministrative: true))) throw new AuthorizationDeniedException("permission_denied");
            nodes = query.Clauses.Where(clause => clause.Matches(new(IsAdministrative: true)))
                .SelectMany(clause => clause.RequiredNodes).Distinct().ToArray();
        }
        var identities = database.GetCollection<BsonDocument>("identities");
        var target = F.Eq("_id", binding.TargetId) & F.Eq("issuer", binding.Issuer) & F.Eq("subject", binding.Subject) &
            F.Eq("securityVersion", binding.TargetEpoch) & F.Eq("status", "active");
        if (await identities.Find(tx, target).FirstOrDefaultAsync(ct) is not { } identity ||
            !Long(identity, "securityVersion", out _) || !Long(row, "version", out _))
            throw new AuthorizationDeniedException("target_changed");
        await authority.FenceAsync(actor, nodes, tx, ct);
        var changed = await identities.UpdateOneAsync(tx, target, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct);
        if (changed.ModifiedCount != 1) throw new AuthorizationDeniedException("target_changed");
        RequireTime(actor);
    }

    private void RequireTime(ActorContext actor, BsonDocument? claim = null)
    {
        if (!AssurancePolicy.Satisfies(actor, new(true, TimeSpan.FromMinutes(5)), clock.UtcNow) ||
            claim is not null && (!claim["leaseUntil"].IsBsonDateTime || claim["leaseUntil"].ToUniversalTime() <= clock.UtcNow.UtcDateTime))
            throw new AuthorizationDeniedException("fresh_factor_or_lease_required");
    }

    private static bool Owns(BsonDocument row, BsonDocument original, BsonDocument claimed, ActorContext actor, string status)
    {
        if (row.GetValue("status", "") != status || !row.TryGetValue("delivery", out var value) || !value.IsBsonDocument) return false;
        var delivery = value.AsBsonDocument;
        return Long(row, "version", out var version) && version == (status == "claimed" ? 2L : 3L) &&
            claimed.Where(field => field.Name is not "dispatchedAt" and not "completedAt" and not "outcomeCode")
                .All(field => delivery.TryGetValue(field.Name, out var stored) && stored.Equals(field.Value)) &&
            delivery.GetValue("bindingHash", "") == Hash(original) && Hash(row) == Hash(original) &&
            delivery.GetValue("actorSessionId", "") == actor.Session!.Id && delivery.GetValue("actorSessionVersion", 0) == actor.Session.Version &&
            delivery.GetValue("actorSecurityVersion", 0) == actor.Session.SecurityVersion && delivery.GetValue("actorGrantVersion", 0) == actor.GrantVersion;
    }

    private async Task ChangeAsync(BsonDocument row, IClientSessionHandle tx, UpdateDefinition<BsonDocument> update, CancellationToken ct)
    {
        var filter = F.Eq("_id", row["_id"]) & F.Eq("version", row["version"]) & F.Eq("status", row["status"]);
        // Match all original immutable fields, rather than trusting ID/version alone or caller selection.
        foreach (var field in row.Where(field => field.Name is not "_id" and not "version" and not "status" and not "delivery"))
            filter &= F.Eq(field.Name, field.Value);
        if (row.TryGetValue("delivery", out var delivery)) filter &= F.Eq("delivery", delivery);
        if ((await Intents.UpdateOneAsync(tx, filter, update, cancellationToken: ct)).ModifiedCount != 1)
            throw new PersistenceConflictException("intent_changed", "The provider intent changed");
    }

    private static string Hash(BsonDocument row) => Convert.ToHexStringLower(SHA256.HashData(new BsonDocument(
        row.Where(field => field.Name is not "status" and not "version" and not "delivery").OrderBy(field => field.Name, StringComparer.Ordinal)).ToBson()));
    private static bool Long(BsonDocument row, string field, out long number) {
        number = 0; if (!row.TryGetValue(field, out var value) || !value.IsInt64) return false;
        number = value.AsInt64; return number > 0;
    }
    private static bool PayloadLong(BsonDocument row, string field, out long number) {
        number = 0; if (!row.TryGetValue(field, out var value) || !value.IsInt32 && !value.IsInt64) return false;
        number = value.ToInt64(); return number > 0;
    }
    private static IdentityProviderIntentDispatchResult Blocked(string code) => new("blocked", code);
    private static IdentityProviderIntentDispatchResult Unknown() => new("outcomeUnknown", "provider_outcome_unknown");
    private static IdentityProviderIntentDispatchResult Existing(BsonDocument row, DateTimeOffset now) => row["status"].AsString switch {
        "providerAccepted" => new("providerAccepted", "provider_request_accepted"),
        "providerRejected" => new("providerRejected", "provider_request_rejected"),
        "notSent" => new("notSent", "provider_request_not_sent"),
        "claimed" or "dispatched" when row["delivery"]["leaseUntil"].ToUniversalTime() > now.UtcDateTime => new(row["status"].AsString, "attempt_in_progress"),
        _ => Unknown()
    };
    private static bool Unavailable(Exception error) => error is AuthorizationDeniedException or PersistenceConflictException or
        UncertainCommitException or MongoException or CryptographicException or FormatException or InvalidOperationException or
        ArgumentException or OperationCanceledException;

    private async Task<bool> StorageReadyAsync(CancellationToken ct)
    {
        try {
            using var cursor = await database.ListCollectionsAsync(new() { Filter = new BsonDocument("name", "identityProviderIntents") }, ct);
            var rows = await cursor.ToListAsync(ct);
            if (rows.Count != 1 || !rows[0].TryGetValue("options", out var raw) || !raw.IsBsonDocument) return false;
            var stored = raw.AsBsonDocument;
            var required = RequiredValidator();
            return AccountContactStorage.SafeDefinition(rows[0], required) &&
                stored.TryGetValue("validator", out var validator) && validator.Equals(required);
        } catch (Exception error) when (Unavailable(error) || error is InvalidCastException) { return false; }
    }

    /// <summary>Exact migration contract, not installed here. Original blocked producer rows remain valid.
    /// Integrators must migrate this existing collection before registering/enabling the dispatcher.</summary>
    internal static BsonDocument RequiredValidator()
    {
        var validator = IdentitySchemas.Collections()["identityProviderIntents"].DeepClone().AsBsonDocument;
        var schema = validator["$jsonSchema"].AsBsonDocument; var properties = schema["properties"].AsBsonDocument;
        string[] active = ["claimed", "dispatched", "providerAccepted", "providerRejected", "notSent", "outcomeUnknown"];
        properties["status"]["enum"] = new BsonArray(new[] { "blocked" }.Concat(active));
        var fields = new BsonDocument();
        foreach (var field in new[] { "actorSessionVersion", "actorSecurityVersion", "actorGrantVersion", "targetSecurityVersion" })
            fields.Add(field, new BsonDocument { { "bsonType", "long" }, { "minimum", 1L } });
        foreach (var field in new[] { "claimId", "bindingHash", "actorSessionId" })
            fields.Add(field, new BsonDocument { { "bsonType", "string" }, { "pattern", field == "claimId" ? "^[a-f0-9]{32}\\z" : "^[a-f0-9]{64}\\z" } });
        foreach (var field in new[] { "claimedAt", "leaseUntil" }) fields.Add(field, new BsonDocument("bsonType", "date"));
        foreach (var field in new[] { "dispatchedAt", "completedAt" }) fields.Add(field, new BsonDocument("bsonType", new BsonArray { "date", "null" }));
        fields.Add("outcomeCode", new BsonDocument { { "bsonType", new BsonArray { "string", "null" } },
            { "enum", new BsonArray { BsonNull.Value, "provider_request_accepted", "provider_request_rejected", "provider_request_not_sent", "provider_outcome_unknown" } } });
        properties.Add("delivery", new BsonDocument { { "bsonType", "object" }, { "additionalProperties", false },
            { "required", new BsonArray(fields.Names) }, { "properties", fields } });
        schema.Add("oneOf", new BsonArray {
            new BsonDocument { { "properties", new BsonDocument { { "status", new BsonDocument("enum", new BsonArray { "blocked" }) },
                { "version", new BsonDocument("enum", new BsonArray { 1L }) } } }, { "not", new BsonDocument("required", new BsonArray { "delivery" }) } },
            new BsonDocument { { "required", new BsonArray { "delivery" } }, { "properties", new BsonDocument {
                { "kind", new BsonDocument("enum", new BsonArray { "passwordRecovery" }) }, { "status", new BsonDocument("enum", new BsonArray(active)) } } } }
        });
        return validator;
    }
}
