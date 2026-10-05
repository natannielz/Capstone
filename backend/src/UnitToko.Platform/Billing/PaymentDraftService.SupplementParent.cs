using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Billing;

internal sealed record PaymentSupplementParent(BsonDocument Head, BsonDocument Submission, PaymentSupplementPrimary Binding,
    BsonDocument PolicyScope, BsonArray Scopes, PersonAuthorityProof? IndependentPeople);

public sealed partial class PaymentDraftService
{
    internal async Task<PaymentSupplementParent> SupplementParentAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long declarationId, long expectedRevision, PaymentSupplementPrimary? requested,
        bool review, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw PaymentSupplementRules.Invalid();
        PaymentSupplementRules.Id(declarationId); PaymentSupplementRules.Id(expectedRevision);
        var head = review ? await ReviewHeadAsync(actor, authority, tx, declarationId, ct) : await HeadAsync(actor, authority, tx, declarationId, ct);
        if (head["state"] != "submitted" || head["revision"] != expectedRevision) throw PaymentDraftRecords.Changed();
        var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
        // That authoritative owner is image-only under P01. A supplement never supplies or
        // substitutes this image and never changes the primary review/verification source.
        var primary = new PaymentSupplementPrimary(declarationId, expectedRevision, submitted["_id"].AsInt64,
            PaymentSubmissionBson.Reference(submitted["evidence"].AsBsonDocument)).Validate();
        if (requested is not null && requested.Validate() != primary) throw PaymentDraftRecords.Changed();
        PersonAuthorityProof? people = null;
        if (review) {
            if (submitted["recordedBy"] != submitted["uploaderId"] || actor.UserId == submitted["uploaderId"].AsInt64)
                throw new AuthorizationDeniedException("independent_person_required");
            people = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [actor.UserId, submitted["uploaderId"].AsInt64], ct);
            if (!PersonIndependencePolicy.AreIndependent(people.Evidence.Single(person => person.UserId == actor.UserId),
                people.Evidence.Single(person => person.UserId == submitted["uploaderId"].AsInt64))) throw new AuthorizationDeniedException("independent_person_required");
            await new PersonAuthorizationFence(database, clock).TouchAsync(tx, people, ct);
        }
        var party = head["party"];
        return new(head, submitted, primary, new() { { "organizationId", party["fulfillmentOrganizationId"] },
            { "shopId", party["shopId"] }, { "channelId", party["channelId"] } }, head["scopes"].AsBsonArray, people);
    }
    internal async Task SupplementParentFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        PaymentSupplementParent before, bool review, CancellationToken ct) {
        var current = await SupplementParentAsync(actor, authority, tx, before.Binding.DeclarationId, before.Binding.DeclarationRevision,
            before.Binding, review, ct);
        if (!InvoiceRecords.Equal(current.Submission, before.Submission)) throw PaymentDraftRecords.Changed();
        // The existing source fence serializes competing attachments/count checks without
        // changing image revision, review decisions, money, stock or verified history.
        await records.TouchAsync(tx, current.Head, ct);
        if (review) await ReviewFenceAsync(actor, authority, tx, current.Scopes, ct);
        else await FenceAsync(actor, authority, tx, current.Scopes, ct);
        before.IndependentPeople?.RequireCurrent(tx, clock.UtcNow); ct.ThrowIfCancellationRequested();
    }
}
