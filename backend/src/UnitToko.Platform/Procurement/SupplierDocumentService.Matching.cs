using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentService
{
    private async Task<List<SupplierDocumentSourceCapacity>> SourceCapacitiesAsync(Context context, Dictionary<long, ProcurementInspectionHistory> inspections, BsonArray links, CancellationToken ct) {
        var result = new List<SupplierDocumentSourceCapacity>(); var ordersSeen = new HashSet<(long Order, long Sku)>();
        foreach (var pair in inspections.OrderBy(pair => pair.Key)) {
            var history = pair.Value; var inspection = history.Submissions.Single(row => row["_id"] == pair.Key);
            SupplierDocumentRecords.Equal(inspection["location"], R.Location(context.Location));
            var lines = new BsonArray(inspection["input"]["lines"].AsBsonArray.Select(line => new BsonDocument("packages", line["receivedPackages"])));
            result.Add(await CapacityAsync(context, "inspection", pair.Key, null, inspection["snapshotHash"].AsString, B.Capacity(lines), ct));
            foreach (var (table, row) in new[] { (ProcurementReceivingBson.Heads, history.CustodyHead), (ProcurementInspectionBson.Heads, history.Head!),
                (ProcurementInspectionBson.Drafts, history.Drafts.Single(row => row["_id"] == inspection["draftId"])) })
                if (!context.ExtraFences.Any(existing => existing.Table == table && existing.Row["_id"] == row["_id"])) context.ExtraFences.Add((table, row));
            foreach (var link in links.Where(link => !link.IsBsonNull && link["kind"] == "inspection" && link["id"] == pair.Key)) {
                var key = (link["orderId"].AsInt64, link["requestSkuId"].AsInt64); if (!ordersSeen.Add(key)) continue;
                var source = history.Source["notice"]["source"]; var quoted = source["proposal"]["input"]["lines"].AsBsonArray.Single(line => line["requestSkuId"] == key.Item2);
                var maximum = B.Capacity(new BsonArray { new BsonDocument("packages", quoted["packages"]) });
                maximum.Add("maximumAmounts", new BsonDocument { { "netAmount", Money.Multiply(Money.Parse(quoted["netPricePerPackage"].AsString), quoted["packages"].AsInt32).ToString() },
                    { "taxAmount", quoted["taxAmount"] }, { "chargeAmount", quoted["supplierChargeAmount"] } });
                result.Add(await CapacityAsync(context, "order", key.Item1, key.Item2, link["orderHash"].AsString, maximum, ct));
            }
        }
        return result;
    }
    private async Task<IReadOnlyList<SupplierDocumentSourceCapacity>> PrepareMatchAsync(Context context, SupplierDocumentMatchInput input, CancellationToken ct) {
        var history = context.History ?? throw B.Broken(); var document = context.Submission ?? throw B.Broken();
        await VerifySourcesAsync(context, ct);
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
        var resolved = await records.ResolveAsync(context.Tx, document, input, ct); context.Resolved = resolved.Lines; context.Links = resolved.Links;
        _ = B.After(history.Capacity, input);
        var sources = await SourceCapacitiesAsync(context, resolved.Inspections, resolved.Links, ct);
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
    public async Task<BsonDocument> PreviewMatchAsync(ActorContext actor, SupplierDocumentMatchInput input, CancellationToken ct = default) {
        input = SupplierDocumentRules.Copy(input); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await DocumentContextAsync(actor, authority, tx, input.DocumentId, Permissions.ProcurementSupplierDocumentMatch, token); _ = await PrepareMatchAsync(context, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "document", B.DraftView(context.Draft!) }, { "capacity", context.History!.Capacity }, { "lines", PublicLines(context.Resolved!) },
                { "possibleDuplicate", context.History.Identity["draftCount"].AsInt64 > 1 }, { "binding", MatchBinding(context, input) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> MatchAsync(ActorContext actor, SupplierDocumentMatchInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = SupplierDocumentRules.Copy(input); Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Matches, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value; var body = B.MatchBody(input, binding);
        Context? context = null; IReadOnlyList<SupplierDocumentSourceCapacity> sources = [];
        return await authorization.ExecuteCommandAsync(actor, B.MatchOperation, metadata, body, async (authority, tx, token) => {
            var existing = await ExistingAsync(actor, authority, tx, B.MatchOperation, metadata.IdempotencyKey, token);
            context = await DocumentContextAsync(actor, authority, tx, input.DocumentId, Permissions.ProcurementSupplierDocumentMatch, token);
            if (existing is null) { sources = await PrepareMatchAsync(context, input, token); SupplierDocumentRecords.Equal(binding, MatchBinding(context, input)); }
            else context.Match = context.History!.Matches.SingleOrDefault(row => row["_id"] == existing["_id"]) ?? throw B.Broken();
            return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); Current(active); var document = active.Submission!; var history = active.History!; var head = history.Head; var at = Now;
            var after = B.After(history.Capacity, input); var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "draftId", document["draftId"] }, { "documentId", document["_id"] }, { "version", 1L },
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
            var active = context ?? throw B.Broken(); var actual = await records.MatchAsync(tx, active.Match!["_id"].AsInt64, token, active.Submission);
            SupplierDocumentRecords.Equal(actual["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason));
            await VerifySourcesAsync(active, token); await records.OrphansAsync(tx, token); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    internal static BsonArray PublicLines(BsonArray rows) => new(rows.Select(value => { var row = value.DeepClone().AsBsonDocument; row.Remove("invoiceMatchedAmount"); return row; }));
}
