using System.Diagnostics;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidencePinTimingTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_lease_excludes_prior_admission_but_preserves_request_deadline_post_creation_expiry_and_held_file()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        await data.ValidateAsync();
        var frozenUtc = data.Catalog.Clock.UtcNow;
        var policy = await data.Rows("evidenceReadPolicies").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(5000L, policy["leaseDurationMs"].AsInt64);
        Assert.Equal(10000L, policy["verificationDurationMs"].AsInt64);
        Assert.Equal(60000L, policy["requestDurationMs"].AsInt64);
        var unit = new MongoUnitOfWork(data.Catalog.Client);
        var before = await OwnerStateAsync();

        // All cases use the genuine owner, admitted image, scanner result and association.
        // The delegating adapter only delays entry/return; it cannot grant authority.
        foreach (var scenario in new[] { "closed-file-at-final-fence", "stalled-acquire-after-lease", "request-expired-before-lease", "slow-admission" }) {
            var targets = new DelayedPaymentTarget(data.Payment.Service,
                scenario == "request-expired-before-lease" ? TimeSpan.FromMilliseconds(60500) :
                    scenario == "slow-admission" ? TimeSpan.FromMilliseconds(5500) : TimeSpan.Zero,
                scenario == "stalled-acquire-after-lease" ? TimeSpan.FromMilliseconds(5500) : TimeSpan.Zero);
            var documents = new EvidenceService(data.Database, unit, new(data.Database), data.Catalog.Clock, data.Files.Registration, targets);
            var started = Stopwatch.GetTimestamp();
            var claim = await unit.ExecuteAsync((tx, ct) => documents.AcquirePaymentReadAsync(tx, data.Actor,
                new(EvidenceTargetKind.PaymentDeclaration, data.DeclarationId), 1, data.Reservation.ReservationId, data.Reference,
                PaymentEvidenceReadTests.ReadData.Metadata, ct));
            Assert.Equal(TimeSpan.FromSeconds(5), claim.InitialLeaseDuration);
            Assert.Equal(TimeSpan.FromSeconds(60), claim.RequestDuration);
            Assert.True(claim.LeaseStarted >= started);
            if (scenario is "slow-admission" or "request-expired-before-lease")
                Assert.True(Stopwatch.GetElapsedTime(started, claim.LeaseStarted) > claim.InitialLeaseDuration);
            if (scenario == "stalled-acquire-after-lease")
                Assert.True(Stopwatch.GetElapsedTime(claim.LeaseStarted) >= claim.InitialLeaseDuration);
            if (scenario == "request-expired-before-lease")
                Assert.True(Stopwatch.GetElapsedTime(started) >= claim.RequestDuration);
            VerifiedOriginalHandle? handle = null;
            var pinnedBeforeFinalCheck = false;
            try {
                var opened = await data.Files.Registration.Store!.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, default);
                handle = Assert.IsType<OpenOutcome.Opened>(opened).Handle;
                Assert.True(handle.Stream.CanRead);
                var metadata = EvidenceAdmissionTestData.Meta("pin-timing-" + scenario);
                var error = await Record.ExceptionAsync(() => data.Payment.Service.SubmitAsync(data.Actor, data.DeclarationId, 1,
                    data.Reservation.ReservationId, data.Reference, metadata, async (tx, id, ct) => {
                        var final = await documents.PinPaymentAsync(tx, data.Actor, id, claim, handle, started, ct);
                        pinnedBeforeFinalCheck = true;
                        if (scenario == "closed-file-at-final-fence") await handle.DisposeAsync();
                        return final;
                    }, default));
                if (scenario == "slow-admission") {
                    Assert.Null(error); Assert.True(pinnedBeforeFinalCheck);
                    Assert.Single(await data.Rows("paymentSubmissions").Find(new BsonDocument()).ToListAsync());
                    var pin = Assert.Single(await data.Rows("evidencePins").Find(new BsonDocument()).ToListAsync());
                    Assert.Equal("PaymentSubmission", pin["associationKind"].AsString);
                    Assert.Equal(data.Reference.Sha256, pin["reference"]["sha256"].AsString);
                } else {
                    Assert.Equal("evidence_read_lease_expired", Assert.IsType<PersistenceConflictException>(error).Code);
                    Assert.Equal(scenario == "closed-file-at-final-fence", pinnedBeforeFinalCheck);
                    Assert.Equal(before, await OwnerStateAsync());
                    Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
                }
                Assert.Equal(frozenUtc, data.Catalog.Clock.UtcNow);
            } finally {
                if (handle is not null) { await handle.DisposeAsync(); Assert.False(handle.Stream.CanRead); }
                await unit.ExecuteAsync(async (tx, ct) => { await documents.ReleaseReadAsync(tx, claim, ct); return true; });
            }
            Assert.Equal("released", (await data.Rows("evidenceReadLeases").Find(new BsonDocument("_id", claim.Id)).SingleAsync())["state"].AsString);
        }
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));

        async Task<BsonDocument> OwnerStateAsync() {
            var state = new BsonDocument();
            foreach (var name in new[] { "paymentSubmissions", "evidencePins", "evidencePinAudits", "payments", "bankReceipts", "journalEntries" })
                state.Add(name, new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            foreach (var (name, filter) in new[] {
                ("idempotencyRecords", new BsonDocument("operation", PaymentSubmissionBson.Operation)),
                ("auditEvents", new BsonDocument("operation", PaymentSubmissionBson.Operation)),
                ("outboxEvents", new BsonDocument("kind", "payment.ocr.requested")) })
                state.Add(name, new BsonArray(await data.Rows(name).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            return state;
        }
    }

    private sealed class DelayedPaymentTarget(IPaymentEvidenceUploadTarget actual, TimeSpan beforeLease, TimeSpan afterLease) : IPaymentEvidenceUploadTarget
    {
        public IMongoDatabase Database => actual.Database;
        public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? revision,
            Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => actual.WithTargetAsync(tx, actor, target, revision, action, ct);
        public async Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? revision, EvidenceRef? reference,
            Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
            // Before any command starts the server transaction, so the request-budget case
            // exercises the evidence deadline rather than Mongo's transaction lifetime limit.
            if (beforeLease > TimeSpan.Zero) await Task.Delay(beforeLease, ct);
            return await actual.WithReadTargetAsync(tx, actor, target, revision, reference, async (authority, token) => {
                var result = await action(authority, token);
                if (afterLease > TimeSpan.Zero) await Task.Delay(afterLease, token);
                return result;
            }, ct);
        }
        public Task<PaymentEvidenceAssociation> RequireSubmissionAssociationAsync(IClientSessionHandle tx, ActorContext actor, long submissionId,
            EvidenceReadClaim claim, CancellationToken ct) => actual.RequireSubmissionAssociationAsync(tx, actor, submissionId, claim, ct);
    }
}
