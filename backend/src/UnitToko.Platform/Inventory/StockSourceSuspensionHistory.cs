using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    private async Task<BsonDocument?> LatestSuspensionAsync(IClientSessionHandle tx, long poolId, BsonDocument current, CancellationToken ct)
    {
        BsonDocument? previous = null; long revision = 0;
        using var cursor = await Rows(StockSourceSuspensionSchemas.Collection).Find(tx, F.Eq("poolId", poolId))
            .Sort(new BsonDocument("suspensionRevision", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
        {
            StockSourceSuspensionBson.Require(row);
            if (row["suspensionRevision"].AsInt64 != checked(++revision) ||
                previous is not null && (row["poolRevision"].AsInt64 < previous["poolRevision"].AsInt64 ||
                    StockSourceSetupBson.Time(row["suspendedAt"]) < StockSourceSetupBson.Time(previous["suspendedAt"])))
                throw StockSourceSuspensionBson.Invalid();
            await RequireSuspensionBindingAsync(tx, row, current, ct);
            var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
            StockSourceSuspensionBson.Guard(() =>
            {
                if (audit is null) throw StockSourceSuspensionBson.Invalid();
                StockSourceSuspensionBson.Exact(audit, "_id", "schemaVersion", "kind", "operation", "actorId", "actorGrantVersion", "createdAt", "record");
                if (audit["_id"] != row["auditId"] || !audit["schemaVersion"].IsInt32 || audit["schemaVersion"] != 1 ||
                    audit["kind"] != "stock.source.suspension.audit" || audit["operation"] != StockSourceSuspensionBson.Operation ||
                    audit["actorId"] != row["suspendedBy"] || !StockSourceSuspensionBson.Positive(audit, "actorGrantVersion") ||
                    audit["createdAt"] != row["suspendedAt"] || !audit["record"].Equals(row)) throw StockSourceSuspensionBson.Invalid();
            });
            var receipts = await Rows("idempotencyRecords").Find(tx, F.Eq("operation", StockSourceSuspensionBson.Operation) &
                F.Eq("result.poolId", poolId) & F.Eq("result.suspensionId", row["_id"])).Limit(2).ToListAsync(ct);
            if (receipts.Count != 1) throw StockSourceSuspensionBson.Invalid();
            RequireSuspensionReceipt(receipts[0], row);
            previous = row;
        }
        if (await Rows("auditEvents").CountDocumentsAsync(tx, F.Eq("kind", "stock.source.suspension.audit") & F.Eq("record.poolId", poolId), cancellationToken: ct) != revision ||
            await Rows("idempotencyRecords").CountDocumentsAsync(tx, F.Eq("operation", StockSourceSuspensionBson.Operation) & F.Eq("result.poolId", poolId), cancellationToken: ct) != revision)
            throw StockSourceSuspensionBson.Invalid();
        return previous;
    }
    private async Task RequireSuspensionBindingAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument current, CancellationToken ct)
    {
        if (row["poolId"] != current["pool"]["_id"] || row["poolRevision"].AsInt64 > current["pool"]["revision"].AsInt64 ||
            !row["ownerLocation"].Equals(current["pool"]["location"]) || row["physicalKey"] != current["input"]["physicalKey"])
            throw StockSourceSuspensionBson.Invalid();
        var source = row["configuration"]["source"].AsBsonDocument;
        if (source["creationProposalId"] != current["creationProposalId"]) throw StockSourceSuspensionBson.Invalid();
        if (row["poolRevision"] == 1L)
        {
            var proposal = await ProposalAsync(tx, source["creationProposalId"].AsInt64, ct);
            var original = await ViewAsync(tx, proposal, ct);
            if (!source["input"].Equals(proposal["input"]) || original["decision"]["poolId"] != row["poolId"] ||
                source["approvedAt"] != original["decision"]["decidedAt"]) throw StockSourceSuspensionBson.Invalid();
        }
        else
        {
            var view = await RevisionViewAsync(tx, source["revisionProposalId"].AsInt64, ct);
            if (view["appliedRevision"].IsBsonNull || view["appliedRevision"]["poolId"] != row["poolId"] ||
                view["appliedRevision"]["poolRevision"] != row["poolRevision"] || !view["appliedRevision"]["input"].Equals(source["input"]) ||
                source["approvedAt"] != view["decision"]["decidedAt"]) throw StockSourceSuspensionBson.Invalid();
        }
        foreach (var value in row["configuration"]["mappings"].AsBsonArray)
        {
            var mapping = value.AsBsonDocument;
            var view = await AliasViewAsync(tx, mapping["proposalId"].AsInt64, ct);
            if (view["alias"].IsBsonNull) throw StockSourceSuspensionBson.Invalid();
            var alias = view["alias"].AsBsonDocument;
            if (alias["poolId"] != row["poolId"] || alias["_id"] != mapping["aliasId"] || alias["decisionId"] != mapping["decisionId"] ||
                alias["aliasRevision"] != mapping["aliasRevision"] || alias["poolRevision"] != mapping["poolRevision"] ||
                !alias["requestedLocation"].Equals(mapping["requestedLocation"]) || alias["enabled"] != mapping["enabled"] ||
                StockSourceSetupBson.Time(view["decision"]["decidedAt"]) > StockSourceSetupBson.Time(row["suspendedAt"])) throw StockSourceSuspensionBson.Invalid();
        }
    }
    private static void RequireSuspensionReceipt(BsonDocument receipt, BsonDocument row) => StockSourceSuspensionBson.Guard(() =>
    {
        StockSourceSuspensionBson.Exact(receipt, "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "result", "createdAt");
        var poolId = row["poolId"].AsInt64;
        var expected = StockSourceSuspensionBson.Expectation(row);
        if (!receipt["_id"].IsObjectId || !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"] != 1 ||
            !receipt["actorId"].IsInt64 || receipt["actorId"] != row["suspendedBy"] || receipt["operation"] != StockSourceSuspensionBson.Operation ||
            !UnitToko.Modules.Inventory.StockSourceSetupPolicy.Text(receipt["key"].AsString, 200) ||
            receipt["bodyHash"] != AuthorizationStore.CommandHash(new { poolId, expected }, row["reason"].AsString) ||
            !receipt["result"].Equals(StockSourceSuspensionBson.Result(row)) ||
            StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(row["suspendedAt"])) throw StockSourceSuspensionBson.Invalid();
    });
}
