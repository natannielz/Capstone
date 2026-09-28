using System.Globalization;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;

namespace UnitToko.Api.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PersonReviewListDto([property: JsonRequired] string Purpose, string? Status = null, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PersonReviewSearchDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Term, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonReviewReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonReviewSubmissionDto([property: JsonRequired] string PreviewReference, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonReviewReceiptQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record PersonReviewResultDto(LongId ProposalId, string Version, string Status, LongId GroupId,
    string GroupVersion, DateTimeOffset RecordedAt);
public sealed record PersonReviewAccountDto(LongId UserId, string? DisplayName, string VerifiedEmail, string Status);
public sealed record PersonReviewTargetDto(LongId GroupId, string Version, string Status, PersonReviewAccountDto[] Accounts);
public sealed record PersonReviewTargetPageDto(string Total, PersonReviewTargetDto[] Items);
public sealed record PersonReviewSummaryDto(PersonReviewResultDto Result, PersonReviewAccountDto Requester, string Reason,
    string AffectedAccountCount, string[] AllowedActions);
public sealed record PersonReviewPageDto(string Total, PersonReviewSummaryDto[] Items);
public sealed record PersonReviewDecisionDto(PersonReviewAccountDto Actor, string Reason, DateTimeOffset DecidedAt);
public sealed record PersonReviewDetailDto(PersonReviewSummaryDto Summary, PersonReviewAccountDto[] Accounts,
    PersonReviewDecisionDto? Decision);
public sealed record PersonReviewPreviewDto(string Reference, string Kind, DateTimeOffset ExpiresAt,
    PersonReviewTargetDto Target, PersonReviewSummaryDto? Proposal);
public sealed record PersonReviewReceiptDto(string State, PersonReviewResultDto? Result, DateTimeOffset? RecordedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonReviewResolutionDto([property: JsonRequired] string Operation, [property: JsonRequired] string Reason);

internal static class PersonReviewResponses
{
    internal static PersonReviewReceiptDto From(PersonReviewOperationOutcome item) => new(item.State, item.Result is null ? null : From(item.Result), item.RecordedAt);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static PersonReviewResultDto From(PersonWithdrawalResult item) => new(new(item.ProposalId), Number(item.Version),
        item.Status, new(item.GroupId), Number(item.GroupVersion), item.RecordedAt);
    internal static PersonReviewAccountDto From(PersonReviewAccount item) => new(new(item.UserId), item.DisplayName, item.VerifiedEmail, item.Status);
    internal static PersonReviewTargetDto From(PersonReviewTarget item) => new(new(item.GroupId), Number(item.Version), item.Status, item.Accounts.Select(From).ToArray());
    internal static PersonReviewTargetPageDto From(PersonReviewTargetPage page) => new(Number(page.Total), page.Items.Select(From).ToArray());
    internal static PersonReviewSummaryDto From(PersonReviewSummary item) => new(From(item.Result), From(item.Requester), item.Reason,
        Number(item.AffectedAccountCount), [.. item.AllowedActions]);
    internal static PersonReviewPageDto From(PersonReviewPage page) => new(Number(page.Total), page.Items.Select(From).ToArray());
    internal static PersonReviewDetailDto From(PersonReviewDetail item) => new(From(item.Summary), item.Accounts.Select(From).ToArray(),
        item.Decision is { } decision ? new(From(decision.Actor), decision.Reason, decision.DecidedAt) : null);
    internal static PersonReviewPreviewDto From(PersonReviewPreview item) => new(item.Reference, item.Kind, item.ExpiresAt,
        From(item.Target), item.Proposal is { } proposal ? From(proposal) : null);
}
