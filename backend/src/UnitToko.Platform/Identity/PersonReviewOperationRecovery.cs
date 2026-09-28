using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

public sealed partial class PersonWithdrawalService
{
    public Task<PersonReviewOperationOutcome> ReadOperationOutcomeAsync(ActorContext actor, string operation, string key,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        AccessAdministrationService.ValidateReadMetadata(audit);
        return RecoverOperationAsync(actor, operation, key, audit, null, cancellationToken);
    }

    public Task<PersonReviewOperationOutcome> ResolveOperationAsync(ActorContext actor, string operation,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.Reason.Length > 1000 || metadata.Reason.Any(char.IsControl))
            throw new DomainValidationException("invalid_reason", "A bounded resolution reason is required");
        return RecoverOperationAsync(actor, operation, metadata.IdempotencyKey,
            new("operation-recovery", metadata.CorrelationId), metadata, cancellationToken);
    }

    private async Task<PersonReviewOperationOutcome> RecoverOperationAsync(ActorContext actor, string operation, string key,
        AuthorizationReadMetadata audit, AuthorizationCommandMetadata? resolution, CancellationToken cancellationToken)
    {
        var selection = ReceiptSelection(operation, key);
        var action = operation["person.review.".Length..];
        var intent = new PersonWithdrawalIntent(0, PersonWithdrawalTarget.Digest(selection, false), new());
        var auditId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var guards = new Guards();
            var snapshot = await AuthorizeAsync(actor, "receiptRead", tx, guards, ct);
            // Own-key recovery has no target selector. This separately appointed capability cannot disclose names.
            await PermitAsync(tx, actor.UserId, "receiptRead", intent, guards, ct);
            var receipt = await Collection("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).FirstOrDefaultAsync(ct);
            PersonReviewOperationOutcome outcome;
            if (receipt is not null)
            {
                var closedAt = PersonReviewReceiptRules.ClosedAt(receipt, actor.UserId, operation, key, clock.UtcNow);
                if (closedAt is { } closed) outcome = new("closed_no_effect", null, closed);
                else
                {
                    var proposal = await ReceiptProposalAsync(tx, actor.UserId, action, receipt, ct);
                    await PermitAsync(tx, actor.UserId, "receiptRead", Intent(proposal), guards, ct);
                    var result = Result(proposal);
                    outcome = new("committed", result, result.RecordedAt);
                }
            }
            else if (resolution is not null)
            {
                await AuthorizeAsync(actor, action, tx, guards, ct);
                await PersonAsync(tx, actor.UserId, guards, ct);
                var permit = await PermitAsync(tx, actor.UserId, "receiptResolve", intent, guards, ct);
                var security = await ActorSecurityAsync(tx, actor, ct);
                receipt = PersonReviewReceiptRules.Create(actor.UserId, actor.GrantVersion, security, operation, key,
                    resolution.Reason.Trim(), resolution.CorrelationId, permit.Pin, clock.UtcNow);
                // The shared permanent unique key serializes this barrier against the original command's receipt.
                // Losing original transactions roll back their complete business/history/preview effects.
                await Collection("idempotencyRecords").InsertOneAsync(tx, receipt, cancellationToken: ct);
                outcome = new("closed_no_effect", null, PersonAuthorityBson.At(receipt, "createdAt"));
            }
            else outcome = new("not_confirmed", null, null);
            await authorization.RecordPrivilegedReadAsync(auditId, actor, snapshot, "access.receipt.read", audit,
                selection, outcome.State == "not_confirmed" ? 0 : 1, [new(GrantScopeKind.System)], tx, ct);
            await CompleteAsync(actor, tx, guards, ct);
            return outcome;
        }, cancellationToken);
    }

    private static BsonDocument ReceiptSelection(string operation, string key)
    {
        if (operation is not ("person.review.propose" or "person.review.approve" or "person.review.reject" or "person.review.withdraw") ||
            string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl)) throw Denied();
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return new() { { "operation", operation }, { "keyHash", keyHash } };
    }
}
