using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using B = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

internal sealed class SupplierPurchaseOrderCapture(BsonDocument snapshot, Func<CancellationToken, Task> fence, Action current,
    IMongoDatabase database, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx)
{
    private readonly string hash = B.Hash(snapshot);
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    internal BsonDocument Snapshot { get; } = snapshot;
    internal void Require(IMongoDatabase owner, ActorContext caller, AuthorizationSnapshot auth, IClientSessionHandle session) {
        if (!ReferenceEquals(owner, database) || !ReferenceEquals(caller, actor) || !ReferenceEquals(auth, authority) || !ReferenceEquals(session, tx) ||
            !session.IsInTransaction || session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(Snapshot) != hash) throw B.Broken(); current();
    }
    internal async Task FenceAsync(IMongoDatabase owner, ActorContext caller, AuthorizationSnapshot auth, IClientSessionHandle session, CancellationToken ct) {
        Require(owner, caller, auth, session); await fence(ct); Require(owner, caller, auth, session); ct.ThrowIfCancellationRequested();
    }
}

public sealed partial class SupplierService
{
    internal async Task<SupplierPurchaseOrderCapture> CapturePurchaseOrderAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, PurchaseOrderInput input, string permission, CancellationToken ct) {
        if (!ReferenceEquals(owner, database) || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            permission is not (Permissions.ProcurementPrepare or Permissions.ProcurementApprove)) throw B.Broken();
        await StorageAsync(ct); var location = input.Location;
        var context = await ContextAsync(actor, authority, tx, new(location.OrganizationId, location.ShopId, location.ChannelId), permission, ct, location);
        context.Head = await records.HeadAsync(tx, input.SupplierId, ct); Equal(context.Head["owner"], B.Owner(context.Owner));
        Equal(context.Head["versionId"], input.SupplierVersionId); Equal(context.Head["version"], input.SupplierVersion);
        var version = await records.RowAsync(tx, B.Versions, input.SupplierVersionId, ct);
        context.Proposal = await records.ProposalAsync(tx, version["proposalId"].AsInt64, ct); var approved = B.Input(context.Proposal["input"]);
        var decision = await records.DecisionForAsync(tx, context.Proposal, ct) ?? throw B.Broken(); Equal(decision["decision"], "approve");
        await HistoricalPeopleAsync(context, context.Proposal, ct); await PolicyAsync(context, approved, ct); Equal(context.Sources, context.Proposal["sources"]);
        var selected = input.Lines.Select(line => approved.Mappings.SingleOrDefault(mapping => mapping.Location == location && mapping.SkuId == line.RequestSkuId &&
            mapping.DefinitionId == line.DefinitionId && mapping.DefinitionVersion == line.DefinitionVersion && mapping.SourceSystem == line.SourceSystem &&
            mapping.SourceAccountReference == line.SourceAccountReference && mapping.ExternalItemCode == line.ExternalItemCode && mapping.ValidFrom <= Now && Now < mapping.ValidUntil) ?? throw B.Changed()).ToImmutableArray();
        await CaptureMappingsAsync(context, approved with { Mappings = selected }, ct); context.UnexpiredMappings = approved with { Mappings = selected };
        for (var index = 0; index < selected.Length; index++) Equal(context.MappingSources[index], context.Proposal["mappingSources"][approved.Mappings.IndexOf(selected[index])]);
        var snapshot = SupplierRecords.PurchaseOrderSnapshot(input, version, context.Proposal, decision, selected);
        return new(snapshot, token => FenceAsync(context, token), () => Current(context), owner, actor, authority, tx);
    }
    internal Task FencePurchaseOrderAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        SupplierPurchaseOrderCapture capture, CancellationToken ct) => capture.FenceAsync(owner, actor, authority, tx, ct);
}

internal sealed partial class SupplierRecords
{
    internal static BsonDocument PurchaseOrderSnapshot(PurchaseOrderInput input, BsonDocument version, BsonDocument proposal, BsonDocument decision, ImmutableArray<SupplierMappingInput> mappings) {
        var approved = B.Input(proposal["input"]); return new() {
            { "supplierId", input.SupplierId }, { "versionId", input.SupplierVersionId }, { "version", input.SupplierVersion }, { "versionHash", version["snapshotHash"] },
            { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] }, { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] },
            { "subjectReference", approved.Details.SubjectReference }, { "displayName", approved.Details.DisplayName }, { "policyId", approved.PolicyId }, { "policyRevision", approved.PolicyRevision },
            { "policyHash", B.Hash(proposal["policy"].AsBsonDocument) }, { "mappings", new BsonArray(mappings.Select(mapping => new BsonDocument {
                { "mapping", B.Mapping(mapping) }, { "source", proposal["mappingSources"][approved.Mappings.IndexOf(mapping)] } })) } };
    }
    internal async Task PurchaseOrderSourceAsync(IClientSessionHandle tx, PurchaseOrderInput input, BsonDocument snapshot, DateTimeOffset recordedAt, CancellationToken ct) {
        var version = await RowAsync(tx, B.Versions, input.SupplierVersionId, ct); Equal(version["snapshotHash"], B.Hash(version)); Equal(version["supplierId"], input.SupplierId); Equal(version["version"], input.SupplierVersion);
        var proposal = await ProposalAsync(tx, version["proposalId"].AsInt64, ct); var decision = await DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
        Equal(decision["decision"], "approve"); Equal(decision["versionId"], version["_id"]); Equal(decision["supplierVersion"], version["version"]);
        Equal(version["details"], proposal["input"]["details"]); Equal(version["mappings"], proposal["input"]["mappings"]); Equal(version["mappingSources"], proposal["mappingSources"]);
        Equal(version["owner"], B.Owner(new SupplierOwner(input.Location.OrganizationId, input.Location.ShopId, input.Location.ChannelId)));
        if (version["effectiveAt"].ToUniversalTime() > recordedAt.UtcDateTime) throw B.Broken(); var approved = B.Input(proposal["input"]); PolicyAt(proposal["policy"].AsBsonDocument, recordedAt);
        var mappings = input.Lines.Select(line => approved.Mappings.SingleOrDefault(mapping => mapping.Location == input.Location && mapping.SkuId == line.RequestSkuId &&
            mapping.DefinitionId == line.DefinitionId && mapping.DefinitionVersion == line.DefinitionVersion && mapping.SourceSystem == line.SourceSystem &&
            mapping.SourceAccountReference == line.SourceAccountReference && mapping.ExternalItemCode == line.ExternalItemCode && mapping.ValidFrom <= recordedAt && recordedAt < mapping.ValidUntil) ?? throw B.Broken()).ToImmutableArray();
        Equal(snapshot, PurchaseOrderSnapshot(input, version, proposal, decision, mappings));
    }
}
