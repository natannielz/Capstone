using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Streaming version-19 policy provenance. Historical participants are not current person proofs.</summary>
internal static class StockSourcePolicyMigrationGraph
{
    internal static BsonDocument ProvenanceFilter(string collection)
    {
        var alternatives = new BsonArray {
            new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\.policy(?:\.|$)"))
        };
        if (collection == "auditEvents")
        {
            alternatives.Add(new BsonDocument("kind", "stock.source.policy.audit"));
            alternatives.Add(new BsonDocument("record.kind", new BsonDocument("$in", new BsonArray { "stock.source.policy.proposal", "stock.source.policy.decision" })));
            PoolFields("record", "basePolicyRevision", "policyRevision");
        }
        else PoolFields("result", "basePolicyRevision", "policyId", "policyRevision");
        return new("$or", alternatives);
        void PoolFields(string prefix, params string[] names) =>
            alternatives.Add(new BsonDocument {
                { prefix + ".poolId", new BsonDocument("$exists", true) },
                { "$or", new BsonArray(names.Select(name => new BsonDocument(prefix + "." + name, new BsonDocument("$exists", true)))) }
            });
    }
    internal static async Task ValidateAsync(IMongoDatabase database, MigrationLease lease, IClock clock, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        async Task<BsonDocument> One(string name, BsonDocument filter) =>
            await Rows(name).Find(filter).SingleOrDefaultAsync(ct) ?? throw Invalid("missing " + name);
        async Task Participant(BsonValue id)
        {
            var actor = await One("identities", new("_id", id));
            if (!actor["_id"].IsInt64 || actor["_id"].AsInt64 <= 0) throw Invalid("historical participant identity");
        }
        async Task BindingAt(BsonDocument proposal, DateTimeOffset at)
        {
            if (await Rows("stockSourceRevisionDecisions").Find(new BsonDocument {
                { "poolId", proposal["poolId"] }, { "decision", "approve" },
                { "poolRevision", new BsonDocument("$gt", proposal["poolRevision"]) }, { "decidedAt", new BsonDocument("$lt", at.UtcDateTime) }
            }).AnyAsync(ct) || await Rows(StockSourceSuspensionSchemas.Collection).Find(new BsonDocument {
                { "poolId", proposal["poolId"] }, { "suspensionRevision", new BsonDocument("$gt", proposal["suspensionRevision"]) },
                { "suspendedAt", new BsonDocument("$lt", at.UtcDateTime) }
            }).AnyAsync(ct)) throw Invalid("already superseded policy binding");
            var mappings = proposal["configuration"]["mappings"].AsBsonArray.Select(value => value.AsBsonDocument)
                .ToDictionary(value => value["requestedLocation"]["channelId"].AsInt64);
            using var cursor = await Rows("stockSourceAliasDecisions").FindAsync(new BsonDocument {
                { "poolId", proposal["poolId"] }, { "decision", "approve" }, { "decidedAt", new BsonDocument("$lt", at.UtcDateTime) }
            }, new FindOptions<BsonDocument> { BatchSize = 64 }, ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
            {
                await lease.RenewAsync(ct);
                if (!mappings.TryGetValue(row["requestedLocation"]["channelId"].AsInt64, out var mapping) ||
                    mapping["aliasRevision"].AsInt64 < row["aliasRevision"].AsInt64) throw Invalid("missing or replaced policy channel");
            }
        }
        async Task Binding(BsonDocument proposal)
        {
            var pool = await One("inventoryPools", new("_id", proposal["poolId"])); InventoryGraph.RequirePool(pool);
            var source = proposal["configuration"]["source"].AsBsonDocument;
            var registration = await One("inventoryPoolRegistrations", new("_id", proposal["poolId"]));
            if (proposal["poolRevision"].AsInt64 > pool["revision"].AsInt64 ||
                !source["input"]["location"].Equals(pool["location"]) || source["creationProposalId"] != registration["proposalId"] ||
                source["input"]["physicalKey"] != registration["physicalKey"]) throw Invalid("policy physical ownership");
            BsonDocument input; BsonValue approvedAt;
            if (proposal["poolRevision"] == 1L)
            {
                var original = await One("stockSourceProposals", new("_id", registration["proposalId"]));
                var decision = await One("stockSourceDecisions", new("_id", registration["decisionId"]));
                input = original["input"].AsBsonDocument; approvedAt = decision["decidedAt"];
            }
            else
            {
                var revision = await One("stockSourceRevisions", new() { { "poolId", proposal["poolId"] }, { "poolRevision", proposal["poolRevision"] } });
                if (revision["proposalId"] != source["revisionProposalId"]) throw Invalid("policy source revision");
                input = revision["input"].AsBsonDocument;
                approvedAt = (await One("stockSourceRevisionDecisions", new("_id", revision["decisionId"])))["decidedAt"];
            }
            if (!input.Equals(source["input"]) || approvedAt != source["approvedAt"]) throw Invalid("policy source snapshot");
            var at = Time(proposal["proposedAt"]);
            if (proposal["suspensionRevision"].AsInt64 > 0)
            {
                var stop = await One(StockSourceSuspensionSchemas.Collection, new() {
                    { "poolId", proposal["poolId"] }, { "suspensionRevision", proposal["suspensionRevision"] }
                });
                if (Time(stop["suspendedAt"]) > at) throw Invalid("future policy suspension");
            }
            foreach (var value in proposal["configuration"]["mappings"].AsBsonArray)
            {
                await lease.RenewAsync(ct);
                var mapping = value.AsBsonDocument; var alias = await One("stockSourceAliases", new("_id", mapping["aliasId"]));
                foreach (var field in new[] { "aliasRevision", "poolRevision", "requestedLocation", "enabled", "proposalId", "decisionId" })
                    if (!alias[field].Equals(mapping[field])) throw Invalid("policy alias snapshot");
                if (alias["poolId"] != proposal["poolId"] || !alias["ownerLocation"].Equals(pool["location"]) ||
                    alias["physicalKey"] != registration["physicalKey"] ||
                    Time((await One("stockSourceAliasDecisions", new("_id", alias["decisionId"])))["decidedAt"]) > at) throw Invalid("policy alias provenance");
            }
            await BindingAt(proposal, at);
            var baseVersion = proposal["basePolicyRevision"].AsInt64;
            if (baseVersion > 0)
            {
                var previous = await One(StockSourcePolicyBson.Policies, new() { { "poolId", proposal["poolId"] }, { "policyRevision", baseVersion } });
                var decision = await One(StockSourcePolicyBson.Decisions, new("_id", previous["decisionId"]));
                if (Time(decision["decidedAt"]) > at) throw Invalid("future accepted policy base");
            }
            if (await Rows(StockSourcePolicyBson.Decisions).Find(new BsonDocument {
                { "poolId", proposal["poolId"] }, { "decision", "approve" }, { "policyRevision", new BsonDocument("$gt", baseVersion) },
                { "decidedAt", new BsonDocument("$lt", at.UtcDateTime) }
            }).AnyAsync(ct)) throw Invalid("already superseded policy base");
        }
        async Task Provenance(BsonDocument proposal, BsonDocument? decision)
        {
            var row = decision ?? proposal; var operation = StockSourcePolicyBson.Prefix + (decision?["decision"].AsString ?? "propose");
            var actor = row[decision is null ? "proposedBy" : "decidedBy"]; var at = row[decision is null ? "proposedAt" : "decidedAt"];
            await Participant(actor);
            StockSourcePolicyBson.RequireAudit(await One("auditEvents", new("_id", row["auditId"])), row, operation, actor, at);
            var receipts = await Rows("idempotencyRecords").Find(new BsonDocument {
                { "operation", operation }, { "result.poolId", proposal["poolId"] }, { "result.proposalId", proposal["_id"] }
            }).Limit(2).ToListAsync(ct);
            if (receipts.Count != 1) throw Invalid("missing or duplicate policy receipt");
            StockSourcePolicyBson.Receipt(receipts[0], proposal, decision);
        }
        foreach (var name in StockSourcePolicySchemas.Collections)
        {
            long poolId = 0, version = 0; BsonDocument? previousProposal = null, previousDecision = null;
            using var cursor = await Rows(name).FindAsync(new BsonDocument(), new FindOptions<BsonDocument> {
                BatchSize = 64, Sort = name == StockSourcePolicyBson.Policies ? new BsonDocument { { "poolId", 1 }, { "policyRevision", 1 } } : new BsonDocument("_id", 1)
            }, ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
            {
                await lease.RenewAsync(ct);
                if (name == StockSourcePolicyBson.Proposals)
                {
                    StockSourcePolicyBson.Proposal(row);
                    if (Time(row["proposedAt"]) > clock.UtcNow) throw Invalid("future policy proposal");
                    await Binding(row); await Provenance(row, null);
                }
                else if (name == StockSourcePolicyBson.Decisions)
                {
                    var proposal = await One(StockSourcePolicyBson.Proposals, new("_id", row["proposalId"]));
                    StockSourcePolicyBson.Decision(row, proposal);
                    if (Time(row["decidedAt"]) > clock.UtcNow) throw Invalid("future policy decision");
                    await Provenance(proposal, row);
                    if (row["decision"] == "approve")
                    {
                        await BindingAt(proposal, Time(row["decidedAt"]));
                        StockSourcePolicyBson.Policy(await One(StockSourcePolicyBson.Policies, new("_id", row["policyId"])), proposal, row);
                    }
                }
                else
                {
                    var proposal = await One(StockSourcePolicyBson.Proposals, new("_id", row["proposalId"]));
                    var decision = await One(StockSourcePolicyBson.Decisions, new("_id", row["decisionId"]));
                    StockSourcePolicyBson.Policy(row, proposal, decision);
                    if (row["poolId"].AsInt64 != poolId) { poolId = row["poolId"].AsInt64; version = 0; previousProposal = null; previousDecision = null; }
                    if (version == long.MaxValue || row["policyRevision"].AsInt64 != ++version) throw Invalid("accepted policy sequence");
                    if (previousProposal is not null && previousDecision is not null)
                        StockSourcePolicyBson.AcceptedContinuity(previousProposal, previousDecision, proposal);
                    previousProposal = proposal; previousDecision = decision;
                }
            }
        }
        foreach (var name in new[] { "auditEvents", "idempotencyRecords" })
        {
            using var cursor = await Rows(name).FindAsync(ProvenanceFilter(name), new FindOptions<BsonDocument> { BatchSize = 64 }, ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
            {
                await lease.RenewAsync(ct);
                if (name == "auditEvents")
                {
                    if (!row.TryGetValue("record", out var raw) || !raw.IsBsonDocument) throw Invalid("orphan policy audit");
                    var nested = raw.AsBsonDocument;
                    var proposed = nested.GetValue("kind", "") == StockSourcePolicyBson.Kind(StockSourcePolicyBson.Proposals);
                    var actual = await One(proposed ? StockSourcePolicyBson.Proposals : StockSourcePolicyBson.Decisions, new("_id", nested["_id"]));
                    StockSourcePolicyBson.RequireAudit(row, actual, StockSourcePolicyBson.Prefix + (proposed ? "propose" : actual["decision"].AsString),
                        actual[proposed ? "proposedBy" : "decidedBy"], actual[proposed ? "proposedAt" : "decidedAt"]);
                }
                else
                {
                    if (!row.TryGetValue("result", out var raw) || !raw.IsBsonDocument ||
                        !StockSourcePolicyBson.Positive(raw.AsBsonDocument, "proposalId")) throw Invalid("orphan policy receipt");
                    var proposal = await One(StockSourcePolicyBson.Proposals, new("_id", raw["proposalId"]));
                    var decision = row.GetValue("operation", "") == "stock.source.policy.propose" ? null :
                        await One(StockSourcePolicyBson.Decisions, new("proposalId", proposal["_id"]));
                    StockSourcePolicyBson.Receipt(row, proposal, decision);
                }
            }
        }
    }
    private static DateTimeOffset Time(BsonValue value) => StockSourceSetupBson.Time(value);
    private static InvalidOperationException Invalid(string reason) => StockSourcePolicyMigration.Invalid(reason);
}