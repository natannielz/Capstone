using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Tests.Identity;

public sealed class ProviderSecurityVerifierTests
{
    [Fact]
    public void Blocked_event_is_bound_to_configured_issuer_and_exact_affected_subject()
    {
        var batch = Verify(ProviderSecurityFixtures.Event());
        var change = Assert.Single(batch.Events);
        Assert.Equal("https://unit-toko.eu.auth0.com/", change.Issuer);
        Assert.Equal("event:evt_0123456789ABCDEFGHIJKL", change.EventId);
        Assert.Equal("auth0|target-user", change.Subject);
        Assert.Equal(ProviderSecurityChange.AccountDisabled, change.Change);
        Assert.Equal(ProviderSecurityFixtures.Now, change.OccurredAt);
        Assert.Matches("^[0-9a-f]{64}$", change.Fingerprint);
        Assert.Equal(0, batch.IgnoredCount);
    }

    [Fact]
    public void Deleted_event_has_distinct_account_deletion_semantics()
    {
        var change = Assert.Single(Verify(ProviderSecurityFixtures.Event("user.deleted")).Events);
        Assert.Equal(ProviderSecurityChange.AccountDeleted, change.Change);
    }

    [Theory]
    [InlineData("scp", ProviderSecurityChange.PasswordChanged)]
    [InlineData("gd_unenroll", ProviderSecurityChange.FactorRemoved)]
    [InlineData("gd_enrollment_complete", ProviderSecurityChange.FactorChanged)]
    [InlineData("gd_update_device_account", ProviderSecurityChange.FactorChanged)]
    public void Native_log_array_uses_target_user_not_initiating_administrator(string type, ProviderSecurityChange expected)
    {
        var batch = Verify(ProviderSecurityFixtures.Log(type), ProviderEventTransport.Auth0Logs);
        var change = Assert.Single(batch.Events);
        Assert.Equal(expected, change.Change);
        Assert.Equal("auth0|target-user", change.Subject);
        Assert.Equal("log:90020260928080000000000001", change.EventId);
        Assert.Equal(ProviderSecurityFixtures.Now, change.OccurredAt);
    }

    [Theory]
    [InlineData(ProviderEventTransport.Auth0Events)]
    [InlineData(ProviderEventTransport.Auth0Logs)]
    public void Current_and_previous_secrets_work_only_on_their_own_transport(ProviderEventTransport transport)
    {
        var options = ProviderSecurityFixtures.Options();
        var verifier = new ProviderSecurityEventVerifier(options, ProviderSecurityFixtures.Clock());
        var body = transport == ProviderEventTransport.Auth0Events ? ProviderSecurityFixtures.Event() : ProviderSecurityFixtures.Log();
        var current = transport == ProviderEventTransport.Auth0Events ? options.EventsBearerToken : options.LogsBearerToken;
        var previous = transport == ProviderEventTransport.Auth0Events ? options.PreviousEventsBearerToken : options.PreviousLogsBearerToken;
        var other = transport == ProviderEventTransport.Auth0Events ? options.LogsBearerToken : options.EventsBearerToken;
        Assert.Single(verifier.Verify(transport, body, "Bearer " + current).Events);
        Assert.Single(verifier.Verify(transport, body, "bearer " + previous).Events);
        var error = Assert.Throws<AuthorizationDeniedException>(() => verifier.Verify(transport, body, "Bearer " + other));
        Assert.Equal("invalid_provider_credential", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Basic test")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer ")]
    public void Invalid_authentication_wins_over_malformed_body(string header)
    {
        var error = Assert.Throws<AuthorizationDeniedException>(() => new ProviderSecurityEventVerifier(
            ProviderSecurityFixtures.Options(), ProviderSecurityFixtures.Clock()).Verify(ProviderEventTransport.Auth0Events, Bytes("not JSON"), header));
        Assert.Equal("invalid_provider_credential", error.Code);
    }

    [Fact]
    public void Whitespace_or_multiple_credentials_are_not_silently_normalized()
    {
        var options = ProviderSecurityFixtures.Options();
        var verifier = new ProviderSecurityEventVerifier(options, ProviderSecurityFixtures.Clock());
        foreach (var header in new[] { "Bearer  " + options.EventsBearerToken, "Bearer " + options.EventsBearerToken + " ",
            "Bearer " + options.EventsBearerToken + ", Bearer " + options.PreviousEventsBearerToken })
            Assert.Throws<AuthorizationDeniedException>(() => verifier.Verify(ProviderEventTransport.Auth0Events, ProviderSecurityFixtures.Event(), header));
    }

    [Fact]
    public void Disabled_transport_cannot_verify_even_a_valid_payload()
    {
        var options = ProviderSecurityFixtures.Options(); options.EventsEnabled = false;
        var verifier = new ProviderSecurityEventVerifier(options, ProviderSecurityFixtures.Clock());
        var error = Assert.Throws<AuthorizationDeniedException>(() => verifier.Verify(ProviderEventTransport.Auth0Events,
            ProviderSecurityFixtures.Event(), "Bearer " + options.EventsBearerToken));
        Assert.Equal("provider_events_disabled", error.Code);
    }

    [Fact]
    public void Configuration_mutation_after_construction_cannot_change_trusted_issuer_or_secret()
    {
        var options = ProviderSecurityFixtures.Options(); var token = options.EventsBearerToken;
        var verifier = new ProviderSecurityEventVerifier(options, ProviderSecurityFixtures.Clock());
        options.Issuer = "https://other.eu.auth0.com/"; options.EventsBearerToken = options.LogsBearerToken;
        var change = Assert.Single(verifier.Verify(ProviderEventTransport.Auth0Events, ProviderSecurityFixtures.Event(), "Bearer " + token).Events);
        Assert.Equal("https://unit-toko.eu.auth0.com/", change.Issuer);
        Assert.Throws<AuthorizationDeniedException>(() => verifier.Verify(ProviderEventTransport.Auth0Events,
            ProviderSecurityFixtures.Event(), "Bearer " + options.EventsBearerToken));
    }

    [Theory]
    [InlineData("source", "urn:auth0:other")]
    [InlineData("a0tenant", "other-tenant")]
    [InlineData("a0stream", "est_ZYXWVUTSRQPONMLKJIHGFE")]
    [InlineData("specversion", "2.0")]
    public void Cloud_event_configuration_mismatch_is_rejected(string field, string value)
    {
        var json = EventNode(); json[field] = value;
        Reject(json.ToJsonString());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("time")]
    [InlineData("a0stream")]
    [InlineData("data")]
    public void Required_cloud_event_fields_cannot_be_missing(string field)
    {
        var json = EventNode(); json.Remove(field); Reject(json.ToJsonString());
    }

    [Fact]
    public void Native_boolean_and_exact_user_id_are_required_without_email_or_actor_fallback()
    {
        var json = EventNode(); json["data"]!["object"]!["blocked"] = "true"; Reject(json.ToJsonString());
        json = EventNode(); json["data"]!["object"]!.AsObject().Remove("user_id");
        json["data"]!["object"]!["email"] = "owner@example.test"; Reject(json.ToJsonString());
        json = EventNode(); json["data"]!["object"]!["user_id"] = 123; Reject(json.ToJsonString());
        json = EventNode(); json["data"]!["object"]!["user_id"] = " auth0|target-user"; Reject(json.ToJsonString());
        var logs = JsonNode.Parse(ProviderSecurityFixtures.Log().Span)!;
        logs[0]!["data"]!.AsObject().Remove("user_id");
        Reject(logs.ToJsonString(), ProviderEventTransport.Auth0Logs);
    }

    [Theory]
    [InlineData("blocked", "true")]
    [InlineData("user_id", "\"auth0|other-user\"")]
    public void Duplicate_security_properties_are_rejected(string name, string extra)
    {
        var original = Encoding.UTF8.GetString(ProviderSecurityFixtures.Event().Span);
        var json = original.Replace("\"blocked\":true", "\"blocked\":true,\"" + name + "\":" + extra, StringComparison.Ordinal);
        Reject(json);
    }

    [Fact]
    public void Duplicate_properties_in_ignored_nested_data_are_also_rejected()
    {
        var json = Encoding.UTF8.GetString(ProviderSecurityFixtures.Event().Span);
        json = json.Replace("\"email\":\"owner@example.test\"", "\"email\":\"owner@example.test\",\"email\":\"other@example.test\"", StringComparison.Ordinal);
        Reject(json);
        json = json.Replace("\"email\":\"other@example.test\"", "\"\\u0065mail\":\"other@example.test\"", StringComparison.Ordinal);
        Reject(json);
    }

    [Fact]
    public void Unblock_or_unrelated_events_never_create_a_security_change()
    {
        var json = EventNode(); json["data"]!["object"]!["blocked"] = false;
        var result = Verify(Bytes(json.ToJsonString())); Assert.Empty(result.Events); Assert.Equal(1, result.IgnoredCount);
        result = Verify(ProviderSecurityFixtures.Event("user.created")); Assert.Empty(result.Events); Assert.Equal(1, result.IgnoredCount);
        result = Verify(ProviderSecurityFixtures.Log("scpr"), ProviderEventTransport.Auth0Logs); Assert.Empty(result.Events); Assert.Equal(1, result.IgnoredCount);
    }

    [Fact]
    public void Late_events_are_accepted_but_future_or_zone_free_times_are_rejected()
    {
        var json = EventNode(); json["time"] = "2020-01-01T00:00:00Z";
        Assert.Single(Verify(Bytes(json.ToJsonString())).Events);
        json["time"] = ProviderSecurityFixtures.Now.AddSeconds(31).ToString("O"); Reject(json.ToJsonString());
        json["time"] = "2026-09-28T08:00:00"; Reject(json.ToJsonString());
        json["time"] = 123; Reject(json.ToJsonString());
    }

    [Fact]
    public void Semantic_fingerprint_ignores_untrusted_profile_noise_but_detects_changed_subject_kind_or_time()
    {
        var baseline = Assert.Single(Verify(ProviderSecurityFixtures.Event()).Events);
        var json = EventNode(); json["data"]!["object"]!["email"] = "different@example.test";
        json["data"]!["object"]!["nickname"] = "Different person label";
        Assert.Equal(baseline.Fingerprint, Assert.Single(Verify(Bytes(json.ToJsonString())).Events).Fingerprint);
        json["data"]!["object"]!["user_id"] = "auth0|other-user";
        Assert.NotEqual(baseline.Fingerprint, Assert.Single(Verify(Bytes(json.ToJsonString())).Events).Fingerprint);
        json = EventNode(); json["type"] = "user.deleted";
        Assert.NotEqual(baseline.Fingerprint, Assert.Single(Verify(Bytes(json.ToJsonString())).Events).Fingerprint);
        json = EventNode(); json["time"] = ProviderSecurityFixtures.Now.AddSeconds(-1).ToString("O");
        Assert.NotEqual(baseline.Fingerprint, Assert.Single(Verify(Bytes(json.ToJsonString())).Events).Fingerprint);
    }

    [Theory]
    [InlineData("user.updated", ProviderSecurityChange.AccountDisabled)]
    [InlineData("user.deleted", ProviderSecurityChange.AccountDeleted)]
    public void Large_ignored_user_app_and_previous_metadata_do_not_drop_security_changes(string type, ProviderSecurityChange expected)
    {
        var baseline = Assert.Single(Verify(ProviderSecurityFixtures.Event(type)).Events);
        var json = JsonNode.Parse(ProviderSecurityFixtures.Event(type).Span)!;
        json["data"]!["object"]!["user_metadata"] = new JsonObject { ["description"] = new string('u', 8192) };
        json["data"]!["object"]!["app_metadata"] = new JsonObject { ["description"] = new string('a', 8192) };
        json["data"]!["previous_object"] = new JsonObject
        {
            ["user_id"] = "auth0|not-the-current-target",
            ["user_metadata"] = new JsonObject { ["description"] = new string('p', 8192) }
        };
        var body = Bytes(json.ToJsonString());
        Assert.True(body.Length < 65_536);
        var change = Assert.Single(Verify(body).Events);
        Assert.Equal(expected, change.Change);
        Assert.Equal(baseline.Subject, change.Subject);
        Assert.Equal(baseline.Fingerprint, change.Fingerprint);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("object")]
    [InlineData("long-name")]
    [InlineData("escaped-control-name")]
    [InlineData("empty-name")]
    public void Ignored_metadata_shapes_are_bounded_by_message_bytes_and_depth(string shape)
    {
        var json = EventNode();
        JsonNode metadata;
        if (shape == "array")
            metadata = new JsonArray(Enumerable.Range(0, 300).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        else if (shape == "object")
        {
            var properties = new JsonObject();
            for (var index = 0; index < 300; index++) properties["field" + index] = index;
            metadata = properties;
        }
        else
        {
            var name = shape == "long-name" ? new string('n', 1024) : shape == "escaped-control-name" ? "user\tlabel" : "";
            metadata = new JsonObject { [name] = "ignored metadata" };
        }
        json["data"]!["object"]!["user_metadata"] = metadata;
        var body = Bytes(json.ToJsonString());
        Assert.True(body.Length < 65_536);
        var change = Assert.Single(Verify(body).Events);
        Assert.Equal(ProviderSecurityChange.AccountDisabled, change.Change);
        Assert.Equal(Assert.Single(Verify(ProviderSecurityFixtures.Event()).Events).Fingerprint, change.Fingerprint);
    }

    [Fact]
    public void Unrelated_log_metadata_arrays_and_long_values_do_not_drop_password_changes()
    {
        var json = JsonNode.Parse(ProviderSecurityFixtures.Log().Span)!;
        json[0]!["data"]!["details"]!["metadata"] = new JsonArray(
            Enumerable.Range(0, 300).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        json[0]!["data"]!["description"] = new string('x', 8192);
        var body = Bytes(json.ToJsonString());
        Assert.True(body.Length < 65_536);
        var change = Assert.Single(Verify(body, ProviderEventTransport.Auth0Logs).Events);
        Assert.Equal(ProviderSecurityChange.PasswordChanged, change.Change);
        Assert.Equal("auth0|target-user", change.Subject);
    }

    [Fact]
    public void Log_stream_wrapper_and_optional_inner_log_id_must_be_unambiguous()
    {
        var json = JsonNode.Parse(ProviderSecurityFixtures.Log().Span)!;
        json[0]!["data"]!["log_id"] = "999";
        Reject(json.ToJsonString(), ProviderEventTransport.Auth0Logs);
        Reject(json[0]!["data"]!.ToJsonString(), ProviderEventTransport.Auth0Logs);
        Reject("[]", ProviderEventTransport.Auth0Logs);
    }

    [Fact]
    public void Entire_batch_is_validated_before_any_events_are_returned()
    {
        var first = JsonNode.Parse(ProviderSecurityFixtures.Log().Span)![0]!.DeepClone();
        var invalid = first.DeepClone(); invalid["data"]!["user_id"] = null;
        var array = new JsonArray(first, invalid);
        Reject(array.ToJsonString(), ProviderEventTransport.Auth0Logs);
    }

    [Fact]
    public void Bounds_reject_large_body_batch_security_field_and_depth_with_safe_errors()
    {
        Reject("[" + new string(' ', 65_536) + "]");
        var item = JsonNode.Parse(ProviderSecurityFixtures.Log().Span)![0]!;
        var batch = new JsonArray(Enumerable.Range(0, 101).Select(_ => item.DeepClone()).ToArray());
        Reject(batch.ToJsonString(), ProviderEventTransport.Auth0Logs);
        var json = EventNode(); json["data"]!["object"]!["user_id"] = new string('x', 257); Reject(json.ToJsonString());
        JsonNode nested = JsonValue.Create("leaf")!;
        for (var i = 0; i < 20; i++) nested = new JsonObject { ["nested"] = nested };
        json = EventNode(); json["extra"] = nested; Reject(json.ToJsonString());
    }

    [Fact]
    public void Invalid_enabled_configuration_is_rejected_without_echoing_secrets()
    {
        var options = ProviderSecurityFixtures.Options(); options.EventsBearerToken = new string('a', 64);
        var error = Assert.Throws<InvalidOperationException>(options.Validate); Assert.DoesNotContain(options.EventsBearerToken, error.Message);
        options = ProviderSecurityFixtures.Options(); options.LogsBearerToken = options.EventsBearerToken;
        Assert.Throws<InvalidOperationException>(options.Validate);
        options = ProviderSecurityFixtures.Options(); options.Issuer = "http://unit-toko.eu.auth0.com/";
        Assert.Throws<InvalidOperationException>(options.Validate);
        options = ProviderSecurityFixtures.Options(); options.EventSource = "";
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Disabled_defaults_and_logs_only_configuration_do_not_require_event_stream_settings()
    {
        new Auth0SecurityEventOptions().Validate();
        var options = ProviderSecurityFixtures.Options(); options.EventsEnabled = false;
        options.EventSource = ""; options.EventStreamId = ""; options.TenantId = "";
        options.EventsBearerToken = ""; options.PreviousEventsBearerToken = "";
        options.Validate();
        Assert.Single(new ProviderSecurityEventVerifier(options, ProviderSecurityFixtures.Clock())
            .Verify(ProviderEventTransport.Auth0Logs, ProviderSecurityFixtures.Log(), "Bearer " + options.LogsBearerToken).Events);
    }

    private static JsonObject EventNode() => JsonNode.Parse(ProviderSecurityFixtures.Event().Span)!.AsObject();
    private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);
    private static VerifiedProviderSecurityBatch Verify(ReadOnlyMemory<byte> body, ProviderEventTransport transport = ProviderEventTransport.Auth0Events)
    {
        var options = ProviderSecurityFixtures.Options();
        return new ProviderSecurityEventVerifier(options, ProviderSecurityFixtures.Clock()).Verify(transport, body,
            "Bearer " + (transport == ProviderEventTransport.Auth0Events ? options.EventsBearerToken : options.LogsBearerToken));
    }
    private static void Reject(string body, ProviderEventTransport transport = ProviderEventTransport.Auth0Events)
    {
        var error = Assert.Throws<DomainValidationException>(() => Verify(Bytes(body), transport));
        Assert.Equal("invalid_provider_event", error.Code);
        Assert.DoesNotContain("owner@example.test", error.Message);
        Assert.DoesNotContain("target-user", error.Message);
    }
}

internal static class ProviderSecurityFixtures
{
    internal static DateTimeOffset Now => new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    internal static IClock Clock() => new FixtureClock();
    private sealed class FixtureClock : IClock { public DateTimeOffset UtcNow => Now; }
    internal static Auth0SecurityEventOptions Options() => new()
    {
        EventsEnabled = true, LogsEnabled = true, Issuer = "https://unit-toko.eu.auth0.com/",
        EventSource = "urn:auth0:unit-toko.eu.auth0.com", TenantId = "unit-toko", EventStreamId = "est_ABCDEFGHIJKLMNOPQRSTUV",
        EventsBearerToken = "events-test-key-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop",
        PreviousEventsBearerToken = "previous-events-key-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop",
        LogsBearerToken = "logs-test-key-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop",
        PreviousLogsBearerToken = "previous-logs-key-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop"
    };
    internal static ReadOnlyMemory<byte> Event(string type = "user.updated", string subject = "auth0|target-user",
        string id = "evt_0123456789ABCDEFGHIJKL", DateTimeOffset? occurredAt = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        specversion = "1.0", type, source = "urn:auth0:unit-toko.eu.auth0.com", id, time = (occurredAt ?? Now).ToString("O"),
        a0tenant = "unit-toko", a0stream = "est_ABCDEFGHIJKLMNOPQRSTUV",
        data = new { @object = new { user_id = subject, blocked = true, email = "owner@example.test" } }
    });
    internal static ReadOnlyMemory<byte> Log(string type = "scp", string subject = "auth0|target-user",
        string id = "90020260928080000000000001", DateTimeOffset? occurredAt = null) => JsonSerializer.SerializeToUtf8Bytes(new[]
    {
        new { log_id = id, data = new { date = (occurredAt ?? Now).ToString("O"), type, user_id = subject,
            details = new { auth = new { subject = "auth0|administrator" } } } }
    });
}
