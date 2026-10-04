using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

/// <summary>Self-declared personal contact details, separate from provider identity and business authority.</summary>
public sealed class AccountContactService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids,
    AuthorizationStore authority, IClock clock)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);

    public Task<AccountContactCurrent> CurrentAsync(ActorContext actor, CancellationToken ct = default) =>
        WithOwnAccountAsync<AccountContactCurrent>(actor, async (tx, token) =>
        {
            var (head, _) = await AccountContactRecords.ReadAsync(database, tx, actor.UserId, token);
            return head is null ? new(actor.UserId, 0, null, null, null, null, null) :
                new AccountContactCurrent(actor.UserId, head["revision"].AsInt64, head["name"].AsString,
                    Optional(head, "phone"), Optional(head, "position"), Optional(head, "address"),
                    new DateTimeOffset(head["updatedAt"].ToUniversalTime()));
        }, ct);

    public Task<AccountContactHistoryPage> HistoryAsync(ActorContext actor, long? beforeHistoryId = null,
        int limit = 20, CancellationToken ct = default)
    {
        if (beforeHistoryId is <= 0 or >= long.MaxValue - 1 || limit is < 1 or > 100)
            throw new DomainValidationException("invalid_account_contact_page", "Select a supported history page");
        return WithOwnAccountAsync(actor, async (tx, token) =>
        {
            var (_, history) = await AccountContactRecords.ReadAsync(database, tx, actor.UserId, token);
            var selected = history.Where(row => beforeHistoryId is null || row["_id"].AsInt64 < beforeHistoryId)
                .OrderByDescending(row => row["revision"].AsInt64).Take(limit + 1).ToArray();
            var items = selected.Take(limit).Select(row => new AccountContactHistoryItem(row["_id"].AsInt64,
                row["revision"].AsInt64, AccountContactRecords.Contact(row),
                new DateTimeOffset(row["recordedAt"].ToUniversalTime()), row["reason"].AsString)).ToArray();
            return new AccountContactHistoryPage(items, selected.Length > limit ? items[^1].HistoryId : null);
        }, ct);
    }

    public Task<AccountContactResult?> LookupAsync(ActorContext actor, string key, CancellationToken ct = default)
    {
        Text(key, 200);
        return WithOwnAccountAsync<AccountContactResult?>(actor, async (tx, token) =>
        {
            var (_, history) = await AccountContactRecords.ReadAsync(database, tx, actor.UserId, token);
            var original = history.SingleOrDefault(row => row["requestKey"] == key);
            return original is null ? null : Result(original);
        }, ct);
    }

    public Task<AccountContactResult> SaveAsync(ActorContext actor, AccountContactInput input, long expectedRevision,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(metadata);
        input = input.Validate(); Text(metadata.IdempotencyKey, 200); Text(metadata.Reason, 1000); Text(metadata.CorrelationId, 128);
        if (expectedRevision is < 0 or > 256)
            throw new DomainValidationException("invalid_account_contact_revision", "Use the current contact revision");
        var bodyHash = AccountContactRecords.BodyHash(input, expectedRevision, metadata.Reason);
        return WithOwnAccountAsync(actor, async (tx, token) =>
        {
            var (current, history) = await AccountContactRecords.ReadAsync(database, tx, actor.UserId, token);
            var original = history.SingleOrDefault(row => row["requestKey"] == metadata.IdempotencyKey);
            if (original is not null)
            {
                if (original["bodyHash"] != bodyHash)
                    throw new PersistenceConflictException("idempotency_key_reused", "The original key belongs to another contact change");
                return Result(original);
            }
            var revision = current?["revision"].AsInt64 ?? 0L;
            if (revision != expectedRevision)
                throw new PersistenceConflictException("account_contact_changed", "Reload contact details before saving");
            if (revision >= 256)
                throw new PersistenceConflictException("account_contact_history_full", "Contact history requires operator review");
            if (current is not null && current["fence"].AsInt64 >= long.MaxValue - 2) throw AccountContactRecords.Broken();
            var historyId = (await ids.NextAsync(AccountContactRecords.History, token)).Value;
            if (historyId >= long.MaxValue - 1)
                throw new PersistenceConflictException("id_exhausted", "Contact history sequence requires operator review");
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            var row = new BsonDocument {
                { "_id", historyId }, { "schemaVersion", 1 }, { "userId", actor.UserId }, { "revision", revision + 1 },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "requestKey", metadata.IdempotencyKey }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                { "recordedAt", now.UtcDateTime }, { "previousHistoryId", current is null ? BsonNull.Value : current["lastHistoryId"] },
                { "previousHash", current is null ? BsonNull.Value : current["lastHash"] }, { "bodyHash", bodyHash },
                { "name", input.Name }, { "phone", Value(input.Phone) }, { "position", Value(input.Position) }, { "address", Value(input.Address) }
            };
            row["snapshotHash"] = AccountContactRecords.Hash(row, "snapshotHash");
            await Rows(AccountContactRecords.History).InsertOneAsync(tx, row, cancellationToken: token);
            await Rows(AccountContactRecords.Receipts).InsertOneAsync(tx, new BsonDocument {
                { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", actor.UserId },
                { "key", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "historyId", historyId },
                { "historyHash", row["snapshotHash"] }, { "recordedAt", now.UtcDateTime }
            }, cancellationToken: token);
            var head = new BsonDocument {
                { "_id", actor.UserId }, { "schemaVersion", 1 }, { "revision", revision + 1 },
                { "name", input.Name }, { "phone", Value(input.Phone) }, { "position", Value(input.Position) }, { "address", Value(input.Address) },
                { "lastHistoryId", historyId }, { "lastHash", row["snapshotHash"] }, { "updatedAt", now.UtcDateTime },
                { "fence", current is null ? 0L : checked(current["fence"].AsInt64 + 1) }
            };
            if (current is null) await Rows(AccountContactRecords.Heads).InsertOneAsync(tx, head, cancellationToken: token);
            else if ((await Rows(AccountContactRecords.Heads).ReplaceOneAsync(tx, current, head, cancellationToken: token)).ModifiedCount != 1)
                throw new PersistenceConflictException("account_contact_changed", "Reload contact details before saving");
            return Result(row);
        }, ct);
    }

    private async Task<T> WithOwnAccountAsync<T>(ActorContext actor,
        Func<IClientSessionHandle, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await AccountContactStorage.RequireAsync(database, ct);
        return await unit.ExecuteAsync(async (tx, token) =>
        {
            var snapshot = await authority.LoadAsync(actor, tx, token);
            var clauses = OwnAuthority(actor, snapshot);
            await authority.FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), tx, token);
            var result = await action(tx, token);
            // A suspended branch, revoked account/session or expired membership cannot receive a contact receipt.
            clauses = OwnAuthority(actor, snapshot);
            await authority.FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), tx, token);
            _ = OwnAuthority(actor, snapshot);
            return result;
        }, ct);
    }

    private ImmutableArray<ScopeClause> OwnAuthority(ActorContext actor, AuthorizationSnapshot snapshot)
    {
        if (!AssurancePolicy.Satisfies(actor, new(true), clock.UtcNow)) throw new AuthorizationDeniedException("mfa_required");
        var clauses = snapshot.Grants.SelectMany(grant => grant.Permissions.IsDefault ? [] : grant.Permissions)
            .Distinct(StringComparer.Ordinal).SelectMany(permission =>
                ScopePolicy.CompileParticipantGrants(actor, snapshot, permission, clock.UtcNow).Clauses).Distinct().ToImmutableArray();
        if (clauses.IsDefaultOrEmpty) throw new AuthorizationDeniedException("account_scope_unavailable");
        return clauses;
    }

    internal static string Text(string? text, int maximum)
    {
        if (text is null || text.Length is < 1 or > 2000 || text != text.Trim()) throw InvalidText();
        var remaining = text.AsSpan(); var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Surrogate)
                throw InvalidText();
            remaining = remaining[consumed..]; count++;
        }
        if (count > maximum || !text.IsNormalized(NormalizationForm.FormC)) throw InvalidText();
        return text;
    }
    private static DomainValidationException InvalidText() => new("invalid_account_contact_metadata", "Use canonical bounded contact command metadata");
    private static BsonValue Value(string? value) => value is null ? BsonNull.Value : new BsonString(value);
    private static string? Optional(BsonDocument row, string name) => row[name].IsBsonNull ? null : row[name].AsString;
    private static AccountContactResult Result(BsonDocument row) => new(row["userId"].AsInt64, row["revision"].AsInt64,
        row["_id"].AsInt64, new DateTimeOffset(row["recordedAt"].ToUniversalTime()));
}
