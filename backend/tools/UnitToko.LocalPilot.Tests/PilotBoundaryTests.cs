using System.Runtime.Versioning;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using UnitToko.Api.Identity;

namespace UnitToko.LocalPilot.Tests;

public sealed class PilotBoundaryTests
{
    [Theory]
    [InlineData("mongodb://remote.internal:27029/?replicaSet=unitTokoPilot", PilotBoundary.DatabaseName)]
    [InlineData("mongodb://127.0.0.1:27028/?replicaSet=unitTokoPilot", PilotBoundary.DatabaseName)]
    [InlineData("mongodb://127.0.0.1:27029/?replicaSet=rs0", PilotBoundary.DatabaseName)]
    [InlineData("mongodb://127.0.0.1:27029/other?replicaSet=unitTokoPilot", PilotBoundary.DatabaseName)]
    [InlineData(PilotBoundary.MongoUri, "production")]
    public void Bootstrap_refuses_foreign_or_disposable_database_targets(string uri, string database) =>
        Assert.Throws<InvalidOperationException>(() => PilotBoundary.RequireDatabase(uri, database));

    [Fact]
    public void Synthetic_origins_use_the_unchanged_production_origin_validator()
    {
        PilotBoundary.RequireDatabase(PilotBoundary.MongoUri, PilotBoundary.DatabaseName);
        var options = new Auth0Options { Enabled = true, Issuer = PilotBoundary.Issuer, PublicOrigin = PilotBoundary.Origin,
            ClientId = SyntheticOidcProvider.ClientId, ClientSecret = "synthetic-private-test", ConnectionId = SyntheticOidcProvider.ConnectionId,
            ConnectionName = SyntheticOidcProvider.ConnectionName, ClaimsNamespace = SyntheticOidcProvider.ClaimsNamespace };
        Assert.True(new Auth0OptionsValidator().Validate(null, options).Succeeded);
        options.PublicOrigin = "https://localhost";
        Assert.True(new Auth0OptionsValidator().Validate(null, options).Failed);
        Assert.DoesNotContain("order.submit", PilotBootstrap.PermissionsFor(1001));
        Assert.DoesNotContain("journal.post", PilotBootstrap.PermissionsFor(2001));
        Assert.Contains("order.request.submit", PilotBootstrap.PermissionsFor(1001));
    }

    [WindowsPilotFact]
    [SupportedOSPlatform("windows")]
    public void Restart_preserves_private_accounts_certificate_and_encrypted_session_key_directory()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests", Guid.NewGuid().ToString("N")));
        try
        {
            string settings; byte[] certificate; string keyDirectory;
            using (var first = new PilotState(path))
            {
                settings = File.ReadAllText(Path.Combine(path, "private-settings.json"));
                certificate = SHA256.HashData(File.ReadAllBytes(first.CertificatePath));
                keyDirectory = first.KeyDirectory;
                File.WriteAllText(Path.Combine(keyDirectory, "preserved-test-file"), "unchanged");
                var acl = new DirectoryInfo(path).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected);
                var user = WindowsIdentity.GetCurrent().User;
                Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => Assert.Equal(user, rule.IdentityReference));
            }
            using var second = new PilotState(path);
            Assert.Equal(settings, File.ReadAllText(Path.Combine(path, "private-settings.json")));
            Assert.Equal(certificate, SHA256.HashData(File.ReadAllBytes(second.CertificatePath)));
            Assert.Equal("unchanged", File.ReadAllText(Path.Combine(second.KeyDirectory, "preserved-test-file")));
            Assert.Equal(keyDirectory, second.KeyDirectory);
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests")) + Path.DirectorySeparatorChar;
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [WindowsPilotFact]
    [SupportedOSPlatform("windows")]
    public async Task Pilot_certificate_completes_real_Windows_TLS_without_machine_trust()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests", Guid.NewGuid().ToString("N")));
        try
        {
            using var state = new PilotState(path);
            var certificateHash = SHA256.HashData(state.Certificate.RawData);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var server = ServeAsync();
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
                using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
                    CryptographicOperations.FixedTimeEquals(certificateHash, SHA256.HashData(certificate.GetRawCertData())));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = PilotBoundary.PortalHost }, deadline.Token);
                Assert.True(tls.IsAuthenticated && tls.IsEncrypted);
                await tls.WriteAsync(new byte[] { 42 }, deadline.Token);
                Assert.Equal(42, await server);

                async Task<int> ServeAsync()
                {
                    using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
                    using var secure = new SslStream(accepted.GetStream());
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = state.Certificate }, deadline.Token);
                    var received = new byte[1];
                    Assert.Equal(1, await secure.ReadAsync(received, deadline.Token));
                    return received[0];
                }
            }
            finally { listener.Stop(); }
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests")) + Path.DirectorySeparatorChar;
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
public sealed class WindowsPilotFactAttribute : FactAttribute
{
    public WindowsPilotFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows current-user ACL boundary."; }
}
