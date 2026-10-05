using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentService
{
    private Task<List<SupplierDocumentSourceCapacity>> SourceCapacitiesAsync(Context context, Dictionary<long, ProcurementInspectionHistory> inspections, BsonArray links,
        SupplierDocumentMatchInput input, CancellationToken ct) =>
        records.SourceCapacitiesAsync(context.Tx, context.Location, inspections, links, context.ExtraFences, ct, B.Input(input)["lines"].AsBsonArray);
    private async Task<IReadOnlyList<SupplierDocumentSourceCapacity>> PrepareMatchAsync(Context context, SupplierDocumentMatchInput input, CancellationToken ct,
        DateTimeOffset? pdfDeadline = null, Action<DateTimeOffset>? captureWindow = null, string? correlationId = null,
        EvidenceStoreRegistration? pdfRegistration = null) {
        var history = context.History ?? throw B.Broken(); var document = context.Submission ?? throw B.Broken();
        using (SupplierDocumentMatchDiagnostics.Begin("matching.verify-sources")) await VerifySourcesAsync(context, ct);
        SupplierDocumentRules.SameIdentity(B.Input(document["input"]), input.ReviewedInput);
        if (input.ReviewedInput.DocumentDate is { } day && day > AccountingCalendar.BusinessDate(Now)) throw B.Changed();
        var claiming = input.Lines.Any(line => line.Discrepancy is null);
        if (claiming) {
            if (history.Identity["draftCount"].AsInt64 != 1) throw B.Changed("supplier_document_identity_ambiguous");
            if (!history.Head["reviewedInput"].IsBsonNull) SupplierDocumentRecords.Equal(history.Head["reviewedInput"], B.Input(input.ReviewedInput));
        }
        if (input.OcrResultId is { } resultId) {
            context.IncludeEvidence = true; _ = Require(context);
            var job = await Rows("outboxEvents").Find(context.Tx, new BsonDocument("sourceKey", B.OcrKey(document))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var proof = await RequireOcrSourceAsync(context.Tx, job, ct);
            _ = await new SupplierDocumentExtractionRecords(database).RequireResultAsync(context.Tx, job, proof, resultId, ct);
        }
        if (input.PdfObservations is not null) {
            if (pdfDeadline is null || captureWindow is null || correlationId is null) throw B.Broken();
            using (SupplierDocumentMatchDiagnostics.Begin("matching.pdf-candidates"))
                await RequirePdfMatchAsync(context, input, pdfDeadline.Value, captureWindow, correlationId, pdfRegistration, ct);
        }
        (BsonArray Lines, BsonArray Links, Dictionary<long, ProcurementInspectionHistory> Inspections) resolved;
        using (SupplierDocumentMatchDiagnostics.Begin("matching.resolve")) {
            if (input.PdfObservations is not null) {
                context.PdfMatchProof = await records.ResolveMatchProofAsync(context.Tx, document, input, ct);
                resolved = context.PdfMatchProof.Require(records, context.Tx, document, input);
            } else resolved = await records.ResolveAsync(context.Tx, document, input, ct);
        }
        context.Resolved = resolved.Lines; context.Links = resolved.Links;
        if (context.PdfMatchProof is { } pdfProof) {
            using var qualityHeadsStage = SupplierDocumentMatchDiagnostics.Begin("matching.quality-heads");
            // ResolveCoreAsync has already authenticated the exact held receipt,
            // quality allocation, private claims and receipt-local capacity in
            // this transaction. Reuse that proof only to retain the same head CAS.
            foreach (var head in pdfProof.RequireQualityHeads(records, context.Tx, document, input))
                if (!context.ExtraFences.Any(pair => pair.Table == ProcurementQualityAcceptanceBson.ReceiptHeads && pair.Row["_id"] == head["_id"]))
                    context.ExtraFences.Add((ProcurementQualityAcceptanceBson.ReceiptHeads, head));
        } else foreach (var receiptId in resolved.Links.Where(link => !link.IsBsonNull && link.AsBsonDocument.Contains("quality"))
            .Select(link => link["quality"]["receiptId"].AsInt64).Distinct()) {
            var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders,
                new UnitToko.Platform.Inventory.StockSourceSetupService(database, unit, ids, clock)).ReadAsync(context.Tx, receiptId, ct);
            var quality = await new ProcurementQualityAcceptanceCapacityReader(database,
                new UnitToko.Platform.Inventory.StockSourceSetupService(database, unit, ids, clock)).ReadAsync(context.Tx, receipt, ct);
            if (quality.Head is null) throw B.Broken();
            if (!context.ExtraFences.Any(pair => pair.Table == ProcurementQualityAcceptanceBson.ReceiptHeads && pair.Row["_id"] == quality.Head["_id"]))
                context.ExtraFences.Add((ProcurementQualityAcceptanceBson.ReceiptHeads, quality.Head));
        }
        _ = B.After(history.Capacity, input);
        IReadOnlyList<SupplierDocumentSourceCapacity> sources;
        using (SupplierDocumentMatchDiagnostics.Begin("matching.source-capacities"))
            sources = await SourceCapacitiesAsync(context, resolved.Inspections, resolved.Links, input, ct);
        foreach (var source in sources) _ = SourceAfter(source.Capacity, source.Kind, source.SourceId, source.SkuId, B.Input(input)["lines"].AsBsonArray, resolved.Links, source.Maximum);
        RequireStatementCapacity(history.Matches, input, resolved.Lines);
        context.Review = new() { { "capacity", history.Capacity }, { "identityVersion", history.Identity["version"] },
            { "inspectionCapacities", new BsonArray(sources.Where(source => source.Kind == "inspection").Select(source => new BsonDocument { { "inspectionId", source.SourceId }, { "capacity", source.Capacity } })) },
            { "orderCapacities", new BsonArray(sources.Where(source => source.Kind == "order").Select(source => new BsonDocument { { "orderId", source.SourceId }, { "requestSkuId", source.SkuId!.Value }, { "capacity", source.Capacity } })) } };
        Current(context); return sources;
    }
    private static void RequireStatementCapacity(IEnumerable<BsonDocument> history, SupplierDocumentMatchInput input, BsonArray current) {
        if (input.ReviewedInput.Kind != SupplierDocumentKind.Sph) return;
        var lines = history.SelectMany(row => row["lines"].AsBsonArray).Concat(current).Where(line => line["state"] == "matched");
        foreach (var group in lines.GroupBy(line => (line["invoiceMatchId"].AsInt64, line["invoiceMatchLineNumber"].AsInt32))) {
            var sum = group.Sum(line => Money.Parse(line["statementAmount"].AsString).Value);
            // Every resolved SPH line carries the exact immutable invoice-match amount, never a physical claim.
            var ceiling = group.First()["invoiceMatchedAmount"].AsString;
            if (group.Any(line => line["invoiceMatchedAmount"] != ceiling) || sum > Money.Parse(ceiling).Value) throw B.Changed("supplier_document_capacity_exceeded");
        }
    }
    private static string MatchBinding(Context context, SupplierDocumentMatchInput input) => B.Binding(context.Actor.UserId, context.Actor.GrantVersion, B.Input(input),
        new() { { "documentHash", context.Submission!["snapshotHash"] }, { "links", context.Links } }, context.Review!);
    public Task<BsonDocument> PreviewMatchAsync(ActorContext actor, SupplierDocumentMatchInput input, CancellationToken ct = default) =>
        PreviewMatchCoreAsync(actor, input, null, ct);
    internal Task<BsonDocument> PreviewMatchWithEvidenceAsync(ActorContext actor, SupplierDocumentMatchInput input,
        EvidenceStoreRegistration registration, CancellationToken ct) => PreviewMatchCoreAsync(actor, input, registration, ct);
    private async Task<BsonDocument> PreviewMatchCoreAsync(ActorContext actor, SupplierDocumentMatchInput input,
        EvidenceStoreRegistration? registration, CancellationToken ct) {
        input = SupplierDocumentRules.Copy(input);
        using (SupplierDocumentMatchDiagnostics.Begin("matching.storage")) await StorageAsync(ct);
        if (input.PdfObservations is not null) await SupplierDocumentPdfMatchStorage.RequireAsync(database, B.Matches, ct, requireAnnotated: true);
        var pdfDeadline = clock.UtcNow.AddSeconds(5); DateTimeOffset? pdfWindow = null;
        void Capture(DateTimeOffset until) { if (pdfWindow is null || until < pdfWindow.Value) pdfWindow = until; }
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            Context context;
            using (SupplierDocumentMatchDiagnostics.Begin("matching.context"))
                context = await DocumentContextAsync(actor, authority, tx, input.DocumentId, Permissions.ProcurementSupplierDocumentMatch, token);
            if (input.PdfObservations is not null) context.PdfEligibility = () => CheckPdfMatchWindow(pdfDeadline, pdfWindow);
            _ = await PrepareMatchAsync(context, input, token, pdfDeadline, Capture, "supplier-document-pdf-match-preview", registration);
            var result = new BsonDocument { { "input", B.Input(input) }, { "document", B.DraftView(context.Draft!) }, { "capacity", context.History!.Capacity }, { "lines", PublicLines(context.Resolved!) },
                { "possibleDuplicate", context.History.Identity["draftCount"].AsInt64 > 1 }, { "binding", MatchBinding(context, input) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public Task<BsonDocument> MatchAsync(ActorContext actor, SupplierDocumentMatchInput input, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) => MatchCoreAsync(actor, input, binding, metadata, null, ct);
    internal Task<BsonDocument> MatchWithEvidenceAsync(ActorContext actor, SupplierDocumentMatchInput input, string binding,
        AuthorizationCommandMetadata metadata, EvidenceStoreRegistration registration, CancellationToken ct) =>
        MatchCoreAsync(actor, input, binding, metadata, registration, ct);
    private async Task<BsonDocument> MatchCoreAsync(ActorContext actor, SupplierDocumentMatchInput input, string binding,
        AuthorizationCommandMetadata metadata, EvidenceStoreRegistration? registration, CancellationToken ct) {
        input = SupplierDocumentRules.Copy(input); Binding(binding); Metadata(metadata);
        using (SupplierDocumentMatchDiagnostics.Begin("matching.storage")) await StorageAsync(ct);
        if (input.PdfObservations is not null) await SupplierDocumentPdfMatchStorage.RequireAsync(database, B.Matches, ct, requireAnnotated: true);
        var pdfDeadline = clock.UtcNow.AddSeconds(5); DateTimeOffset? pdfWindow = null;
        void Capture(DateTimeOffset until) { if (pdfWindow is null || until < pdfWindow.Value) pdfWindow = until; }
        var id = (await ids.NextAsync(B.Matches, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.MatchBody(input, binding);
        Context? context = null; IReadOnlyList<SupplierDocumentSourceCapacity> sources = [];
        return await authorization.ExecuteCommandAsync(actor, B.MatchOperation, metadata, body, async (authority, tx, token) => {
            var existing = await ExistingAsync(actor, authority, tx, B.MatchOperation, metadata.IdempotencyKey, token);
            using (SupplierDocumentMatchDiagnostics.Begin("matching.context"))
                context = await DocumentContextAsync(actor, authority, tx, input.DocumentId, Permissions.ProcurementSupplierDocumentMatch, token);
            if (existing is null) {
                if (input.PdfObservations is not null) context.PdfEligibility = () => CheckPdfMatchWindow(pdfDeadline, pdfWindow);
                sources = await PrepareMatchAsync(context, input, token, pdfDeadline, Capture, metadata.CorrelationId, registration);
                SupplierDocumentRecords.Equal(binding, MatchBinding(context, input));
            }
            else context.Match = context.History!.Matches.SingleOrDefault(row => row["_id"] == existing["_id"]) ?? throw B.Broken();
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var document = active.Submission!; var history = active.History!; var head = history.Head; var at = Now;
            var after = B.After(history.Capacity, input); var row = new BsonDocument { { "_id", id },
                { "schemaVersion", input.PdfObservations is null ? input.Lines.Any(line => line.QualityAllocationId is not null) ? 2 : 1 : input.Lines.Any(line => line.QualityAllocationId is not null) ? 4 : 3 },
                { "draftId", document["draftId"] }, { "documentId", document["_id"] }, { "version", 1L },
                { "supplierId", document["supplierId"] }, { "identityId", document["identityId"] }, { "documentHash", document["snapshotHash"] }, { "location", document["location"] },
                { "input", B.Input(input) }, { "before", history.Capacity }, { "after", after }, { "lines", active.Resolved }, { "links", active.Links }, { "review", active.Review }, { "binding", binding },
                { "sequence", after["version"] }, { "previousMatchId", head["lastMatchId"] }, { "previousHash", head["lastHash"] }, { "people", new BsonArray(active.People!.Evidence.Select(R.Person)) } };
            row.Add("claims", await WriteClaimsAsync(active, row, sources, token)); R.Metadata(row, B.MatchOperation, audit, actor, metadata, body, at);
            await Rows(B.Matches).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, token); await PersistClaimsAsync(active, row, sources, token);
            var next = head.DeepClone().AsBsonDocument; next["version"] = after["version"]; next["lines"] = after["lines"]; next["lastMatchId"] = id; next["lastHash"] = row["snapshotHash"]; next["updatedAt"] = at.UtcDateTime;
            if (input.Lines.Any(line => line.Discrepancy is null)) next["reviewedInput"] = B.Input(input.ReviewedInput);
            if ((await Rows(B.Heads).ReplaceOneAsync(tx, head, next, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            active.Match = row; active.History = history with { Head = next, Capacity = after, Matches = [.. history.Matches, row] }; return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var actual = await records.MatchAsync(tx, active.Match!["_id"].AsInt64, token,
                active.Submission, active.PdfMatchProof);
            SupplierDocumentRecords.Equal(actual["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            await VerifySourcesAsync(active, token); await records.OrphansAsync(tx, token); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    internal static BsonArray PublicLines(BsonArray rows) => new(rows.Select(value => { var row = value.DeepClone().AsBsonDocument;
        row.Remove("invoiceMatchedAmount");
        if (row.TryGetValue("quality", out var quality)) row["quality"] = B.PublicQuality(quality.AsBsonDocument);
        return row; }));
}
