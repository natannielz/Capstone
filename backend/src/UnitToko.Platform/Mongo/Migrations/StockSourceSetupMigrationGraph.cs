using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Historical graph checks; past actors/grants/person sources may legitimately be inactive.
/// This step never treats a historical review reference as operational source acceptance.</summary>
internal static class StockSourceSetupMigrationGraph
{
    private static readonly string[] Operations = ["stock.source.propose", "stock.source.approve", "stock.source.reject", "stock.source.withdraw"];
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
                if (!pool["location"].Equals(StockSourceSetupBson.Location(input.Location)) ||
                    pool["active"].AsBoolean || pool["revision"].AsInt64 != 1 || pool["fence"].AsInt64 != 0)
                    throw Invalid("unaccepted pool activation or revision");
            }
        }
        async Task Audit(BsonDocument row, string operation, long actor, BsonValue at)
        {
            var linked = await Rows("auditEvents").Find(new BsonDocument
                { { "kind", "stock.source.audit" }, { "record.kind", row["kind"] }, { "record._id", row["_id"] } }).ToListAsync(ct);
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
            var idField = operation == "stock.source.propose" ? "proposalId" : "decisionId";
            var linked = await Rows("idempotencyRecords").Find(new BsonDocument
                { { "operation", operation }, { "result." + idField, row["_id"] } }).ToListAsync(ct);
            if (linked.Count != 1) throw Invalid("missing or duplicated receipt");
            var receipt = linked[0]; var reviewed = operation is "stock.source.approve" or "stock.source.reject";
            if (receipt.ElementCount != (reviewed ? 9 : 8) || !receipt["_id"].IsObjectId ||
                !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"].AsInt32 != 1 ||
                !receipt["actorId"].IsInt64 || receipt["actorId"].AsInt64 != actor ||
                !StockSourceSetupPolicy.Text(receipt["key"].AsString, 200) || !receipt["result"].Equals(result) ||
                receipt["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) ||
                StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(row[operation == "stock.source.propose" ? "proposedAt" : "decidedAt"]))
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

        foreach (var name in StockSourceSetupSchemas.Collections.Concat(["auditEvents", "idempotencyRecords"]))
        {
            var filter = name switch
            {
                "auditEvents" => new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("kind", "stock.source.audit"),
                    new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\."))
                }),
                "idempotencyRecords" => new BsonDocument("operation", new BsonDocument("$regex", @"\Astock\.source\.")),
                _ => new BsonDocument()
            };
            using var cursor = await Rows(name).FindAsync(filter, new FindOptions<BsonDocument> { BatchSize = 64 }, ct);
            while (await cursor.MoveNextAsync(ct))
                foreach (var row in cursor.Current)
                {
                    await lease.RenewAsync(ct);
                    switch (name)
                    {
                        case "stockSourceProposals": await Proposal(row); break;
                        case "stockSourceDecisions": await Decision(row); break;
                        case "inventoryPools":
                        case "inventoryPoolRegistrations":
                            var registration = name == "inventoryPoolRegistrations" ? row : await One("inventoryPoolRegistrations", new("_id", row["_id"]));
                            var decision = await One("stockSourceDecisions", new("_id", registration["decisionId"]));
                            if (decision.GetValue("decision", "") != "approve" || decision["poolId"] != row["_id"]) throw Invalid("orphan pool");
                            await Decision(decision);
                            break;
                        case "auditEvents":
                            if (row.GetValue("kind", "") != "stock.source.audit" || !row.TryGetValue("record", out var nested) || !nested.IsBsonDocument)
                                throw Invalid("orphan audit");
                            var record = nested.AsBsonDocument;
                            var collection = record.GetValue("kind", "") == "stock.source.proposal" ? "stockSourceProposals" :
                                record.GetValue("kind", "") == "stock.source.decision" ? "stockSourceDecisions" : throw Invalid("audit record kind");
                            var stored = await One(collection, new("_id", record["_id"]));
                            if (!stored.Equals(record) || stored["auditId"] != row["_id"]) throw Invalid("audit linkage");
                            if (collection == "stockSourceProposals") await Proposal(stored); else await Decision(stored);
                            break;
                        case "idempotencyRecords":
                            var operation = row.GetValue("operation", "").AsString;
                            if (!Operations.Contains(operation) || !row.TryGetValue("result", out var raw) || !raw.IsBsonDocument)
                                throw Invalid("orphan receipt");
                            var output = raw.AsBsonDocument;
                            if (operation == "stock.source.propose") await Proposal(await One("stockSourceProposals", new("_id", output["proposalId"])));
                            else await Decision(await One("stockSourceDecisions", new("_id", output["decisionId"])));
                            break;
                    }
                }
        }
    }
    private static InvalidOperationException Invalid(string reason) => StockSourceSetupMigration.Invalid(reason);
}
