using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace UnitToko.Tests.Identity;

public sealed class IdentityRuntimeTests
{
    [Fact]
    public void Enabled_identity_without_persistent_keys_refuses_startup()
    {
        using var factory = Factory(Settings());
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Disabled_identity_reports_unavailable_without_issuing_security_cookies()
    {
        using var factory = Factory(new());
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/api/v1/identity/csrf");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("identity_unavailable", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Protected_host_issues_host_cookie_and_requires_origin_plus_real_antiforgery()
    {
        using var keys = new RuntimeKeys();
        using var factory = Factory(Settings(keys));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), AllowAutoRedirect = false, HandleCookies = false });
        var response = await client.GetAsync("/api/v1/identity/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-ut_csrf=", cookie); Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("requestToken").GetString()!;
        async Task<HttpResponseMessage> Send(string? origin, string? suppliedToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/access/grants");
            request.Headers.Add("Cookie", cookie.Split(';')[0]);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (suppliedToken is not null) request.Headers.Add("X-CSRF-Token", suppliedToken);
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("https://attacker.unit-toko.co.id", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("https://portal.unit-toko.co.id", "forged")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("https://portal.unit-toko.co.id", token)).StatusCode);
    }

    [Theory]
    [InlineData("http://portal.unit-toko.co.id")]
    [InlineData("https://attacker.unit-toko.co.id")]
    public async Task Wrong_host_or_plain_http_is_rejected_even_with_spoofed_forwarding(string baseAddress)
    {
        using var keys = new RuntimeKeys();
        using var factory = Factory(Settings(keys));
        using var client = factory.CreateClient(new() { BaseAddress = new(baseAddress), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "portal.unit-toko.co.id");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        var response = await client.GetAsync("/api/v1/identity/csrf");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Independently_constructed_hosts_share_protected_antiforgery_keys()
    {
        using var keys = new RuntimeKeys();
        using var first = Factory(Settings(keys));
        using var second = Factory(Settings(keys));
        using var browser = first.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), HandleCookies = false });
        using var other = second.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), AllowAutoRedirect = false, HandleCookies = false });
        var response = await browser.GetAsync("/api/v1/identity/csrf");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/access/grants");
        request.Headers.Add("Origin", "https://portal.unit-toko.co.id");
        request.Headers.Add("X-CSRF-Token", json.RootElement.GetProperty("requestToken").GetString());
        request.Headers.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.SendAsync(request)).StatusCode);
        Assert.All(System.IO.Directory.GetFiles(keys.Directory, "key-*.xml"), path => Assert.Contains("encryptedSecret", File.ReadAllText(path)));
    }

    [Fact]
    public async Task Challenge_flood_is_bounded_before_any_database_or_provider_call()
    {
        using var keys = new RuntimeKeys();
        using var factory = Factory(Settings(keys));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://portal.unit-toko.co.id"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://portal.unit-toko.co.id");
        for (var attempt = 0; attempt < 20; attempt++)
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/auth/login", new StringContent(""))).StatusCode);
        var response = await client.PostAsync("/auth/login", new StringContent(""));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("192.0.2.10", true)]
    [InlineData("192.0.2.11", false)]
    public async Task Only_explicit_proxy_may_supply_public_origin(string remoteAddress, bool trusted)
    {
        using var keys = new RuntimeKeys();
        var settings = Settings(keys);
        settings["Identity:TrustedProxies:0"] = "192.0.2.10";
        using var factory = Factory(settings).WithWebHostBuilder(web => web.ConfigureServices(services =>
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(services, new RemoteAddressFilter(remoteAddress))));
        using var client = factory.CreateClient(new() { BaseAddress = new("http://backend.internal"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "portal.unit-toko.co.id");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.25");
        var response = await client.GetAsync("/api/v1/identity/csrf");
        Assert.Equal(trusted ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class RemoteAddressFilter(string address) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            Microsoft.AspNetCore.Builder.UseExtensions.Use(app, (context, nextRequest) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                return nextRequest();
            });
            next(app);
        };
    }
    internal static WebApplicationFactory<Program> Factory(Dictionary<string, string?> settings) => new RuntimeFactory(settings);

    private sealed class RuntimeFactory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            // Host bootstrap configuration reaches Program before its conditional service registration.
            // ConfigureAppConfiguration on the test web host runs too late for those decisions.
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(settings)
                {
                    ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0",
                    ["Mongo:Database"] = settings.GetValueOrDefault("Mongo:Database") ?? "unittoko_runtime_test_" + Guid.NewGuid().ToString("N")
                }));
            return base.CreateHost(builder);
        }
    }
    internal static Dictionary<string, string?> Settings(RuntimeKeys? keys = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Identity:Auth0:Enabled"] = "true", ["Identity:Auth0:Issuer"] = "https://login.unit-toko.co.id/",
            ["Identity:Auth0:PublicOrigin"] = "https://portal.unit-toko.co.id", ["Identity:Auth0:ClientId"] = "synthetic-client",
            ["Identity:Auth0:ClientSecret"] = "synthetic-client-secret", ["Identity:Auth0:ConnectionId"] = "con_synthetic",
            ["Identity:Auth0:ConnectionName"] = "synthetic-connection", ["Identity:Auth0:ClaimsNamespace"] = "https://portal.unit-toko.co.id/claims/"
        };
        if (keys is not null)
        {
            settings["Identity:Keys:Directory"] = keys.Directory;
            settings["Identity:Keys:ApplicationName"] = "UnitToko.Runtime.Tests";
            settings["Identity:Keys:CertificatePath"] = keys.CertificatePath;
            settings["Identity:Keys:CertificatePassword"] = "synthetic-password";
        }
        return settings;
    }

    internal sealed class RuntimeKeys : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-RuntimeTests"));
        public string Directory { get; }
        public string CertificatePath { get; }
        public RuntimeKeys()
        {
            Directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
            System.IO.Directory.CreateDirectory(Directory);
            CertificatePath = Path.Combine(Directory, "synthetic.pfx");
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=UnitToko runtime fixture", rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx, "synthetic-password"));
        }
        public void Dispose()
        {
            if (!Directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path");
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
