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
    public async Task<PersonWithdrawalResult?> ReadOperationReceiptAsync(ActorContext actor, string operation, string key,
        AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        AccessAdministrationService.ValidateReadMetadata(audit);
        if (operation is not ("person.review.propose" or "person.review.approve" or "person.review.reject" or "person.review.withdraw") ||
            string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl)) throw Denied();
        var action = operation["person.review.".Length..];
        var keyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var selection = new BsonDocument { { "operation", operation }, { "keyHash", keyHash } };
        var auditId = (await ids.NextAsync("authorizationReadEvents", cancellationToken)).Value;
        return await unit.ExecuteAsync<PersonWithdrawalResult?>(async (tx, ct) =>
        {
            var guards = new Guards();
            var snapshot = await AuthorizeAsync(actor, "receiptRead", tx, guards, ct);
            await PermitAsync(tx, actor.UserId, "receiptRead", new(0, PersonWithdrawalTarget.Digest(selection, false), new()), guards, ct);
            var receipt = await Collection("idempotencyRecords").Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).FirstOrDefaultAsync(ct);
            BsonDocument? proposal = null;
            if (receipt is not null)
            {
                RequireReceiptOwner(receipt, actor.UserId, operation, key);
                proposal = await ReceiptProposalAsync(tx, actor.UserId, action, receipt, ct);
                await PermitAsync(tx, actor.UserId, "receiptRead", Intent(proposal), guards, ct);
            }
            await authorization.RecordPrivilegedReadAsync(auditId, actor, snapshot, "access.receipt.read", audit,
                selection, proposal is null ? 0 : 1, [new(GrantScopeKind.System)], tx, ct);
            await CompleteAsync(actor, tx, guards, ct);
            return proposal is null ? null : Result(proposal);
        }, cancellationToken);
    }
    private static void RequireReceiptOwner(BsonDocument receipt, long actorId, string operation, string key)
    {
        if (!receipt.TryGetValue("actorId", out var actor) || !actor.IsInt64 || actor.AsInt64 != actorId ||
            !receipt.TryGetValue("operation", out var op) || !op.IsString || op.AsString != operation ||
            !receipt.TryGetValue("key", out var storedKey) || !storedKey.IsString || storedKey.AsString != key)
            throw Denied("person_receipt_invalid");
    }
}
