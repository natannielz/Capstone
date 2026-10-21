using System.Runtime.Versioning;
using System.Text.Json;
using MongoDB.Bson;
using UnitToko.Modules.Identity;

namespace UnitToko.LocalPilot.Tests;

/// <summary>A persistent review or full database follows the persona definitions on every start, and an existing install gains
/// any persona it lacks without losing a secret.</summary>
public sealed class PilotPersonaUpgradeTests
{
    private static PilotPersona Persona(string name) => PilotPersonas.All.Single(persona => persona.Name == name);
    private static BsonDocument Identity(long id, long grantVersion) => new() { { "_id", id }, { "status", "active" }, { "grantVersion", grantVersion } };
    private static BsonDocument StoredGrant(long id, long userId, string template, BsonDocument scope, IEnumerable<string> permissions, long version = 1) => new()
    {
        { "_id", id }, { "userId", userId }, { "membershipId", id + 1000 }, { "templateId", template }, { "templateVersion", 1 },
        { "permissions", new BsonArray(permissions) }, { "scope", scope }, { "status", "active" }, { "version", version },
    };
    private static BsonDocument StoredMembership(long id, long userId, BsonDocument scope) => new()
        { { "_id", id }, { "userId", userId }, { "scope", scope }, { "status", "active" }, { "version", 1L } };

    [Fact]
    public void Stored_grants_are_reconciled_to_the_persona_definition_and_the_grant_version_moves()
    {
        var persona = Persona("penyetuju-cabang");
        var expected = persona.Grants[0];
        var scope = PilotPersonas.ScopeDocument(expected.Scope);
        // A grant seeded by an older definition: it still holds order.submit and lacks order.spending.policy.read.
        var stale = StoredGrant(7001, persona.AccountId, "buyer", scope, expected.Permissions.Remove(Permissions.OrderSpendingPolicyRead).Add(Permissions.OrderSubmit), version: 3);
        var foreign = StoredGrant(7002, persona.AccountId, "store-staff", scope, [Permissions.CatalogRead]);
        var plan = PilotPersonas.Reconcile(persona, Identity(persona.AccountId, 5), [stale, foreign], [StoredMembership(8001, persona.AccountId, scope)]);
        Assert.True(plan.Changed);
        var corrected = Assert.Single(plan.Corrected);
        Assert.Equal(7001, corrected["_id"].AsInt64);
        Assert.Equal(expected.Permissions.Order(StringComparer.Ordinal), corrected["permissions"].AsBsonArray.Select(value => value.AsString).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Permissions.OrderSubmit, corrected["permissions"].AsBsonArray.Select(value => value.AsString));
        Assert.Equal(4, corrected["version"].AsInt64);
        Assert.Equal(new long[] { 7002 }, plan.RevokedGrants);
        Assert.Empty(plan.Missing); Assert.Empty(plan.RevokedMemberships);
        Assert.Equal(6, plan.GrantVersion);

        // Once stored grants match, nothing changes and the grant version stays.
        var current = StoredGrant(7001, persona.AccountId, "buyer", scope, expected.Permissions, version: 4);
        var settled = PilotPersonas.Reconcile(persona, Identity(persona.AccountId, 6), [current], [StoredMembership(8001, persona.AccountId, scope)]);
        Assert.False(settled.Changed); Assert.Equal(6, settled.GrantVersion);

        // A scope that moved (kurir's roster grant) revokes the old grant and membership and adds the new one.
        var kurir = Persona("kurir");
        var oldScope = new BsonDocument { { "kind", "courierAssignment" }, { "organizationId", 100L }, { "shopId", 101L }, { "warehouseId", 102L }, { "channelId", 103L } };
        var moved = PilotPersonas.Reconcile(kurir, Identity(kurir.AccountId, 1), [StoredGrant(7003, kurir.AccountId, "courier", oldScope, kurir.Grants[0].Permissions)],
            [StoredMembership(8003, kurir.AccountId, oldScope)]);
        Assert.Equal(new long[] { 7003 }, moved.RevokedGrants); Assert.Equal(new long[] { 8003 }, moved.RevokedMemberships);
        Assert.Equal(kurir.Grants[0], Assert.Single(moved.Missing)); Assert.Equal(2, moved.GrantVersion);
    }

    [WindowsPilotFact]
    [SupportedOSPlatform("windows")]
    public void An_existing_install_gains_missing_personas_after_a_backup_and_keeps_every_secret()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        try
        {
            var settings = Path.Combine(path, "private-settings.json"); var accounts = Path.Combine(path, "ACCOUNTS-PRIVATE.txt");
            static PilotAccount Account(long id, string name) => new(id, name + " · SINTETIS", name, "secret-" + id, "12" + id);
            // An install from before the latest persona: the four originals and all personas but the last one defined.
            var originals = new[] { Account(1001, "pemohon-a"), Account(1002, "pemohon-b"), Account(2001, "petugas"), Account(2002, "peninjau") };
            var older = PilotPersonas.SignedIn.SkipLast(1).Select(persona => Account(persona.AccountId, persona.Name)).ToArray();
            var before = new PilotSecrets(Guid.NewGuid().ToString("N"), "client-secret", "certificate-password", [.. originals, .. older]);
            File.WriteAllText(settings, JsonSerializer.Serialize(before)); File.WriteAllText(accounts, "old private list");
            var originalBytes = File.ReadAllBytes(settings);
            var at = new DateTimeOffset(2026, 10, 21, 9, 30, 15, 123, TimeSpan.Zero);
            var backup = settings + ".before-upgrade-20261021T093015123Z";

            // The backup comes first: if it cannot be written, nothing is rewritten.
            Directory.CreateDirectory(backup);
            Assert.ThrowsAny<Exception>(() => PilotState.LoadSecrets(path, fullRelease: true, at));
            Assert.Equal(originalBytes, File.ReadAllBytes(settings));
            Assert.Equal("old private list", File.ReadAllText(accounts));
            Directory.Delete(backup);

            var upgraded = PilotState.LoadSecrets(path, fullRelease: true, at);
            Assert.Equal(originalBytes, File.ReadAllBytes(backup));
            Assert.Equal("old private list", File.ReadAllText(Path.Combine(path, "ACCOUNTS-PRIVATE.txt.before-upgrade-20261021T093015123Z")));
            Assert.Equal(PilotPersonas.OriginalAccountIds.Concat(PilotPersonas.AccountIds).Order(), upgraded.Accounts.Select(account => account.Id).Order());
            Assert.All(before.Accounts, account => Assert.Contains(account, upgraded.Accounts));
            Assert.Equal((before.InstanceId, before.ClientSecret, before.CertificatePassword), (upgraded.InstanceId, upgraded.ClientSecret, upgraded.CertificatePassword));
            var added = upgraded.Accounts.Single(account => account.Id == PilotPersonas.SignedIn.Last().AccountId);
            Assert.Contains("Nama masuk: " + added.Username, File.ReadAllText(accounts));
            Assert.Equal(JsonSerializer.Serialize(upgraded, new JsonSerializerOptions { WriteIndented = true }), File.ReadAllText(settings));

            // A current install is read as it is: no new backup, no rewrite.
            var current = File.ReadAllBytes(settings);
            Assert.Equal(upgraded.Accounts.Select(account => account.Id), PilotState.LoadSecrets(path, fullRelease: true, at.AddMinutes(1)).Accounts.Select(account => account.Id));
            Assert.Equal(current, File.ReadAllBytes(settings));
            Assert.Equal(2, Directory.GetFiles(path, "*.before-upgrade-*").Length);
            // Release1 never gains personas, and an unknown account id is refused rather than rewritten.
            Assert.Throws<InvalidOperationException>(() => PilotState.LoadSecrets(path, fullRelease: false, at));
            File.WriteAllText(settings, JsonSerializer.Serialize(before with { Accounts = [.. originals, Account(4999, "unknown")] }));
            Assert.Throws<InvalidOperationException>(() => PilotState.LoadSecrets(path, fullRelease: true, at.AddMinutes(2)));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests")) + Path.DirectorySeparatorChar;
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
