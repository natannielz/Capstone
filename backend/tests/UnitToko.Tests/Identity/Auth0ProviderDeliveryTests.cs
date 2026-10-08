using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using UnitToko.Platform.Identity;

namespace UnitToko.Tests.Identity;

// HTTP fixtures only: no Mongo fixture, provider connection, email or ticket delivery.
public sealed class Auth0ProviderDeliveryTests
{
    private const string Issuer = "https://unit-toko.us.auth0.com/";
    private const string Subject = "auth0|owned-fixture";
    private const string Token = "fixture-management-token";
    private static readonly SessionPayloadProtector Protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
    private static Auth0ProviderDeliveryOptions Options(bool enabled = true, string issuer = Issuer) => new() {
        Enabled = enabled, Issuer = issuer, ClientId = "fixture-client", ConnectionId = "con_fixture", ConnectionName = "FixtureDatabase"
    };
    private static BsonDocument Payload() => new() {
        { "issuer", Issuer }, { "subject", Subject }, { "kind", "password" }, { "actorGrantVersion", 1L }, { "actorSecurityVersion", 2L }
    };
    private static BsonDocument Intent(BsonDocument? payload = null) => new() {
        { "_id", 101L }, { "schemaVersion", 1 }, { "kind", "passwordRecovery" }, { "userId", 201L }, { "invitationId", BsonNull.Value },
        { "requestedBy", 301L }, { "approvedBy", BsonNull.Value }, { "securityVersion", 3L }, { "status", "blocked" },
        { "reason", "Owner reviewed recovery" }, { "createdAt", new BsonDateTime(DateTime.UtcNow) }, { "version", 1L },
        { "protectedPayload", Protector.Protect("account-recovery", (payload ?? Payload()).ToJson()) }
    };
    private static object User(string subject = Subject, string email = "owner@example.invalid", string provider = "auth0", bool verified = true) => new {
        user_id = subject, email, email_verified = verified, blocked = false,
        identities = new[] { new { provider, user_id = "owned-fixture", connection = "FixtureDatabase", isSocial = false } }
    };
    private static object Connection(bool custom = false) => new {
        id = "con_fixture", name = "FixtureDatabase", strategy = "auth0",
        options = new { enabledDatabaseCustomization = custom }
    };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
    private static Task<bool> Current(CancellationToken _) => Task.FromResult(true);
    private static Task<string> Credential(CancellationToken _) => Task.FromResult(Token);

    [Fact]
    public async Task Exact_producer_json_roundtrip_accepts_small_and_lossless_large_epochs_but_not_other_numeric_types() {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection()) : new(HttpStatusCode.OK));
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        foreach (var pair in new[] { (Grant: 1L, Security: 2L), (Grant: 9007199254740993L, Security: long.MaxValue) }) {
            var payload = Payload(); payload["actorGrantVersion"] = pair.Grant; payload["actorSecurityVersion"] = pair.Security;
            var parsed = BsonDocument.Parse(payload.ToJson());
            Assert.Equal(pair.Grant, parsed["actorGrantVersion"].ToInt64());
            Assert.Equal(pair.Security, parsed["actorSecurityVersion"].ToInt64());
            Assert.Equal(pair.Grant == 1 ? BsonType.Int32 : BsonType.Int64, parsed["actorGrantVersion"].BsonType);
            var result = await delivery.DeliverAsync(Intent(payload), Current);
            Assert.Equal(Auth0ProviderDeliveryState.ProviderAccepted, result.State);
        }
        var before = handler.Requests.Count;
        foreach (var field in new[] { "actorGrantVersion", "actorSecurityVersion" })
        foreach (var invalid in new BsonValue[] { new BsonDouble(1.0), new BsonDecimal128(1m), BsonNull.Value, new BsonString("1"), new BsonInt64(0), new BsonInt64(-1) }) {
            var payload = Payload(); payload[field] = invalid;
            var result = await delivery.DeliverAsync(Intent(payload), Current);
            Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State); Assert.Equal("invalid_intent_binding", result.Code);
        }
        // Do not widen persisted Mongo types to repair a JSON representation issue.
        var outer = Intent(); outer["securityVersion"] = new BsonInt32(3);
        var outerResult = await delivery.DeliverAsync(outer, Current);
        Assert.Equal(Auth0ProviderDeliveryState.Blocked, outerResult.State); Assert.Equal("invalid_intent", outerResult.Code);
        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task Defaults_block_every_outbound_request_and_do_not_fetch_credentials() {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("Must not dispatch")); var credentials = 0;
        using var delivery = new Auth0ProviderDelivery(new(), Protector, _ => { credentials++; return Task.FromResult(Token); }, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State); Assert.Equal("delivery_disabled", result.Code);
        Assert.Equal(0, credentials); Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://unit-toko.us.auth0.com/")]
    [InlineData("https://user:secret@unit-toko.us.auth0.com/")]
    [InlineData("https://unit-toko.us.auth0.com/path")]
    [InlineData("https://unit-toko.us.auth0.com/?tenant=other")]
    [InlineData("https://unit-toko.us.auth0.com.evil.invalid/")]
    [InlineData("https://127.0.0.1/")]
    public void Enabled_transport_requires_one_native_https_tenant_origin(string issuer) {
        var error = Assert.Throws<ArgumentException>(() => new Auth0ProviderDelivery(Options(issuer: issuer), Protector, Credential));
        Assert.DoesNotContain(issuer, error.Message);
    }

    [Fact]
    public async Task Reads_the_protected_subject_and_reviewed_connection_before_one_non_atomic_password_email_request_without_secret_results() {
        var handler = new FakeHandler(request => {
            if (request.Method == HttpMethod.Get) return Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection());
            return new(HttpStatusCode.OK) { Content = new StringContent("Private provider message and reset ticket must not be returned") };
        });
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.ProviderAccepted, result.State);
        Assert.True(result.MessageRequestStarted); Assert.False(result.DeliveryVerified); Assert.False(result.EmailVerified);
        Assert.False(result.SubjectBoundAtomically);
        Assert.False(result.CanAutomaticallyRetry);
        Assert.Equal(4, handler.Requests.Count);
        foreach (var read in handler.Requests.Take(3)) Assert.Equal("Bearer " + Token, read.Authorization);
        Assert.Equal(Issuer + "api/v2/connections/con_fixture/clients?take=100", handler.Requests[2].Uri.AbsoluteUri);
        var send = handler.Requests[3]; Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Equal(Issuer + "dbconnections/change_password", send.Uri.AbsoluteUri);
        Assert.Null(send.Authorization); Assert.Null(send.IdempotencyKey);
        using var body = JsonDocument.Parse(send.Body);
        Assert.Equal("owner@example.invalid", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("FixtureDatabase", body.RootElement.GetProperty("connection").GetString());
        Assert.Equal("fixture-client", body.RootElement.GetProperty("client_id").GetString());
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Token, serialized); Assert.DoesNotContain("owner@example.invalid", serialized);
        Assert.DoesNotContain("Private provider", serialized); Assert.DoesNotContain("ticket", serialized);
    }

    [Fact]
    public async Task Documented_sparse_not_blocked_user_and_native_options_object_can_request_one_password_email() {
        var handler = new FakeHandler(request => {
            if (request.Method == HttpMethod.Post) return new(HttpStatusCode.OK);
            if (request.RequestUri!.AbsolutePath.Contains("/users/")) return Json(new {
                user_id = Subject, email = "owner@example.invalid", email_verified = true,
                identities = new[] { new { provider = "auth0", user_id = "owned-fixture", connection = "FixtureDatabase", isSocial = false } }
            });
            return Json(new { id = "con_fixture", name = "FixtureDatabase", strategy = "auth0", options = new { } });
        });
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.ProviderAccepted, result.State);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.False(result.DeliveryVerified); Assert.False(result.EmailVerified); Assert.False(result.SubjectBoundAtomically);
    }

    [Theory]
    [InlineData("blocked", "true")]
    [InlineData("blocked", "null")]
    [InlineData("blocked", "string")]
    [InlineData("customization", "true")]
    [InlineData("customization", "null")]
    [InlineData("customization", "string")]
    public async Task Optional_false_flags_refuse_true_and_invalid_types(string field, string kind) {
        object? invalid = kind switch { "true" => true, "null" => null, _ => "false" };
        var handler = new FakeHandler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("/users/")) return field == "blocked" ? Json(new {
                user_id = Subject, email = "owner@example.invalid", email_verified = true, blocked = invalid,
                identities = new[] { new { provider = "auth0", user_id = "owned-fixture", connection = "FixtureDatabase", isSocial = false } }
            }) : Json(User());
            return Json(new { id = "con_fixture", name = "FixtureDatabase", strategy = "auth0", enabled_clients = new[] { "fixture-client" },
                options = new { enabledDatabaseCustomization = invalid } });
        });
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Modern_enabled_client_lookup_follows_a_bounded_opaque_cursor_without_deprecated_connection_fields() {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection()) : new(HttpStatusCode.OK)) {
            ClientReply = request => request.RequestUri!.Query.Contains("from=cursor-1", StringComparison.Ordinal)
                ? Json(new { clients = new[] { new { client_id = "fixture-client" } } })
                : Json(new { clients = new[] { new { client_id = "other-client" } }, next = "cursor-1" })
        };
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.ProviderAccepted, result.State);
        var pages = handler.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/clients", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, pages.Length); Assert.DoesNotContain("from=", pages[0].Uri.Query);
        Assert.Equal("?take=100&from=cursor-1", pages[1].Uri.Query);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("missing-client")]
    [InlineData("invalid-client")]
    [InlineData("cursor-cycle")]
    [InlineData("oversized-page")]
    public async Task Modern_enabled_client_lookup_cannot_send_without_bounded_matching_client_evidence(string fault) {
        var handler = new FakeHandler(request => Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection())) {
            ClientReply = _ => fault switch {
                "missing-client" => Json(new { clients = new[] { new { client_id = "other-client" } } }),
                "invalid-client" => Json(new { clients = new[] { new { client_id = 1 } } }),
                "cursor-cycle" => Json(new { clients = Array.Empty<object>(), next = "same-cursor" }),
                _ => Json(new { clients = Enumerable.Range(0, 101).Select(index => new { client_id = "other-" + index }).ToArray() })
            }
        };
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("oversized-stream")]
    [InlineData("redirect")]
    public async Task Ambiguous_oversized_or_redirected_lookup_never_reaches_the_email_endpoint(string fault) {
        var handler = new FakeHandler(_ => fault switch {
            "duplicate" => new(HttpStatusCode.OK) { Content = new StringContent("{\"user_id\":\"auth0|owned-fixture\",\"user_id\":\"auth0|other\"}", Encoding.UTF8, "application/json") },
            "oversized-stream" => new(HttpStatusCode.OK) { Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(new string('x', 65537))) },
            _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://outside.invalid/private") } }
        });
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State); Assert.False(result.MessageRequestStarted);
        Assert.Single(handler.Requests); Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_is_blocked_but_cancellation_after_the_email_request_starts_is_unknown() {
        using var before = new CancellationTokenSource(); before.Cancel();
        var never = new FakeHandler(_ => throw new InvalidOperationException("Cancelled request must not start"));
        using (var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, never)) {
            var result = await delivery.DeliverAsync(Intent(), Current, before.Token);
            Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State); Assert.False(result.MessageRequestStarted); Assert.Empty(never.Requests);
        }
        using var after = new CancellationTokenSource();
        var handler = new FakeHandler(request => {
            if (request.Method != HttpMethod.Post) return Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection());
            after.Cancel(); throw new OperationCanceledException(after.Token);
        });
        using (var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler)) {
            var result = await delivery.DeliverAsync(Intent(), Current, after.Token);
            Assert.Equal(Auth0ProviderDeliveryState.OutcomeUnknown, result.State); Assert.True(result.MessageRequestStarted);
            Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post); Assert.False(result.CanAutomaticallyRetry);
        }
    }

    [Fact]
    public async Task Unsupported_or_invalid_protected_intents_never_reach_the_provider() {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("Must not dispatch"));
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var cases = new List<BsonDocument>();
        var factor = Intent(); factor["kind"] = "factorRecovery"; cases.Add(factor);
        var invitation = Intent(); invitation["kind"] = "invitation"; cases.Add(invitation);
        var foreign = Payload(); foreign["issuer"] = "https://other.us.auth0.com/"; cases.Add(Intent(foreign));
        var social = Payload(); social["subject"] = "google-oauth2|owned-fixture"; cases.Add(Intent(social));
        var callerEmail = Payload(); callerEmail["email"] = "replacement@example.invalid"; cases.Add(Intent(callerEmail));
        var malformed = Intent(); malformed["protectedPayload"] = "not-authenticated-ciphertext"; cases.Add(malformed);
        var wrongPurpose = Intent(); wrongPurpose["protectedPayload"] = Protector.Protect("invitation-delivery", Payload().ToJson()); cases.Add(wrongPurpose);
        foreach (var value in cases) {
            var result = await delivery.DeliverAsync(value, Current);
            Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State); Assert.False(result.MessageRequestStarted);
        }
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("different-subject")]
    [InlineData("unverified-email")]
    [InlineData("social-identity")]
    [InlineData("custom-database")]
    [InlineData("different-connection")]
    [InlineData("malformed")]
    public async Task Provider_lookup_must_bind_the_exact_subject_verified_email_and_reviewed_native_connection(string fault) {
        var handler = new FakeHandler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("/users/")) return fault switch {
                "different-subject" => Json(User(subject: "auth0|different")), "unverified-email" => Json(User(verified: false)),
                "social-identity" => Json(User(provider: "google-oauth2")), "malformed" => Json(new { user_id = Subject }), _ => Json(User())
            };
            if (fault == "different-connection") return Json(new { id = "con_other", name = "FixtureDatabase", strategy = "auth0", enabled_clients = new[] { "fixture-client" } });
            return Json(Connection(custom: fault == "custom-database"));
        });
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State); Assert.False(result.MessageRequestStarted);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Lost_authority_after_lookup_prevents_the_message_request() {
        var handler = new FakeHandler(request => Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection()));
        var checks = 0;
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), _ => Task.FromResult(++checks == 1));
        Assert.Equal(Auth0ProviderDeliveryState.Blocked, result.State); Assert.Equal("authority_changed", result.Code);
        Assert.False(result.MessageRequestStarted); Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(400, Auth0ProviderDeliveryState.ProviderRejected)]
    [InlineData(404, Auth0ProviderDeliveryState.ProviderRejected)]
    [InlineData(429, Auth0ProviderDeliveryState.ProviderRejected)]
    [InlineData(408, Auth0ProviderDeliveryState.OutcomeUnknown)]
    [InlineData(500, Auth0ProviderDeliveryState.OutcomeUnknown)]
    [InlineData(302, Auth0ProviderDeliveryState.OutcomeUnknown)]
    [InlineData(202, Auth0ProviderDeliveryState.OutcomeUnknown)]
    public async Task Outbound_response_classification_never_retries_or_claims_delivery(int status, Auth0ProviderDeliveryState expected) {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Get
            ? Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection())
            : new((HttpStatusCode)status) { Content = new StringContent("Sensitive provider diagnostic must stay private") });
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(expected, result.State); Assert.True(result.MessageRequestStarted); Assert.False(result.CanAutomaticallyRetry);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.False(result.DeliveryVerified); Assert.False(result.EmailVerified);
        Assert.DoesNotContain("Sensitive provider", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task A_lost_outbound_response_is_unknown_and_the_transport_makes_no_second_send() {
        var handler = new FakeHandler(request => {
            if (request.Method == HttpMethod.Post) throw new HttpRequestException(Token + " secret reset ticket");
            return Json(request.RequestUri!.AbsolutePath.Contains("/users/") ? User() : Connection());
        });
        using var delivery = new Auth0ProviderDelivery(Options(), Protector, Credential, handler);
        var result = await delivery.DeliverAsync(Intent(), Current);
        Assert.Equal(Auth0ProviderDeliveryState.OutcomeUnknown, result.State); Assert.True(result.MessageRequestStarted);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result)); Assert.DoesNotContain("ticket", result.ToString());
    }

    private sealed record Request(HttpMethod Method, Uri Uri, string? Authorization, string? IdempotencyKey, string Body);
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];
        internal Func<HttpRequestMessage, HttpResponseMessage>? ClientReply { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/clients", StringComparison.Ordinal))
                return ClientReply?.Invoke(request) ?? Json(new { clients = new[] { new { client_id = "fixture-client" } } });
            return reply(request);
        }
    }
}
