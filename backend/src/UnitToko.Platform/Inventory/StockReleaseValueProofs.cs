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
        if (!ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(tx, transaction) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw StockReleaseBson.Broken();
        owner.RequireValueAuthority(current, tx, decisionId, SourceHash);
    }
    internal Task AuthenticateSourceAsync(IMongoDatabase current, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) =>
        owner.AuthenticateSourceAsync(current, tx, source, hash, ct);
}

/// <summary>Only the genuine in-flight conversion owner may mint this proof after
/// both exact physical legs have been written in the same transaction attempt.</summary>
internal sealed class LotValueConsumptionProof
{
    private readonly PackConversionService owner;
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle transaction;
    private readonly long number, conversionId;
    private LotValueConsumptionProof(PackConversionService owner, IMongoDatabase database, IClientSessionHandle tx, MongoDB.Bson.BsonDocument row) {
        this.owner = owner; this.database = database; transaction = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        conversionId = row["_id"].AsInt64; Before = InventoryBson.ReadBalance(row["before"].AsBsonDocument); After = InventoryBson.ReadBalance(row["after"].AsBsonDocument);
        DestinationBefore = InventoryBson.ReadBalance(row["destinationBefore"].AsBsonDocument); DestinationAfter = InventoryBson.ReadBalance(row["destinationAfter"].AsBsonDocument);
        Source = PackConversionBson.Source(conversionId, Before.PhysicalPoolId); SourceHash = row["causeHash"].AsString;
        At = new(row["recordedAt"].ToUniversalTime()); Policy = row["policy"].AsString;
        ExpectedLotRevision = row["expected"]["lotValueRevision"].AsInt64; Packages = row["quantities"]["sourcePackages"].AsInt64;
    }
    internal static async Task<LotValueConsumptionProof> CreateAsync(PackConversionService owner, IMongoDatabase database,
        IClientSessionHandle tx, long conversionId, CancellationToken ct) {
        var row = await owner.ValueAuthorityAsync(database, tx, conversionId, ct);
        var proof = new LotValueConsumptionProof(owner, database, tx, row); proof.Require(database, tx); return proof;
    }
    internal InventorySource Source { get; }
    internal string SourceHash { get; }
    internal InventoryBalanceDocument Before { get; }
    internal InventoryBalanceDocument After { get; }
    internal DateTimeOffset At { get; }
    internal string Policy { get; }
    internal long ExpectedLotRevision { get; }
    internal long Packages { get; }
    internal InventoryBalanceDocument? DestinationBefore { get; }
    internal InventoryBalanceDocument? DestinationAfter { get; }
    internal long ExpectedDestinationLotRevision => 0;
    internal void Require(IMongoDatabase current, IClientSessionHandle tx) {
        new InventoryRepository(current).RequireSession(tx);
        if (!ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(tx, transaction) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw PackConversionBson.Broken();
        owner.RequireValueAuthority(current, tx, conversionId, SourceHash);
    }
    internal Task AuthenticateSourceAsync(IMongoDatabase current, IClientSessionHandle tx, InventorySource source, string hash, CancellationToken ct) =>
        owner.AuthenticateSourceAsync(current, tx, source, hash, ct);
}
