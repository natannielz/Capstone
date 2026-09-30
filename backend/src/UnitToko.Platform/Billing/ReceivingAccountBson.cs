using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Billing;
internal static class ReceivingAccountBson
{
    internal static object ProposeBody(long? accountId, long baseVersion, long policyId, long policyRevision, ReceivingAccountDetails details) => new { accountId, baseVersion, policyId, policyRevision, details };
    internal static object ObserveBody(long proposalId, long expectedRevision, ReceivingAccountDetails details, string sourceReference, bool attested) => new { proposalId, expectedRevision, details, sourceReference, attested };
    internal static object DecideBody(long proposalId, long expectedRevision, ReceivingAccountDecision decision, long? observationId) => new { proposalId, expectedRevision, decision, observationId };
    internal static string Name(ReceivingAccountDecision decision) => decision switch { ReceivingAccountDecision.Approve => "approve", ReceivingAccountDecision.Reject => "reject", ReceivingAccountDecision.Withdraw => "withdraw", _ => throw ReceivingAccountPolicy.Invalid() };
    internal static string Permission(string operation) => operation switch {
        Prefix + "propose" or Prefix + "withdraw" => Permissions.ReceivingAccountPropose,
        Prefix + "observe" => Permissions.ReceivingAccountVerify,
        Prefix + "approve" or Prefix + "reject" => Permissions.ReceivingAccountApprove,
        _ => throw ReceivingAccountPolicy.Invalid() };
    internal static UnitToko.Modules.Organizations.ResourceScope Scope(BsonValue row) => new(FulfillmentOrganizationId: row["fulfillmentOrganizationId"].AsInt64,
        ShopId: row["shopId"].AsInt64, ChannelId: row["channelId"].AsInt64, WarehouseId: row["warehouseId"].IsBsonNull ? null : row["warehouseId"].AsInt64);
    internal static PersonEvidence Person(BsonValue row) => new(row["userId"].AsInt64, row["personId"].IsBsonNull ? null : row["personId"].AsInt64,
        Enum.Parse<PersonVerification>(row["verification"].AsString), row["version"].AsInt64);
    internal const string Policies = "receivingAccountVerificationPolicies", Heads = "receivingAccountHeads", Proposals = "receivingAccountProposals",
        Observations = "receivingAccountObservations", Decisions = "receivingAccountDecisions", Versions = "receivingAccountVersions", Prefix = "billing.receiving-account.";
    internal static BsonDocument Details(ReceivingAccountDetails value) { ReceivingAccountPolicy.Copy(value); return new() {
        { "bankCode", value.BankCode }, { "accountNumber", value.AccountNumber }, { "holderName", value.HolderName }, { "currency", value.Currency } }; }
    internal static ReceivingAccountDetails Details(BsonValue value) => ReceivingAccountPolicy.Copy(new(value["bankCode"].AsString, value["accountNumber"].AsString, value["holderName"].AsString, value["currency"].AsString));
    internal static BsonDocument Person(PersonEvidence person) => new() { { "userId", person.UserId }, { "personId", person.PersonId is { } id ? new BsonInt64(id) : BsonNull.Value }, { "verification", person.Verification.ToString() }, { "version", person.Version } };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "snapshotHash")));
    internal static string PolicyHash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "fence")));
    internal static BsonDocument PolicySnapshot(BsonDocument row) => new(row.Where(field => field.Name != "fence"));
    // Identity is independent of lineage/version so later receipt uniqueness cannot double-count one physical bank account.
    internal static string PhysicalIdentity(ReceivingAccountDetails details) => CatalogRepository.Digest(new BsonDocument {
        { "country", "ID" }, { "bankCode", details.BankCode }, { "accountNumber", details.AccountNumber }, { "currency", details.Currency } });
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", Prefix + operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion); row.Add("recordedAt", now.UtcDateTime);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason));
        row.Add("reason", metadata.Reason); row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "accountId", row["accountId"] }, { "proposalId", row["proposalId"] }, { "recordId", row["_id"] },
        { "operation", row["operation"] }, { "accountVersion", row.GetValue("accountVersion", BsonNull.Value) }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Broken() => new("receiving_account_integrity_unavailable", "The complete receiving account history is unavailable");
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Changed() => new("receiving_account_changed", "Reload the account, proposal and verification before deciding");
}
