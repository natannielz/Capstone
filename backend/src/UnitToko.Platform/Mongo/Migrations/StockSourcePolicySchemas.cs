using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Closed operating-policy definition storage installed by migration 19. It does not authorize activation.</summary>
internal static class StockSourcePolicySchemas
{
    internal static readonly string[] Collections = [StockSourcePolicyBson.Proposals, StockSourcePolicyBson.Decisions, StockSourcePolicyBson.Policies];
    internal static BsonDocument Key(string collection) => collection switch
    {
        StockSourcePolicyBson.Proposals => new("_id", 1),
        StockSourcePolicyBson.Decisions => new("proposalId", 1),
        StockSourcePolicyBson.Policies => new() { { "poolId", 1 }, { "policyRevision", 1 } },
        _ => throw StockSourcePolicyBson.Invalid()
    };
    internal sealed record LookupIndex(string Collection, string Name, BsonDocument Key);
    internal static LookupIndex[] LookupIndexes() =>
    [
        new(StockSourcePolicyBson.Proposals, "ix_stock_source_policy_proposals", new() { { "poolId", 1 }, { "_id", 1 } }),
        new(StockSourcePolicyBson.Decisions, "ix_stock_source_policy_decisions", new() { { "poolId", 1 }, { "policyRevision", 1 } }),
        new("auditEvents", "ix_stock_source_policy_record", new() { { "record.kind", 1 }, { "record.poolId", 1 } }),
        new("idempotencyRecords", "ix_stock_source_policy_result", new() { { "result.poolId", 1 }, { "result.basePolicyRevision", 1 }, { "result.policyId", 1 } }),
        new("auditEvents", "ix_stock_source_policy_audit", new() { { "kind", 1 }, { "record.poolId", 1 }, { "record._id", 1 } }),
        new("idempotencyRecords", "ix_stock_source_policy_receipt", new() { { "operation", 1 }, { "result.poolId", 1 }, { "result.proposalId", 1 } })
    ];
    internal static BsonDocument Validator(string collection)
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(StockSourcePolicyBson.Kind(collection)) }, { "poolId", S.Number(1) } };
        var terms = new BsonArray();
        var nullableId = new BsonDocument { { "bsonType", new BsonArray { "long", "null" } }, { "minimum", 1L } };
        switch (collection)
        {
            case StockSourcePolicyBson.Proposals:
                fields.Add("poolRevision", S.Number(1)); fields.Add("suspensionRevision", S.Number(0));
                fields.Add("basePolicyRevision", S.Number(0, long.MaxValue - 1));
                fields.Add("configuration", StockSourceSuspensionSchemas.Validator()["$and"][0]["$jsonSchema"]["properties"]["configuration"].DeepClone());
                fields.Add("configurationHash", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } });
                fields.Add("input", S.Object(new()
                {
                    { "writer", S.Choice("unitToko", "externalSystem") },
                    { "externalTenant", new BsonDocument { { "bsonType", new BsonArray { "string", "null" } }, { "minLength", 1 }, { "maxLength", 128 } } },
                    { "feedIntervalMilliseconds", S.Number(0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond) },
                    { "reconciliationProcedureReference", S.Text(160) }, { "outageProcedureReference", S.Text(160) }
                }));
                fields.Add("revision", S.Number(1, 1)); fields.Add("proposedBy", S.Number(1)); fields.Add("proposedAt", new BsonDocument("bsonType", "date"));
                S.Metadata(fields);
                terms.Add(S.DateRange("proposedAt")); terms.Add(S.DateRange("configuration.source.approvedAt"));
                terms.Add(new BsonDocument("configuration.source.active", false));
                terms.Add(new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument {
                        { "configuration.source.input.mode", "WebLedger" }, { "input.writer", "unitToko" },
                        { "input.externalTenant", BsonNull.Value }, { "input.feedIntervalMilliseconds", 0L }
                    },
                    new BsonDocument {
                        { "configuration.source.input.mode", "ExternalLedger" }, { "input.writer", "externalSystem" },
                        { "input.externalTenant", new BsonDocument("$type", "string") },
                        { "input.feedIntervalMilliseconds", new BsonDocument("$gt", 0L) }
                    }
                }));
                terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { "$poolRevision", "$configuration.source.poolRevision" }),
                    new BsonDocument("$lte", new BsonArray { "$configuration.source.approvedAt", "$proposedAt" }) })));
                break;
            case StockSourcePolicyBson.Decisions:
                fields.Add("proposalId", S.Number(1)); fields.Add("revision", S.Number(1, 1));
                fields.Add("decision", S.Choice("approve", "reject", "withdraw"));
                fields.Add("policyId", nullableId.DeepClone()); fields.Add("policyRevision", nullableId.DeepClone());
                fields.Add("decidedBy", S.Number(1)); fields.Add("decidedAt", new BsonDocument("bsonType", "date"));
                fields.Add("reviewReference", S.Text(160)); S.Metadata(fields); terms.Add(S.DateRange("decidedAt"));
                terms.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$eq", new BsonArray { "$decision", "approve" }), new BsonDocument("$ne", new BsonArray { "$policyId", BsonNull.Value }) }),
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$eq", new BsonArray { "$decision", "approve" }), new BsonDocument("$ne", new BsonArray { "$policyRevision", BsonNull.Value }) })
                })));
                break;
            case StockSourcePolicyBson.Policies:
                fields.Add("policyRevision", S.Number(1)); fields.Add("proposalId", S.Number(1)); fields.Add("decisionId", S.Number(1));
                break;
            default: throw StockSourcePolicyBson.Invalid();
        }
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields)));
        return new("$and", terms);
    }
}
