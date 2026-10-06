using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Inventory.StockSourceActivationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class StockSourceActivationSchemas
{
    internal static readonly string[] Collections = [B.Proposals, B.Decisions, B.Activations];
    private static BsonDocument Long(long minimum = 1) => S.Number(minimum, long.MaxValue - 1);
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Hash() => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    private static BsonDocument True() => new() { { "bsonType", "bool" }, { "enum", new BsonArray { true } } };
    internal static BsonDocument Expected() => S.Object(new() {
        { "poolRevision", Long() }, { "poolFence", Long(0) }, { "suspensionRevision", Long(0) }, { "policyId", Long() },
        { "policyRevision", Long() }, { "activationRevision", Long(0) }, { "configurationHash", Hash() } });
    internal static BsonDocument Handoff() => S.Object(new() {
        { "reference", S.Text(160) }, { "observedAt", Date() }, { "physicalPackages", S.Number(0, 0) },
        { "carriedValueMinorUnits", S.Number(0, 0) }, { "emptyPoolConfirmed", True() }, { "previousWriterStopped", True() }, { "noUnrecordedStock", True() } });
    private static BsonDocument Obligations(int minimum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", 7 },
        { "items", S.Object(new() { { "domain", S.Choice("Orders", "Reservations", "Fulfillment", "Procurement", "AfterSales", "SourceReconciliation", "InventoryOperations") },
            { "producerRevision", Long() }, { "complete", True() }, { "openCount", S.Number(0, 0) } }) } };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(B.Header(name, 1)["kind"].AsString) }, { "poolId", Long() } };
        var terms = new BsonArray();
        if (name == B.Proposals) {
            fields.Add("revision", S.Number(1, 1)); fields.Add("expected", Expected());
            fields.Add("configuration", StockSourcePolicySchemas.Validator(StockSourcePolicyBson.Proposals)["$and"][0]["$jsonSchema"]["properties"]["configuration"].DeepClone());
            fields.Add("policyHash", Hash()); fields.Add("handoff", Handoff()); fields.Add("obligations", Obligations(7));
            terms.Add(S.DateRange("handoff.observedAt")); terms.Add(new BsonDocument("configuration.source.active", false));
            terms.Add(new BsonDocument("configuration.source.input.mode", "WebLedger"));
        } else if (name == B.Decisions) {
            fields.Add("proposalId", Long()); fields.Add("proposalHash", Hash()); fields.Add("revision", S.Number(1, 1));
            fields.Add("decision", S.Choice("approve", "reject")); fields.Add("handoff", Nullable(Handoff())); fields.Add("expected", Expected());
            fields.Add("binding", Hash()); fields.Add("activationId", Nullable(Long())); fields.Add("activationRevision", Nullable(Long())); fields.Add("obligations", Obligations(0));
            terms.Add(new BsonDocument("$or", new BsonArray {
                new BsonDocument { { "decision", "approve" }, { "handoff", new BsonDocument("$type", "object") },
                    { "activationId", new BsonDocument("$type", "long") }, { "activationRevision", new BsonDocument("$type", "long") },
                    { "obligations", new BsonDocument("$size", 7) } },
                new BsonDocument { { "decision", "reject" }, { "handoff", BsonNull.Value }, { "activationId", BsonNull.Value },
                    { "activationRevision", BsonNull.Value }, { "obligations", new BsonDocument("$size", 0) } } }));
        } else if (name == B.Activations) {
            fields.Add("activationRevision", Long()); fields.Add("proposalId", Long()); fields.Add("decisionId", Long());
            fields.Add("proposalHash", Hash()); fields.Add("decisionHash", Hash()); fields.Add("expected", Expected());
        } else throw new ArgumentOutOfRangeException(nameof(name));
        fields.Add("committedPoolFence", Long()); fields.Add("recordedAt", Date()); fields.Add("snapshotHash", Hash());
        if (name != B.Activations) {
            fields.Add("actorId", Long()); fields.Add("actorGrantVersion", Long()); fields.Add("requestKey", S.Text(200)); fields.Add("bodyHash", Hash());
            fields.Add("reason", S.Text(1000)); fields.Add("correlationId", S.Text(128)); fields.Add("auditId", Long());
        }
        terms.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields))); terms.Add(S.DateRange("recordedAt")); return new("$and", terms);
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        B.Proposals => [("ux_activation_proposal_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
            ("ix_activation_proposal_pool", new() { { "poolId", 1 }, { "_id", 1 } }, false)],
        B.Decisions => [("ux_activation_proposal_decision", new("proposalId", 1), true),
            ("ux_activation_decision_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        B.Activations => [("ux_activation_revision", new() { { "poolId", 1 }, { "activationRevision", 1 } }, true),
            ("ux_activation_proposal", new("proposalId", 1), true), ("ux_activation_decision", new("decisionId", 1), true)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
}
