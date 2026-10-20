using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

internal static class CatalogPublicShowcaseBson
{
    internal const string Heads = "catalogPublicShowcaseHeads", Proposals = "catalogPublicShowcaseProposals",
        Decisions = "catalogPublicShowcaseDecisions", Prefix = "catalog.public.showcase.";
    internal static AuthorizationDeniedException Invalid(string code = "catalog_public_showcase_unavailable") => new(code);
    internal static PersistenceConflictException Changed() =>
        new("catalog_public_showcase_changed", "The assortment's public state changed; reload it and review again");
    internal static PersistenceConflictException Broken() =>
        new("catalog_public_showcase_integrity_unavailable", "The public catalog decision history is unavailable");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(item => item.Name is not ("snapshotHash" or "fence"))));
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, string bodyHash) {
        row.Add("operation", operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", bodyHash); row.Add("reason", metadata.Reason);
        row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("snapshotHash", Hash(row));
    }
    internal static bool IsProposal(BsonDocument row) => row["operation"] == Prefix + "propose";
    internal static BsonDocument Result(BsonDocument row) => IsProposal(row)
        ? new() { { "proposalId", row["_id"] }, { "assortmentId", row["assortmentId"] }, { "makePublic", row["makePublic"] }, { "revision", row["revision"] } }
        : new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "assortmentId", row["assortmentId"] },
            { "status", row["decision"] }, { "headRevision", row["headRevision"] } };
    internal static BsonValue At(BsonDocument row) => row[IsProposal(row) ? "proposedAt" : "decidedAt"];
    internal static BsonDocument Audit(BsonDocument row) => new() {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Prefix + "command" }, { "operation", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "createdAt", At(row) },
        { "record", row.DeepClone() }, { "result", Result(row) } };
    internal static void Require(BsonDocument row) { if (row["snapshotHash"] != Hash(row)) throw Broken(); }

    internal static async Task RequireStorageAsync(IMongoDatabase db, CancellationToken ct) {
        foreach (var name in CatalogPublicSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(db, name, ct) ?? throw Invalid("public_catalog_unavailable");
            var keys = CatalogPublicSchemas.Keys(name).Select((key, index) => ("ux_" + name + "_" + index, key, true)).ToArray();
            var indexes = await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!AccountContactStorage.SafeDefinition(definition, CatalogPublicSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                throw Invalid("public_catalog_unavailable");
        }
    }
}
