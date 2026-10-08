using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.SupplierBankBson;

namespace UnitToko.Platform.Procurement;

internal sealed class SupplierBankCapture(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
    BsonDocument source, Func<CancellationToken, Task> fence, Action current)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(source);
    internal BsonDocument Source { get; } = source;
    internal void RequireContext(object service, ActorContext caller, AuthorizationSnapshot auth, IClientSessionHandle session) {
        if (!ReferenceEquals(service, owner) || caller != actor || !ReferenceEquals(auth, authority) || !ReferenceEquals(session, tx) || !session.IsInTransaction ||
            session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(Source) != hash) throw B.Broken(); current();
    }
    internal void RequireCurrent() => RequireContext(owner, actor, authority, tx);
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class SupplierBankService
{
    // This authorizes private beneficiary disclosure only. A later payment owner must separately prove its money authority.
    internal async Task<SupplierBankCapture> CaptureBeneficiaryAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long supplierId, long versionId, long version, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace) || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            supplierId <= 0 || versionId <= 0 || version <= 0) throw B.Broken();
        await StorageAsync(ct); var context = await SupplierContextAsync(actor, authority, tx, supplierId, Permissions.SupplierBankDetailsRead, true, ct); await HeadAsync(context, supplierId, ct);
        if (context.Head is not { } head || head["versionId"] != versionId || head["version"] != version) throw B.Changed();
        var bank = await records.RowAsync(tx, B.Versions, versionId, ct); context.Proposal = await records.ProposalAsync(tx, bank["proposalId"].AsInt64, ct); await HistoricalPeopleAsync(context, ct);
        if (!await EligibleAsync(context, context.Proposal, ct)) throw B.Changed();
        var decision = await records.DecisionForAsync(tx, context.Proposal, ct) ?? throw B.Broken(); var observation = context.Observation ?? throw B.Broken();
        var source = new BsonDocument { { "beneficiaryId", head["_id"] }, { "versionId", versionId }, { "version", version }, { "versionHash", bank["snapshotHash"] },
            { "owner", head["owner"] }, { "supplierId", supplierId }, { "supplier", bank["supplier"] }, { "details", bank["details"] }, { "physicalIdentity", bank["physicalIdentity"] }, { "policy", bank["policy"] },
            { "proposalId", context.Proposal["_id"] }, { "proposalHash", context.Proposal["snapshotHash"] }, { "observationId", observation["_id"] }, { "observationHash", observation["snapshotHash"] },
            { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] }, { "effectiveAt", bank["effectiveAt"] } };
        await AuditReadAsync(context, "consumer-disclosure", new() { { "supplierId", supplierId }, { "versionId", versionId } }, ct);
        Current(context); ct.ThrowIfCancellationRequested(); return new(this, actor, authority, tx, source, token => FenceAsync(context, token), () => Current(context));
    }
    internal async Task FenceBeneficiaryAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, SupplierBankCapture capture, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw B.Broken();
        capture.RequireContext(this, actor, authority, tx); await capture.FenceAsync(ct);
    }
}
