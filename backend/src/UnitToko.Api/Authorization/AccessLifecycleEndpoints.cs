using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Api.Authorization;

public sealed record AccessHistoryQueryDto([property: JsonRequired] string Purpose,
    int Offset = 0, int Limit = 25, LongId? SubjectId = null);
public sealed record AccessHistoryPageDto(string Total, AccessHistoryEntryDto[] Items);
public sealed record AccessHistoryEntryDto(LongId Id, string Operation, LongId ActorId, LongId SubjectId,
    AccessScopeDto? Scope, DateTimeOffset CreatedAt, string Reason, LongId? ResourceId,
    string? BeforeVersion, string? AfterVersion, string? Status);
public sealed record OperationLookupDto([property: JsonRequired] string Operation,
    [property: JsonRequired] string Key, [property: JsonRequired] string Purpose);
public sealed record OperationResolveDto([property: JsonRequired] string Operation, [property: JsonRequired] string Reason);
public sealed record OperationReceiptDto(string Operation, string State, OperationReceiptResultDto? Result, DateTimeOffset? RecordedAt);
public sealed record OperationReceiptResultDto(LongId Id, LongId SubjectId, string Version, string Status, string? Kind, string? GrantVersion);

public static partial class AccessEndpoints
{
    private static void MapLifecycle(RouteGroupBuilder group)
    {
        MapDecision(group, "/proposals/{id}/reject", "RejectAccessProposal",
            (service, actor, id, version, metadata, ct) => service.RejectProposalAsync(actor, id, version, metadata, ct));
        MapDecision(group, "/proposals/{id}/withdraw", "WithdrawAccessProposal",
            (service, actor, id, version, metadata, ct) => service.WithdrawProposalAsync(actor, id, version, metadata, ct));

        group.MapPost("/history/query", async (AccessHistoryQueryDto request, HttpContext http,
            AccessAdministrationService service, CancellationToken ct) =>
        {
            var page = await service.ListHistoryAsync(Actor(http), ReadMetadata(http, request.Purpose),
                request.Offset, request.Limit, request.SubjectId?.Value, ct);
            return TypedResults.Ok(new AccessHistoryPageDto(AccessResponses.Version(page.Total), page.Items.Select(item =>
                new AccessHistoryEntryDto(new(item.Id), item.Operation, new(item.ActorId), new(item.SubjectId),
                    item.Scope is null ? null : AccessScopeDto.From(item.Scope), item.CreatedAt, item.Reason,
                    item.ResourceId.HasValue ? new LongId(item.ResourceId.Value) : null,
                    item.BeforeVersion.HasValue ? AccessResponses.Version(item.BeforeVersion.Value) : null,
                    item.AfterVersion.HasValue ? AccessResponses.Version(item.AfterVersion.Value) : null, item.Status)).ToArray()));
        }).WithName("AccessHistory");

        group.MapPost("/operations/lookup", async (OperationLookupDto request, HttpContext http,
            AccessAdministrationService service, CancellationToken ct) =>
            TypedResults.Ok(Receipt(await service.ReadOperationReceiptAsync(Actor(http), request.Operation,
                request.Key, ReadMetadata(http, request.Purpose), ct))))
            .WithName("LookupAccessOperation");

        group.MapPost("/operations/resolve", async (OperationResolveDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            var metadata = Metadata(http, request.Reason);
            return TypedResults.Ok(Receipt(await service.ResolveOperationReceiptAsync(Actor(http), request.Operation,
                metadata.IdempotencyKey, metadata, ct)));
        }).WithName("ResolveAccessOperation");
    }

    private static void MapDecision(RouteGroupBuilder group, string path, string name,
        Func<AccessAdministrationService, ActorContext, long, long, AuthorizationCommandMetadata,
            CancellationToken, Task<AccessProposalResult>> execute) =>
        group.MapPost(path, async (string id, AccessReasonDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await execute(service, Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName(name);

    private static AuthorizationReadMetadata ReadMetadata(HttpContext http, string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 1000 || purpose.Any(char.IsControl))
            throw new DomainValidationException("invalid_read_purpose", "A bounded purpose is required");
        return new(purpose.Trim(), http.TraceIdentifier);
    }
    private static OperationReceiptDto Receipt(OperationReceipt receipt) => new(receipt.Operation, receipt.State,
        receipt.Result is { } result ? new(new(result.Id), new(result.SubjectId), AccessResponses.Version(result.Version),
            result.Status, result.Kind, result.GrantVersion.HasValue ? AccessResponses.Version(result.GrantVersion.Value) : null) : null,
        receipt.RecordedAt);
}
