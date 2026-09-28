using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>Reference-only recovery; the original receipt must still exist inside the replay transaction.</summary>
    public async Task<BsonDocument?> RecoverAliasAsync(ActorContext actor, long poolId, StockLocation requestedLocation,
        string operation, string key, string correlationId, CancellationToken ct = default)
    {
        requestedLocation.Validate();
        if (poolId <= 0 || !StockSourceSetupPolicy.Text(key, 200) || !StockSourceSetupPolicy.Text(correlationId, 128) ||
            operation is not ("stock.source.alias.propose" or "stock.source.alias.approve" or "stock.source.alias.reject" or "stock.source.alias.withdraw"))
            throw StockSourceAliasBson.Invalid();
        await AliasStorageAsync(ct);
        var proposed = operation == "stock.source.alias.propose";
        var reviewed = operation is "stock.source.alias.approve" or "stock.source.alias.reject";
        var permission = reviewed ? Permissions.StockSourceApprove : Permissions.StockSourcePropose;
        var recovery = await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            var resources = await AliasScopesAsync(actor, snapshot, tx, poolId, requestedLocation, permission, token);
            var receipt = await Rows("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            BsonDocument? result = null;
            if (receipt is not null)
            {
                try
                {
                    if (receipt.ElementCount != (reviewed ? 9 : 8) || !receipt["_id"].IsObjectId ||
                        !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"].AsInt32 != 1 || !receipt["actorId"].IsInt64 ||
                        !receipt["result"].IsBsonDocument || !receipt["result"]["proposalId"].IsInt64 || receipt["result"]["proposalId"].AsInt64 <= 0)
                        throw StockSourceAliasBson.Invalid();
                    var proposalId = receipt["result"]["proposalId"].AsInt64;
                    var scoped = await Rows("stockSourceAliasProposals").Find(tx, F.Eq("_id", proposalId))
                        .Project(new BsonDocument { { "poolId", 1 }, { "ownerLocation", 1 }, { "requestedLocation", 1 } }).SingleOrDefaultAsync(token);
                    if (scoped is null) throw StockSourceAliasBson.Invalid();
                    if (!scoped["poolId"].IsInt64 || scoped["poolId"].AsInt64 != poolId ||
                        InventoryGraph.Location(scoped["requestedLocation"].AsBsonDocument) != requestedLocation ||
                        Resource(InventoryGraph.Location(scoped["ownerLocation"].AsBsonDocument)) != resources[0])
                        throw new AuthorizationDeniedException("scope_denied");
                    var view = await AliasViewAsync(tx, proposalId, token);
                    var proposal = view["proposal"].AsBsonDocument;
                    var current = await CurrentSourceAsync(tx, poolId, token);
                    RequireAliasBinding(proposal, current);
                    var row = proposed ? proposal : view["decision"].AsBsonDocument;
                    BsonDocument expected;
                    object body;
                    if (proposed)
                    {
                        if (proposal["proposedBy"].AsInt64 != actor.UserId) throw StockSourceAliasBson.Invalid();
                        var expectedPoolRevision = proposal["poolRevision"].AsInt64;
                        var expectedAliasRevision = proposal["baseAliasRevision"].AsInt64;
                        var enabled = proposal["enabled"].AsBoolean;
                        body = new { poolId, expectedPoolRevision, requestedLocation, expectedAliasRevision, enabled };
                        expected = new() { { "proposalId", proposalId }, { "revision", 1L }, { "poolId", poolId },
                            { "poolRevision", expectedPoolRevision }, { "baseAliasRevision", expectedAliasRevision } };
                    }
                    else
                    {
                        if (row["decidedBy"].AsInt64 != actor.UserId || operation != "stock.source.alias." + row["decision"].AsString)
                            throw StockSourceAliasBson.Invalid();
                        var expectedRevision = row["revision"].AsInt64; var decision = AliasOutcome(operation);
                        var reviewReference = row["reviewReference"].AsString;
                        body = new { proposalId, expectedRevision, decision, reviewReference };
                        expected = new() { { "proposalId", proposalId }, { "decisionId", row["_id"] }, { "poolId", row["poolId"] },
                            { "poolRevision", row["poolRevision"] }, { "aliasRevision", row["aliasRevision"] }, { "status", row["decision"] } };
                    }
                    if (!receipt["result"].Equals(expected) || receipt["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) ||
                        StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(row[proposed ? "proposedAt" : "decidedAt"]))
                        throw StockSourceAliasBson.Invalid();
                    if (reviewed)
                    {
                        var participants = PersonApprovalReceipt.Read(receipt, Permissions.StockSourceApprove);
                        if (participants != new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64)) throw StockSourceAliasBson.Invalid();
                    }
                    else if (receipt.Contains(PersonApprovalReceipt.Field)) throw StockSourceAliasBson.Invalid();
                    result = new() { { "row", row }, { "expected", expected } };
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
                { throw StockSourceAliasBson.Invalid(); }
            }
            await CompleteAuthorityAsync(actor, snapshot, permission, resources, tx, token);
            return result;
        }, ct);
        if (recovery is null) return null;
        var stored = recovery["row"].AsBsonDocument;
        var replayed = proposed
            ? await ProposeAliasCoreAsync(actor, stored["poolId"].AsInt64, stored["poolRevision"].AsInt64,
                InventoryGraph.Location(stored["requestedLocation"].AsBsonDocument), stored["baseAliasRevision"].AsInt64,
                stored["enabled"].AsBoolean, new(key, stored["reason"].AsString, correlationId), true, ct)
            : await DecideAliasCoreAsync(actor, stored["proposalId"].AsInt64, stored["revision"].AsInt64, AliasOutcome(operation),
                stored["reviewReference"].AsString, new(key, stored["reason"].AsString, correlationId), true, ct);
        if (!replayed.Equals(recovery["expected"])) throw StockSourceAliasBson.Invalid();
        return replayed;
    }
    private static StockSourceSetupDecision AliasOutcome(string operation) => operation switch
    {
        "stock.source.alias.approve" => StockSourceSetupDecision.Approve,
        "stock.source.alias.reject" => StockSourceSetupDecision.Reject,
        "stock.source.alias.withdraw" => StockSourceSetupDecision.Withdraw,
        _ => throw StockSourceAliasBson.Invalid()
    };
}
