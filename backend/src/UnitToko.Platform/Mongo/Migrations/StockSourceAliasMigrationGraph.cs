using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Complete alias graph preflight, after the frozen original/source-settings graph succeeds.
/// Historical participants are retained facts, not reconstructed historical person assurance.</summary>
internal static class StockSourceAliasMigrationGraph
{
    private static readonly string[] Operations = ["stock.source.alias.propose", "stock.source.alias.approve", "stock.source.alias.reject", "stock.source.alias.withdraw"];
    internal static async Task ValidateAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        async Task<BsonDocument> One(string name, BsonDocument filter) =>
            await Rows(name).Find(filter).SingleOrDefaultAsync(ct) ?? throw Invalid("missing linked " + name);
        BsonDocument MappingKey(BsonDocument row, long version) => new()
            { { "poolId", row["poolId"] }, { "requestedLocation.channelId", row["requestedLocation"]["channelId"] }, { "aliasRevision", version } };

        async Task<DateTimeOffset?> Source(BsonDocument proposal)
        {
            var pool = await One("inventoryPools", new("_id", proposal["poolId"]));
            InventoryGraph.RequirePool(pool);
            var version = proposal["poolRevision"].AsInt64;
            if (version > pool["revision"].AsInt64 || !pool["location"].Equals(proposal["ownerLocation"])) throw Invalid("alias physical owner or settings version");
            var registration = await One("inventoryPoolRegistrations", new("_id", pool["_id"]));
            BsonDocument input; BsonValue approvedAt;
            if (version == 1)
            {
                var original = await One("stockSourceProposals", new("_id", registration["proposalId"]));
                var decision = await One("stockSourceDecisions", new("_id", registration["decisionId"]));
                StockSourceSetupBson.RequireProposal(original); StockSourceSetupBson.RequireDecision(decision, original);
                if (decision["decision"] != "approve" || decision["poolId"] != pool["_id"]) throw Invalid("original pool approval");
                input = original["input"].AsBsonDocument; approvedAt = decision["decidedAt"];
            }
            else
            {
                var source = await One("stockSourceRevisions", new() { { "poolId", pool["_id"] }, { "poolRevision", version } });
                var original = await One("stockSourceRevisionProposals", new("_id", source["proposalId"]));
                var decision = await One("stockSourceRevisionDecisions", new("_id", source["decisionId"]));
                StockSourceRevisionBson.Revision(source, original, decision);
                input = source["input"].AsBsonDocument; approvedAt = decision["decidedAt"];
            }
            if (!input["location"].Equals(proposal["ownerLocation"]) || input["physicalKey"] != proposal["physicalKey"] ||
                registration["physicalKey"] != proposal["physicalKey"] ||
                StockSourceSetupBson.Time(proposal["proposedAt"]) < StockSourceSetupBson.Time(approvedAt)) throw Invalid("alias source binding or chronology");
            if (version == pool["revision"].AsInt64) return null;
            var next = await One("stockSourceRevisions", new() { { "poolId", pool["_id"] }, { "poolRevision", checked(version + 1) } });
            var nextDecision = await One("stockSourceRevisionDecisions", new("_id", next["decisionId"]));
            var nextAt = StockSourceSetupBson.Time(nextDecision["decidedAt"]);
            // Same-millisecond event order cannot be reconstructed from timestamps or allocated IDs.
            if (nextAt < StockSourceSetupBson.Time(proposal["proposedAt"])) throw Invalid("proposal bound to already replaced settings");
            return nextAt;
        }
        async Task RequestedScope(BsonDocument proposal)
        {
            var requested = InventoryGraph.Location(proposal["requestedLocation"].AsBsonDocument);
            var channel = await One("authorizationScopes", new("_id", requested.ChannelId.Value));
            if (!channel["_id"].IsInt64 || channel.GetValue("kind", "") != "channel" ||
                channel.GetValue("parentId", BsonNull.Value) != new BsonInt64(requested.OrganizationId.Value) ||
                !channel.GetValue("version", BsonNull.Value).IsInt64 || channel["version"].AsInt64 <= 0) throw Invalid("requested channel ancestry");
        }
        async Task Identity(BsonValue actor)
        {
            var identity = await One("identities", new("_id", actor));
            if (!identity["_id"].IsInt64) throw Invalid("historical actor type");
        }
        async Task Audit(BsonDocument row, string operation, BsonValue actor, BsonValue time)
        {
            var linked = await Rows("auditEvents").Find(new BsonDocument
                { { "kind", "stock.source.alias.audit" }, { "record.kind", row["kind"] }, { "record._id", row["_id"] } }).Limit(2).ToListAsync(ct);
            if (linked.Count != 1) throw Invalid("missing or duplicated alias audit");
            var audit = linked[0];
            if (audit.ElementCount != 8 || !audit["_id"].IsInt64 || audit["_id"] != row["auditId"] ||
                !audit["schemaVersion"].IsInt32 || audit["schemaVersion"].AsInt32 != 1 || audit["operation"] != operation ||
                !audit["actorId"].IsInt64 || audit["actorId"] != actor || !audit["actorGrantVersion"].IsInt64 ||
                audit["actorGrantVersion"].AsInt64 <= 0 || audit["createdAt"] != time || !audit["record"].Equals(row))
                throw Invalid("alias audit provenance");
        }
        async Task Receipt(BsonDocument row, BsonDocument proposal, string operation, BsonValue actor, BsonDocument result, object body)
        {
            var proposed = operation == "stock.source.alias.propose";
            var linked = await Rows("idempotencyRecords").Find(new BsonDocument
                { { "operation", operation }, { "result." + (proposed ? "proposalId" : "decisionId"), row["_id"] } }).Limit(2).ToListAsync(ct);
            if (linked.Count != 1) throw Invalid("missing or duplicated alias receipt");
            var receipt = linked[0]; var reviewed = operation is "stock.source.alias.approve" or "stock.source.alias.reject";
            if (receipt.ElementCount != (reviewed ? 9 : 8) || !receipt["_id"].IsObjectId ||
                !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"].AsInt32 != 1 ||
                !receipt["actorId"].IsInt64 || receipt["actorId"] != actor || !StockSourceSetupPolicy.Text(receipt["key"].AsString, 200) ||
                !receipt["result"].Equals(result) || receipt["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) ||
                StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(row[proposed ? "proposedAt" : "decidedAt"]))
                throw Invalid("alias receipt provenance");
            if (reviewed)
            {
                var participants = PersonApprovalReceipt.Read(receipt, Permissions.StockSourceApprove);
                if (participants != new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64) ||
                    !ApprovalPolicy.Evaluate(actor.AsInt64, Permissions.StockSourceApprove, participants).Allowed)
                    throw Invalid("historical alias approval participants");
            }
            else if (receipt.Contains(PersonApprovalReceipt.Field)) throw Invalid("unexpected alias approval participants");
        }
        async Task Proposal(BsonDocument row)
        {
            StockSourceAliasBson.Proposal(row);
            await Source(row); await RequestedScope(row); await Identity(row["proposedBy"]);
            var baseVersion = row["baseAliasRevision"].AsInt64;
            if (baseVersion > 0)
            {
                var previous = await One("stockSourceAliases", MappingKey(row, baseVersion));
                var previousProposal = await One("stockSourceAliasProposals", new("_id", previous["proposalId"]));
                var previousDecision = await One("stockSourceAliasDecisions", new("_id", previous["decisionId"]));
                StockSourceAliasBson.Alias(previous, previousProposal, previousDecision);
                if (!previous["ownerLocation"].Equals(row["ownerLocation"]) || !previous["requestedLocation"].Equals(row["requestedLocation"]) ||
                    previous["physicalKey"] != row["physicalKey"] || previous["poolRevision"].AsInt64 > row["poolRevision"].AsInt64 ||
                    StockSourceSetupBson.Time(previousDecision["decidedAt"]) > StockSourceSetupBson.Time(row["proposedAt"]) ||
                    previous["enabled"] == row["enabled"] && previous["poolRevision"] == row["poolRevision"]) throw Invalid("alias proposal base or chronology");
            }
            var next = await Rows("stockSourceAliases").Find(MappingKey(row, checked(baseVersion + 1))).SingleOrDefaultAsync(ct);
            if (next is not null)
            {
                var nextDecision = await One("stockSourceAliasDecisions", new("_id", next["decisionId"]));
                if (StockSourceSetupBson.Time(nextDecision["decidedAt"]) < StockSourceSetupBson.Time(row["proposedAt"]))
                    throw Invalid("proposal bound to already replaced alias");
            }
            await Audit(row, "stock.source.alias.propose", row["proposedBy"], row["proposedAt"]);
            var poolId = row["poolId"].AsInt64; var expectedPoolRevision = row["poolRevision"].AsInt64;
            var requestedLocation = InventoryGraph.Location(row["requestedLocation"].AsBsonDocument);
            var expectedAliasRevision = baseVersion; var enabled = row["enabled"].AsBoolean;
            await Receipt(row, row, "stock.source.alias.propose", row["proposedBy"],
                new() { { "proposalId", row["_id"] }, { "revision", 1L }, { "poolId", poolId },
                    { "poolRevision", expectedPoolRevision }, { "baseAliasRevision", expectedAliasRevision } },
                new { poolId, expectedPoolRevision, requestedLocation, expectedAliasRevision, enabled });
        }
        async Task Decision(BsonDocument row)
        {
            var proposal = await One("stockSourceAliasProposals", new("_id", row["proposalId"]));
            StockSourceAliasBson.Decision(row, proposal); await Identity(row["decidedBy"]);
            var outcome = row["decision"].AsString;
            var decision = outcome switch
            {
                "approve" => StockSourceSetupDecision.Approve, "reject" => StockSourceSetupDecision.Reject,
                "withdraw" => StockSourceSetupDecision.Withdraw, _ => throw Invalid("alias decision")
            };
            await Audit(row, "stock.source.alias." + outcome, row["decidedBy"], row["decidedAt"]);
            var proposalId = row["proposalId"].AsInt64; var expectedRevision = row["revision"].AsInt64; var reviewReference = row["reviewReference"].AsString;
            await Receipt(row, proposal, "stock.source.alias." + outcome, row["decidedBy"],
                new() { { "proposalId", proposalId }, { "decisionId", row["_id"] }, { "poolId", row["poolId"] },
                    { "poolRevision", row["poolRevision"] }, { "aliasRevision", row["aliasRevision"] }, { "status", outcome } },
                new { proposalId, expectedRevision, decision, reviewReference });
            var linked = await Rows("stockSourceAliases").Find(new BsonDocument("$or", new BsonArray
                { new BsonDocument("proposalId", proposalId), new BsonDocument("decisionId", row["_id"]) })).Limit(2).ToListAsync(ct);
            if (decision == StockSourceSetupDecision.Approve)
            {
                if (linked.Count != 1) throw Invalid("missing or duplicated applied alias");
                StockSourceAliasBson.Alias(linked[0], proposal, row);
                var replacedAt = await Source(proposal);
                if (replacedAt is { } at && at < StockSourceSetupBson.Time(row["decidedAt"])) throw Invalid("approval bound to replaced source settings");
            }
            else if (linked.Count != 0) throw Invalid("unapproved applied alias");
        }
        async Task Alias(BsonDocument row)
        {
            var proposal = await One("stockSourceAliasProposals", new("_id", row["proposalId"]));
            var decision = await One("stockSourceAliasDecisions", new("_id", row["decisionId"]));
            StockSourceAliasBson.Alias(row, proposal, decision);
            await Proposal(proposal); await Decision(decision);
        }
        foreach (var name in StockSourceAliasSchemas.Collections.Concat(["auditEvents", "idempotencyRecords"]))
        {
            var filter = name switch
            {
                "auditEvents" => new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("kind", "stock.source.alias.audit"),
                    new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\.alias\."))
                }),
                "idempotencyRecords" => new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\.alias\.")),
                _ => new BsonDocument()
            };
            using var cursor = await Rows(name).FindAsync(filter, new FindOptions<BsonDocument> { BatchSize = 64 }, ct);
            while (await cursor.MoveNextAsync(ct))
                foreach (var row in cursor.Current)
                {
                    await lease.RenewAsync(ct);
                    switch (name)
                    {
                        case "stockSourceAliasProposals": await Proposal(row); break;
                        case "stockSourceAliasDecisions": await Decision(row); break;
                        case "stockSourceAliases": await Alias(row); break;
                        case "auditEvents":
                            if (row.GetValue("kind", "") != "stock.source.alias.audit" || !row.TryGetValue("record", out var record) || !record.IsBsonDocument)
                                throw Invalid("orphan alias audit");
                            var nested = record.AsBsonDocument;
                            var collection = nested.GetValue("kind", "") == "stock.source.alias.proposal" ? "stockSourceAliasProposals" :
                                nested.GetValue("kind", "") == "stock.source.alias.decision" ? "stockSourceAliasDecisions" : throw Invalid("alias audit kind");
                            var stored = await One(collection, new("_id", nested["_id"]));
                            if (!stored.Equals(nested) || stored["auditId"] != row["_id"]) throw Invalid("orphan alias audit link");
                            if (collection == "stockSourceAliasProposals") await Proposal(stored); else await Decision(stored);
                            break;
                        case "idempotencyRecords":
                            var operation = row.GetValue("operation", "").AsString;
                            if (!Operations.Contains(operation) || !row.TryGetValue("result", out var raw) || !raw.IsBsonDocument) throw Invalid("orphan alias receipt");
                            if (operation == "stock.source.alias.propose") await Proposal(await One("stockSourceAliasProposals", new("_id", raw["proposalId"])));
                            else
                            {
                                var receiptDecision = await One("stockSourceAliasDecisions", new("_id", raw["decisionId"]));
                                if (operation != "stock.source.alias." + receiptDecision["decision"].AsString)
                                    throw Invalid("receipt operation does not match its decision");
                                await Decision(receiptDecision);
                            }
                            break;
                    }
                }
        }
        long poolIdInSequence = 0, channel = 0, revision = 0;
        using var sequence = await Rows("stockSourceAliases").FindAsync(new BsonDocument(), new FindOptions<BsonDocument>
            { Sort = new BsonDocument { { "poolId", 1 }, { "requestedLocation.channelId", 1 }, { "aliasRevision", 1 } }, BatchSize = 64 }, ct);
        while (await sequence.MoveNextAsync(ct))
            foreach (var row in sequence.Current)
            {
                await lease.RenewAsync(ct);
                if (row["poolId"].AsInt64 != poolIdInSequence || row["requestedLocation"]["channelId"].AsInt64 != channel)
                { poolIdInSequence = row["poolId"].AsInt64; channel = row["requestedLocation"]["channelId"].AsInt64; revision = 0; }
                if (revision == long.MaxValue || row["aliasRevision"].AsInt64 != revision + 1) throw Invalid("missing or duplicate alias sequence");
                revision++;
            }
    }
    private static InvalidOperationException Invalid(string reason) => StockSourceAliasMigration.Invalid(reason);
}
