namespace UnitToko.Modules.Organizations;

/// <summary>Approved membership bounds grants; membership itself grants no visibility.</summary>
public sealed record Membership(
    long Id, long UserId, GrantScope Scope, AuthorizationStatus Status,
    long ApprovedBy, DateTimeOffset EffectiveAt, DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? RevokedAt = null, long Version = 1);
