using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Procurement;

// Created only by the actual resolver. It lives in one owning Context and one
// transaction attempt; it cannot authorize another command, retry or history read.
internal sealed class SupplierDocumentResolvedMatchProof
{
    private readonly SupplierDocumentRecords owner;
    private readonly IClientSessionHandle transaction;
    private readonly long transactionNumber;
    private readonly BsonDocument document;
    private readonly BsonDocument input;
    private readonly BsonArray lines;
    private readonly BsonArray links;
    private readonly Dictionary<long, ProcurementInspectionHistory> inspections;
    private readonly Dictionary<long, (BsonDocument Snapshot, BsonDocument Head)> qualityHeads = [];
    private BsonDocument? completed;

    internal SupplierDocumentResolvedMatchProof(SupplierDocumentRecords owner, IClientSessionHandle tx,
        BsonDocument document, SupplierDocumentMatchInput input,
        (BsonArray Lines, BsonArray Links, Dictionary<long, ProcurementInspectionHistory> Inspections) resolved,
        IReadOnlyDictionary<long, ProcurementApprovedQualitySource> qualitySources) {
        this.owner = owner; transaction = tx; transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        this.document = document.DeepClone().AsBsonDocument; this.input = B.Input(input);
        lines = resolved.Lines.DeepClone().AsBsonArray; links = resolved.Links.DeepClone().AsBsonArray;
        inspections = new(resolved.Inspections);
        foreach (var (id, source) in qualitySources) {
            if (source.Capacity.Head is null || source.Allocation["_id"] != id ||
                source.Capacity.Head["receiptId"] != source.Receipt["_id"]) throw B.Broken();
            qualityHeads.Add(id, (source.Snapshot.DeepClone().AsBsonDocument, source.Capacity.Head.DeepClone().AsBsonDocument));
        }
    }
    private void Context(SupplierDocumentRecords expectedOwner, IClientSessionHandle tx) {
        if (!ReferenceEquals(expectedOwner, owner) || !ReferenceEquals(tx, transaction) || !tx.IsInTransaction ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber) throw B.Broken();
    }
    internal (BsonArray Lines, BsonArray Links, Dictionary<long, ProcurementInspectionHistory> Inspections) Require(
        SupplierDocumentRecords expectedOwner, IClientSessionHandle tx, BsonDocument actualDocument, SupplierDocumentMatchInput actualInput) {
        Context(expectedOwner, tx);
        SupplierDocumentRecords.Equal(actualDocument, document); SupplierDocumentRecords.Equal(B.Input(actualInput), input);
        return (lines.DeepClone().AsBsonArray, links.DeepClone().AsBsonArray, new(inspections));
    }
    internal void Complete(SupplierDocumentRecords expectedOwner, IClientSessionHandle tx, BsonDocument row) {
        Context(expectedOwner, tx);
        SupplierDocumentRecords.Equal(row["input"], input); SupplierDocumentRecords.Equal(row["documentHash"], document["snapshotHash"]);
        SupplierDocumentRecords.Equal(row["lines"], lines); SupplierDocumentRecords.Equal(row["links"], links);
        if (completed is not null) SupplierDocumentRecords.Equal(completed, row);
        completed = row.DeepClone().AsBsonDocument;
    }
    internal IReadOnlyList<BsonDocument> RequireQualityHeads(SupplierDocumentRecords expectedOwner, IClientSessionHandle tx,
        BsonDocument actualDocument, SupplierDocumentMatchInput actualInput) {
        _ = Require(expectedOwner, tx, actualDocument, actualInput);
        var requested = actualInput.Lines.Where(line => line.QualityAllocationId is not null)
            .Select(line => line.QualityAllocationId!.Value).ToHashSet();
        if (!requested.SetEquals(qualityHeads.Keys)) throw B.Broken();
        var heads = new Dictionary<long, BsonDocument>();
        foreach (var link in links.Where(value => !value.IsBsonNull && value.AsBsonDocument.Contains("quality"))) {
            var quality = link["quality"].AsBsonDocument;
            if (!qualityHeads.TryGetValue(quality["allocationId"].AsInt64, out var source)) throw B.Broken();
            SupplierDocumentRecords.Equal(quality, source.Snapshot);
            var head = source.Head; var id = head["_id"].AsInt64;
            if (heads.TryGetValue(id, out var previous)) SupplierDocumentRecords.Equal(previous, head);
            else heads.Add(id, head.DeepClone().AsBsonDocument);
        }
        if (heads.Count != qualityHeads.Values.Select(value => value.Head["_id"].AsInt64).Distinct().Count()) throw B.Broken();
        return heads.Values.ToArray();
    }
    internal Dictionary<long, ProcurementInspectionHistory> RequireCompleted(SupplierDocumentRecords expectedOwner,
        IClientSessionHandle tx, SupplierDocumentHistory history) {
        Context(expectedOwner, tx);
        if (completed is null || history.Document is null) throw B.Broken();
        SupplierDocumentRecords.Equal(history.Document, document);
        var row = history.Matches.SingleOrDefault(value => value["_id"] == completed["_id"]) ?? throw B.Broken();
        SupplierDocumentRecords.Equal(row, completed);
        return new(inspections);
    }
}

internal sealed partial class SupplierDocumentRecords
{
    internal async Task<SupplierDocumentResolvedMatchProof> ResolveMatchProofAsync(IClientSessionHandle tx,
        BsonDocument document, SupplierDocumentMatchInput input, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || input.PdfObservations is null) throw B.Broken();
        var qualitySources = new Dictionary<long, ProcurementApprovedQualitySource>();
        var graph = new ProcurementQualityProofGraph(tx);
        var resolved = await ResolveCoreAsync(tx, document, input, ct, graph, qualitySources);
        return new(this, tx, document, input, resolved, qualitySources);
    }
}
