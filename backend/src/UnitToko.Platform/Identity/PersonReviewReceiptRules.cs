using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

// One discriminator for the original receipt key. A marker alone never proves a permanent closure.
internal static class PersonReviewReceiptRules
{
    internal static readonly string ClosureHash = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("UnitToko/person-review/closed-no-effect/v1"))).ToLowerInvariant();

    internal static DateTimeOffset? ClosedAt(BsonDocument row, long actorId, string operation, string key, DateTimeOffset now)
    {
        if (!row.TryGetValue("_id", out var id) || !id.IsObjectId || id.AsObjectId == ObjectId.Empty ||
            !Integer(row, "schemaVersion", 1) || !PositiveLong(row, "actorId") || row["actorId"].AsInt64 != actorId ||
            !Text(row, "operation", 128) || row["operation"].AsString != operation ||
            !Text(row, "key", 200) || row["key"].AsString != key || !Hash(row, "bodyHash") ||
            !Date(row, "createdAt", now, out var at) || !row.TryGetValue("result", out var result) || !result.IsBsonDocument)
            throw Invalid();
        var closed = row.Contains("personReviewClosure") || row["bodyHash"].AsString == ClosureHash || result.AsBsonDocument.Contains("receiptState");
        if (!closed)
        {
            if (!Fields(row, "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result", "personWithdrawal") ||
                !row["personWithdrawal"].IsBsonDocument) throw Invalid();
            return null; // Existing immutable proposal/history validation remains mandatory for committed rows.
        }
        if (!Fields(row, "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result", "personReviewClosure") ||
            row["bodyHash"].AsString != ClosureHash || !Fields(result.AsBsonDocument, "receiptState") ||
            result["receiptState"] != "closed_no_effect" || !row["personReviewClosure"].IsBsonDocument) throw Invalid();
        var closure = row["personReviewClosure"].AsBsonDocument;
        if (!Fields(closure, "schemaVersion", "actorGrantVersion", "actorSecurityVersion", "policy", "reason", "correlationId") ||
            !Integer(closure, "schemaVersion", 1) || !PositiveLong(closure, "actorGrantVersion") || !PositiveLong(closure, "actorSecurityVersion") ||
            !Text(closure, "reason", 1000, trimmed: true) || !Text(closure, "correlationId", 128) || !closure["policy"].IsBsonDocument) throw Invalid();
        var policy = closure["policy"].AsBsonDocument;
        if (!Fields(policy, "id", "version", "digest") || !PositiveLong(policy, "id") || !PositiveLong(policy, "version") || !Hash(policy, "digest"))
            throw Invalid();
        return at;
    }

    internal static BsonDocument Create(long actorId, long grantVersion, long securityVersion, string operation,
        string key, string reason, string correlationId, BsonDocument policy, DateTimeOffset now)
    {
        var row = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", actorId }, { "operation", operation },
            { "key", key }, { "bodyHash", ClosureHash }, { "createdAt", now.UtcDateTime },
            { "result", new BsonDocument("receiptState", "closed_no_effect") },
            { "personReviewClosure", new BsonDocument
                {
                    { "schemaVersion", 1 }, { "actorGrantVersion", grantVersion }, { "actorSecurityVersion", securityVersion },
                    { "policy", policy.DeepClone() }, { "reason", reason }, { "correlationId", correlationId }
                }
            }
        };
        _ = ClosedAt(row, actorId, operation, key, now);
        return row;
    }
    private static bool Fields(BsonDocument row, params string[] fields) => row.ElementCount == fields.Length && fields.All(row.Contains);
    private static bool Integer(BsonDocument row, string field, int expected) => row.TryGetValue(field, out var value) && value.IsInt32 && value.AsInt32 == expected;
    private static bool PositiveLong(BsonDocument row, string field) => row.TryGetValue(field, out var value) && value.IsInt64 && value.AsInt64 > 0;
    private static bool Hash(BsonDocument row, string field) => row.TryGetValue(field, out var value) && value.IsString &&
        value.AsString.Length == 64 && value.AsString.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Text(BsonDocument row, string field, int maximum, bool trimmed = false) =>
        row.TryGetValue(field, out var value) && value.IsString && !string.IsNullOrWhiteSpace(value.AsString) &&
        value.AsString.Length <= maximum && !value.AsString.Any(char.IsControl) && (!trimmed || value.AsString.Trim() == value.AsString);
    private static bool Date(BsonDocument row, string field, DateTimeOffset now, out DateTimeOffset at)
    {
        at = default;
        if (!row.TryGetValue(field, out var value) || !value.IsBsonDateTime) return false;
        var milliseconds = value.AsBsonDateTime.MillisecondsSinceEpoch;
        if (milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || milliseconds > now.ToUnixTimeMilliseconds()) return false;
        at = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); return true;
    }
    private static AuthorizationDeniedException Invalid() => new("person_receipt_invalid");
}
