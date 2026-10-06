using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Inventory;

namespace UnitToko.Platform.Inventory;

/// <summary>Only a current independently approved in-flight release owner can mint
/// this proof, after InventoryLedger wrote its exact physical ReleaseHold transition.</summary>
internal sealed class HeldStockReleaseValueProof
{
    private readonly StockReleaseService owner;
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle transaction;
    private readonly long number, decisionId;
    private HeldStockReleaseValueProof(StockReleaseService owner, IMongoDatabase database, IClientSessionHandle tx,
        long decisionId, string hash, StockReleaseSourceProof origin, InventoryBalanceDocument before, InventoryBalanceDocument after,
        StockReleaseExpectation expected, DateTimeOffset at, long packages, string policy) {
        this.owner = owner; this.database = database; transaction = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; this.decisionId = decisionId;
        Source = StockReleaseBson.Source(decisionId, before.PhysicalPoolId); SourceHash = hash; Before = before; After = after; At = at; Packages = packages; Policy = policy;
        AllocationId = origin.Selection.ValuationAllocationId; AllocationHash = origin.ValuationAllocation["snapshotHash"].AsString;
        AcquisitionPackages = origin.ValuedPackages; AcquisitionValue = origin.CapitalizedValue;
        ExpectedHeldRevision = expected.HeldValueRevision; ExpectedLotRevision = expected.LotValueRevision;
    }
    internal InventorySource Source { get; }
    internal string SourceHash { get; }
    internal InventoryBalanceDocument Before { get; }
    internal InventoryBalanceDocument After { get; }
    internal DateTimeOffset At { get; }
    internal string Policy { get; }
    internal long ExpectedLotRevision { get; }
    internal long Packages { get; }
    internal long AllocationId { get; }
    internal string AllocationHash { get; }
    internal long AcquisitionPackages { get; }
    internal Money AcquisitionValue { get; }
    internal long ExpectedHeldRevision { get; }
    internal static async Task<HeldStockReleaseValueProof> CreateAsync(StockReleaseService owner, IMongoDatabase database,
        IClientSessionHandle tx, long decisionId, CancellationToken ct) {
        var authority = await owner.ValueAuthorityAsync(database, tx, decisionId, ct); var row = authority.Row;
        var proof = new HeldStockReleaseValueProof(owner, database, tx, decisionId, row["causeHash"].AsString, authority.Source,
            authority.Before, authority.After, authority.Expected, new DateTimeOffset(row["recordedAt"].ToUniversalTime()),
            row["input"]["packages"].AsInt64, row["input"]["policy"].AsString); proof.Require(database, tx); return proof;
    }
    internal void Require(IMongoDatabase current, IClientSessionHandle tx) {
        new InventoryRepository(current).RequireSession(tx);
        if (!ReferenceEquals(current.Client, database.Client) || current.DatabaseNamespace != database.DatabaseNamespace ||
            !ReferenceEquals(tx, transaction) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw StockReleaseBson.Broken();
        owner.RequireValueAuthority(current, tx, decisionId, SourceHash);
    }
    internal Task AuthenticateSourceAsync(IMongoDatabase current, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) =>
        owner.AuthenticateSourceAsync(current, tx, source, hash, ct);
}

/// <summary>No dispatch or conversion value authority exists until the real respective
/// business producer is implemented. There is deliberately no factory or dummy source.</summary>
internal sealed class LotValueConsumptionProof
{
    private LotValueConsumptionProof() => throw StockReleaseBson.Broken();
    internal InventorySource Source => throw StockReleaseBson.Broken();
    internal string SourceHash => throw StockReleaseBson.Broken();
    internal InventoryBalanceDocument Before => throw StockReleaseBson.Broken();
    internal InventoryBalanceDocument After => throw StockReleaseBson.Broken();
    internal DateTimeOffset At => throw StockReleaseBson.Broken();
    internal string Policy => throw StockReleaseBson.Broken();
    internal long ExpectedLotRevision => throw StockReleaseBson.Broken();
    internal long Packages => throw StockReleaseBson.Broken();
    internal InventoryBalanceDocument? DestinationBefore => throw StockReleaseBson.Broken();
    internal InventoryBalanceDocument? DestinationAfter => throw StockReleaseBson.Broken();
    internal long ExpectedDestinationLotRevision => throw StockReleaseBson.Broken();
    internal void Require(IMongoDatabase database, IClientSessionHandle tx) => throw StockReleaseBson.Broken();
    internal Task AuthenticateSourceAsync(IMongoDatabase database, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) =>
        Task.FromException(StockReleaseBson.Broken());
}
