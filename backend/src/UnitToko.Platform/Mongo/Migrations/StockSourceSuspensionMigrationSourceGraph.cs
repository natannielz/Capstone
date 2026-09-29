using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Historical graph checks; past actors/grants/person sources may legitimately be inactive.
/// Version-18 snapshot; applied versions 1-17 remain unchanged. Legacy active flags are preserved, not accepted as control.
/// Alias and suspension records are validated by companion graphs before DDL. References are not source acceptance.</summary>
internal static class StockSourceSuspensionMigrationSourceGraph
{
    private static readonly string[] Operations = ["stock.source.propose", "stock.source.approve", "stock.source.reject", "stock.source.withdraw", "stock.source.revision.propose", "stock.source.revision.approve", "stock.source.revision.reject", "stock.source.revision.withdraw"];
    internal static async Task ValidateAsync(IMongoDatabase database, MigrationLease lease, CancellationToken ct)
    {
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
        async Task<BsonDocument> One(string name, BsonDocument filter) =>
            await Rows(name).Find(filter).SingleOrDefaultAsync(ct) ?? throw Invalid("missing linked " + name);

        async Task Proposal(BsonDocument row)
        {
            StockSourceSetupBson.RequireProposal(row);
            var input = StockSourceSetupBson.ReadInput(row["input"].AsBsonDocument);
            var location = input.Location;
            var dimensions = new[]
            {
                (location.OrganizationId.Value, "fulfillmentOrganization", (long?)null),
                (location.ShopId.Value, "shop", (long?)location.OrganizationId.Value),
                (location.WarehouseId.Value, "warehouse", (long?)location.ShopId.Value),
                (location.ChannelId.Value, "channel", (long?)location.OrganizationId.Value)
            };
            foreach (var (id, kind, parent) in dimensions)
            {
                var node = await One("authorizationScopes", new("_id", id));
                if (!node["_id"].IsInt64 || node.GetValue("kind", "") != kind ||
                    node.GetValue("parentId", BsonNull.Value) != (parent.HasValue ? new BsonInt64(parent.Value) : BsonNull.Value) ||
                    !node.TryGetValue("version", out var version) || !version.IsInt64 || version.AsInt64 <= 0)
                    throw Invalid("scope ancestry");
            }
            var identity = await One("identities", new("_id", row["proposedBy"]));
            if (!identity["_id"].IsInt64) throw Invalid("historical actor type");
            await Audit(row, "stock.source.propose", row["proposedBy"].AsInt64, row["proposedAt"]);
            await Receipt(row, row, "stock.source.propose", row["proposedBy"].AsInt64,
                new() { { "proposalId", row["_id"] }, { "revision", 1L } }, input);
        }
        async Task Decision(BsonDocument row)
        {
            var proposal = await One("stockSourceProposals", new("_id", row["proposalId"]));
            StockSourceSetupBson.RequireProposal(proposal);
            StockSourceSetupBson.RequireDecision(row, proposal);
            var identity = await One("identities", new("_id", row["decidedBy"]));
            if (!identity["_id"].IsInt64) throw Invalid("historical reviewer type");
            var name = row["decision"].AsString;
            var decision = name switch
            {
                "approve" => StockSourceSetupDecision.Approve, "reject" => StockSourceSetupDecision.Reject,
                "withdraw" => StockSourceSetupDecision.Withdraw, _ => throw Invalid("decision")
            };
            await Audit(row, "stock.source." + name, row["decidedBy"].AsInt64, row["decidedAt"]);
            var proposalId = row["proposalId"].AsInt64; var expectedRevision = row["revision"].AsInt64; var reviewReference = row["reviewReference"].AsString;
            await Receipt(row, proposal, "stock.source." + name, row["decidedBy"].AsInt64,
                new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "poolId", row["poolId"] }, { "status", name } },
                new { proposalId, expectedRevision, decision, reviewReference });
            if (decision == StockSourceSetupDecision.Approve)
            {
                var input = StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument);
                var registration = await One("inventoryPoolRegistrations", new("_id", row["poolId"]));
                var expected = StockSourceSetupBson.Header("inventoryPoolRegistrations", row["poolId"].AsInt64);
                expected.Add("organizationId", input.Location.OrganizationId.Value); expected.Add("physicalKey", input.PhysicalKey);
                expected.Add("proposalId", row["proposalId"]); expected.Add("decisionId", row["_id"]);
                if (!expected.Equals(registration)) throw Invalid("pool registration");
                var pool = await One("inventoryPools", new("_id", row["poolId"]));
                InventoryGraph.RequirePool(pool);
                if (!pool["location"].Equals(StockSourceSetupBson.Location(input.Location)))
                    throw Invalid("pool ownership change");
                await PoolSequence(pool, proposal, row);
            }
        }
        async Task Audit(BsonDocument row, string operation, long actor, BsonValue at)
        {
            var linked = await Rows("auditEvents").Find(new BsonDocument
                { { "kind", operation.StartsWith("stock.source.revision.", StringComparison.Ordinal) ? "stock.source.revision.audit" : "stock.source.audit" }, { "record.kind", row["kind"] }, { "record._id", row["_id"] } }).Limit(2).ToListAsync(ct);
            if (linked.Count != 1) throw Invalid("missing or duplicated audit");
            var audit = linked[0];
            if (audit.ElementCount != 8 || !audit["_id"].IsInt64 || audit["_id"] != row["auditId"] ||
                !audit["schemaVersion"].IsInt32 || audit["schemaVersion"].AsInt32 != 1 || audit["operation"] != operation ||
                !audit["actorId"].IsInt64 || audit["actorId"].AsInt64 != actor ||
                !audit["actorGrantVersion"].IsInt64 || audit["actorGrantVersion"].AsInt64 <= 0 ||
                audit["createdAt"] != at || !audit["record"].Equals(row)) throw Invalid("audit provenance");
        }
        async Task Receipt(BsonDocument row, BsonDocument proposal, string operation, long actor, BsonDocument result, object body)
        {
            var idField = operation.EndsWith(".propose", StringComparison.Ordinal) ? "proposalId" : "decisionId";
            var linked = await Rows("idempotencyRecords").Find(new BsonDocument
                { { "operation", operation }, { "result." + idField, row["_id"] } }).Limit(2).ToListAsync(ct);
            if (linked.Count != 1) throw Invalid("missing or duplicated receipt");
            var receipt = linked[0]; var reviewed = operation.EndsWith(".approve", StringComparison.Ordinal) || operation.EndsWith(".reject", StringComparison.Ordinal);
            if (receipt.ElementCount != (reviewed ? 9 : 8) || !receipt["_id"].IsObjectId ||
                !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"].AsInt32 != 1 ||
                !receipt["actorId"].IsInt64 || receipt["actorId"].AsInt64 != actor ||
                !StockSourceSetupPolicy.Text(receipt["key"].AsString, 200) || !receipt["result"].Equals(result) ||
                receipt["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) ||
                StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(row[operation.EndsWith(".propose", StringComparison.Ordinal) ? "proposedAt" : "decidedAt"]))
                throw Invalid("receipt provenance");
            if (reviewed)
            {
                var expected = new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64);
                var evidence = PersonApprovalReceipt.Read(receipt, Permissions.StockSourceApprove);
                if (evidence != expected || !ApprovalPolicy.Evaluate(actor, Permissions.StockSourceApprove, evidence).Allowed)
                    throw Invalid("historical approval participants");
            }
            else if (receipt.Contains(PersonApprovalReceipt.Field)) throw Invalid("unexpected approval participants");
        }

        async Task<(BsonDocument Pool, BsonDocument Proposal, BsonDocument Decision)> Root(long poolId)
        {
            var registration = await One("inventoryPoolRegistrations", new("_id", poolId));
            var proposal = await One("stockSourceProposals", new("_id", registration["proposalId"]));
            var decision = await One("stockSourceDecisions", new("_id", registration["decisionId"]));
            StockSourceSetupBson.RequireProposal(proposal); StockSourceSetupBson.RequireDecision(decision, proposal);
            if (decision["decision"] != "approve" || !decision["poolId"].IsInt64 || decision["poolId"].AsInt64 != poolId)
                throw Invalid("revision original approval");
            var facts = StockSourceSetupBson.ReadInput(proposal["input"].AsBsonDocument);
            var expected = StockSourceSetupBson.Header("inventoryPoolRegistrations", poolId);
            expected.Add("organizationId", facts.Location.OrganizationId.Value); expected.Add("physicalKey", facts.PhysicalKey);
            expected.Add("proposalId", proposal["_id"]); expected.Add("decisionId", decision["_id"]);
            var pool = await One("inventoryPools", new("_id", poolId));
            InventoryGraph.RequirePool(pool);
            if (!expected.Equals(registration) ||
                !pool["location"].Equals(StockSourceSetupBson.Location(facts.Location))) throw Invalid("revision physical owner");
            return (pool, proposal, decision);
        }
        async Task RevisionProposal(BsonDocument row)
        {
            StockSourceRevisionBson.Proposal(row);
            var (pool, original, originalDecision) = await Root(row["poolId"].AsInt64);
            var baseRevision = row["basePoolRevision"].AsInt64;
            if (baseRevision > pool["revision"].AsInt64) throw Invalid("future revision base");
            var baseInput = original["input"]; var baseApprovedAt = originalDecision["decidedAt"];
            if (baseRevision > 1)
            {
                var previous = await One("stockSourceRevisions", new() { { "poolId", row["poolId"] }, { "poolRevision", baseRevision } });
                var previousProposal = await One("stockSourceRevisionProposals", new("_id", previous["proposalId"]));
                var previousDecision = await One("stockSourceRevisionDecisions", new("_id", previous["decisionId"]));
                StockSourceRevisionBson.Revision(previous, previousProposal, previousDecision);
                baseInput = previous["input"]; baseApprovedAt = previousDecision["decidedAt"];
            }
            if (!row["baseInput"].Equals(baseInput) ||
                StockSourceSetupBson.Time(row["proposedAt"]) < StockSourceSetupBson.Time(baseApprovedAt)) throw Invalid("revision base snapshot or chronology");
            var identity = await One("identities", new("_id", row["proposedBy"]));
            if (!identity["_id"].IsInt64) throw Invalid("historical revision actor type");
            await Audit(row, "stock.source.revision.propose", row["proposedBy"].AsInt64, row["proposedAt"]);
            var poolId = row["poolId"].AsInt64; var expectedPoolRevision = baseRevision;
            var input = StockSourceSetupBson.ReadInput(row["input"].AsBsonDocument);
            await Receipt(row, row, "stock.source.revision.propose", row["proposedBy"].AsInt64,
                new() { { "proposalId", row["_id"] }, { "revision", 1L }, { "poolId", poolId }, { "basePoolRevision", baseRevision } },
                new { poolId, expectedPoolRevision, input });
        }
        async Task RevisionDecision(BsonDocument row)
        {
            var proposal = await One("stockSourceRevisionProposals", new("_id", row["proposalId"]));
            StockSourceRevisionBson.Decision(row, proposal);
            var identity = await One("identities", new("_id", row["decidedBy"]));
            if (!identity["_id"].IsInt64) throw Invalid("historical revision reviewer type");
            var name = row["decision"].AsString;
            var decision = name switch
            {
                "approve" => StockSourceSetupDecision.Approve, "reject" => StockSourceSetupDecision.Reject,
                "withdraw" => StockSourceSetupDecision.Withdraw, _ => throw Invalid("revision decision")
            };
            await Audit(row, "stock.source.revision." + name, row["decidedBy"].AsInt64, row["decidedAt"]);
            var proposalId = row["proposalId"].AsInt64; var expectedRevision = row["revision"].AsInt64;
            var reviewReference = row["reviewReference"].AsString;
            await Receipt(row, proposal, "stock.source.revision." + name, row["decidedBy"].AsInt64,
                new() { { "proposalId", proposalId }, { "decisionId", row["_id"] }, { "poolId", row["poolId"] },
                    { "poolRevision", row["poolRevision"] }, { "status", name } },
                new { proposalId, expectedRevision, decision, reviewReference });
            var linked = await Rows("stockSourceRevisions").Find(new BsonDocument("$or", new BsonArray
                { new BsonDocument("proposalId", proposalId), new BsonDocument("decisionId", row["_id"]) })).Limit(2).ToListAsync(ct);
            if (decision == StockSourceSetupDecision.Approve)
            {
                if (linked.Count != 1) throw Invalid("missing or duplicated applied revision");
                StockSourceRevisionBson.Revision(linked[0], proposal, row);
            }
            else if (linked.Count != 0) throw Invalid("unapproved applied revision");
        }
        async Task Revision(BsonDocument row)
        {
            var proposal = await One("stockSourceRevisionProposals", new("_id", row["proposalId"]));
            var decision = await One("stockSourceRevisionDecisions", new("_id", row["decisionId"]));
            StockSourceRevisionBson.Revision(row, proposal, decision);
            await RevisionProposal(proposal); await RevisionDecision(decision);
        }
        async Task PoolSequence(BsonDocument pool, BsonDocument original, BsonDocument originalDecision)
        {
            long version = 1; var input = original["input"]; var approvedAt = originalDecision["decidedAt"];
            using var history = await Rows("stockSourceRevisions").FindAsync(new BsonDocument("poolId", pool["_id"]),
                new FindOptions<BsonDocument> { Sort = new BsonDocument("poolRevision", 1), BatchSize = 64 }, ct);
            while (await history.MoveNextAsync(ct))
                foreach (var revision in history.Current)
                {
                    await lease.RenewAsync(ct);
                    if (!revision["poolRevision"].IsInt64 || version == long.MaxValue || revision["poolRevision"].AsInt64 != version + 1)
                        throw Invalid("missing or duplicate revision sequence");
                    var proposal = await One("stockSourceRevisionProposals", new("_id", revision["proposalId"]));
                    var decision = await One("stockSourceRevisionDecisions", new("_id", revision["decisionId"]));
                    StockSourceRevisionBson.Revision(revision, proposal, decision);
                    if (proposal["basePoolRevision"].AsInt64 != version || !proposal["baseInput"].Equals(input) ||
                        StockSourceSetupBson.Time(proposal["proposedAt"]) < StockSourceSetupBson.Time(approvedAt))
                        throw Invalid("approved revision continuity");
                    version++; input = revision["input"]; approvedAt = decision["decidedAt"];
                }
            if (pool["revision"].AsInt64 != version) throw Invalid("unexplained current pool head");
        }
        foreach (var name in StockSourceSetupSchemas.Collections.Concat(StockSourceRevisionSchemas.Collections).Concat(["auditEvents", "idempotencyRecords"]))
        {
            var filter = name switch
            {
                "auditEvents" => new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("kind", new BsonDocument("$in", new BsonArray { "stock.source.audit", "stock.source.revision.audit" })),
                    new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\."))
                }),
                "idempotencyRecords" => new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\.")),
                _ => new BsonDocument()
            };
            // Partition only the known alias/suspension families; companion validators inspect every excluded row.
            // Unknown operations still fail; an active flag is historical state and grants no new writer capability.
            if (name is "auditEvents" or "idempotencyRecords")
            {
                var alias = new BsonArray { new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\.alias\.")), new BsonDocument("operation", "stock.source.suspend") };
                if (name == "auditEvents") { alias.Add(new BsonDocument("kind", "stock.source.alias.audit")); alias.Add(new BsonDocument("kind", "stock.source.suspension.audit")); }
                filter = new("$and", new BsonArray { filter, new BsonDocument("$nor", alias) });
            }
            using var cursor = await Rows(name).FindAsync(filter, new FindOptions<BsonDocument> { BatchSize = 64 }, ct);
            while (await cursor.MoveNextAsync(ct))
                foreach (var row in cursor.Current)
                {
                    await lease.RenewAsync(ct);
                    switch (name)
                    {
                        case "stockSourceProposals": await Proposal(row); break;
                        case "stockSourceDecisions": await Decision(row); break;
                        case "stockSourceRevisionProposals": await RevisionProposal(row); break;
                        case "stockSourceRevisionDecisions": await RevisionDecision(row); break;
                        case "stockSourceRevisions": await Revision(row); break;
                        case "inventoryPools":
                        case "inventoryPoolRegistrations":
                            var registration = name == "inventoryPoolRegistrations" ? row : await One("inventoryPoolRegistrations", new("_id", row["_id"]));
                            var decision = await One("stockSourceDecisions", new("_id", registration["decisionId"]));
                            if (decision.GetValue("decision", "") != "approve" || decision["poolId"] != row["_id"]) throw Invalid("orphan pool");
                            await Decision(decision);
                            break;
                        case "auditEvents":
                            if (row.GetValue("kind", "") != "stock.source.audit" && row.GetValue("kind", "") != "stock.source.revision.audit" || !row.TryGetValue("record", out var nested) || !nested.IsBsonDocument)
                                throw Invalid("orphan audit");
                            var record = nested.AsBsonDocument;
                            var collection = record.GetValue("kind", "") == "stock.source.proposal" ? "stockSourceProposals" :
                                record.GetValue("kind", "") == "stock.source.decision" ? "stockSourceDecisions" :
                                record.GetValue("kind", "") == "stock.source.revision.proposal" ? "stockSourceRevisionProposals" :
                                record.GetValue("kind", "") == "stock.source.revision.decision" ? "stockSourceRevisionDecisions" : throw Invalid("audit record kind");
                            var stored = await One(collection, new("_id", record["_id"]));
                            if (!stored.Equals(record) || stored["auditId"] != row["_id"]) throw Invalid("audit linkage");
                            if (collection == "stockSourceProposals") await Proposal(stored);
                            else if (collection == "stockSourceDecisions") await Decision(stored);
                            else if (collection == "stockSourceRevisionProposals") await RevisionProposal(stored); else await RevisionDecision(stored);
                            break;
                        case "idempotencyRecords":
                            var operation = row.GetValue("operation", "").AsString;
                            if (!Operations.Contains(operation) || !row.TryGetValue("result", out var raw) || !raw.IsBsonDocument)
                                throw Invalid("orphan receipt");
                            var output = raw.AsBsonDocument;
                            if (operation == "stock.source.propose") await Proposal(await One("stockSourceProposals", new("_id", output["proposalId"])));
                            else if (operation == "stock.source.revision.propose") await RevisionProposal(await One("stockSourceRevisionProposals", new("_id", output["proposalId"])));
                            else
                            {
                                var revisionOperation = operation.StartsWith("stock.source.revision.", StringComparison.Ordinal);
                                var receiptDecision = await One(revisionOperation ? "stockSourceRevisionDecisions" : "stockSourceDecisions", new("_id", output["decisionId"]));
                                var prefix = revisionOperation ? "stock.source.revision." : "stock.source.";
                                if (operation != prefix + receiptDecision["decision"].AsString)
                                    throw Invalid("receipt operation does not match its decision");
                                if (revisionOperation) await RevisionDecision(receiptDecision); else await Decision(receiptDecision);
                            }
                            break;
                    }
                }
        }
    }
    private static InvalidOperationException Invalid(string reason) => StockSourceSuspensionMigration.Invalid(reason);
}
