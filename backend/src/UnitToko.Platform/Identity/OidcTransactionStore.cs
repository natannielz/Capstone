using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

public enum OidcTransactionPurpose { Login, StepUp }
public sealed record OidcTransactionRequest(OidcTransactionPurpose Purpose, string Issuer, string ClientId, string ReturnPath,
    long? ExpectedUserId = null, SessionReference? ExpectedSession = null, long? ExpectedGrantVersion = null,
    string? ExpectedSubject = null, string? Operation = null, long? InvitationId = null, string? InvitationSecretHash = null);
public sealed record OidcTransaction(string Id, long Version, OidcTransactionRequest Request, string ChallengeHash,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed class OidcTransactionStart(string stateToken, string challengeBinding, OidcTransaction transaction)
{
    public string StateToken { get; } = stateToken;
    public string ChallengeBinding { get; } = challengeBinding;
    public OidcTransaction Transaction { get; } = transaction;
    public override string ToString() => "OidcTransactionStart [redacted]";
}

/// <summary>One-use durable OIDC state. Consume inside the admission/session transaction;
/// expiration is checked explicitly, independently of asynchronous TTL cleanup.</summary>
public sealed class OidcTransactionStore(IMongoDatabase database, IClock clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IMongoCollection<BsonDocument> transactions = database.GetCollection<BsonDocument>("oidcTransactions");
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public async Task<OidcTransactionStart> BeginAsync(OidcTransactionRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var stateToken = RandomToken(); var challenge = RandomToken(); var now = clock.UtcNow;
        var transaction = new OidcTransaction(Hash(stateToken), 1, request, Hash(challenge), now, now + EffectiveLifetime(request.Purpose));
        await transactions.WithWriteConcern(WriteConcern.WMajority).InsertOneAsync(Document(transaction), cancellationToken: cancellationToken);
        return new(stateToken, challenge, transaction);
    }
    public async Task<OidcTransaction> ReadPendingAsync(string stateToken, CancellationToken cancellationToken = default)
    {
        if (!IsToken(stateToken)) throw new AuthorizationDeniedException("oidc_state_unavailable");
        var value = await transactions.Find(Pending(Hash(stateToken))).FirstOrDefaultAsync(cancellationToken)
            ?? throw new AuthorizationDeniedException("oidc_state_unavailable");
        return Parse(value);
    }
    public async Task<OidcTransaction> ReadPendingAsync(string stateToken, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        if (!IsToken(stateToken)) throw new AuthorizationDeniedException("oidc_state_unavailable");
        var value = await transactions.Find(session, Pending(Hash(stateToken))).FirstOrDefaultAsync(cancellationToken)
            ?? throw new AuthorizationDeniedException("oidc_state_unavailable");
        return Parse(value);
    }
    public async Task ConsumeAsync(OidcTransaction transaction, IClientSessionHandle session, CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction || !IsHash(transaction.Id) || transaction.Version != 1)
            throw new AuthorizationDeniedException("oidc_state_unavailable");
        var result = await transactions.UpdateOneAsync(session, Pending(transaction.Id) & F.Eq("version", transaction.Version),
            Builders<BsonDocument>.Update.Set("status", "consumed").Set("consumedAt", clock.UtcNow.UtcDateTime).Inc("version", 1L), cancellationToken: cancellationToken);
        if (result.ModifiedCount != 1) throw new PersistenceConflictException("oidc_state_unavailable", "Sign-in state has expired or was already used");
    }
    public static bool MatchesChallenge(OidcTransaction transaction, string? challenge) => IsToken(challenge) && IsHash(transaction.ChallengeHash) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(transaction.ChallengeHash), SHA256.HashData(Encoding.UTF8.GetBytes(challenge!)));
    public static bool IsToken(string? value) => value is { Length: 43 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static TimeSpan EffectiveLifetime(OidcTransactionPurpose purpose) => purpose == OidcTransactionPurpose.StepUp ? SessionPolicy.StepUpLifetime : Lifetime;
    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private FilterDefinition<BsonDocument> Pending(string id) => F.Eq("_id", id) & F.Eq("status", "pending") & F.Gt("expiresAt", clock.UtcNow.UtcDateTime) & F.Lte("createdAt", clock.UtcNow.UtcDateTime);
    private static void Validate(OidcTransactionRequest request)
    {
        if (!Enum.IsDefined(request.Purpose) || !Uri.TryCreate(request.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != "https" ||
            request.Issuer.Length > 500 || string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 200 ||
            request.ReturnPath.Length is < 1 or > 200 || !request.ReturnPath.StartsWith('/') || request.ReturnPath.Contains("//", StringComparison.Ordinal) ||
            request.ReturnPath.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '\\' or '%' or '?' or '#' or ':') || request.ReturnPath.Split('/').Any(segment => segment is "." or ".."))
            throw new DomainValidationException("invalid_oidc_transaction", "A reviewed issuer, client and return path are required");
        if (request.Purpose == OidcTransactionPurpose.StepUp)
        {
            if (request.ExpectedUserId is not > 0 || request.ExpectedSession is not { Version: > 0, SecurityVersion: > 0 } reference || !IsHash(reference.Id) ||
                request.ExpectedGrantVersion is not > 0 || string.IsNullOrWhiteSpace(request.ExpectedSubject) || request.ExpectedSubject.Length > 200 ||
                request.Operation is null || Permissions.Find(request.Operation) is null || request.InvitationId is not null || request.InvitationSecretHash is not null)
                throw new DomainValidationException("invalid_oidc_transaction", "Step-up must bind the current session, identity, versions and operation");
        }
        else if (request.ExpectedUserId is not null || request.ExpectedSession is not null || request.ExpectedGrantVersion is not null ||
            request.ExpectedSubject is not null || request.Operation is not null ||
            (request.InvitationId is null ? request.InvitationSecretHash is not null : request.InvitationId <= 0 || !IsHash(request.InvitationSecretHash)))
            throw new DomainValidationException("invalid_oidc_transaction", "Login invitation and identity references are invalid");
    }
    private static BsonValue Number(long? value) => value is { } number ? new BsonInt64(number) : BsonNull.Value;
    private static BsonValue Text(string? value) => value is null ? BsonNull.Value : new BsonString(value);
    private static BsonDocument Document(OidcTransaction transaction)
    {
        var request = transaction.Request;
        return new()
        {
            { "_id", transaction.Id }, { "schemaVersion", 1 }, { "purpose", request.Purpose == OidcTransactionPurpose.Login ? "login" : "stepUp" },
            { "status", "pending" }, { "issuer", request.Issuer }, { "clientId", request.ClientId }, { "challengeHash", transaction.ChallengeHash },
            { "returnPath", request.ReturnPath }, { "createdAt", transaction.CreatedAt.UtcDateTime }, { "expiresAt", transaction.ExpiresAt.UtcDateTime }, { "version", 1L },
            { "expectedUserId", Number(request.ExpectedUserId) }, { "expectedSessionHash", Text(request.ExpectedSession?.Id) },
            { "expectedSessionVersion", Number(request.ExpectedSession?.Version) }, { "expectedSecurityVersion", Number(request.ExpectedSession?.SecurityVersion) },
            { "expectedGrantVersion", Number(request.ExpectedGrantVersion) }, { "expectedSubject", Text(request.ExpectedSubject) }, { "operation", Text(request.Operation) },
            { "invitationId", Number(request.InvitationId) }, { "invitationSecretHash", Text(request.InvitationSecretHash) }
        };
    }
    private static OidcTransaction Parse(BsonDocument value)
    {
        long? NumberAt(string field) => value[field].IsBsonNull ? null : value[field].AsInt64;
        string? TextAt(string field) => value[field].IsBsonNull ? null : value[field].AsString;
        var request = new OidcTransactionRequest(value["purpose"].AsString switch { "login" => OidcTransactionPurpose.Login, "stepUp" => OidcTransactionPurpose.StepUp, _ => throw new AuthorizationDeniedException("invalid_oidc_state") },
            value["issuer"].AsString, value["clientId"].AsString, value["returnPath"].AsString, NumberAt("expectedUserId"),
            TextAt("expectedSessionHash") is { } hash ? new(hash, NumberAt("expectedSessionVersion") ?? 0, NumberAt("expectedSecurityVersion") ?? 0) : null,
            NumberAt("expectedGrantVersion"), TextAt("expectedSubject"), TextAt("operation"), NumberAt("invitationId"), TextAt("invitationSecretHash"));
        Validate(request);
        var transaction = new OidcTransaction(value["_id"].AsString, value["version"].AsInt64, request, value["challengeHash"].AsString,
            new(value["createdAt"].ToUniversalTime(), TimeSpan.Zero), new(value["expiresAt"].ToUniversalTime(), TimeSpan.Zero));
        if (!IsHash(transaction.Id) || !IsHash(transaction.ChallengeHash) || transaction.Version != 1 ||
            transaction.ExpiresAt <= transaction.CreatedAt || transaction.ExpiresAt - transaction.CreatedAt > EffectiveLifetime(request.Purpose))
            throw new AuthorizationDeniedException("invalid_oidc_state");
        return transaction;
    }
}
