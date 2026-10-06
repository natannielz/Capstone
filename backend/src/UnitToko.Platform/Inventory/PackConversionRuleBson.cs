using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

internal static class PackConversionRuleBson
{
    internal const string Proposals = "packConversionRuleProposals", Decisions = "packConversionRuleDecisions", Rules = "packConversionRules";
    internal const string Propose = "inventory.pack-conversion.rule.propose", Decide = "inventory.pack-conversion.rule.decide";
    internal static BsonDocument Input(PackConversionRuleInput value) {
        value = PackConversionRulePolicy.Copy(value); return new() { { "location", StockCountBson.Scope(value.Location) },
            { "sourceSkuId", value.SourceSkuId }, { "sourceSkuRevision", value.SourceSkuRevision }, { "sourceDefinitionId", value.SourceDefinitionId },
            { "destinationSkuId", value.DestinationSkuId }, { "destinationSkuRevision", value.DestinationSkuRevision }, { "destinationDefinitionId", value.DestinationDefinitionId },
            { "sourcePackagesPerStep", value.SourcePackagesPerStep }, { "destinationPackagesPerStep", value.DestinationPackagesPerStep } };
    }
    internal static PackConversionRuleInput Input(BsonDocument row) => PackConversionRulePolicy.Copy(new PackConversionRuleInput(StockCountBson.Scope(row["location"].AsBsonDocument),
        row["sourceSkuId"].AsInt64, row["sourceSkuRevision"].AsInt64, row["sourceDefinitionId"].AsInt64, row["destinationSkuId"].AsInt64,
        row["destinationSkuRevision"].AsInt64, row["destinationDefinitionId"].AsInt64, row["sourcePackagesPerStep"].AsInt32, row["destinationPackagesPerStep"].AsInt32));
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "snapshotHash")));
    internal static string Binding(PackConversionRuleInput input, BsonDocument catalog) => CatalogRepository.Digest(new BsonDocument {
        { "input", Input(input) }, { "catalog", catalog } });
    internal static string DecisionBinding(BsonDocument proposal, PackConversionRuleDecision decision) => CatalogRepository.Digest(new BsonDocument {
        { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] }, { "decision", decision == PackConversionRuleDecision.Approve ? "approve" : "reject" } });
    internal static object ProposeBody(PackConversionRuleInput input, string binding) => new { input, binding };
    internal static object DecideBody(long proposalId, long expectedRevision, PackConversionRuleDecision decision, string binding) => new { proposalId, expectedRevision, decision, binding };
    internal static BsonDocument Result(BsonDocument row) => row["kind"] == "pack.conversion.rule.proposal"
        ? new() { { "proposalId", row["_id"] }, { "revision", 1L }, { "state", "pending" }, { "physicalConversionAvailable", false } }
        : new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "ruleId", row["ruleId"] }, { "revision", 1L },
            { "state", row["decision"] }, { "physicalConversionAvailable", false } };
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason)); row.Add("reason", metadata.Reason);
        row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("recordedAt", DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()).UtcDateTime);
        row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "requestKey", row["requestKey"] },
        { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static BsonDocument Rule(BsonDocument proposal, BsonDocument decision) {
        if (decision["decision"] != "approve") throw Broken();
        var row = new BsonDocument { { "_id", proposal["_id"] }, { "schemaVersion", 1 }, { "kind", "pack.conversion.rule" }, { "version", 1L },
            { "proposalId", proposal["_id"] }, { "decisionId", decision["_id"] }, { "proposalHash", proposal["snapshotHash"] }, { "decisionHash", decision["snapshotHash"] },
            { "input", proposal["input"].DeepClone() }, { "catalog", proposal["catalog"].DeepClone() }, { "recordedAt", decision["recordedAt"] }, { "physicalConversionAvailable", false } };
        row.Add("snapshotHash", Hash(row)); return row;
    }
    internal static PersistenceConflictException Broken() => new("pack_conversion_rule_integrity_unavailable", "The exact reviewed catalogue rule history is unavailable");
    internal static PersistenceConflictException Changed() => new("pack_conversion_rule_changed", "Catalogue identities or the reviewed rule changed; inspect them again");
}
