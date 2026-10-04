using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Billing;

internal sealed record BankReceiptSupplierReturnSource(BsonDocument Receipt, BsonDocument Account,
    BsonDocument BankRow, BsonDocument SuspenseAccount, Action RequireCurrent);

public sealed partial class BankReceiptService
{
    internal async Task<BankReceiptSupplierReturnSource> SupplierReturnSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long receiptId, bool newEffect, CancellationToken ct) {
        // This real source admission checks and fences bank/account permissions; it
        // does not borrow a customer verification permission or invent an actor.
        var bank = await PaymentSourceAsync(actor, authority, tx, receiptId, ct);
        var context = new Context(actor, authority, tx, false, bank.Receipt["scope"].AsBsonDocument);
        PersonAuthorityProof? people = newEffect ? await RequireAllocationApprovalAsync(actor, authority, tx, bank.Receipt, ct) : null;
        var suspense = await AllocationSuspenseAsync(tx, bank.Receipt, ct);
        void Current() { _ = Require(context); people?.RequireCurrent(tx, clock.UtcNow); }
        Current(); return new(bank.Receipt, bank.Account, bank.BankRow, suspense, Current);
    }

    // Internal owning provenance only. Public callers must authorize their actual
    // procurement scope and must not project private bank data from this proof.
    internal static async Task<(BankReceiptPaymentSource Bank, BsonDocument SuspenseAccount)> ReadSupplierReturnHistoryAsync(
        IMongoDatabase database, IClientSessionHandle tx, long receiptId, CancellationToken ct) {
        var receipt = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, receiptId, ct);
        await new BankTransactionClaims(database).RequireCreditAsync(tx, receipt, ct);
        var import = await database.GetCollection<BsonDocument>(BankStatementBson.Imports)
            .Find(tx, new BsonDocument("_id", receipt["importId"])).SingleOrDefaultAsync(ct) ?? throw BankReceiptBson.Broken();
        var line = import["preview"]["rows"].AsBsonArray.SingleOrDefault(value => value["number"] == receipt["rowNumber"])?.AsBsonDocument ?? throw BankReceiptBson.Broken();
        var journal = await database.GetCollection<BsonDocument>("journalEntries")
            .Find(tx, new BsonDocument("_id", receipt["journalId"])).SingleOrDefaultAsync(ct) ?? throw BankReceiptBson.Broken();
        var suspense = AccountingBson.ReadDraft(journal).Lines.Single(item => item.MappingKey == receipt["policy"]["suspenseMapping"].AsString);
        return (new(receipt, import["account"].AsBsonDocument, line), new() { { "accountId", suspense.AccountId }, { "accountCode", suspense.AccountCode } });
    }
}
