using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Billing;

internal sealed record BankReceiptPaymentSource(BsonDocument Receipt, BsonDocument Account, BsonDocument BankRow);

public sealed partial class BankReceiptService
{
    internal IMongoDatabase PaymentDatabase => database;
    internal Task PaymentStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal async Task<BsonDocument> AllocationSuspenseAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw BankReceiptBson.Broken();
        var journal = await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", receipt["journalId"])).SingleOrDefaultAsync(ct) ?? throw BankReceiptBson.Broken();
        var draft = UnitToko.Platform.Accounting.AccountingBson.ReadDraft(journal);
        if (draft.Fingerprint != receipt["journalFingerprint"].AsString) throw BankReceiptBson.Broken();
        var line = draft.Lines.Single(item => item.MappingKey == receipt["policy"]["suspenseMapping"].AsString);
        return new() { { "accountId", line.AccountId }, { "accountCode", line.AccountCode } };
    }
    internal async Task<PersonAuthorityProof> RequireAllocationApprovalAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        var confirmation = await records.ConfirmationAsync(actor, authority, tx, receipt["confirmationId"].AsInt64, ct);
        var importer = confirmation.Source.Import["actorId"].AsInt64; var checker = confirmation.Row["actorId"].AsInt64;
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [importer, checker], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(person => person.UserId == checker), proof.Evidence.Single(person => person.UserId == importer)))
            throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, ct); return proof;
    }
    internal async Task<BankReceiptPaymentSource> PaymentSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long id, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw BankReceiptBson.Broken();
        var context = new Context(actor, authority, tx, false); _ = Require(context);
        var raw = await records.RowAsync(tx, BankReceiptBson.Receipts, id, ct);
        context = context with { Scope = raw["scope"].AsBsonDocument }; _ = Require(context);
        var row = await records.ReceiptAsync(actor, authority, tx, id, ct);
        var source = await statements.ReceiptSourceAsync(actor, authority, tx, row["importId"].AsInt64, row["rowNumber"].AsInt32, false, ct);
        await records.TouchAsync(tx, BankReceiptBson.Receipts, row, ct);
        await FenceAsync(context, ct);
        return new(row, source.Import["account"].DeepClone().AsBsonDocument, source.Row.DeepClone().AsBsonDocument);
    }
}
