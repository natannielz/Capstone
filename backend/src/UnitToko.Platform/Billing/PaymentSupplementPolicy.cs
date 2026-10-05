using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

// D06 is a real deployment input. This reader never seeds, creates, renews or approves a policy.
internal sealed class PaymentSupplementPolicy(IMongoDatabase database, IClock clock)
{
    internal async Task<BsonDocument?> FindCurrentAsync(IClientSessionHandle tx, BsonDocument scope, CancellationToken ct) {
        var selected = new BsonDocument { { "scope.organizationId", scope["organizationId"] }, { "scope.shopId", scope["shopId"] }, { "scope.channelId", scope["channelId"] } };
        var rows = await database.GetCollection<BsonDocument>(PaymentSupplementBson.Policies).Find(tx,
            selected).Limit(2).ToListAsync(ct);
        if (rows.Count > 1) throw PaymentSupplementRecords.Broken();
        if (rows.Count == 0) return null;
        var row = rows[0];
        if (!await database.GetCollection<BsonDocument>(PaymentSupplementBson.Policies).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("_id", row["_id"]), PaymentSupplementSchemas.Validator(PaymentSupplementBson.Policies) })).AnyAsync(ct)) throw PaymentSupplementRecords.Broken();
        if (!row["active"].AsBoolean || row["approvedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime || row["expiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) return null;
        ValidateSnapshot(PaymentSupplementBson.PolicySnapshot(row)); return row;
    }
    internal async Task<BsonDocument> RequireAsync(IClientSessionHandle tx, BsonDocument scope, CancellationToken ct) =>
        await FindCurrentAsync(tx, scope, ct) ?? throw new PersistenceConflictException("payment_supplement_policy_unavailable",
            "PDF lampiran belum diaktifkan untuk unit ini. Persetujuan kebijakan operasi, keuangan dan pemilik data diperlukan.");
    internal async Task FenceAsync(IClientSessionHandle tx, BsonDocument observed, CancellationToken ct) {
        if (clock.UtcNow.UtcDateTime >= observed["expiresAt"].ToUniversalTime() || observed["fence"].AsInt64 >= long.MaxValue - 2 ||
            (await database.GetCollection<BsonDocument>(PaymentSupplementBson.Policies).UpdateOneAsync(tx, observed,
                new BsonDocument("$inc", new BsonDocument("fence", 1L)), cancellationToken: ct)).ModifiedCount != 1)
            throw new PersistenceConflictException("payment_supplement_policy_changed", "Persetujuan PDF lampiran berubah; muat ulang.");
    }
    internal static void ValidateSnapshot(BsonDocument snapshot) {
        foreach (var name in new[] { "decisionRef", "decisionHash" }) PaymentSupplementRules.Text(snapshot[name].AsString, name == "decisionHash" ? 64 : 256);
        if (snapshot["decisionHash"].AsString.Length != 64 || snapshot["decisionHash"].AsString.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) ||
            snapshot["maximumSupplements"].AsInt32 is < 1 or > PaymentSupplementRules.MaximumSupplements || snapshot["approvedAt"].CompareTo(snapshot["expiresAt"]) >= 0 ||
            snapshot["expiresAt"].ToUniversalTime() - snapshot["approvedAt"].ToUniversalTime() > TimeSpan.FromDays(90)) throw PaymentSupplementRecords.Broken();
        var approvals = snapshot["approvals"].AsBsonArray;
        if (approvals.Count != 3 || !approvals.Select(row => row["owner"].AsString).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(new[] { "data-owner", "finance", "operations" })) throw PaymentSupplementRecords.Broken();
        foreach (var approval in approvals) {
            PaymentSupplementRules.Text(approval["reference"].AsString, 256);
            if (approval["approvedAt"].CompareTo(snapshot["approvedAt"]) > 0 || approval["approvedAt"].CompareTo(snapshot["expiresAt"]) >= 0) throw PaymentSupplementRecords.Broken();
        }
    }
    internal static void ValidateUse(BsonDocument snapshot, BsonDocument scope, BsonValue recordedAt) {
        ValidateSnapshot(snapshot);
        if (!InvoiceRecords.Equal(snapshot["scope"], scope) || snapshot["approvedAt"].CompareTo(recordedAt) > 0 || snapshot["expiresAt"].CompareTo(recordedAt) <= 0)
            throw PaymentSupplementRecords.Broken();
    }
}
