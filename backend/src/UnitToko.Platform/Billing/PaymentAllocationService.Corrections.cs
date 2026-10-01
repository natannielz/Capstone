using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;
public sealed record PaymentAllocationCorrectionPreview(LongId OriginalAllocationId, string Binding, PaymentAllocationView View, BsonDocument Policy, PaymentAllocationResult Result, BsonArray Capacities);
public sealed record PaymentAllocationCorrectionView(LongId OriginalAllocationId, DateTimeOffset OriginalRecordedAt, PaymentAllocationView View, BsonArray Capacities);
public sealed partial class PaymentAllocationService
{
    private async Task<PersonAuthorityProof> CorrectionPeopleAsync(Context context, BsonDocument original, CancellationToken ct) {
        var allocator = original["actorId"].AsInt64; var maker = original["recordedBy"].AsInt64;
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. new[] { context.Actor.UserId, allocator, maker }.Distinct()], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == context.Actor.UserId), proof.Evidence.Single(person => person.UserId == allocator), proof.Evidence.Single(person => person.UserId == maker))) throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.Current.Add(() => proof.RequireCurrent(context.Tx, clock.UtcNow)); return proof;
    }
    private async Task<BsonDocument> CorrectionPolicyAsync(Context context, CancellationToken ct) {
        var bank = context.Source.Bank.Receipt;
        var raw = await records.Rows(C.Policies).Find(context.Tx, new BsonDocument("scope", bank["scope"])).SingleOrDefaultAsync(ct) ?? throw B.Changed("payment_correction_policy_unavailable");
        var policy = await records.RowAsync(context.Tx, C.Policies, raw["_id"].AsInt64, ct); C.PolicyAt(policy, Now);
        var allocation = context.Policy ?? throw B.Broken(); foreach (var field in new[] { "scope", "arMapping", "suspenseMapping" }) R.Equal(policy[field], allocation[field]);
        await records.TouchAsync(context.Tx, C.Policies, policy, ct); context.CorrectionPolicy = B.Snapshot(policy); return context.CorrectionPolicy;
    }
    private async Task<(Prepared Ready, BsonDocument Original)> PrepareCorrectionAsync(Context context, long allocationId, long[] invoiceIds, CancellationToken ct, bool newEffect = true) {
        var original = await records.RawAsync(context.Tx, allocationId, ct); var totals = await records.CorrectionTotalsAsync(context.Tx, allocationId, ct);
        if (invoiceIds.Any(id => !totals.ContainsKey(id))) throw PaymentAllocationPolicy.Invalid("payment_reversal_exceeds_allocation");
        var ready = await PrepareAsync(context, original["verificationId"].AsInt64, invoiceIds, newEffect, ct); await ValidateSourceAsync(context, original, ct);
        foreach (var line in ready.Lines) { line["originallyAllocated"] = original["lines"].AsBsonArray.Single(value => value["invoiceId"] == line["invoiceId"])["amount"]; line["previouslyReversed"] = new BsonDecimal128(totals[line["invoiceId"].AsInt64]); }
        return (ready, original);
    }
    private static BsonArray CorrectionCapacities(Prepared ready) => new(ready.Lines.Select(line => new BsonDocument { { "invoiceId", line["invoiceId"] }, { "originallyAllocated", line["originallyAllocated"] }, { "previouslyReversed", line["previouslyReversed"] } }));
    public async Task<PaymentAllocationCorrectionView> ReadCorrectionAsync(ActorContext actor, long allocationId, CancellationToken ct = default) {
        if (allocationId <= 0) throw PaymentAllocationPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, false); var original = await records.RawAsync(tx, allocationId, token);
            var (ready, _) = await PrepareCorrectionAsync(context, allocationId, [.. original["lines"].AsBsonArray.Select(line => line["invoiceId"].AsInt64)], token, false);
            await AuditReadAsync(context, "correction.balances", original["verificationId"].AsInt64, token); await FenceAsync(context, token);
            return new PaymentAllocationCorrectionView(new(allocationId), new(original["recordedAt"].ToUniversalTime()), ready.View, CorrectionCapacities(ready));
        }, ct);
    }
    public async Task<(ImmutableArray<PaymentAllocationCorrectionView> Items, bool HasMore)> CorrectionCandidatesAsync(ActorContext actor, long verificationId, long? before = null, CancellationToken ct = default) {
        if (verificationId <= 0 || before is <= 0) throw PaymentAllocationPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, false); await SourceAsync(context, verificationId, false, token);
            var query = new BsonDocument { { "verificationId", verificationId }, { "receiptId", context.Source.Bank.Receipt["_id"] } }; if (before is { } cursor) query["_id"] = new BsonDocument("$lt", cursor);
            var rows = await records.Rows(B.Events).Find(tx, query).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var results = ImmutableArray.CreateBuilder<PaymentAllocationCorrectionView>();
            foreach (var candidate in rows.Take(25)) {
                var (ready, original) = await PrepareCorrectionAsync(context, candidate["_id"].AsInt64, [.. candidate["lines"].AsBsonArray.Select(line => line["invoiceId"].AsInt64)], token, false);
                results.Add(new(new(original["_id"].AsInt64), new(original["recordedAt"].ToUniversalTime()), ready.View, CorrectionCapacities(ready)));
            }
            await AuditReadAsync(context, "correction.candidates", verificationId, token); await FenceAsync(context, token); return (results.ToImmutable(), rows.Count > 25);
        }, ct);
    }
    private PaymentAllocationResult ReviewCorrection(Prepared ready, PaymentAllocationInput input) {
        var party = B.Party(ready.Context.Source.Decision["source"]["party"].AsBsonDocument); var receipt = ready.Context.Source.Bank.Receipt; var before = ready.ReceiptBalance;
        var result = PaymentAllocationPolicy.ReviewReversal(new(new(receipt["_id"].AsInt64), before["version"].AsInt64, party, "IDR", InvoiceBson.Money(receipt["amount"]), InvoiceBson.Money(before["allocated"]), InvoiceBson.Money(before["reservedForRefund"])),
            [.. ready.Lines.Select(line => new InvoiceFunds(new(line["invoiceId"].AsInt64), line["before"]["version"].AsInt64, party, "IDR", InvoiceBson.Money(line["source"]["total"]), InvoiceBson.Money(line["before"]["paid"]), InvoiceBson.Money(line["before"]["credited"])))],
            [.. ready.Lines.Select(line => new AllocationReversalCapacity(new(line["invoiceId"].AsInt64), InvoiceBson.Money(line["originallyAllocated"]), InvoiceBson.Money(line["previouslyReversed"])))], input);
        foreach (var line in ready.Lines) {
            if (ready.Context.Policy is { } policy) R.Equal(line["source"]["arMapping"], policy["arMapping"]);
            var reviewed = result.Invoices.Single(value => value.InvoiceId.Value == line["invoiceId"].AsInt64); line["amount"] = InvoiceBson.Money(reviewed.Amount); line["version"] = checked(reviewed.PreviousVersion + 1); line["remaining"] = InvoiceBson.Money(reviewed.Remaining);
        }
        return result;
    }
    public async Task<PaymentAllocationCorrectionPreview> PreviewCorrectionAsync(ActorContext actor, long allocationId, PaymentAllocationInput input, CancellationToken ct = default) {
        input = PaymentAllocationPolicy.Copy(input); if (allocationId <= 0) throw PaymentAllocationPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, true, true); var (ready, original) = await PrepareCorrectionAsync(context, allocationId, [.. input.Lines.Select(line => line.InvoiceId.Value)], token);
            var policy = await PolicyAsync(context, token); var correctionPolicy = await CorrectionPolicyAsync(context, token); await CorrectionPeopleAsync(context, original, token); var result = ReviewCorrection(ready, input);
            var binding = C.Binding(original, correctionPolicy, policy, ready.ReceiptBalance, ready.Lines); await AuditReadAsync(context, "correction.preview", original["verificationId"].AsInt64, token); await FenceAsync(context, token);
            return new PaymentAllocationCorrectionPreview(new(allocationId), binding, ready.View, correctionPolicy, result, CorrectionCapacities(ready));
        }, ct);
    }
    public async Task<BsonDocument> CorrectAsync(ActorContext actor, long allocationId, string binding, PaymentAllocationInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = PaymentAllocationPolicy.Copy(input); Metadata(metadata); if (allocationId <= 0 || binding is null || binding.Length != 64 || binding.Any(ch => !char.IsAsciiHexDigitLower(ch))) throw PaymentAllocationPolicy.Invalid(); await StorageAsync(ct);
        var body = C.Body(allocationId, binding, input); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason); var previous = await RecoverCorrectionCoreAsync(actor, metadata.IdempotencyKey, bodyHash, ct); if (previous is not null) { R.Equal(previous["originalAllocationId"], allocationId); return C.Result(previous); }
        // Shared allocator keeps lastEventId unambiguous across the mixed canonical chain.
        var id = (await ids.NextAsync(B.Events, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value; Context? context = null; Prepared? ready = null; BsonDocument? original = null; PersonAuthorityProof? proof = null;
        return await authorization.ExecuteCommandAsync(actor, C.Operation, metadata, body, async (authority, tx, token) => {
            context = new(actor, authority, tx, true, true); var prepared = await PrepareCorrectionAsync(context, allocationId, [.. input.Lines.Select(line => line.InvoiceId.Value)], token); ready = prepared.Ready; original = prepared.Original;
            var saved = await records.CorrectionForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token); if (saved is null) { await PolicyAsync(context, token); await CorrectionPolicyAsync(context, token); } else await ValidateSourceAsync(context, saved, token);
            proof = await CorrectionPeopleAsync(context, original, token); var queries = Require(context); var resources = context.Resources; var source = context.Source.Decision["source"]; var revision = source["revision"].AsInt64;
            return new(new(true, "allowed", queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches)).Select(clause => clause.GrantId)).Distinct().ToImmutableArray()), [.. resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, authority.Directory)).Distinct()], Permissions.PaymentAllocate, new(resources[0], new(revision, revision, RecordedBy: source["recordedBy"].AsInt64)));
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); var prepared = ready ?? throw B.Broken(); var prior = original ?? throw B.Broken(); var policy = active.Policy ?? throw B.Broken(); var correctionPolicy = active.CorrectionPolicy ?? throw B.Broken(); var result = ReviewCorrection(prepared, input);
            if (binding != C.Binding(prior, correctionPolicy, policy, prepared.ReceiptBalance, prepared.Lines)) throw B.Changed(); var receipt = active.Source.Bank.Receipt; var at = Now;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "originalAllocationId", allocationId }, { "originalAllocationHash", prior["snapshotHash"] }, { "originalJournalId", prior["journalId"] }, { "originalJournalFingerprint", prior["journalFingerprint"] },
                { "verificationId", prior["verificationId"] }, { "verificationHash", prior["verificationHash"] }, { "declarationId", prior["declarationId"] }, { "declarationRevision", prior["declarationRevision"] }, { "recordedBy", prior["recordedBy"] },
                { "receiptId", receipt["_id"] }, { "receiptHash", receipt["snapshotHash"] }, { "receiptTotal", receipt["amount"] }, { "receiptVersion", prepared.ReceiptBalance["version"].AsInt64 + 1 }, { "receiptBefore", prepared.ReceiptBalance }, { "suspenseAccount", active.Source.SuspenseAccount }, { "party", prior["party"] },
                { "policy", policy }, { "correctionPolicy", correctionPolicy }, { "people", new BsonArray((proof ?? throw B.Broken()).Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) }, { "binding", binding }, { "amount", InvoiceBson.Money(result.Total) }, { "unapplied", InvoiceBson.Money(result.RemainingReceipt) }, { "lines", prepared.Lines },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "recordedAt", at.UtcDateTime }, { "postingDate", InvoiceBson.Date(DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(7)).DateTime)) }, { "operation", C.Operation }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId },
                { "fence", 0L }, { "snapshotHash", BsonNull.Value }, { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "postingFence", 0L } };
            await records.Rows(C.Events).InsertOneAsync(tx, row, cancellationToken: token); var journal = await new PostJournal(database, clock, [new PaymentAllocationPostingSource(database, row)]).PostAsync(tx, actor, PaymentAllocationPostingSource.Source(id, true), 1, new(metadata.Reason, metadata.CorrelationId), token);
            var unposted = row.DeepClone().AsBsonDocument; unposted["postingFence"] = 1L; row["postingFence"] = 1L; row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint; row["snapshotHash"] = B.Hash(row);
            if ((await records.Rows(C.Events).ReplaceOneAsync(tx, unposted, row, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            await records.SaveHeadAsync(tx, true, receipt["_id"].AsInt64, prepared.ReceiptHead, row, receipt["amount"], receipt["snapshotHash"], token);
            foreach (var line in prepared.Lines) await records.SaveHeadAsync(tx, false, line["invoiceId"].AsInt64, prepared.InvoiceHeads[line["invoiceId"].AsInt64], row, line["source"]["total"], line["source"]["snapshotHash"], token);
            await records.Rows("auditEvents").InsertOneAsync(tx, C.Audit(row), cancellationToken: token); await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", C.Operation }, { "sourceKey", C.SourceKey(row) }, { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", C.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token); return C.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var saved = await records.CorrectionForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token) ?? throw B.Broken(); R.Equal(saved["originalAllocationId"], allocationId); R.Equal(saved["bodyHash"], bodyHash);
            await ValidateSourceAsync(active, saved, token); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument?> RecoverCorrectionAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover original allocation correction", "payment-correction-recovery")); await StorageAsync(ct); var row = await RecoverCorrectionCoreAsync(actor, key, null, ct); return row is null ? null : C.Result(row);
    }
    private Task<BsonDocument?> RecoverCorrectionCoreAsync(ActorContext actor, string key, string? bodyHash, CancellationToken ct) => authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
        var context = new Context(actor, authority, tx, true, true); _ = Require(context); var saved = await records.CorrectionForKeyAsync(tx, actor.UserId, key, token);
        if (saved is not null) {
            var (_, original) = await PrepareCorrectionAsync(context, saved["originalAllocationId"].AsInt64, [.. saved["lines"].AsBsonArray.Select(line => line["invoiceId"].AsInt64)], token); await CorrectionPeopleAsync(context, original, token); await ValidateSourceAsync(context, saved, token);
            if (bodyHash is not null && saved["bodyHash"] != bodyHash) throw B.Changed("idempotency_key_reused");
        }
        await AuditReadAsync(context, "correction.recovery", saved?["verificationId"].AsInt64, token); await FenceAsync(context, token); return saved;
    }, ct);
}
