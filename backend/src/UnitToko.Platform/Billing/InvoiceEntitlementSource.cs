using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Billing;

internal sealed record FinancialInvoiceSource(BsonDocument Invoice, BsonDocument MoneySource, BsonArray OriginalAccounts,
    Action RequireCurrent, Func<CancellationToken, Task> FenceAsync);

public sealed partial class InvoiceService
{
    // Owning-module source only: neither a browser amount nor current catalog/account mapping
    // can replace an issued line, its original monetary allocation or its posted account identity.
    internal async Task<FinancialInvoiceSource> FinancialEntitlementSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long invoiceId, CancellationToken ct) {
        void Session() {
            if (actor is null || authority is null || tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || invoiceId <= 0)
                throw InvoiceRecords.Invalid();
            _ = actor.Validate();
        }
        async Task<(BsonDocument Invoice, BsonDocument Money, BsonArray Accounts)> ReadAsync(CancellationToken token) {
            Session(); token.ThrowIfCancellationRequested();
            var row = await AuthorizedInvoiceAsync(actor, authority, tx, invoiceId, Permissions.InvoiceRead, token);
            // Keep the canonical money source exactly equal to the existing allocation owner.
            var money = await AllocationSnapshotAsync(database, tx, row, token);
            var journal = await records.Rows("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(token)
                ?? throw InvoiceRecords.Invalid();
            var posted = AccountingBson.ReadDraft(journal); var terms = InvoiceBson.Terms(row["terms"].AsBsonDocument);
            var components = new[] { ("total", terms.ReceivableMapping), ("net", terms.RevenueMapping),
                ("tax", terms.TaxMapping), ("charges", terms.ChargesMapping) };
            var accounts = new BsonArray();
            foreach (var (component, mapping) in components) {
                if (InvoiceBson.Money(row["totals"][component]).Value == 0) continue;
                var line = posted.Lines.Single(value => value.MappingKey == mapping);
                accounts.Add(new BsonDocument { { "component", component }, { "mappingKey", mapping },
                    { "accountId", line.AccountId }, { "accountCode", line.AccountCode } });
            }
            return (row.DeepClone().AsBsonDocument, money.DeepClone().AsBsonDocument, accounts);
        }
        var frozen = await ReadAsync(ct);
        var scopes = InvoiceBson.Sources(frozen.Invoice["sources"].AsBsonArray).Select(source => source.Resource).ToArray();
        void Current() {
            Session();
            var query = ScopePolicy.Compile(actor, authority, Permissions.InvoiceRead, clock.UtcNow); AuthorizationStore.Require(query);
            if (scopes.Length == 0 || scopes.Any(scope => !query.Matches(scope) || !ScopePolicy.IsResourceActive(scope, authority.Directory)))
                throw InvoiceRecords.Invalid();
        }
        static bool Same(BsonValue left, BsonValue right) => new BsonDocument("value", left).ToBson().SequenceEqual(new BsonDocument("value", right).ToBson());
        async Task Fence(CancellationToken token) {
            Current();
            var current = await ReadAsync(token);
            if (!Same(frozen.Invoice, current.Invoice) || !Same(frozen.Money, current.Money) || !Same(frozen.Accounts, current.Accounts))
                throw InvoiceRecords.Invalid();
            Current();
        }
        Current();
        // Callback baselines are private clones, so mutating a returned projection cannot
        // change what the final source fence requires from the actual issued history.
        return new(frozen.Invoice.DeepClone().AsBsonDocument, frozen.Money.DeepClone().AsBsonDocument,
            frozen.Accounts.DeepClone().AsBsonArray, Current, Fence);
    }
}
