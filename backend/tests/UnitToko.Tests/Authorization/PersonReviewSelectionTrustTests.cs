using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonReviewSelectionTrustTests(MongoFixture fixture)
{
    private static readonly PersonReviewSelectionRequest List = new(PersonReviewSelectionKind.ProposalList);
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private static BsonDocument Id(long id) => new("_id", id);

    [Fact]
    public async Task Explicit_empty_selection_is_fenced_without_appointing_or_listing_anyone()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var permit = data.Permit(tx);
            Assert.Empty(permit.ProposalIds); Assert.Empty(permit.GroupIds); Assert.Empty(permit.DisclosureUserIds);
            Assert.Equal(data.Clock.UtcNow.AddHours(1), permit.PolicyExpiresAt);
            await permit.TouchAsync(data.Database, tx, data.Actor, List, data.Clock, ct);
            permit.RequireCurrent(data.Database, tx, data.Actor, List, data.Clock.UtcNow);
            return true;
        });
        Assert.Equal(1L, (await data.PolicyAsync())["fence"].AsInt64);
        Assert.Equal(1L, (await data.PolicyAsync())["version"].AsInt64);
    }

    [Fact]
    public async Task Production_default_never_turns_missing_trust_into_an_empty_allowlist()
    {
        await using var data = await Data.CreateAsync(fixture);
        var failure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(
            (tx, ct) => new UnavailablePersonReviewSelectionTrust().RequireAsync(tx, data.Actor, List, ct)));
        Assert.Equal("person_governance_unavailable", failure.Code);
        Assert.Equal(0L, (await data.PolicyAsync())["fence"].AsInt64);
    }

    [Fact]
    public void Request_normalization_and_digests_are_typed_and_kind_specific()
    {
        var request = new PersonReviewSelectionRequest(PersonReviewSelectionKind.TargetSearch, Query: "  Nama Staf  ", Offset: 2, Limit: 7);
        Assert.Equal("Nama Staf", request.Normalize().Query);
        Assert.Equal(request with { Query = "Nama Staf" }, request.Normalize());
        Assert.Equal(request.Digest, (request with { Query = "Nama Staf" }).Digest);
        Assert.NotEqual(request.Digest, (request with { Query = "Nama Lain" }).Digest);
        Assert.NotEqual(request.Digest, (request with { Offset = 3 }).Digest);
        Assert.NotEqual(request.Digest, (request with { Limit = 8 }).Digest);
        Assert.Equal("pending", (List with { Status = " PENDING " }).Normalize().Status);
        Assert.NotEqual(List.Digest, (List with { Status = "pending" }).Digest);
        var preview = new PersonReviewSelectionRequest(PersonReviewSelectionKind.TargetPreview, ReferenceId: 10, ExpectedVersion: 1);
        Assert.NotEqual(preview.Digest, (preview with { ReferenceId = 11 }).Digest);
        Assert.NotEqual(preview.Digest, (preview with { ExpectedVersion = 2 }).Digest);
        Assert.NotEqual(preview.Digest, (preview with { Kind = PersonReviewSelectionKind.ProposalDetail }).Digest);
        Assert.Equal(new string('a', 80), (request with { Query = new string('a', 80) }).Normalize().Query);
    }

    [Theory]
    [InlineData("kind")] [InlineData("query-missing")] [InlineData("query-short")] [InlineData("query-long")]
    [InlineData("query-control")] [InlineData("list-query")] [InlineData("status")] [InlineData("search-status")]
    [InlineData("list-reference")] [InlineData("offset")] [InlineData("limit")] [InlineData("preview-reference")]
    [InlineData("preview-version")] [InlineData("preview-page")] [InlineData("detail-reference")]
    public void Malformed_or_ignored_request_fields_fail_closed(string kind)
    {
        var request = kind switch
        {
            "kind" => List with { Kind = (PersonReviewSelectionKind)99 },
            "query-missing" => List with { Kind = PersonReviewSelectionKind.TargetSearch },
            "query-short" => List with { Kind = PersonReviewSelectionKind.TargetSearch, Query = "a" },
            "query-long" => List with { Kind = PersonReviewSelectionKind.TargetSearch, Query = new string('a', 81) },
            "query-control" => List with { Kind = PersonReviewSelectionKind.TargetSearch, Query = "ab\nc" },
            "list-query" => List with { Query = "staff" },
            "status" => List with { Status = "all-users" },
            "search-status" => List with { Kind = PersonReviewSelectionKind.TargetSearch, Query = "staff", Status = "pending" },
            "list-reference" => List with { ReferenceId = 10 },
            "offset" => List with { Offset = 10001 },
            "limit" => List with { Limit = 0 },
            "preview-reference" => List with { Kind = PersonReviewSelectionKind.TargetPreview, ExpectedVersion = 1 },
            "preview-version" => List with { Kind = PersonReviewSelectionKind.TargetPreview, ReferenceId = 10 },
            "preview-page" => List with { Kind = PersonReviewSelectionKind.TargetPreview, ReferenceId = 10, ExpectedVersion = 1, Offset = 1 },
            _ => List with { Kind = PersonReviewSelectionKind.ProposalDetail, ReferenceId = 0 }
        };
        Assert.Throws<AuthorizationDeniedException>(() => request.Normalize());
    }

    [Fact]
    public async Task Permit_pins_all_actual_actor_fields_and_exact_selection_request()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            var permit = data.Permit(tx);
            var actor = data.Actor;
            var changed = new[]
            {
                actor with { UserId = 11 }, actor with { GrantVersion = 2 }, actor with { ProviderIssuer = "https://other.example/" },
                actor with { ProviderSubject = "other" }, actor with { AuthenticatedAt = actor.AuthenticatedAt.AddSeconds(1) },
                actor with { Assurance = FactorAssurance.PhishingResistant }, actor with { Session = null },
                actor with { Session = actor.Session! with { Id = new string('b', 64) } },
                actor with { Session = actor.Session! with { Version = 2 } },
                actor with { Session = actor.Session! with { SecurityVersion = 2 } }
            };
            foreach (var alternative in changed) Assert.Throws<AuthorizationDeniedException>(() =>
                permit.RequireCurrent(data.Database, tx, alternative, List, data.Clock.UtcNow));
            foreach (var alternative in new[] { List with { Offset = 1 }, List with { Limit = 26 }, List with { Status = "pending" } })
                Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(data.Database, tx, actor, alternative, data.Clock.UtcNow));
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, actor: actor with { Session = null }));
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, actor: actor with { Session = actor.Session! with { Version = 0 } }));
            permit.RequireCurrent(data.Database, tx, actor with { Session = actor.Session! with { } }, List, data.Clock.UtcNow);
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task Permit_cannot_move_to_another_transaction_database_or_client()
    {
        await using var data = await Data.CreateAsync(fixture);
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        var permit = data.Permit(tx);
        using var other = await fixture.Client.StartSessionAsync(); other.StartTransaction();
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(data.Database, other, data.Actor, List, data.Clock.UtcNow));
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(fixture.Client.GetDatabase("other_selection_fixture"), tx, data.Actor, List, data.Clock.UtcNow));
        var anotherClient = new MongoClient(fixture.Client.Settings);
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(anotherClient.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), tx, data.Actor, List, data.Clock.UtcNow));
        await other.AbortTransactionAsync(); await tx.AbortTransactionAsync();
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(data.Database, tx, data.Actor, List, data.Clock.UtcNow));
        tx.StartTransaction();
        Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(data.Database, tx, data.Actor, List, data.Clock.UtcNow));
        await tx.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("default")] [InlineData("zero")] [InlineData("negative")] [InlineData("duplicate")] [InlineData("unsorted")] [InlineData("large")]
    public async Task Every_authorized_id_set_is_explicit_bounded_sorted_and_unique(string kind)
    {
        await using var data = await Data.CreateAsync(fixture);
        ImmutableArray<long> ids = kind switch
        {
            "default" => default, "zero" => [0], "negative" => [-1], "duplicate" => [1, 1], "unsorted" => [2, 1],
            _ => Enumerable.Range(1, 257).Select(value => (long)value).ToImmutableArray()
        };
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, proposals: ids));
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, groups: ids));
            var disclosureIds = kind == "large" ? Enumerable.Range(1, 259).Select(value => (long)value).ToImmutableArray() : ids;
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, disclosures: disclosureIds));
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task Policy_heads_and_public_pin_are_defensive_and_operational_fences_do_not_change_pin()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var policy = Data.Policy(data.Clock.UtcNow);
            var source = PersonTestData.Source(data.Clock.UtcNow);
            var head = Data.Head("personEvidenceSources", source);
            var permit = data.Permit(tx, policy: policy, proposals: [21, 22], groups: [31], disclosures: [10], heads: [head]);
            var original = permit.PolicyPin;
            policy["manifestReference"] = "caller mutation"; source["policyReference"] = "caller mutation";
            var exposed = permit.PolicyPin; exposed["id"] = 999L;
            Assert.Equal(100L, permit.PolicyPin["id"].AsInt64);
            Assert.Equal(new long[] { 21, 22 }, permit.ProposalIds);
            Assert.Equal(new long[] { 31 }, permit.GroupIds); Assert.Equal(new long[] { 10 }, permit.DisclosureUserIds);
            await data.Rows("personGovernancePolicies").UpdateOneAsync(tx, Id(100), U.Inc("fence", 3L), cancellationToken: ct);
            await data.Rows("personEvidenceSources").UpdateOneAsync(tx, Id(PersonTestData.SourceId), U.Inc("fence", 2L), cancellationToken: ct);
            await permit.TouchAsync(data.Database, tx, data.Actor, List, data.Clock, ct);
            Assert.Equal(original, permit.PolicyPin);
            return true;
        });
        Assert.Equal(4L, (await data.PolicyAsync())["fence"].AsInt64);
        Assert.Equal(3L, (await data.RowAsync("personEvidenceSources", PersonTestData.SourceId))["fence"].AsInt64);
    }

    [Theory]
    [InlineData("schema")] [InlineData("unknown")] [InlineData("policy-fence")] [InlineData("policy-date")]
    [InlineData("policy-status")] [InlineData("not-yet-effective")] [InlineData("expired")]
    public async Task Unsupported_or_noncurrent_policy_never_supplies_selection(string kind)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            var policy = Data.Policy(data.Clock.UtcNow);
            switch (kind)
            {
                case "schema": policy["schemaVersion"] = 2; break;
                case "unknown": policy["users"] = new BsonArray { 10L }; break;
                case "policy-fence": policy["fence"] = long.MaxValue; break;
                case "policy-date": policy["effectiveAt"] = new BsonDateTime(long.MinValue); break;
                case "policy-status": policy["status"] = "suspended"; break;
                case "not-yet-effective": policy["effectiveAt"] = data.Clock.UtcNow.AddMinutes(1).UtcDateTime; break;
                case "expired": policy["expiresAt"] = data.Clock.UtcNow.UtcDateTime; break;
            }
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, policy: policy)
                .RequireCurrent(data.Database, tx, data.Actor, List, data.Clock.UtcNow));
            return Task.FromResult(true);
        });
    }

    [Theory]
    [InlineData("unknown-collection")] [InlineData("immutable-evidence")] [InlineData("proposal")] [InlineData("profile")]
    [InlineData("wrong-id")] [InlineData("wrong-revision")] [InlineData("wrong-digest")] [InlineData("wrong-type")]
    [InlineData("fence-overflow")] [InlineData("duplicate")] [InlineData("unsorted")] [InlineData("too-many")] [InlineData("default")]
    public async Task Unsupported_missing_or_malformed_eligibility_heads_fail_before_any_write(string kind)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            var source = PersonTestData.Source(data.Clock.UtcNow); var head = Data.Head("personEvidenceSources", source);
            ImmutableArray<PersonReviewEligibilityHead> heads = [head];
            switch (kind)
            {
                case "unknown-collection": heads = [head with { Collection = "arbitrary" }]; break;
                case "immutable-evidence": heads = [Data.Head("personEvidenceVersions", PersonTestData.Evidence(10, data.Clock.UtcNow), "evidenceVersion")]; break;
                case "proposal": heads = [head with { Collection = "personReviewProposals" }]; break;
                case "profile": heads = [head with { Collection = "accountProfiles" }]; break;
                case "wrong-id": heads = [head with { Id = head.Id + 1 }]; break;
                case "wrong-revision": heads = [head with { Revision = 2 }]; break;
                case "wrong-digest": heads = [head with { Digest = new string('b', 64) }]; break;
                case "wrong-type": source["version"] = 1; heads = [head with { Document = source }]; break;
                case "fence-overflow": source["fence"] = long.MaxValue; heads = [Data.Head("personEvidenceSources", source)]; break;
                case "duplicate": heads = [head, head]; break;
                case "unsorted": heads = [head, Data.Head("identities", Data.Identity(), "grantVersion")]; break;
                case "too-many": heads = Enumerable.Repeat(head, 2049).ToImmutableArray(); break;
                case "default": heads = default; break;
            }
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, heads: heads));
            return Task.FromResult(true);
        });
        Assert.Equal(0L, (await data.PolicyAsync())["fence"].AsInt64);
    }

    [Theory]
    [InlineData("policy")] [InlineData("source")] [InlineData("source-type")] [InlineData("identity-security")]
    [InlineData("identity-type")] [InlineData("missing-head")]
    public async Task Exact_semantic_fences_reject_drift_without_revision_bumps_and_roll_back(string kind)
    {
        await using var data = await Data.CreateAsync(fixture);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var heads = new[] { Data.Head("identities", Data.Identity(), "grantVersion"), Data.Head("personEvidenceSources", PersonTestData.Source(data.Clock.UtcNow)) }.ToImmutableArray();
            var permit = data.Permit(tx, heads: heads);
            switch (kind)
            {
                case "policy": await data.Rows("personGovernancePolicies").UpdateOneAsync(tx, Id(100), U.Set("manifestReference", "changed"), cancellationToken: ct); break;
                case "source": await data.Rows("personEvidenceSources").UpdateOneAsync(tx, Id(PersonTestData.SourceId), U.Set("policyReference", "changed"), cancellationToken: ct); break;
                case "source-type": await data.Rows("personEvidenceSources").UpdateOneAsync(tx, Id(PersonTestData.SourceId), U.Set("version", 1), cancellationToken: ct); break;
                case "identity-security": await data.Rows("identities").UpdateOneAsync(tx, Id(10), U.Set("securityVersion", 1L), cancellationToken: ct); break;
                case "identity-type": await data.Rows("identities").UpdateOneAsync(tx, Id(10), U.Set("grantVersion", 1), cancellationToken: ct); break;
                case "missing-head": await data.Rows("personEvidenceSources").DeleteOneAsync(tx, Id(PersonTestData.SourceId), cancellationToken: ct); break;
            }
            await permit.TouchAsync(data.Database, tx, data.Actor, List, data.Clock, ct);
            return true;
        }));
        Assert.Equal(0L, (await data.PolicyAsync())["fence"].AsInt64);
        Assert.Equal(0L, (await data.RowAsync("identities", 10))["fence"].AsInt64);
        Assert.Equal("synthetic-reviewed-policy", (await data.RowAsync("personEvidenceSources", PersonTestData.SourceId))["policyReference"].AsString);
    }

    [Fact]
    public async Task Guard_expires_after_touch_and_downstream_denial_rolls_back_all_fences()
    {
        await using var data = await Data.CreateAsync(fixture);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var permit = data.Permit(tx);
            await permit.TouchAsync(data.Database, tx, data.Actor, List, data.Clock, ct);
            data.Clock.UtcNow = permit.PolicyExpiresAt!.Value;
            permit.RequireCurrent(data.Database, tx, data.Actor, List, data.Clock.UtcNow);
            return true;
        }));
        Assert.Equal(0L, (await data.PolicyAsync())["fence"].AsInt64);
    }

    [Fact]
    public async Task Concurrent_policy_change_forces_new_attempt_and_new_selection_proof()
    {
        await using var data = await Data.CreateAsync(fixture);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var attempts = 0;
        var work = data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            var policy = await data.Rows("personGovernancePolicies").Find(tx, Id(100)).SingleAsync(ct);
            Interlocked.Increment(ref attempts);
            if (policy["version"].AsInt64 != 1L) return false;
            var permit = data.Permit(tx, policy: policy);
            loaded.TrySetResult(); await resume.Task.WaitAsync(ct);
            await permit.TouchAsync(data.Database, tx, data.Actor, List, data.Clock, ct);
            return true;
        });
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { await data.Rows("personGovernancePolicies").UpdateOneAsync(Id(100), U.Inc("version", 1L).Inc("fence", 1L)); }
        finally { resume.TrySetResult(); }
        Assert.False(await work); Assert.True(attempts >= 2);
    }


    [Fact]
    public async Task Maximum_reference_sets_include_two_independent_participant_labels()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            var references = Enumerable.Range(1, 256).Select(value => (long)value).ToImmutableArray();
            var disclosures = Enumerable.Range(1, 258).Select(value => (long)value).ToImmutableArray();
            var permit = data.Permit(tx, proposals: references, groups: references, disclosures: disclosures);
            Assert.Equal(256, permit.ProposalIds.Length); Assert.Equal(256, permit.GroupIds.Length);
            Assert.Equal(258, permit.DisclosureUserIds.Length);
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task Adapter_decision_bounds_cap_authority_without_expiring_structural_target_heads()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            var source = PersonTestData.Source(data.Clock.UtcNow);
            source["expiresAt"] = data.Clock.UtcNow.AddHours(-1).UtcDateTime;
            source["status"] = "revoked";
            var end = data.Clock.UtcNow.AddMinutes(5);
            var permit = data.Permit(tx, heads: [Data.Head("personEvidenceSources", source)],
                decisionFrom: data.Clock.UtcNow.AddMinutes(-1), decisionUntil: end);
            Assert.Equal(end, permit.ValidUntil);
            permit.RequireCurrent(data.Database, tx, data.Actor, List, data.Clock.UtcNow);
            Assert.Throws<AuthorizationDeniedException>(() => permit.RequireCurrent(data.Database, tx, data.Actor, List, end));
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, decisionFrom: data.Clock.UtcNow.AddMinutes(1))
                .RequireCurrent(data.Database, tx, data.Actor, List, data.Clock.UtcNow));
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, decisionFrom: end, decisionUntil: end));
            Assert.Equal(data.Clock.UtcNow.AddHours(1), data.Permit(tx, decisionUntil: data.Clock.UtcNow.AddHours(2)).ValidUntil);
            var noEnd = Data.Policy(data.Clock.UtcNow); noEnd.Remove("expiresAt");
            Assert.Null(data.Permit(tx, policy: noEnd).PolicyExpiresAt);
            Assert.Null(data.Permit(tx, policy: noEnd).ValidUntil);
            Assert.Equal(end, data.Permit(tx, policy: noEnd, decisionUntil: end).ValidUntil);
            return Task.FromResult(true);
        });
    }


    [Fact]
    public async Task All_supported_mutable_head_kinds_are_fenced_without_granting_positive_target_authority()
    {
        await using var data = await Data.CreateAsync(fixture);
        var person = PersonTestData.Person(10); person["status"] = "disputed";
        var binding = PersonTestData.Binding(10, data.Clock.UtcNow); binding["status"] = "revoked";
        var group = PersonTestData.Group(10, data.Clock.UtcNow); group["status"] = "superseded";
        await data.Rows("personRecords").InsertOneAsync(person);
        await data.Rows("personAccountBindings").InsertOneAsync(binding);
        await data.Rows("personEquivalenceGroups").InsertOneAsync(group);
        await data.Unit.ExecuteAsync(async (tx, ct) =>
        {
            ImmutableArray<PersonReviewEligibilityHead> heads =
            [
                Data.Head("identities", Data.Identity(), "grantVersion"), Data.Head("personAccountBindings", binding),
                Data.Head("personEquivalenceGroups", group), Data.Head("personEvidenceSources", PersonTestData.Source(data.Clock.UtcNow)),
                Data.Head("personRecords", person)
            ];
            var permit = data.Permit(tx, heads: heads);
            await permit.TouchAsync(data.Database, tx, data.Actor, List, data.Clock, ct);
            foreach (var head in heads)
            {
                var row = await data.Rows(head.Collection).Find(tx, Id(head.Id)).SingleAsync(ct);
                Assert.Equal(1L, row["fence"].AsInt64);
                Assert.Equal(head.Document["status"], row["status"]);
            }
            return true;
        });
    }

    [Theory]
    [InlineData("schema")] [InlineData("security")] [InlineData("issuer")] [InlineData("extra")] [InlineData("duplicate")]
    public async Task Identity_eligibility_heads_require_the_supported_exact_authority_projection(string kind)
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            var identity = Data.Identity();
            switch (kind)
            {
                case "schema": identity["schemaVersion"] = 1L; break;
                case "security": identity["securityVersion"] = new BsonArray { 1L }; break;
                case "issuer": identity["issuer"] = "bad\\nissuer"; break;
                case "extra": identity["displayName"] = "Must not become authority"; break;
                case "duplicate": identity.AllowDuplicateNames = true; identity.Add("fence", 0L); break;
            }
            // A malformed row must be rejected before canonical hashing (which rejects duplicate fields too).
            var head = new PersonReviewEligibilityHead("identities", 10, 1, new string('a', 64), identity);
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, heads: [head]));
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task Missing_policy_or_head_proof_is_denied_before_writes()
    {
        await using var data = await Data.CreateAsync(fixture);
        await data.Unit.ExecuteAsync((tx, ct) =>
        {
            Assert.Throws<AuthorizationDeniedException>(() => new PersonReviewSelectionPermit(data.Database, tx, data.Actor, List, null!, [], [], [], []));
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx, heads: [null!]));
            Assert.Throws<AuthorizationDeniedException>(() => data.Permit(tx,
                heads: [new PersonReviewEligibilityHead("identities", 10, 1, new string('a', 64), null!)]));
            return Task.FromResult(true);
        });
        Assert.Equal(0L, (await data.PolicyAsync())["fence"].AsInt64);
    }

    private sealed class Data : IAsyncDisposable
    {
        private readonly MongoClient client;
        internal IMongoDatabase Database { get; }
        internal MongoUnitOfWork Unit { get; }
        internal MutableAuthorizationClock Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        internal ActorContext Actor => new(10, "https://synthetic-person.example/", "person-fixture-10", 1,
            startedAt.AddMinutes(-1), FactorAssurance.MultiFactor, new(new string('a', 64), 1, 1));
        private readonly DateTimeOffset startedAt;
        private Data(MongoFixture fixture)
        {
            client = fixture.Client; Database = client.GetDatabase("ut_person_selection_" + Guid.NewGuid().ToString("N"));
            Unit = new(client); startedAt = Clock.UtcNow;
        }
        internal static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var data = new Data(fixture);
            // Deliberately unvalidated collections exercise the primitive's own typed fences.
            await data.Rows("personGovernancePolicies").InsertOneAsync(Policy(data.Clock.UtcNow));
            await data.Rows("identities").InsertOneAsync(Identity());
            await data.Rows("personEvidenceSources").InsertOneAsync(PersonTestData.Source(data.Clock.UtcNow));
            return data;
        }
        internal PersonReviewSelectionPermit Permit(IClientSessionHandle tx, ActorContext? actor = null, BsonDocument? policy = null,
            ImmutableArray<long>? proposals = null, ImmutableArray<long>? groups = null, ImmutableArray<long>? disclosures = null,
            ImmutableArray<PersonReviewEligibilityHead>? heads = null, DateTimeOffset? decisionFrom = null, DateTimeOffset? decisionUntil = null) => new(Database, tx, actor ?? Actor, List,
                policy ?? Policy(startedAt), proposals ?? [], groups ?? [], disclosures ?? [], heads ?? [], decisionFrom, decisionUntil);
        internal static BsonDocument Policy(DateTimeOffset now) => new()
        {
            { "_id", 100L }, { "schemaVersion", 1 }, { "status", "active" }, { "version", 1L }, { "fence", 0L },
            { "manifestReference", "synthetic-selection-manifest" }, { "manifestDigest", new string('a', 64) },
            { "effectiveAt", now.AddDays(-1).UtcDateTime }, { "expiresAt", now.AddHours(1).UtcDateTime }
        };
        internal static BsonDocument Identity() => new()
        {
            { "_id", 10L }, { "schemaVersion", 1 }, { "issuer", "https://synthetic-person.example/" }, { "subject", "person-fixture-10" },
            { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L }
        };
        internal static PersonReviewEligibilityHead Head(string collection, BsonDocument row, string revision = "version") =>
            new(collection, row["_id"].AsInt64, row[revision].AsInt64, PersonWithdrawalTarget.Digest(row, true), row);
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal Task<BsonDocument> RowAsync(string collection, long id) => Rows(collection).Find(Id(id)).SingleAsync();
        internal Task<BsonDocument> PolicyAsync() => RowAsync("personGovernancePolicies", 100);
        public ValueTask DisposeAsync() => new(client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName));
    }
}
