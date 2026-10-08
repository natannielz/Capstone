using System.Collections.Immutable;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Authorization;

internal static class TemporaryDelegationRecords
{
    internal const string Heads = "temporaryDelegations", Events = "temporaryDelegationEvents", Operation = "access.delegation.change";
    internal static AuthorizationDeniedException Invalid() => new("delegation_unavailable");
    internal static BsonDocument Request(TemporaryDelegationRequest value) => new() {
        { "sourceGrantId", value.SourceGrantId }, { "sourceVersion", value.SourceVersion }, { "beneficiaryUserId", value.BeneficiaryUserId },
        { "beneficiaryMembershipId", value.BeneficiaryMembershipId }, { "scope", AuthorizationBson.Scope(value.Scope) },
        { "permissions", new BsonArray(value.Permissions) }, { "effectiveAt", value.EffectiveAt.UtcDateTime }, { "expiresAt", value.ExpiresAt.UtcDateTime } };
    internal static TemporaryDelegationRequest Request(BsonValue row) {
        var result = new TemporaryDelegationRequest(row["sourceGrantId"].AsInt64, row["sourceVersion"].AsInt64,
            row["beneficiaryUserId"].AsInt64, row["beneficiaryMembershipId"].AsInt64, AuthorizationBson.Scope(row["scope"].AsBsonDocument),
            row["permissions"].AsBsonArray.Select(p => p.AsString).ToImmutableArray(), Time(row["effectiveAt"]), Time(row["expiresAt"]));
        TemporaryDelegationPolicy.Validate(result); return result;
    }
    internal static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    internal static object Body(string action, long? id, long revision, TemporaryDelegationRequest? request) => new { action, id, revision, request };
    internal static object Body(BsonDocument row) => Body(row["action"].AsString, row["action"] == "propose" ? null : row["delegationId"].AsInt64,
        row["revision"].AsInt64 - 1, row["action"] == "propose" ? Request(row["after"]["request"]) : null);
    internal static BsonDocument Result(BsonDocument row) => new() { { "delegationId", row["delegationId"] }, { "eventId", row["_id"] },
        { "revision", row["revision"] }, { "action", row["action"] }, { "status", row["after"]["status"] }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", "access.delegation.changed" }, { "operation", Operation }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "beforeHash", Hash(row["before"]) }, { "afterHash", Hash(row["after"]) }, { "scope", row["after"]["request"]["scope"].DeepClone() }, { "result", Result(row) } };
    private static string Hash(BsonValue value) => Convert.ToHexString(SHA256.HashData(new BsonDocument("value", value).ToBson())).ToLowerInvariant();
    internal static BsonDocument State(BsonDocument head) { var value = head.DeepClone().AsBsonDocument; value.Remove("fence"); return value; }
    internal static PermissionGrant Grant(BsonDocument head) {
        var request = Request(head["request"]);
        return new(head["_id"].AsInt64, request.BeneficiaryUserId, request.BeneficiaryMembershipId, TemporaryDelegationPolicy.TemplateId, 1,
            request.Permissions, request.Scope, head["status"] == "revoked" ? UnitToko.Modules.Organizations.AuthorizationStatus.Revoked : UnitToko.Modules.Organizations.AuthorizationStatus.Active,
            head["sourceUserId"].AsInt64, head["approvedBy"].AsInt64, head["proposalReason"].AsString, request.EffectiveAt, request.ExpiresAt,
            head["revokedAt"].IsBsonNull ? null : Time(head["revokedAt"]), head["grantVersion"].AsInt64, []);
    }

    internal static async Task<BsonDocument> EventAsync(IMongoDatabase database, IClientSessionHandle tx, long id, CancellationToken ct) {
        var rows = database.GetCollection<BsonDocument>(Events);
        var row = await rows.Find(tx, Builders<BsonDocument>.Filter.And(new BsonDocument("_id", id), TemporaryDelegationSchemas.Validator(Events))).SingleOrDefaultAsync(ct) ?? throw Invalid();
        if (AuthorizationStore.CommandHash(Body(row), row["reason"].AsString) != row["bodyHash"].AsString) throw Invalid();
        var audit = await database.GetCollection<BsonDocument>("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var receipt = await database.GetCollection<BsonDocument>("idempotencyRecords").Find(tx, new BsonDocument {
            { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        var approved = row["action"] == "approve";
        if (audit != Audit(row) || receipt is null || receipt.ElementCount != (approved ? 9 : 8) || !receipt["_id"].IsObjectId ||
            receipt["schemaVersion"] != 1 || receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != Result(row) ||
            !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw Invalid();
        if (approved) {
            var expected = new ApprovalRequest(row["revision"].AsInt64 - 1, row["revision"].AsInt64 - 1,
                ProposedBy: row["after"]["sourceUserId"].AsInt64, BeneficiaryId: row["after"]["request"]["beneficiaryUserId"].AsInt64);
            if (PersonApprovalReceipt.Read(receipt, Permissions.AccessApprove) != expected) throw Invalid();
        }
        await ExactAsync(database, tx, Events, row, ct); await ExactAsync(database, tx, "auditEvents", audit!, ct);
        await ExactAsync(database, tx, "idempotencyRecords", receipt, ct); return row;
    }
    internal static async Task<BsonDocument> HeadAsync(IMongoDatabase database, IClientSessionHandle tx, long id, CancellationToken ct) {
        var head = await database.GetCollection<BsonDocument>(Heads).Find(tx,
            Builders<BsonDocument>.Filter.And(new BsonDocument("_id", id), TemporaryDelegationSchemas.Validator(Heads))).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var rows = await database.GetCollection<BsonDocument>(Events).Find(tx, new BsonDocument("delegationId", id))
            .Sort(new BsonDocument("revision", 1)).ToListAsync(ct);
        if (rows.Count == 0 || rows.Count != head["revision"].AsInt64) throw Invalid();
        BsonDocument? previous = null;
        for (var index = 0; index < rows.Count; index++) {
            var row = await EventAsync(database, tx, rows[index]["_id"].AsInt64, ct);
            if (row != rows[index] || row["revision"] != (long)index + 1 || row["delegationId"] != id ||
                row["after"]["_id"] != id || row["after"]["revision"] != row["revision"] || row["after"]["eventId"] != row["_id"] || row["after"]["updatedAt"] != row["recordedAt"] ||
                (previous is null ? row["action"] != "propose" || !row["before"].IsBsonNull : row["before"] != previous["after"] ||
                    row["recordedAt"].CompareTo(previous["recordedAt"]) < 0)) throw Invalid();
            var after = row["after"].AsBsonDocument; _ = Request(after["request"]);
            if (previous is not null && new[] { "request", "sourceUserId", "sourceIdentityVersion", "sourceGrant", "sourceMembership", "beneficiaryMembership", "sourceNodes", "proposalReason", "createdAt" }
                .Any(field => after[field] != previous["after"][field])) throw Invalid();
            var before = row["before"];
            switch (row["action"].AsString) {
                case "propose": if (index != 0 || after["status"] != "pending" || after["sourceUserId"] != row["actorId"] ||
                    after["sourceIdentityVersion"] != row["actorGrantVersion"] || after["createdAt"] != row["recordedAt"] || after["approvedBy"] != 0L || after["grantVersion"] != 0L || !after["revokedAt"].IsBsonNull) throw Invalid(); break;
                case "approve": if (before["status"] != "pending" || after["status"] != "active" || after["approvedBy"] != row["actorId"] ||
                    after["approvedBy"] == after["sourceUserId"] || after["approvedBy"] == after["request"]["beneficiaryUserId"] ||
                    after["grantVersion"] != 1L || !after["revokedAt"].IsBsonNull) throw Invalid(); break;
                case "revoke": if (before["status"] is not { IsString: true } || before["status"] == "revoked" || after["status"] != "revoked" ||
                    after["approvedBy"] != before["approvedBy"] || after["revokedAt"] != row["recordedAt"] ||
                    after["grantVersion"].AsInt64 != before["grantVersion"].AsInt64 + (before["status"] == "active" ? 1 : 0)) throw Invalid(); break;
                case "handover": if (row["actorId"] != after["sourceUserId"] && row["actorId"] != after["request"]["beneficiaryUserId"] ||
                    after["status"] != before["status"] || after["approvedBy"] != before["approvedBy"] || after["grantVersion"] != before["grantVersion"] ||
                    after["revokedAt"] != before["revokedAt"]) throw Invalid(); break;
                default: throw Invalid();
            }
            previous = row;
        }
        if (previous!["after"] != State(head)) throw Invalid();
        return head;
    }
    internal static async Task ExactAsync(IMongoDatabase database, IClientSessionHandle tx, string collection, BsonDocument row, CancellationToken ct) {
        var rows = database.GetCollection<BsonDocument>(collection);
        if ((await rows.DeleteOneAsync(tx, row, cancellationToken: ct)).DeletedCount != 1) throw Invalid();
        await rows.InsertOneAsync(tx, row.DeepClone().AsBsonDocument, cancellationToken: ct);
    }
}
