namespace UnitToko.Modules.Identity;

public enum PersonVerification { Unknown, Verified, Disputed }

/// <summary>Current evidence resolved by the trusted registry inside the deciding transaction.
/// PersonId is the reviewed canonical person, not an account, email, or original historical snapshot.
/// Version is the positive evidence/binding version. The resolver must independently fence all
/// contributing source, binding, person and equivalence versions in the same transaction.
/// This value alone neither establishes a production source nor authorizes an account merge.</summary>
public sealed record PersonEvidence(long UserId, long? PersonId, PersonVerification Verification, long Version);
