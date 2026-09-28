using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

public sealed partial class PersonWithdrawalService
{
    public async Task<PersonWithdrawalDetail> ReadAsync(ActorContext actor, long proposalId,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        if (proposalId <= 0) throw Denied();
        AccessAdministrationService.ValidateReadMetadata(audit);
        var auditId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var guards = new Guards();
            var snapshot = await AuthorizeAsync(actor, "read", tx, guards, ct);
            // App read capability alone cannot probe for the existence of private proposals.
            var selection = new BsonDocument("proposalId", proposalId);
            await PermitAsync(tx, actor.UserId, "read", new(proposalId, PersonWithdrawalTarget.Digest(selection, false), new()), guards, ct);
            var proposal = await ProposalAsync(tx, proposalId, ct);
            await PermitAsync(tx, actor.UserId, "read", Intent(proposal), guards, ct);
            await authorization.RecordPrivilegedReadAsync(auditId, actor, snapshot, "access.history.read", audit,
                new BsonDocument("operation", "person.review.detail/" + proposalId), 1, [new(GrantScopeKind.System)], tx, ct);
            await CompleteAsync(actor, tx, guards, ct);
            var target = proposal["target"].AsBsonDocument;
            return new PersonWithdrawalDetail(Result(proposal), proposal["requestedBy"].AsInt64, proposal["reason"].AsString,
                target["userIds"].AsBsonArray.Select(value => value.AsInt64).ToImmutableArray(),
                target["personIds"].AsBsonArray.Select(value => value.AsInt64).ToImmutableArray());
        }, cancellationToken);
    }
    // Private compatibility result only. The UI uses the typed outcome to distinguish closed from unknown.
    public async Task<PersonWithdrawalResult?> ReadOperationReceiptAsync(ActorContext actor, string operation, string key,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default) =>
        (await ReadOperationOutcomeAsync(actor, operation, key, audit, cancellationToken)).Result;
}
