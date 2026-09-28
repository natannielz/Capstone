using System.Collections.Immutable;
namespace UnitToko.Modules.Identity;
public sealed record PersonWithdrawalResult(long ProposalId, long Version, string Status, long GroupId,
    long GroupVersion, DateTimeOffset RecordedAt);
public sealed record PersonWithdrawalDetail(PersonWithdrawalResult Result, long RequestedBy, string Reason,
    ImmutableArray<long> AffectedUserIds, ImmutableArray<long> AffectedPersonIds);
