using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using B = UnitToko.Platform.Billing.PaymentDraftBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentReviewQueueEntry(PaymentDraftView Declaration, long ReviewRevision, string State, long? AssigneeId,
    DateTimeOffset? RecordedAt, string OcrState, ImmutableArray<string> Discrepancies);
public sealed record PaymentReviewQueuePage(ImmutableArray<PaymentReviewQueueEntry> Items, bool HasMore, DateTimeOffset ObservedAt);

public sealed partial class PaymentDraftService
{
    public async Task<PaymentReviewQueuePage> ListReviewQueueAsync(ActorContext actor, long? beforeId, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await CorrectionStorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct);
        var page = await ListReviewsCoreAsync(actor, beforeId, metadata, "billing.payment.review.queue", QueueEntryAsync, ct);
        if (page.Items.Any(item => item.Declaration.Submission is null || item.Declaration.Submission.SubmittedAt > page.ObservedAt || item.RecordedAt > page.ObservedAt)) throw B.Invalid();
        return new(page.Items, page.HasMore, page.ObservedAt);
    }
    private async Task<PaymentReviewQueueEntry> QueueEntryAsync(IClientSessionHandle tx, BsonDocument head, BsonDocument version, PaymentDraftView view, CancellationToken ct) {
        var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        var source = B.SubmittedSource(head, version, submitted); var action = await new PaymentReviewActionRecords(database).LatestAsync(tx, head["_id"].AsInt64, source, ct);
        var job = await records.Rows("outboxEvents").Find(tx, new BsonDocument { { "kind", "payment.ocr.requested" }, { "payload.submissionId", submitted["_id"] } }).SingleOrDefaultAsync(ct) ?? throw ExtractionRecords.Broken();
        if (job.GetValue("sourceKey", "") != PaymentSubmissionBson.OcrKey(submitted) || !InvoiceRecords.Equal(job.GetValue("payload", BsonNull.Value), PaymentSubmissionBson.OcrPayload(submitted))) throw ExtractionRecords.Broken();
        var extraction = await new ExtractionRecords(database).ResultAsync(tx, job, ct);
        var state = extraction is not null ? "succeeded" : job["status"].AsString switch { "pending" => "queued", "leased" => "running", "retry" => "retry", "dead" => "dead", _ => throw ExtractionRecords.Broken() };
        var differences = ImmutableArray.CreateBuilder<string>();
        // Queue flags summarize retained, source-bound history. They expose no derivative values and never establish bank acceptance.
        if (extraction is not null) {
            var output = ExtractionRecords.Extract(extraction); var evidence = PaymentSubmissionBson.Reference(submitted["evidence"].AsBsonDocument);
            var binding = new PaymentCorrectionBinding(view.Revision, submitted["_id"].AsInt64, evidence, extraction["_id"].AsInt64);
            var extracted = new PaymentExtractionView(view.Id.Value, submitted["_id"].AsInt64, evidence, "succeeded", extraction["_id"].AsInt64, new(extraction["createdAt"].ToUniversalTime()), output);
            var corrected = await CorrectionViewAsync(tx, extracted, binding, ct);
            string? Field(string name) => corrected.Fields.Single(field => field.Field == name).Value;
            if (Field("amount") is not { } amount) differences.Add("amount_unknown");
            else if (Money.Parse(amount) != view.Input.DeclaredAmount) differences.Add("amount_differs");
            if (Field("transfer_date") is not { } date) differences.Add("date_unknown");
            else if (DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) != view.Input.TransferDate) differences.Add("date_differs");
            if (Field("currency") != "IDR") differences.Add("currency_unknown");
            if (output.QualityFlags.Count > 0 || output.Fields.Any(field => field.Flags.Count > 0)) differences.Add("ocr_quality");
            if (corrected.ReviewRevision > 0) differences.Add("human_corrected");
        } else if (await records.Rows(PaymentCorrectionBson.Heads).Find(tx, new BsonDocument("_id", view.Id.Value)).AnyAsync(ct) ||
            await records.Rows(PaymentCorrectionBson.Versions).Find(tx, new BsonDocument("declarationId", view.Id.Value)).AnyAsync(ct)) throw PaymentCorrectionRecords.Bad();
        return new(view, action?["revision"].AsInt64 ?? 0, action?["state"].AsString ?? "pending",
            action is null || action["assigneeId"].IsBsonNull ? null : action["assigneeId"].AsInt64,
            action is null ? null : new(action["recordedAt"].ToUniversalTime()), state, differences.ToImmutable());
    }
}
