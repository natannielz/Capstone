namespace UnitToko.Modules.Identity;

/// <summary>Compares the actor with each historical participant using current canonical evidence.
/// It does not impose pairwise independence between historical participants.</summary>
public static class PersonIndependencePolicy
{
    public static bool AreIndependent(PersonEvidence? actor, params PersonEvidence?[]? history) =>
        IsVerified(actor) && history is { Length: > 0 } &&
        history.All(previous => IsVerified(previous) &&
            previous!.UserId != actor!.UserId && previous.PersonId != actor.PersonId);

    private static bool IsVerified(PersonEvidence? evidence) =>
        evidence is { UserId: > 0, PersonId: > 0, Version: > 0, Verification: PersonVerification.Verified };
}
