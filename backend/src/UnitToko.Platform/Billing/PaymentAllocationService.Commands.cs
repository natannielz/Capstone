using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentAllocationService
{
    public async Task<BsonDocument> AllocateAsync(ActorContext actor, long verificationId, string binding, PaymentAllocationInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = PaymentAllocationPolicy.Copy(input); Metadata(metadata);
        if (verificationId <= 0 || binding is null || binding.Length != 64 || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw PaymentAllocationPolicy.Invalid();
        await StorageAsync(ct); var body = B.Body(verificationId, binding, input); var bodyHash = AuthorizationStore.CommandHash(body, metadata.Reason);
        var previous = await RecoverCoreAsync(actor, metadata.IdempotencyKey, bodyHash, ct); if (previous is not null) return B.Result(previous);
        var id = (await ids.NextAsync(B.Events, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        Context? context = null; Prepared? prepared = null;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, token) => {
            context = new(actor, authority, tx, true); prepared = null;
            var saved = await records.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token);
            if (saved is null) {
                prepared = await PrepareAsync(context, verificationId, [.. input.Lines.Select(line => line.InvoiceId.Value)], true, token);
                await PolicyAsync(context, token);
            } else {
                prepared = await PrepareAsync(context, saved["verificationId"].AsInt64, [.. saved["lines"].AsBsonArray.Select(line => line["invoiceId"].AsInt64)], true, token);
                await ValidateSourceAsync(context, saved, token);
            }
            var queries = Require(context); var resources = context.Resources; var source = context.Source.Decision["source"];
            var revision = source["revision"].AsInt64;
            return new(new(true, "allowed", queries.SelectMany(query => query.Clauses.Where(clause => resources.Any(clause.Matches)).Select(clause => clause.GrantId)).Distinct().ToImmutableArray()),
                [.. resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, authority.Directory)).Distinct()], Permissions.PaymentAllocate,
                new(resources[0], new(revision, revision, RecordedBy: source["recordedBy"].AsInt64)));
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); var ready = prepared ?? throw B.Broken(); var policy = active.Policy ?? throw B.Broken();
            var result = Review(ready, input); var verification = active.Source.Decision; var receipt = active.Source.Bank.Receipt;
            if (binding != B.Binding(verification["snapshotHash"], receipt["snapshotHash"], policy, ready.ReceiptBalance, ready.Lines)) throw B.Changed();
            var at = Now;
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "verificationId", verificationId }, { "verificationHash", verification["snapshotHash"] },
                { "declarationId", verification["declarationId"] }, { "declarationRevision", verification["source"]["revision"] }, { "recordedBy", verification["source"]["recordedBy"] },
                { "receiptId", receipt["_id"] }, { "receiptHash", receipt["snapshotHash"] }, { "receiptTotal", receipt["amount"] }, { "receiptVersion", ready.ReceiptBalance["version"].AsInt64 + 1 },
                { "receiptBefore", ready.ReceiptBalance }, { "suspenseAccount", active.Source.SuspenseAccount }, { "party", verification["source"]["party"] }, { "policy", policy }, { "binding", binding },
                { "amount", InvoiceBson.Money(result.Total) }, { "unapplied", InvoiceBson.Money(result.RemainingReceipt) }, { "lines", ready.Lines },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "recordedAt", at.UtcDateTime }, { "postingDate", InvoiceBson.Date(DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(7)).DateTime)) },
                { "operation", B.Operation }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                { "auditId", auditId }, { "fence", 0L }, { "snapshotHash", BsonNull.Value }, { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "postingFence", 0L } };
            await records.Rows(B.Events).InsertOneAsync(tx, row, cancellationToken: token);
            var journal = await new PostJournal(database, clock, [new PaymentAllocationPostingSource(database, row)]).PostAsync(tx, actor, PaymentAllocationPostingSource.Source(id), 1, new(metadata.Reason, metadata.CorrelationId), token);
            var unposted = row.DeepClone().AsBsonDocument; unposted["postingFence"] = 1L;
            row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint; row["postingFence"] = 1L; row["snapshotHash"] = B.Hash(row);
            if ((await records.Rows(B.Events).ReplaceOneAsync(tx, unposted, row, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            await records.SaveHeadAsync(tx, true, receipt["_id"].AsInt64, ready.ReceiptHead, row, receipt["amount"], receipt["snapshotHash"], token);
            foreach (var line in ready.Lines) await records.SaveHeadAsync(tx, false, line["invoiceId"].AsInt64, ready.InvoiceHeads[line["invoiceId"].AsInt64], row, line["source"]["total"], line["source"]["snapshotHash"], token);
            await records.Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
            await records.Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", B.Operation }, { "sourceKey", B.SourceKey(row) },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "payload", B.Result(row) }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var active = context ?? throw B.Broken(); var saved = await records.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token) ?? throw B.Broken();
            await ValidateSourceAsync(active, saved, token); await FenceAsync(active, token);
        }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string key, CancellationToken ct = default) {
        Metadata(new(key, "Recover payment allocation", "payment-allocation-recovery")); await StorageAsync(ct);
        var saved = await RecoverCoreAsync(actor, key, null, ct); return saved is null ? null : B.Result(saved);
    }
    private Task<BsonDocument?> RecoverCoreAsync(ActorContext actor, string key, string? bodyHash, CancellationToken ct) => authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
        var context = new Context(actor, authority, tx, true); _ = Require(context);
        var saved = await records.ForKeyAsync(tx, actor.UserId, key, token);
        if (saved is not null) {
            await PrepareAsync(context, saved["verificationId"].AsInt64, [.. saved["lines"].AsBsonArray.Select(line => line["invoiceId"].AsInt64)], true, token);
            await ValidateSourceAsync(context, saved, token);
            if (bodyHash is not null && saved["bodyHash"] != bodyHash) throw B.Changed("idempotency_key_reused");
        }
        await AuditReadAsync(context, "recovery", saved?["verificationId"].AsInt64, token); await FenceAsync(context, token); return saved;
    }, ct);
}
