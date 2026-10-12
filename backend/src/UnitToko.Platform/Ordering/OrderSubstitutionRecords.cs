using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

/// <summary>Immutable owner records and exact original command receipts. Recovery does not
/// repair an orphan, and a hash copied from a different order or proposal is insufficient.</summary>
internal sealed class OrderSubstitutionRecords(IMongoDatabase database, IClock clock)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in OrderSubstitutionSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is null || !AccountContactStorage.SafeDefinition(definition, OrderSubstitutionSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, OrderSubstitutionSchemas.Keys(name)) ||
                OrderSubstitutionSchemas.Keys(name).Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw B.Unavailable();
        }
    }
    internal async Task<BsonDocument> ReadAsync(IClientSessionHandle tx, string name, long id, BsonDocument order,
        CancellationToken ct, bool requireReceipt = true) {
        if (!OrderSubstitutionSchemas.Collections.Contains(name) || id <= 0) throw B.Unavailable();
        var row = await Rows(name).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & OrderSubstitutionSchemas.Validator(name)).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
        var proposal = name == B.Proposals; var input = B.Input(row["input"].AsBsonDocument); OrderSubstitutionPolicy.Validate(input);
        if (row["recordHash"] != B.Hash(row) || row["orderId"] != order["_id"] || row["orderSnapshot"] != order["snapshotHash"] ||
            row["orderId"] != input.OrderId.Value || row["suborderId"] != input.SuborderId.Value ||
            !order["suborderIds"].AsBsonArray.Contains(row["suborderId"]) || row["createdBy"] != order["createdBy"] ||
            row["buyingOrganizationId"] != order["buyingOrganizationId"] || row["branchId"] != order["branchId"] || row["divisionId"] != order["divisionId"] ||
            OrderDraftBson.Time(row["recordedAt"]) < OrderDraftBson.Time(order["orderedAt"]) || clock.UtcNow < OrderDraftBson.Time(row["recordedAt"])) throw B.Unavailable();
        var sub = await Rows(OrderSubmissionBson.Suborders).Find(tx, new BsonDocument { { "_id", row["suborderId"] }, { "orderId", order["_id"] } }).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
        foreach (var field in new[] { "fulfillmentOrganizationId", "shopId", "warehouseId", "channelId" }) if (row[field] != sub[field]) throw B.Unavailable();
        var operation = proposal ? B.ProposeOperation : B.Operation(Enum.Parse<OrderSubstitutionDecision>(row["decision"].AsString));
        var body = proposal ? B.ProposalBody(input, row["review"].AsString) : B.DecisionBody(row["proposalId"].AsInt64,
            row["expectedRevision"].AsInt64, Enum.Parse<OrderSubstitutionDecision>(row["decision"].AsString), row["review"].AsString);
        if (row["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString)) throw B.Unavailable();
        if (!proposal) {
            var source = await ReadAsync(tx, B.Proposals, row["proposalId"].AsInt64, order, ct);
            if (row["proposalHash"] != source["recordHash"] || row["input"] != source["input"] ||
                row["revision"].AsInt64 != checked(row["expectedRevision"].AsInt64 + 1) ||
                OrderDraftBson.Time(row["recordedAt"]) < OrderDraftBson.Time(source["recordedAt"])) throw B.Unavailable();
        }
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogSchemas.SemanticEquals(audit, B.Audit(row, proposal))) throw B.Unavailable();
        var result = proposal ? B.ProposalResult(row) : B.DecisionResult(row);
        if (requireReceipt) {
            var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] },
                { "operation", operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
            if (receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != row["bodyHash"] ||
                !CatalogSchemas.SemanticEquals(receipt.GetValue("result", BsonNull.Value), result) ||
                OrderDraftBson.Time(receipt["createdAt"]) > OrderDraftBson.Time(row["recordedAt"])) throw B.Unavailable();
            if (proposal) { if (receipt.Contains(PersonApprovalReceipt.Field)) throw B.Unavailable(); }
            else {
                var source = await ReadAsync(tx, B.Proposals, row["proposalId"].AsInt64, order, ct);
                var evidence = new ApprovalRequest(1, 1, RequestedBy: order["createdBy"].AsInt64, ProposedBy: source["actorId"].AsInt64);
                if (PersonApprovalReceipt.Read(receipt, Permissions.SubstitutionAccept) != evidence ||
                    !ApprovalPolicy.Evaluate(row["actorId"].AsInt64, Permissions.SubstitutionAccept, evidence).Allowed) throw B.Unavailable();
            }
        }
        var outbox = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", operation + ":" + id)).SingleOrDefaultAsync(ct);
        if (outbox is null || outbox["kind"] != operation || outbox["createdAt"] != row["recordedAt"] ||
            !CatalogSchemas.SemanticEquals(outbox["payload"], result)) throw B.Unavailable();
        return row.DeepClone().AsBsonDocument;
    }
}
