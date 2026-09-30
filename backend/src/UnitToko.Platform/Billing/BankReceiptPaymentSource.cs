using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Billing;

internal sealed record BankReceiptPaymentSource(BsonDocument Receipt, BsonDocument Account, BsonDocument BankRow);

public sealed partial class BankReceiptService
{
    internal IMongoDatabase PaymentDatabase => database;
    internal Task PaymentStorageAsync(CancellationToken ct) => StorageAsync(ct);
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
