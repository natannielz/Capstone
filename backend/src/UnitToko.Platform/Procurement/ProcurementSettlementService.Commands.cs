using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementSettlementBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSettlementService
{
    private CommandAuthorization Command(Context context, long importer) {
        var approval = new ApprovalRequest(1, 1, RecordedBy: context.Source!.Record["actorId"].AsInt64, SubmittedBy: importer);
        return new(ScopePolicy.Authorize(context.Actor, context.Authority, Permissions.DisbursementVerify, context.Resource, clock.UtcNow, approval),
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory), Permissions.DisbursementVerify, new(context.Resource, approval));
    }
    private static void Revision(long id, long revision) { if (id <= 0 || revision != 1) throw ProcurementSettlementRules.Invalid(); }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, long disbursementId, long expectedRevision, ProcurementSettlementInput input, CancellationToken ct = default) {
        Revision(disbursementId, expectedRevision); input = ProcurementSettlementRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await PrepareAsync(actor, authority, tx, disbursementId, input, token); var record = context.Source!.Record;
            var binding = B.Binding(actor.UserId, actor.GrantVersion, record, input, context.Bank!.Debit, context.Policy!, context.Accounting!, context.PostingDate!);
            var result = new BsonDocument { { "disbursement", B.DisbursementView(record) }, { "input", B.Input(input) }, { "policy", B.PolicyView(context.Policy!) },
                { "debit", B.DebitView(context.Bank.Debit, context.Bank.Row["reference"]) }, { "accounting", context.Accounting }, { "postingDate", context.PostingDate }, { "binding", binding }, { "observedAt", Now.UtcDateTime } };
            await AuditReadAsync(context, "settlement-preview", new("disbursementId", disbursementId), token); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> VerifyAsync(ActorContext actor, long disbursementId, long expectedRevision, ProcurementSettlementInput input, string binding, bool attested,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Revision(disbursementId, expectedRevision); input = ProcurementSettlementRules.Copy(input); Metadata(metadata);
        if (!attested || binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementSettlementRules.Invalid();
        await StorageAsync(ct); var body = B.Body(disbursementId, expectedRevision, input, binding, attested); var hash = AuthorizationStore.CommandHash(body, metadata.Reason);
        Context? context = null; BsonDocument? replay = null; long? id = null, auditId = null;
        return await authorization.ExecuteCommandAsync(actor, B.Operation, metadata, body, async (authority, tx, token) => {
            context = null; replay = await records.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token);
            if (replay is not null) {
                ProcurementSettlementRecords.Equal(replay["disbursementId"], disbursementId);
                context = await SourceAsync(actor, authority, tx, disbursementId, true, false, token);
                replay = await records.ReadAsync(tx, replay["_id"].AsInt64, token, context.Source); await PeopleAsync(context, replay, token);
                return Command(context, replay["debit"]["importedBy"].AsInt64);
            }
            context = await PrepareAsync(actor, authority, tx, disbursementId, input, token);
            return Command(context, context.Bank!.Debit["importedBy"].AsInt64);
        }, async (tx, token) => {
            var current = context ?? throw B.Broken(); if (replay is not null) throw B.Broken(); var record = current.Source!.Record;
            ProcurementSettlementRecords.Equal(binding, B.Binding(actor.UserId, actor.GrantVersion, record, input, current.Bank!.Debit, current.Policy!, current.Accounting!, current.PostingDate!));
            await FenceAsync(current, token);
            id ??= (await ids.NextAsync(B.Events, token)).Value; auditId ??= (await ids.NextAsync("auditEvents", token)).Value;
            var at = Now; Current(current);
            var row = new BsonDocument { { "_id", id.Value }, { "schemaVersion", 1 }, { "operation", B.Operation }, { "disbursementId", disbursementId }, { "disbursementHash", record["snapshotHash"] },
                { "draftId", record["draftId"] }, { "authorizationId", record["authorizationId"] }, { "location", record["location"].DeepClone() }, { "amount", record["input"]["amount"] },
                { "canonicalIdentity", current.Bank.Debit["canonicalIdentity"] }, { "debit", current.Bank.Debit.DeepClone() }, { "debitReference", current.Bank.Row["reference"] },
                { "input", B.Input(input) }, { "policy", B.Snapshot(current.Policy!) }, { "accounting", current.Accounting!.DeepClone() }, { "binding", binding }, { "attested", true },
                { "people", new BsonArray(current.People!.Evidence.OrderBy(person => person.UserId).Select(ReceivingAccountBson.Person)) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "recordedAt", at.UtcDateTime }, { "postingDate", current.PostingDate }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", hash }, { "reason", metadata.Reason },
                { "correlationId", metadata.CorrelationId }, { "auditId", auditId.Value }, { "fence", 0L }, { "postingFence", 0L }, { "journalId", BsonNull.Value }, { "journalFingerprint", BsonNull.Value }, { "snapshotHash", BsonNull.Value } };
            await Rows(B.Events).InsertOneAsync(tx, row, cancellationToken: token);
            var journal = await new PostJournal(database, clock, [new ProcurementSettlementPostingSource(database, row)]).PostAsync(tx, actor,
                ProcurementSettlementPostingSource.Source(id.Value), 1, new(metadata.Reason, metadata.CorrelationId), token);
            var actual = await Rows("journalEntries").Find(tx, new BsonDocument("_id", journal.Id)).SingleOrDefaultAsync(token) ?? throw B.Broken();
            ProcurementSettlementPostingSource.RequireAccounts(row, AccountingBson.ReadDraft(actual)); ProcurementSettlementRecords.Equal(actual["bookId"], current.Accounting["bookId"]);
            var pending = row.DeepClone().AsBsonDocument; pending["postingFence"] = 1L;
            row["postingFence"] = 1L; row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint; row["snapshotHash"] = B.Hash(row);
            if ((await Rows(B.Events).ReplaceOneAsync(tx, pending, row, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", B.Operation }, { "sourceKey", B.SourceKey(row) },
                { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "location", row["location"] }, { "disbursementId", disbursementId }, { "payload", B.Result(row) },
                { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            await current.Bank.ClaimAsync(row, token); Current(current); return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var current = context ?? throw B.Broken(); var saved = await records.ForKeyAsync(tx, actor.UserId, metadata.IdempotencyKey, token) ?? throw B.Broken();
            saved = await records.ReadAsync(tx, saved["_id"].AsInt64, token, current.Source); ProcurementSettlementRecords.Equal(saved["bodyHash"], hash); ProcurementSettlementRecords.Equal(saved["disbursementId"], disbursementId);
            if (replay is null) ProcurementSettlementRecords.Equal(await AccountingAsync(current, current.Policy!, InvoiceBson.Date(saved["postingDate"]), token), saved["accounting"]);
            await FenceAsync(current, token);
        }, validateCurrent: () => { if (context is null) throw B.Broken(); Current(context); });
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation != B.Operation || !ReceivingAccountPolicy.Text(key, 128)) throw ProcurementSettlementRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, Permissions.ProcurementSettlementRead); _ = Query(actor, authority, Permissions.DisbursementVerify);
            var row = await records.ForKeyAsync(tx, actor.UserId, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, true, token); return null; }
            var context = await SourceAsync(actor, authority, tx, row["disbursementId"].AsInt64, true, false, token);
            row = await records.ReadAsync(tx, row["_id"].AsInt64, token, context.Source); await PeopleAsync(context, row, token); await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
