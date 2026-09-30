using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Billing;

internal static class BankStatementBson
{
    internal const string Profiles = "bankStatementProfiles", Imports = "bankStatementImports", Operation = "billing.bank-statement.stage", Prefix = "billing.bank-statement.";
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not ("snapshotHash" or "fence"))));
    internal static BsonDocument Snapshot(BsonDocument row) => new(row.Where(field => field.Name != "fence"));
    internal static BankStatementFormat Format(BsonValue row) => new(row["delimiter"].AsString, row["dateFormat"].AsString, row["decimalSeparator"].AsString,
        row["creditMarker"].AsString, row["debitMarker"].AsString, [.. row["columns"].AsBsonArray.Select(value => value.AsString)], row["identityNamespace"].IsBsonNull ? null : row["identityNamespace"].AsString);
    internal static BsonDocument Format(BankStatementFormat value) => new() { { "delimiter", value.Delimiter }, { "dateFormat", value.DateFormat }, { "decimalSeparator", value.DecimalSeparator },
        { "creditMarker", value.CreditMarker }, { "debitMarker", value.DebitMarker }, { "columns", new BsonArray(value.Columns) }, { "identityNamespace", (BsonValue?)value.IdentityNamespace ?? BsonNull.Value } };
    internal static BsonDocument Preview(BankStatementPreview value) => new() { { "sourceSha256", value.SourceSha256 },
        { "credits", new Decimal128(value.Credits.Value) }, { "debits", new Decimal128(value.Debits.Value) },
        { "rows", new BsonArray(value.Rows.Select(row => new BsonDocument { { "number", row.Number }, { "transactionId", (BsonValue?)row.TransactionId ?? BsonNull.Value },
            { "bookedDate", row.BookedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }, { "direction", row.Direction }, { "amount", new Decimal128(row.Amount.Value) },
            { "payerName", (BsonValue?)row.PayerName ?? BsonNull.Value }, { "reference", (BsonValue?)row.Reference ?? BsonNull.Value }, { "identityCandidate", (BsonValue?)row.IdentityCandidate ?? BsonNull.Value } })) },
        { "issues", new BsonArray(value.Issues.Select(issue => new BsonDocument { { "row", issue.Row }, { "field", issue.Field }, { "code", issue.Code } })) } };
    internal static string Binding(BsonDocument profile, BsonDocument account, string sourceSha256) => CatalogRepository.Digest(new BsonDocument {
        { "profileHash", Hash(profile) }, { "accountVersionId", account["_id"] }, { "accountSnapshotHash", account["snapshotHash"] }, { "sourceSha256", sourceSha256 } });
    internal static object Body(long profileId, long profileRevision, long accountId, long accountVersionId, string filename, string sourceSha256, string binding, string sourceReference, bool attested) =>
        new { profileId, profileRevision, accountId, accountVersionId, filename, sourceSha256, binding, sourceReference, attested };
    internal static object Body(BsonDocument row) => Body(row["profile"]["_id"].AsInt64, row["profile"]["revision"].AsInt64, row["account"]["accountId"].AsInt64,
        row["account"]["_id"].AsInt64, row["filename"].AsString, row["preview"]["sourceSha256"].AsString, row["binding"].AsString, row["sourceReference"].AsString, true);
    internal static BsonDocument Result(BsonDocument row) => new() { { "importId", row["_id"] }, { "operation", Operation }, { "rowCount", row["preview"]["rows"].AsBsonArray.Count }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static PersistenceConflictException Broken() => new("bank_statement_integrity_unavailable", "The complete statement provenance is unavailable");
    internal static PersistenceConflictException Changed() => new("bank_statement_changed", "Preview the current profile and receiving account before staging");
}
