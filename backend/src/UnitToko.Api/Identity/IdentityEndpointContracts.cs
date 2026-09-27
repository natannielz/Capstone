using UnitToko.Modules.Contracts;

namespace UnitToko.Api.Identity;

public sealed record IdentityAccountDto(LongId UserId, string? DisplayName, string VerifiedEmail);
public sealed record IdentitySessionDto(bool Authenticated, IdentityAccountDto? Account,
    DateTimeOffset? FactorCompletedAt, DateTimeOffset? FreshFactorUntil,
    DateTimeOffset? IdleExpiresAt, DateTimeOffset? AbsoluteExpiresAt,
    DateTimeOffset ServerTime, string[] StepUpOperations);

// These document native URL-encoded browser forms. Endpoints parse strict, single-valued
// fields after the host has checked the actual antiforgery token and canonical origin.
public sealed record IdentityLoginForm(string? ReturnPath, string? InvitationId, string? InvitationSecret,
    string __RequestVerificationToken);
public sealed record IdentityStepUpForm(string Operation, string? ReturnPath, string ExpectedActorId,
    string __RequestVerificationToken);
public sealed record IdentityLogoutForm(string ExpectedActorId, string __RequestVerificationToken);
