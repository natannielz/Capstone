using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Mongo;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Identity;

internal static class AccountContactRecords
{
    internal const string Heads = "accountContacts";
    internal const string History = "accountContactHistory";
    internal const string Receipts = "accountContactReceipts";
    internal const string Operation = "account.contact.update";

    internal static string Hash(BsonDocument row, string field)
    {
        var copy = row.DeepClone().AsBsonDocument;
        copy.Remove(field);
        return Convert.ToHexString(SHA256.HashData(copy.ToBson())).ToLowerInvariant();
    }
    internal static PersistenceConflictException Broken() => new("account_contact_integrity_unavailable",
        "Contact history needs owner review");

    internal static string BodyHash(AccountContactInput contact, long expectedRevision, string reason) =>
        Hash(new BsonDocument { { "name", contact.Name },
            { "phone", contact.Phone is null ? BsonNull.Value : new BsonString(contact.Phone) },
            { "position", contact.Position is null ? BsonNull.Value : new BsonString(contact.Position) },
            { "address", contact.Address is null ? BsonNull.Value : new BsonString(contact.Address) },
            { "expectedRevision", expectedRevision }, { "reason", reason } }, "bodyHash");

    internal static async Task<(BsonDocument? Head, IReadOnlyList<BsonDocument> History)> ReadAsync(
        IMongoDatabase database, IClientSessionHandle tx, long userId, CancellationToken ct)
    {
        var head = await database.GetCollection<BsonDocument>(Heads).Find(tx, new BsonDocument("_id", userId))
            .SingleOrDefaultAsync(ct);
        var history = await database.GetCollection<BsonDocument>(History).Find(tx, new BsonDocument("userId", userId))
            .Sort(new BsonDocument("revision", 1)).Limit(257).ToListAsync(ct);
        var receipts = await database.GetCollection<BsonDocument>(Receipts).Find(tx, new BsonDocument("actorId", userId))
            .Limit(257).ToListAsync(ct);
        foreach (var (name, rows) in new[] {
            (Heads, head is null ? Array.Empty<BsonDocument>() : new[] { head }),
            (History, history.ToArray()), (Receipts, receipts.ToArray()) })
            foreach (var row in rows)
                if (!await database.GetCollection<BsonDocument>(name).Find(tx, new BsonDocument("$and", new BsonArray {
                    new BsonDocument("_id", row["_id"]), AccountContactSchemas.Validator(name) })).AnyAsync(ct)) throw Broken();
        if (history.Count > 256 || receipts.Count > 256 || head is null && (history.Count != 0 || receipts.Count != 0) ||
            head is not null && (history.Count == 0 || history.Count != receipts.Count ||
                head["revision"].AsInt64 != history.Count) ||
            receipts.Select(row => row["historyId"]).Distinct().Count() != receipts.Count ||
            receipts.Select(row => row["key"]).Distinct().Count() != receipts.Count) throw Broken();
        long previousId = 0;
        BsonValue previousHash = BsonNull.Value;
        for (var index = 0; index < history.Count; index++)
        {
            var row = history[index];
            try {
                _ = Contact(row).Validate();
                _ = AccountContactService.Text(row["requestKey"].AsString, 200);
                _ = AccountContactService.Text(row["reason"].AsString, 1000);
                _ = AccountContactService.Text(row["correlationId"].AsString, 128);
            }
            catch (DomainValidationException) { throw Broken(); }
            if (row["userId"] != userId || row["actorId"] != userId || row["revision"] != index + 1L ||
                row["_id"].AsInt64 <= previousId ||
                row["previousHistoryId"] != (previousId == 0 ? BsonNull.Value : new BsonInt64(previousId)) ||
                row["previousHash"] != previousHash || row["snapshotHash"] != Hash(row, "snapshotHash") ||
                row["bodyHash"] != BodyHash(Contact(row), row["revision"].AsInt64 - 1, row["reason"].AsString)) throw Broken();
            var receipt = receipts.SingleOrDefault(value => value["historyId"] == row["_id"]);
            if (receipt is null || receipt["actorId"] != userId || receipt["key"] != row["requestKey"] ||
                receipt["bodyHash"] != row["bodyHash"] || receipt["historyHash"] != row["snapshotHash"] ||
                receipt["recordedAt"] != row["recordedAt"]) throw Broken();
            previousId = row["_id"].AsInt64;
            previousHash = row["snapshotHash"];
        }
        if (head is not null)
        {
            var last = history[^1];
            if (head["lastHistoryId"] != last["_id"] || head["lastHash"] != last["snapshotHash"] ||
                head["name"] != last["name"] || head["phone"] != last["phone"] ||
                head["position"] != last["position"] || head["address"] != last["address"] ||
                head["updatedAt"] != last["recordedAt"]) throw Broken();
        }
        return (head, history);
    }

    internal static AccountContactInput Contact(BsonDocument row) => new(row["name"].AsString,
        row["phone"].IsBsonNull ? null : row["phone"].AsString,
        row["position"].IsBsonNull ? null : row["position"].AsString,
        row["address"].IsBsonNull ? null : row["address"].AsString);
}
