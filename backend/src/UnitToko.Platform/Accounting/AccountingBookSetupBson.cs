using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Accounting;

internal static class AccountingBookSetupBson
{
    internal const string Proposals = "accountingBookSetupProposals", Decisions = "accountingBookSetupDecisions", Prefix = "accounting.book.";
    internal static readonly string[] SourceNames = ["organization", "shop", "channel"];
    internal static BsonDocument Key(OperationalBookKey value) { AccountingBookSetupRules.Key(value); return new() {
        { "organizationId", value.OrganizationId }, { "shopId", value.ShopId }, { "channelId", value.ChannelId }, { "currency", value.Currency } }; }
    internal static OperationalBookKey Key(BsonValue value) => AccountingBookSetupRules.Key(new(value["organizationId"].AsInt64,
        value["shopId"].AsInt64, value["channelId"].AsInt64, value["currency"].AsString));
    internal static BsonArray Mappings(AccountingBookSetupInput input) => new(input.Mappings.Select(value => new BsonDocument {
        { "mappingKey", value.MappingKey }, { "accountCode", value.AccountCode } }));
    internal static BsonDocument Input(AccountingBookSetupInput value) { value = AccountingBookSetupRules.Copy(value); return new() {
        { "key", Key(value.Key) }, { "initialMonth", value.InitialMonth }, { "mappings", Mappings(value) } }; }
    internal static AccountingBookSetupInput Input(BsonValue row) => AccountingBookSetupRules.Copy(new(Key(row["key"]), row["initialMonth"].AsString,
        [.. row["mappings"].AsBsonArray.Select(value => new AccountingBookMappingInput(value["mappingKey"].AsString, value["accountCode"].AsString))]));
    internal static string Decision(AccountingBookSetupDecision value) => value switch { AccountingBookSetupDecision.Approve => "approve", AccountingBookSetupDecision.Reject => "reject", _ => throw Broken() };
    internal static AccountingBookSetupDecision Decision(BsonValue value) => value.AsString switch { "approve" => AccountingBookSetupDecision.Approve, "reject" => AccountingBookSetupDecision.Reject, _ => throw Broken() };
    internal static object ProposeBody(AccountingBookSetupInput input, string binding) => new { input = AccountingBookSetupRules.Copy(input), binding };
    internal static object DecideBody(long proposalId, long expectedVersion, AccountingBookSetupDecision decision, string binding) => new {
        proposalId, expectedVersion, decision = Decision(decision), binding };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not ("snapshotHash" or "fence"))));
    internal static string Binding(long actor, long grantVersion, AccountingBookSetupInput input, BsonDocument sources) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "sources", sources } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument proposal, AccountingBookSetupDecision decision, BsonDocument sources) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "proposalHash", proposal["snapshotHash"] }, { "decision", Decision(decision) }, { "sources", sources } });
    internal static BsonDocument Person(PersonEvidence value) => OrganizationAddressBson.Person(value);
    internal static PersonEvidence Person(BsonValue row) => OrganizationAddressBson.Person(row);
    internal static string Permission(string operation) => operation switch {
        Prefix + "propose" => Permissions.AccountingBookPropose, Prefix + "approve" or Prefix + "reject" => Permissions.AccountingBookApprove, _ => throw AccountingBookSetupRules.Invalid() };
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion); row.Add("recordedAt", now.UtcDateTime);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason)); row.Add("reason", metadata.Reason);
        row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "proposalId", row["proposalId"] }, { "proposalVersion", 1L },
        { "decisionId", row.Contains("decision") ? row["_id"] : BsonNull.Value }, { "bookId", row.GetValue("bookId", BsonNull.Value) },
        { "chartId", row.GetValue("chartId", BsonNull.Value) }, { "periodId", row.GetValue("periodId", BsonNull.Value) },
        { "status", !row.Contains("decision") ? "pending" : row["decision"] == "approve" ? "approved" : "rejected" } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "requestKey", row["requestKey"] }, { "key", row["key"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static BsonDocument Book(BsonDocument decision) => new() { { "_id", decision["bookId"] }, { "schemaVersion", 1 }, { "key", decision["key"] },
        { "canonicalKey", Key(decision["key"]).CanonicalKey }, { "version", 1L }, { "fence", 0L }, { "active", true }, { "chartVersionId", decision["chartId"] }, { "closedThrough", BsonNull.Value } };
    internal static BsonDocument Chart(BsonDocument decision) => new() { { "_id", decision["chartId"] }, { "schemaVersion", 1 }, { "bookId", decision["bookId"] },
        { "version", 1L }, { "active", true }, { "mappings", decision["accounts"] } };
    internal static BsonDocument Period(BsonDocument decision, BsonDocument proposal) => new() { { "_id", decision["periodId"] }, { "schemaVersion", 1 },
        { "bookId", decision["bookId"] }, { "month", proposal["input"]["initialMonth"] }, { "version", 1L }, { "dataRevision", 0L }, { "state", "open" },
        { "approvedRevision", BsonNull.Value }, { "snapshotHash", BsonNull.Value }, { "lastHistoryId", BsonNull.Value } };
    internal static BsonDocument Label(BsonValue source) => new() { { "id", source["profile"]["id"] }, { "code", source["profile"]["code"] },
        { "name", source["profile"]["name"] }, { "scopeVersion", source["target"]["version"] }, { "profileVersion", source["profile"]["version"] } };
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw AccountingBookSetupRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("accounting_book_setup_integrity_unavailable", "The complete reviewed accounting setup is unavailable");
    internal static PersistenceConflictException Changed() => new("accounting_book_setup_changed", "Reload the current organization, book and proposal before continuing");
    internal static PersistenceConflictException Configured() => new("accounting_book_already_configured", "This book is already configured; initial setup cannot replace it");
}
