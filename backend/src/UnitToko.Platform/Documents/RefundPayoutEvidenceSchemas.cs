using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Documents;

// Forward-only v55 extension. All preceding accepted validators remain exact branches.
internal static class RefundPayoutEvidenceSchemas
{
    internal static readonly string[] Collections = ["evidenceLineages", "evidenceUploadReservations", "auditEvents", "idempotencyRecords", "evidenceReadLeases", "evidencePins"];
    internal static BsonDocument PreviousValidator(string name) => name switch {
        "evidenceReadLeases" => EvidenceAccessSchemas.Validator(name), "evidencePins" => PaymentSubmissionSchemas.Validator(name),
        _ when PaymentEvidenceSchemas.Collections.Contains(name) => PaymentEvidenceSchemas.Validator(name),
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var previous = PreviousValidator(name); BsonDocument outgoing;
        if (PaymentEvidenceSchemas.Collections.Contains(name)) {
            outgoing = Translate(EvidenceSchemas.Validator(name)).AsBsonDocument;
            if (name == "evidenceUploadReservations") outgoing = new("$and", new BsonArray { outgoing,
                new BsonDocument("admittedDefinitionId", new BsonDocument("$type", "null")),
                new BsonDocument("admittedDefinitionVersion", new BsonDocument("$type", "null")) });
        } else if (name == "evidenceReadLeases") {
            outgoing = previous.DeepClone().AsBsonDocument;
            outgoing["$and"][0]["$jsonSchema"]["properties"]["target"]["properties"]["kind"] = S.Choice("RefundPayout");
        } else {
            outgoing = previous.DeepClone().AsBsonDocument;
            var shape = outgoing["$and"][0]["$jsonSchema"].AsBsonDocument;
            var fields = shape["properties"].AsBsonDocument;
            foreach (var field in new[] { "declarationId", "declarationVersionId", "declarationRevision", "reviewRevision" }) fields.Remove(field);
            fields["associationKind"] = S.Choice("RefundPayout");
            fields.Add("refundPayoutDraftId", S.Number(1)); fields.Add("refundPayoutDraftRevision", S.Number(1, 1));
            shape["required"] = new BsonArray(fields.Names);
        }
        return new("$or", new BsonArray { previous, outgoing });
    }
    internal static BsonDocument Extend(string name, BsonDocument current) {
        var before = PreviousValidator(name); var after = Validator(name); var found = false;
        BsonDocument Replace(BsonDocument row) {
            if (CatalogSchemas.SemanticEquals(row, after)) { found = true; return row.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(row, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = row.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(value => value.IsBsonDocument ? Replace(value.AsBsonDocument) : value.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw new InvalidOperationException("Refund payout evidence migration requires the accepted predecessor validator: " + name);
    }
    // A caller requesting the original exact branch may also use this one known successor.
    // Arbitrary $or trees, unknown weaker branches and modified successors never qualify.
    internal static bool Supports(string name, BsonDocument current, BsonDocument required) =>
        InventorySchemas.Contains(current, required) || Collections.Contains(name) &&
        KnownPredecessor(name, required) && InventorySchemas.Contains(current, Validator(name));
    private static bool KnownPredecessor(string name, BsonDocument required) =>
        CatalogSchemas.SemanticEquals(required, PreviousValidator(name)) || PaymentEvidenceSchemas.Collections.Contains(name) &&
        CatalogSchemas.SemanticEquals(required, EvidenceSchemas.Validator(name));
    internal static async Task RequireCompatibleStorageAsync(IMongoDatabase database, string name, BsonDocument required, BsonDocument key, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        var current = definition?.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument;
        if (current is null || !Supports(name, current, required)) throw new UnitToko.Platform.Mongo.PersistenceConflictException("evidence_storage_unavailable", "Evidence storage is not ready");
        await InventoryRepository.RequireStorageAsync(database, name, current, key, ct);
    }
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in EvidenceSchemas.Collections) {
            var required = Collections.Contains(name) ? Validator(name) : EvidenceSchemas.Validator(name);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition is null || definition.GetValue("type", "") != "collection" || options is null ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !UnitToko.Platform.Billing.InvoiceStorage.Simple(collation) ||
                !InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, required))
                throw new UnitToko.Platform.Mongo.PersistenceConflictException("evidence_storage_unavailable", "Evidence storage is not ready");
            try { PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)); }
            catch (InvalidOperationException) { throw new UnitToko.Platform.Mongo.PersistenceConflictException("evidence_storage_unavailable", "Evidence storage is not ready"); }
        }
        foreach (var name in EvidenceAccessSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, name == "evidenceReadLeases" ? Validator(name) : EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
        foreach (var name in new[] { "evidencePins", "evidencePinAudits" })
            await InventoryRepository.RequireStorageAsync(database, name, name == "evidencePins" ? Validator(name) : PaymentSubmissionSchemas.Validator(name), PaymentSubmissionSchemas.Key(name), ct);
    }
    private static BsonValue Translate(BsonValue value, bool enumeration = false) {
        if (value.IsString) return value.AsString switch {
            "CatalogFamily" or "CatalogSku" => "RefundPayout", "CatalogImage" => "RefundPayoutImage",
            EvidenceSchemas.ReserveOperation => "evidence.refund-payout-image.reserve", EvidenceSchemas.CompleteOperation => "evidence.refund-payout-image.complete", _ => value.DeepClone() };
        if (value.IsBsonArray) {
            var values = value.AsBsonArray.Select(item => Translate(item)); return new BsonArray(enumeration ? values.Distinct() : values);
        }
        if (value.IsBsonDocument) return new BsonDocument(value.AsBsonDocument.Select(pair => new BsonElement(pair.Name, Translate(pair.Value, pair.Name == "enum"))));
        return value.DeepClone();
    }
}
