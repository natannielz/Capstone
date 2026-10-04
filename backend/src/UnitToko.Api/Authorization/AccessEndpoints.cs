using Microsoft.AspNetCore.Mvc;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;

namespace UnitToko.Api.Authorization;

public static partial class AccessEndpoints
{
    private static readonly string[] AdministrativeActions =
    [Permissions.AccessPropose, Permissions.AccessApprove, Permissions.AccessRevoke,
        Permissions.MembershipPropose, Permissions.MembershipApprove, Permissions.AccessAuditRead,
        Permissions.TechnicalConfigure, Permissions.OrganizationApprove,
        Permissions.PersonReviewPropose, Permissions.PersonReviewApprove, Permissions.PersonReviewRead, Permissions.CatalogManage];

    public static void MapAccessEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/access").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapGet("/current", async (HttpContext http, AuthorizationStore store, IClock clock, CancellationToken ct) =>
        {
            var actor = Actor(http);
            var snapshot = await store.ReadCurrentAsync(actor, ct);
            var scopes = snapshot.Memberships.SelectMany(member => ScopePolicy.RequiredNodes(member.Scope, snapshot.Directory))
                .DistinctBy(node => (node.Kind, node.Id)).Select(node => new AccessNodeDto(new(node.Id), node.Kind,
                    node.ParentId.HasValue ? new LongId(node.ParentId.Value) : null,
                    node.Status.ToString().ToLowerInvariant(), AccessResponses.Version(node.Version))).ToArray();
            var result = new CurrentAccessDto(new(snapshot.UserId), AccessResponses.Version(snapshot.GrantVersion),
                snapshot.Status.ToString().ToLowerInvariant(),
                snapshot.Memberships.Select(member => new AccessMembershipDto(new(member.Id), AccessScopeDto.From(member.Scope),
                    member.Status.ToString().ToLowerInvariant(), AccessResponses.Version(member.Version), [], member.EffectiveAt, member.ExpiresAt)).ToArray(),
                snapshot.Grants.Select(grant => new AccessGrantDto(new(grant.Id), new(grant.MembershipId), grant.TemplateId,
                    grant.TemplateVersion, [.. grant.Permissions], AccessScopeDto.From(grant.Scope),
                    grant.Status.ToString().ToLowerInvariant(), AccessResponses.Version(grant.Version), [],
                    grant.DelegablePermissions.IsDefault ? [] : [.. grant.DelegablePermissions], grant.EffectiveAt, grant.ExpiresAt)).ToArray(), scopes,
                CurrentActions(actor, snapshot, clock.UtcNow));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(snapshot.GrantVersion);
            return TypedResults.Ok(result);
        }).WithName("CurrentAccess");

        group.MapGet("/catalog", async (HttpContext http, AuthorizationStore store, IClock clock, CancellationToken ct) =>
        {
            var actor = Actor(http);
            var snapshot = await store.ReadCurrentAsync(actor, ct);
            var actions = AvailableActions(actor, snapshot, clock.UtcNow);
            if (actions.Length == 0) throw new AuthorizationDeniedException("permission_denied");
            return TypedResults.Ok(new AccessCatalogDto(
                RoleTemplates.Templates.Values.OrderBy(role => role.Id).Select(role => new AccessRoleDto(
                    role.Id, RoleLabel(role.Id), role.Version, [.. role.Permissions],
                    RoleTemplates.Suggestions.TryGetValue(role.Id, out var suggestions) ? [.. suggestions] : [])).ToArray(),
                RoleTemplates.FinancePacks.OrderBy(pack => pack.Key).Select(pack => new AccessPackDto(pack.Key,
                    PackLabel(pack.Key), [.. pack.Value])).ToArray(),
                Permissions.Catalog.Values.OrderBy(permission => permission.Name).Select(permission => new AccessPermissionDto(
                    permission.Name, permission.Description, [.. permission.AllowedScopes],
                    permission.Assurance.MaximumAge.HasValue)).ToArray(), actions));
        }).WithName("AccessCatalog");

        group.MapGet("/subjects/{userId}", async (string userId, HttpContext http, AccessAdministrationService service, CancellationToken ct) =>
        {
            var result = await service.ReadSubjectAsync(Actor(http), LongId.Parse(userId).Value, ct,
                audit: new("access-review", http.TraceIdentifier));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.GrantVersion);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName("AccessSubject");

        group.MapGet("/proposals", async (HttpContext http, AccessAdministrationService service, CancellationToken ct,
            int offset = 0, int limit = 25) =>
        {
            if (offset < 0 || offset > 10_000 || limit is < 1 or > 100)
                throw new DomainValidationException("invalid_pagination", "Use a bounded page size and offset");
            return TypedResults.Ok((await service.ListProposalsAsync(Actor(http), offset, limit, ct,
                audit: new("access-review", http.TraceIdentifier)))
                .Select(AccessResponses.From).ToArray());
        }).WithName("AccessProposals");

        group.MapPost("/grants/preview", async (GrantRequestDto request, HttpContext http,
            AccessAdministrationService service, CancellationToken ct) =>
            TypedResults.Ok(AccessResponses.From(await service.PreviewGrantAsync(Actor(http), request.ToDomain(), ct,
                audit: new("access-review", http.TraceIdentifier)))))
            .WithName("PreviewAccessGrant");

        group.MapPost("/grants/proposals", async (GrantProposalDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            if (request.Grant is null) throw new DomainValidationException("invalid_grant", "A grant proposal is required");
            var result = await service.ProposeGrantAsync(Actor(http), request.Grant.ToDomain(), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName("ProposeAccessGrant");

        group.MapPost("/memberships/proposals", async (MembershipProposalDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            if (request.Membership is null) throw new DomainValidationException("invalid_membership", "A membership proposal is required");
            var result = await service.ProposeMembershipAsync(Actor(http), request.Membership.ToDomain(), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName("ProposeMembership");

        MapChange(group, "/grants/proposals/{id}/approve", "ApproveAccessGrant",
            (service, actor, id, version, metadata, ct) => service.ApproveGrantAsync(actor, id, version, metadata, ct));
        MapChange(group, "/memberships/proposals/{id}/approve", "ApproveMembership",
            (service, actor, id, version, metadata, ct) => service.ApproveMembershipAsync(actor, id, version, metadata, ct));
        MapChange(group, "/grants/{id}/revoke", "RevokeAccessGrant",
            (service, actor, id, version, metadata, ct) => service.RevokeGrantAsync(actor, id, version, metadata, ct));
        MapChange(group, "/memberships/{id}/suspend", "SuspendMembership",
            (service, actor, id, version, metadata, ct) => service.SuspendMembershipAsync(actor, id, version, metadata, ct));
        MapChange(group, "/subjects/{id}/suspend", "SuspendIdentity",
            (service, actor, id, version, metadata, ct) => service.SuspendIdentityAsync(actor, id, version, metadata, ct));
        MapChange(group, "/scopes/{id}/suspend", "SuspendAccessScope",
            (service, actor, id, version, metadata, ct) => service.SuspendScopeAsync(actor, id, version, metadata, ct));
        MapLifecycle(group);
        MapHistoricalGrants(group);
    }

    private static void MapChange(RouteGroupBuilder group, string path, string name,
        Func<AccessAdministrationService, ActorContext, long, long, AuthorizationCommandMetadata,
            CancellationToken, Task<AccessChangeResult>> execute) =>
        group.MapPost(path, async (string id, AccessReasonDto request, HttpContext http,
            AccessAdministrationService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var result = await execute(service, Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Metadata(http, request.Reason), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(AccessResponses.From(result));
        }).WithName(name);

    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");

    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || reason.Any(char.IsControl))
            throw new DomainValidationException("invalid_reason", "A reason of at most 1000 characters is required");
        return new(AccessRequestMetadata.IdempotencyKey(http.Request), reason.Trim(), http.TraceIdentifier);
    }

    // UI task discovery is separate from administrative catalog admission. Reviewers retain
    // the verification entry when MFA ages; every protected read or command still enforces current assurance.
    private static string[] CurrentActions(ActorContext actor, AuthorizationSnapshot snapshot, DateTimeOffset now) =>
        AvailableActions(actor, snapshot, now).Concat(new[] { Permissions.CaseRead, Permissions.CaseRequest, Permissions.JournalReverse, Permissions.PaymentAllocate, Permissions.PaymentVerify, Permissions.BankReceiptRead, Permissions.BankReceiptConfirm, Permissions.BankStatementRead, Permissions.BankStatementStage, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead, Permissions.ReceivingAccountPropose, Permissions.ReceivingAccountVerify, Permissions.ReceivingAccountApprove, Permissions.PaymentReviewCorrect, Permissions.PaymentReviewRead, Permissions.PaymentEvidence, Permissions.InvoiceRead, Permissions.InvoiceIssue, Permissions.ShipmentRead, Permissions.ShipmentPrepare, Permissions.ShipmentDispatch, Permissions.ShipmentAssign, Permissions.DeliveryAttempt, Permissions.DeliveryEvidence, Permissions.ReceiptAccept, Permissions.OrderRead, Permissions.OrderSubmit, Permissions.OrderCancel, Permissions.OrderSpendApprove, Permissions.OrderAccept, Permissions.CatalogRead, Permissions.PricingManage, Permissions.PricingApprove, Permissions.StockRead, Permissions.StockCount,
                Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourceApprove, Permissions.StockSourceSuspend, Permissions.StockSourcePolicyPropose, Permissions.StockSourcePolicyApprove, Permissions.StockSourceReconciliationRead, Permissions.StockSourceReconciliationPropose, Permissions.StockSourceReconciliationDecide, Permissions.PaymentReviewManual, Permissions.PaymentReviewManage, Permissions.CaseManage, Permissions.StockReceive, Permissions.CreditRequest, Permissions.RefundRequest, Permissions.CreditApprove, Permissions.RefundApprove, Permissions.DisbursementRecord, Permissions.DisbursementVerify, Permissions.OrganizationAddressRead, Permissions.OrganizationApprove,
                Permissions.OrderSpendingPolicyRead, Permissions.OrderSpendingPolicyPropose, Permissions.OrderSpendingPolicyApprove,
                Permissions.AccountingBookRead, Permissions.AccountingBookPropose, Permissions.AccountingBookApprove, Permissions.JournalPost,
                Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.ProcurementApprove, Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove,
                Permissions.FundingRead, Permissions.FundingRequest, Permissions.FundingApprove, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose, Permissions.SupplierBankApprove,
                Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.ProcurementSettlementRead, Permissions.ProcurementContainerReceive,
                Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead,
                Permissions.ProcurementSupplierDocumentRecord, Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementSupplierDocumentEvidenceRead,
                Permissions.SupplierClaim, Permissions.SupplierClaimApprove,
                Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose, Permissions.ProcurementStockValuationDecide,
                Permissions.ProcurementPayableRead, Permissions.ProcurementPayablePropose, Permissions.ProcurementPayableDecide,
                Permissions.ProcurementAdvanceReturnRead, Permissions.ProcurementAdvanceReturnPropose, Permissions.ProcurementAdvanceReturnDecide,
                Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementQualityAcceptanceDecide }
            .Where(permission => ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed ||
                (permission is Permissions.StockReceive or Permissions.ProcurementSupplierDocumentRecord or Permissions.ProcurementSupplierDocumentMatch or Permissions.SupplierClaim or Permissions.SupplierClaimApprove or Permissions.ProcurementStockValuationPropose or Permissions.ProcurementStockValuationDecide or Permissions.ProcurementPayablePropose or Permissions.ProcurementPayableDecide or Permissions.ProcurementAdvanceReturnPropose or Permissions.ProcurementAdvanceReturnDecide or Permissions.ProcurementQualityAcceptancePropose or Permissions.ProcurementQualityAcceptanceDecide or Permissions.StockSourceReconciliationPropose or Permissions.StockSourceReconciliationDecide) && ScopePolicy.CanRequestStepUp(actor, snapshot, permission, now) ||
                (permission is Permissions.CaseManage or Permissions.PaymentReviewManage or Permissions.PaymentReviewManual or Permissions.JournalReverse or Permissions.PaymentAllocate or Permissions.PaymentVerify or Permissions.BankReceiptConfirm or Permissions.BankStatementStage or Permissions.ReceivingAccountVerify or Permissions.ReceivingAccountApprove or Permissions.PaymentReviewCorrect or Permissions.OrderSpendApprove or Permissions.PricingApprove or Permissions.StockSourceApprove or Permissions.StockSourceSuspend or Permissions.StockSourcePolicyApprove or Permissions.CreditApprove or Permissions.RefundApprove or Permissions.DisbursementRecord or Permissions.DisbursementVerify or Permissions.OrganizationApprove or Permissions.OrderSpendingPolicyPropose or Permissions.OrderSpendingPolicyApprove or Permissions.AccountingBookPropose or Permissions.AccountingBookApprove or Permissions.ProcurementPrepare or Permissions.ProcurementApprove or Permissions.ProcurementContainerReceive or Permissions.ProcurementInspect or Permissions.SupplierChangePropose or Permissions.SupplierChangeApprove or Permissions.FundingRequest or Permissions.FundingApprove or Permissions.SupplierBankPropose or Permissions.SupplierBankApprove) && ScopePolicy.CanRequestStepUp(actor, snapshot, permission, now))).Distinct(StringComparer.Ordinal).ToArray();
    private static string[] AvailableActions(ActorContext actor, AuthorizationSnapshot snapshot, DateTimeOffset now) =>
        AdministrativeActions.Where(permission => ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed).ToArray();

    private static string RoleLabel(string id) => id switch
    {
        "buyer" => "Pembeli", "store-staff" => "Staf toko", "store-head" => "Kepala toko",
        "courier" => "Kurir", "finance" => "Keuangan", "system-admin" => "Administrator sistem", _ => id
    };
    private static string PackLabel(string id) => id switch
    {
        "reporting" => "Pelaporan", "billing" => "Penagihan", "approval" => "Persetujuan",
        "accounting" => "Akuntansi", _ => id
    };
}
