using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Billing.BankStatementBson;

namespace UnitToko.Platform.Billing;

internal sealed record BankStatementReceiptSource(BsonDocument Import, BsonDocument Row, BsonDocument? CurrentProfile);
public sealed partial class BankStatementService
{
    internal IMongoDatabase ReceiptDatabase => database;
    internal Task RequireReceiptStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal async Task<BankStatementReceiptSource> ReceiptSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long importId, int rowNumber, bool current, CancellationToken ct) {
        if (!ReferenceEquals(tx.Client, database.Client) || !tx.IsInTransaction) throw B.Broken();
        var context = new Context(actor, authority, tx, false); _ = Require(context);
        var raw = await records.RowAsync(tx, B.Imports, importId, ct); context = context with { Scope = raw["scope"].AsBsonDocument }; _ = Require(context);
        var row = await records.ImportAsync(tx, importId, ct); BsonDocument? profile = null;
        await HistoricalAccountAsync(context, row, ct);
        if (current) {
            profile = await records.RowAsync(tx, B.Profiles, row["profile"]["_id"].AsInt64, ct);
            if (B.Hash(profile) != B.Hash(row["profile"].AsBsonDocument)) throw B.Changed();
            BankStatementRecords.ProfileAt(profile, Now);
            _ = await accounts.ReadSourceAsync(actor, authority, tx, row["account"]["accountId"].AsInt64, row["account"]["_id"].AsInt64, true, ct);
            await records.TouchAsync(tx, B.Profiles, profile, ct);
        }
        var selected = row["preview"]["rows"].AsBsonArray.SingleOrDefault(value => value["number"] == rowNumber)?.AsBsonDocument ?? throw BankReceiptPolicy.Invalid();
        await records.TouchAsync(tx, B.Imports, row, ct); await FenceAsync(context, ct);
        if (current) BankStatementRecords.ProfileAt(profile!, Now);
        return new(row, selected, profile);
    }
}
