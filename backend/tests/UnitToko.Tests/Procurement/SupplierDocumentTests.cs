using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.SupplierDocumentTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class SupplierDocumentTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData(ProcurementRoute.OmiRegular)]
    [InlineData(ProcurementRoute.Ddo)]
    [InlineData(ProcurementRoute.Smart)]
    [InlineData(ProcurementRoute.PasarKering)]
    public async Task Genuine_private_invoice_and_two_partial_human_matches_preserve_quarantine_without_financial_effects(ProcurementRoute route) {
        await using var data = await D.CreateAsync(fixture, route);
        var receiving = await data.Inspection.Receiving.StateAsync(); var inspection = await data.Inspection.StateAsync(); var orders = await data.Inspection.Receiving.Orders.StateAsync();
        var alternate = await data.Evidence.ReserveAsync(data.Actor, data.DraftId, 1, D.Meta("unused-original-reservation"));
        await data.UploadAsync(); var before = await data.PreviewSubmitAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.SubmitAsync(before));
        await data.ValidateAsync(); var preview = await data.PreviewSubmitAsync(); var submitted = await data.SubmitAsync(preview);
        Assert.Equal(submitted, await data.SubmitAsync(preview)); Assert.Equal(submitted, await data.Service.LookupAsync(data.Actor, D.SubmitOperation, "supplier-document-submit"));
        Assert.Equal(data.Draft, await data.Service.LookupAsync(data.Actor, D.DraftOperation, "supplier-document-draft"));
        var masked = await data.Service.ReadAsync(data.Reader, data.DraftId); Assert.True(masked["document"]["evidence"].IsBsonNull); Assert.True(masked["document"]["reservationId"].IsBsonNull);
        var visible = await data.Service.ReadAsync(data.EvidenceReader, data.DraftId); Assert.Equal(data.Reference.Sha256, visible["document"]["evidence"]["sha256"].AsString);
        Assert.Equal("decoded", (await data.Evidence.StatusAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId)).DecoderStatus);
        var listed = await data.Evidence.ListAsync(data.EvidenceReader, data.DraftId); Assert.Single(listed); Assert.Equal(data.Reservation.ReservationId, listed[0].ReservationId);
        Assert.Empty(await data.Evidence.ListAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId));
        Assert.Equal("evidence_actor_or_owner_denied", (await Assert.ThrowsAsync<DomainValidationException>(
            () => data.Evidence.StatusAsync(data.EvidenceReader, data.DraftId, alternate.ReservationId))).Code);
        await using (var original = await data.Evidence.OpenReadAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata)) {
            using var buffer = new MemoryStream(); await original.Content.CopyToAsync(buffer); Assert.Equal(data.Bytes, buffer.ToArray());
        }
        var extraction = await data.Evidence.ReadExtractionAsync(data.EvidenceReader, data.DraftId, 1, data.Reference, new("supplier-document-ocr-view", "synthetic-pending-ocr-review"));
        Assert.Equal("queued", extraction.State); Assert.Null(extraction.ResultId); Assert.Empty(extraction.Lines);
        var choices = await data.Service.InspectionsAsync(data.Matcher, data.DocumentId); Assert.Equal(data.InspectionId, Assert.Single(choices["items"].AsBsonArray)["inspectionId"].AsInt64);
        var input = data.MatchInput(); var matchPreview = await data.Service.PreviewMatchAsync(data.Matcher, input);
        var first = await data.Service.MatchAsync(data.Matcher, input, matchPreview["binding"].AsString, D.Meta("partial-match-one"));
        Assert.Equal(first, await data.Service.MatchAsync(data.Matcher, input, matchPreview["binding"].AsString, D.Meta("partial-match-one")));
        Assert.Equal(first, await data.Service.LookupAsync(data.Matcher, D.MatchOperation, "partial-match-one"));
        var partial = await data.Service.ReadAsync(data.Reader, data.DraftId); Assert.Equal(1L, partial["capacity"]["lines"][0]["remainingPackages"].AsInt64);
        Assert.Equal("submitted_unmatched", partial["document"]["state"].AsString);
        await data.MatchAsync("partial-match-two", data.MatchInput(1, new Money(1300), new Money(200)));
        var full = await data.Service.ReadAsync(data.Reader, data.DraftId); var capacity = full["capacity"]["lines"][0];
        Assert.Equal(0L, capacity["remainingPackages"].AsInt64); Assert.Equal(3L, capacity["matchedPackages"].AsInt64);
        Assert.Equal("30000.00", capacity["matchedNetAmount"].AsString); Assert.Equal("3300.00", capacity["matchedTaxAmount"].AsString); Assert.Equal("700.00", capacity["matchedChargeAmount"].AsString);
        Assert.Equal(2, full["matches"].AsBsonArray.Count); Assert.Equal(2, (await data.Service.MatchesAsync(data.Reader, data.DocumentId))["items"].AsBsonArray.Count);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewMatchAsync(data.Matcher, data.MatchInput(1, new Money(0), new Money(0))));
        Assert.Equal(1, await data.Rows("supplierDocumentInspectionHeads").CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("supplierDocumentOrderHeads").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(receiving, await data.Inspection.Receiving.StateAsync()); Assert.Equal(inspection, await data.Inspection.StateAsync()); Assert.Equal(orders, await data.Inspection.Receiving.Orders.StateAsync());
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task SPH_links_real_invoice_match_amounts_without_package_claims_and_cannot_count_the_same_match_twice() {
        await using var data = await D.CreateAsync(fixture); await data.SubmitOriginalAsync(); var invoiceInput = data.Input();
        var invoice = await data.MatchAsync("invoice-for-statement"); var sourceHeads = await HeadsAsync(data); var invoiceId = data.DocumentId;
        var statement = invoiceInput with { Kind = SupplierDocumentKind.Sph, DocumentReference = "SYNTHETIC-SPH-001", DocumentTotal = new Money(30000), Lines = [
            new("001", "First stated invoice balance", null, null, null, null, null, null, null, invoiceInput.DocumentReference, new Money(15000)),
            new("002", "Second stated invoice balance requiring reconciliation", null, null, null, null, null, null, null, invoiceInput.DocumentReference, new Money(15000))] };
        data.Draft = await data.DraftAsync("statement-draft", statement); await data.UploadAsync(); await data.ValidateAsync(); await data.SubmitAsync(await data.PreviewSubmitAsync(), "statement-submit");
        var choices = await data.Service.InvoiceMatchesAsync(data.Matcher, data.DocumentId); Assert.Equal(invoice["matchId"], Assert.Single(choices["items"].AsBsonArray)["match"]["id"]);
        var first = new SupplierDocumentMatchInput(data.DocumentId, 1, statement, null, [new(1, null, null, null, invoice["matchId"].AsInt64, 1, null, null, null, new Money(15000), null)]);
        await data.MatchAsync("statement-first-link", first);
        var second = first with { Lines = [first.Lines[0] with { DocumentLineNumber = 2 }] };
        Assert.Equal("supplier_document_capacity_exceeded", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewMatchAsync(data.Matcher, second))).Code);
        var unresolved = second with { Lines = [new(2, null, null, null, null, null, null, null, null, new Money(15000), "Statement repeats more than the actual invoice match; awaiting resolution")] };
        var result = await data.MatchAsync("statement-unresolved", unresolved); Assert.Equal("discrepancy_pending", result["state"].AsString);
        var read = await data.Service.ReadAsync(data.Reader, data.DraftId); Assert.True(read["capacity"]["lines"][0]["packages"].IsBsonNull);
        Assert.Equal("15000.00", read["capacity"]["lines"][0]["matchedStatementAmount"].AsString); Assert.Equal("0.00", read["capacity"]["lines"][1]["matchedStatementAmount"].AsString);
        Assert.Equal(sourceHeads, await HeadsAsync(data)); Assert.NotEqual(invoiceId, data.DocumentId); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Unknown_original_values_remain_unknown_until_explicit_review_and_later_duplicate_identity_blocks_new_claims() {
        await using var data = await D.CreateAsync(fixture, createDraft: false); var known = data.Input();
        var unknown = known with { DocumentDate = null, DocumentTotal = null, Discrepancy = "Original totals and package code are unreadable", Lines = [known.Lines[0] with {
            SourceAccount = null, SourceCode = null, Packages = null, NetPricePerPackage = null, TaxAmount = null, ChargeAmount = null }] };
        data.Draft = await data.DraftAsync("unknown-original", unknown); await data.SubmitOriginalAsync();
        var unresolved = new SupplierDocumentMatchInput(data.DocumentId, 1, unknown, null,
            [new(1, null, null, null, null, null, null, null, null, null, "Transcription still requires human confirmation")]);
        await data.MatchAsync("unknown-review", unresolved);
        var untouched = await data.Service.ReadAsync(data.Reader, data.DraftId); Assert.True(untouched["draft"]["input"]["documentTotal"].IsBsonNull); Assert.True(untouched["reviewedInput"].IsBsonNull);
        Assert.False(await data.Rows("supplierDocumentInspectionHeads").Find(new BsonDocument()).AnyAsync());
        await data.MatchAsync("transcribed-review", data.MatchInput(reviewed: known));
        var reviewed = await data.Service.ReadAsync(data.Reader, data.DraftId); Assert.True(reviewed["draft"]["input"]["documentTotal"].IsBsonNull);
        Assert.Equal("34000.00", reviewed["reviewedInput"]["documentTotal"].AsString); Assert.Equal("000012", reviewed["reviewedInput"]["lines"][0]["sourceCode"].AsString);
        var changed = known with { DocumentDate = known.DocumentDate!.Value.AddDays(-1) };
        await DeniedAsync(() => data.Service.PreviewMatchAsync(data.Matcher, data.MatchInput(1, new Money(1300), new Money(200), changed)));
        _ = await data.DraftAsync("duplicate-original", known); var duplicated = await data.Service.ReadAsync(data.Reader, data.DraftId); Assert.True(duplicated["possibleDuplicate"].AsBoolean);
        Assert.Equal("supplier_document_identity_ambiguous", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewMatchAsync(data.Matcher,
            data.MatchInput(1, new Money(1300), new Money(200))))).Code);
        var originalMatch = await data.Service.LookupAsync(data.Matcher, D.MatchOperation, "transcribed-review"); Assert.NotNull(originalMatch);
        Assert.Equal(1, await data.Rows("supplierDocumentInspectionHeads").CountDocumentsAsync(new BsonDocument())); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Match_rollback_and_competing_partial_claims_are_atomic_and_missing_source_heads_or_effects_do_not_reopen_capacity() {
        await using var data = await D.CreateAsync(fixture); await data.SubmitOriginalAsync(); var input = data.MatchInput(); var preview = await data.Service.PreviewMatchAsync(data.Matcher, input);
        var state = await data.StateAsync(); var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone().AsBsonDocument;
        try {
            await GuardAsync(data, "outboxEvents", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", D.MatchOperation)) }));
            Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.Service.MatchAsync(data.Matcher, input, preview["binding"].AsString, D.Meta("atomic-match"))));
            Assert.Equal(state, await data.StateAsync());
        } finally { await GuardAsync(data, "outboxEvents", validator); }
        Assert.Null(await data.Service.LookupAsync(data.Matcher, D.MatchOperation, "atomic-match"));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Record.ExceptionAsync(() => data.Service.MatchAsync(data.Matcher, input, preview["binding"].AsString, D.Meta("race-match-" + index)))));
        Assert.Single(outcomes, value => value is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, value => value is not null));
        var winnerKey = "race-match-" + Array.FindIndex(outcomes, value => value is null);
        var committedState = await data.StateAsync();
        // Replays still fence current authority and source history; concurrent callers may exhaust bounded retries.
        async Task<(BsonDocument? Result, PersistenceConflictException? Error)> ReplayAsync() {
            try { return (await data.Service.MatchAsync(data.Matcher, input, preview["binding"].AsString, D.Meta(winnerKey)), null); }
            catch (PersistenceConflictException error) { return (null, error); }
        }
        var replays = await Task.WhenAll(ReplayAsync(), ReplayAsync());
        Assert.Contains(replays, replay => replay.Result is not null);
        foreach (var replay in replays.Where(replay => replay.Error is not null)) Assert.Equal("version_conflict", replay.Error!.Code);
        var recovered = Assert.IsType<BsonDocument>(await data.Service.LookupAsync(data.Matcher, D.MatchOperation, winnerKey));
        foreach (var replay in replays.Where(replay => replay.Result is not null)) Assert.Equal(recovered, replay.Result);
        Assert.Equal(recovered, await data.Service.MatchAsync(data.Matcher, input, preview["binding"].AsString, D.Meta(winnerKey)));
        Assert.Equal(committedState, await data.StateAsync());
        foreach (var (table, filter) in new[] {
            ("supplierDocumentMatches", new BsonDocument()), ("auditEvents", new BsonDocument("kind", D.MatchOperation)),
            ("outboxEvents", new BsonDocument("kind", D.MatchOperation)), ("idempotencyRecords", new BsonDocument("operation", D.MatchOperation)) })
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(filter));
        foreach (var (table, filter) in new[] {
            ("supplierDocumentMatches", new BsonDocument()), ("supplierDocumentHeads", new BsonDocument("_id", data.DraftId)),
            ("supplierDocumentInspectionHeads", new BsonDocument()), ("supplierDocumentOrderHeads", new BsonDocument()),
            ("procurementInspectionSubmissions", new BsonDocument("_id", data.InspectionId)),
            ("auditEvents", new BsonDocument("kind", D.MatchOperation)), ("outboxEvents", new BsonDocument("kind", D.MatchOperation)), ("idempotencyRecords", new BsonDocument("operation", D.MatchOperation)) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
            try {
                await DeniedAsync(() => data.Service.ReadAsync(data.Reader, data.DraftId)); await DeniedAsync(() => data.Service.LookupAsync(data.Matcher, D.MatchOperation, winnerKey));
                await DeniedAsync(() => data.Service.PreviewMatchAsync(data.Matcher, data.MatchInput(1, new Money(1300), new Money(200))));
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        Assert.Equal(recovered, await data.Service.LookupAsync(data.Matcher, D.MatchOperation, winnerKey)); await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Private_source_owner_scope_fresh_factor_and_exact_original_are_required_and_current_revocation_cannot_commit() {
        await using var data = await D.CreateAsync(fixture); var state = await data.StateAsync();
        foreach (var actor in new[] { data.Reader, data.EvidenceReader, data.Outsider }) await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(actor, data.Input()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, data.DraftId));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Actor with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, data.Input()))).Code);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.ReserveAsync(data.Matcher, data.DraftId, 1, D.Meta("wrong-author")));
        Assert.Equal(state, await data.StateAsync()); await data.UploadAsync(); await data.ValidateAsync(); var preview = await data.PreviewSubmitAsync(); state = await data.StateAsync();
        var path = await data.OriginalPathAsync();
        foreach (var missing in new[] { true, false }) {
            if (missing) File.Delete(path); else await File.WriteAllBytesAsync(path, data.Bytes[..(data.Bytes.Length / 2)]);
            try { await Assert.ThrowsAsync<PersistenceConflictException>(() => data.SubmitAsync(preview, "damaged-original")); Assert.Equal(state, await data.StateAsync()); }
            finally { await File.WriteAllBytesAsync(path, data.Bytes); }
        }
        var policy = await data.Rows("evidenceValidationPolicies").Find(new BsonDocument("consumerId", D.Consumer)).SingleAsync();
        var grants = await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Actor.UserId)).ToListAsync();
        foreach (var change in new[] { "policy", "grant" }) {
            var service = data.Service; var evidence = new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, data.Registration, supplierDocumentTargets: service); var observed = 0;
            evidence.SupplierDocumentReadObserver = new(async () => {
                Interlocked.Increment(ref observed);
                if (change == "policy") await data.Rows("evidenceValidationPolicies").UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
                else await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.ProcurementSupplierDocumentRecord));
            });
            try {
                await DeniedAsync(() => evidence.WithSupplierDocumentPinAsync(data.Actor, new(EvidenceTargetKind.SupplierDocument, data.DraftId), 1,
                    data.Reservation.ReservationId, data.Reference, D.ReadMetadata, (claim, original, started, token) => service.SubmitWithProofAsync(data.Actor, data.DraftId, 1,
                        data.Reservation.ReservationId, data.Reference, preview["binding"].AsString, D.Meta("after-original-" + change),
                        (tx, id, target, inner) => new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, data.Registration, supplierDocumentTargets: target)
                            .PinSupplierDocumentAsync(tx, data.Actor, id, claim, original, started, inner), token)));
                Assert.Equal(1, observed); Assert.Equal(state, await data.StateAsync());
            } finally {
                await data.Rows("evidenceValidationPolicies").ReplaceOneAsync(new BsonDocument("_id", policy["_id"]), policy);
                foreach (var grant in grants) await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
            }
        }
        var submitted = await data.SubmitAsync(preview, "restored-source"); await data.Rows("evidenceValidationPolicies").UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
        Assert.Equal(submitted, await data.Service.LookupAsync(data.Actor, D.SubmitOperation, "restored-source"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.OpenReadAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata));
        // Historical pins prove the original reviewed association. This nonfinancial matching command does not assert that a fresh private read is currently eligible.
        Assert.Equal("match_recorded", (await data.MatchAsync("historical-nonfinancial-match"))["state"].AsString);
        await data.Inspection.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public void Statement_rows_do_not_require_invented_packages_and_original_unknowns_cannot_be_silently_changed() {
        var invoice = new SupplierDocumentInput(ProcurementRequestTestData.Location, 1, 1, 1, SupplierDocumentKind.Invoice, "000RefA", null, "IDR", null,
            [new("001", "Unreadable facts", null, null, null, null, null, null, null, null, null)], "Needs exact human transcription");
        Assert.Null(SupplierDocumentRules.Copy(invoice).Lines[0].Packages); Assert.False(SupplierDocumentRules.ExactTotal(invoice));
        var statement = invoice with { Kind = SupplierDocumentKind.Sph, DocumentReference = "000SPH", DocumentDate = new(2026, 10, 3), DocumentTotal = new Money(100), Discrepancy = null,
            Lines = [new("001", "Stated invoice amount", null, null, null, null, null, null, null, "000RefA", new Money(100))] };
        Assert.True(SupplierDocumentRules.ExactTotal(SupplierDocumentRules.Copy(statement))); Assert.Null(statement.Lines[0].Packages);
        Assert.Throws<DomainValidationException>(() => SupplierDocumentRules.Copy(statement with { Lines = [statement.Lines[0] with { Packages = 1 }] }));
        Assert.Throws<DomainValidationException>(() => SupplierDocumentRules.SameIdentity(invoice, invoice with { DocumentReference = "RefA" }));
        var mismatch = statement with { DocumentTotal = new Money(99), Discrepancy = "Original arithmetic mismatch" }; Assert.False(SupplierDocumentRules.ExactTotal(SupplierDocumentRules.Copy(mismatch)));
    }
    private static async Task<BsonDocument> HeadsAsync(D data) {
        var value = new BsonDocument(); foreach (var table in new[] { "supplierDocumentInspectionHeads", "supplierDocumentOrderHeads" }) {
            var rows = await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); value[table] = new BsonArray(rows);
        } return value;
    }
    private static async Task DeniedAsync(Func<Task> action) { var error = await Record.ExceptionAsync(action); Assert.True(error is AuthorizationDeniedException or PersistenceConflictException or DomainValidationException, error?.ToString()); }
    private static Task<BsonDocument> GuardAsync(D data, string table, BsonDocument validator) => data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
}
