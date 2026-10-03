using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

internal static class EvidencePinRecords
{
    internal static string Hash(BsonDocument row) { var value = row.DeepClone().AsBsonDocument; value.Remove("pinHash"); return EvidenceValidationRecords.Hash(value); }
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "pinId", row["_id"] }, { "associationId", row["associationId"] },
        { "versionRowId", row["versionRowId"] }, { "actorId", row["actorId"] }, { "pinHash", row["pinHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["createdAt"] } };
    internal static async Task<BsonDocument> ReadAsync(IMongoDatabase db, IClientSessionHandle tx, long id, CancellationToken ct) {
        var pin = await db.GetCollection<BsonDocument>("evidencePins").Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), ProcurementInspectionEvidenceSchemas.Validator("evidencePins") })).SingleOrDefaultAsync(ct)
            ?? throw EvidenceValidationRecords.Broken();
        var audit = await db.GetCollection<BsonDocument>("evidencePinAudits").Find(tx, new BsonDocument("pinId", id)).Limit(2).ToListAsync(ct);
        if (pin["pinHash"] != Hash(pin) || audit.Count != 1 || !audit[0].Equals(Audit(pin))) throw EvidenceValidationRecords.Broken();
        return pin;
    }
}
