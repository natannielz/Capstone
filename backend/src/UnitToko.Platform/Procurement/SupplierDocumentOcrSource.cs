using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Procurement;

internal sealed class SupplierDocumentOcrSource(SupplierDocumentService owner) : ISupplierDocumentOcrSource
{
    public IMongoDatabase Database => owner.Database;
    public Task<SupplierDocumentOcrSourceSnapshot> RequireAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) => owner.RequireOcrSourceAsync(tx, job, ct);
}

public sealed partial class SupplierDocumentService
{
    internal IMongoDatabase Database => database;
    internal async Task<SupplierDocumentOcrSourceSnapshot> RequireOcrSourceAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            job.GetValue("kind", "") != "supplierdocument.ocr.requested" && job.GetValue("kind", "") != PdfDocumentExtractionContract.SupplierJobKind) throw B.Broken();
        var row = await records.SubmissionAsync(tx, job["payload"]["submissionId"].AsInt64, ct);
        var original = await records.OriginalAsync(tx, row, ct);
        SupplierDocumentRecords.Equal(job["kind"], original is null ? "supplierdocument.ocr.requested" : PdfDocumentExtractionContract.SupplierJobKind);
        SupplierDocumentRecords.Equal(job["sourceKey"], original is null ? B.OcrKey(row) : B.DocumentOcrKey(row));
        SupplierDocumentRecords.Equal(job["payload"], original is null ? B.OcrPayload(row) : B.DocumentOcrPayload(row, original));
        var actual = await Rows("outboxEvents").Find(tx, new BsonDocument("_id", job["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        SupplierDocumentRecords.Equal(actual, job); var location = ProcurementRequestBson.Location(row["location"]);
        return new(row["_id"].AsInt64, row["draftId"].AsInt64, 1, row["reservationId"].AsInt64, row["pinId"].AsInt64, row["actorId"].AsInt64,
            PaymentSubmissionBson.Reference(row["evidence"].AsBsonDocument), row["snapshotHash"].AsString, row["draftHash"].AsString,
            new(GrantScopeKind.FulfillmentShop, location.OrganizationId, ShopId: location.ShopId, ChannelId: location.ChannelId));
    }
}
