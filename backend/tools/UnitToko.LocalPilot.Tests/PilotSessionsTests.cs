using System.Net;
using System.Text.Json;
using UnitToko.Api.Identity;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.LocalPilot.Tests;

public sealed class PilotSessionsTests
{
    private static readonly string[] Personas = ["tamu", "pemohon", "penyetuju-cabang", "peninjau-toko", "petugas-katalog", "peninjau-katalog",
        "petugas-gudang", "petugas-kirim", "kurir", "keuangan-tagihan", "pemeriksa-pembayaran", "pengadaan", "penyetuju-pengadaan", "admin-akses", "staf-pembeli"];

    [Fact]
    public async Task Review_profile_refuses_non_loopback_and_mints_one_session_per_persona()
    {
        // The review edge binds loopback only, and nothing that mints a session accepts any other address.
        var review = PilotBoundary.EdgeFor("review");
        Assert.Equal(new[] { new IPEndPoint(IPAddress.Loopback, 8443), new IPEndPoint(IPAddress.IPv6Loopback, 8443) }, review);
        Assert.Equal("https://localhost:8443", PilotBoundary.BrowserOriginFor("review"));
        PilotSessions.RequireLoopback(review);
        foreach (var address in new[] { IPAddress.Any, IPAddress.IPv6Any, IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fe80::1") })
            Assert.Throws<InvalidOperationException>(() => PilotSessions.RequireLoopback([new IPEndPoint(IPAddress.Loopback, 8443), new IPEndPoint(address, 8443)]));
        Assert.Throws<InvalidOperationException>(() => PilotSessions.RequireLoopback([]));
        // The API keeps its unchanged origin validator: the review profile's internal origin passes it, the browser's localhost does not need to.
        var options = new Auth0Options { Enabled = true, Issuer = PilotBoundary.IssuerFor("review"), PublicOrigin = PilotBoundary.OriginFor("review"),
            ClientId = SyntheticOidcProvider.ClientId, ClientSecret = "synthetic-private-test", ConnectionId = SyntheticOidcProvider.ConnectionId,
            ConnectionName = SyntheticOidcProvider.ConnectionName, ClaimsNamespace = PilotBoundary.OriginFor("review") + "/claims/" };
        Assert.True(new Auth0OptionsValidator().Validate(null, options).Succeeded);
        Assert.NotEqual(PilotBoundary.OriginFor("full"), PilotBoundary.OriginFor("review"));

        // Every persona's grants are exactly a role template (plus its named finance packs and additions) and authorize in the pilot's scopes.
        Assert.Equal(Personas, PilotPersonas.All.Select(persona => persona.Name));
        Assert.Equal(PilotPersonas.All.Length, PilotPersonas.All.Select(persona => persona.AccountId).Distinct().Count());
        Assert.Empty(PilotPersonas.All.Single(persona => persona.Name == "tamu").Grants);
        var now = DateTimeOffset.UtcNow;
        foreach (var persona in PilotPersonas.All.Where(persona => persona.SignedIn))
        {
            foreach (var grant in persona.Grants)
                Assert.Equal(RoleTemplates.Materialize(grant.TemplateId, grant.FinancePacks, grant.Additions).Permissions.ToArray(), grant.Permissions.ToArray());
            var (memberships, grants) = PilotPersonas.Records(persona, Enumerable.Range(1, 4).Select(i => 9000L + i).ToArray(),
                Enumerable.Range(1, 4).Select(i => 9100L + i).ToArray(), now.AddMinutes(-1));
            var snapshot = new AuthorizationSnapshot(persona.AccountId, 1, AuthorizationStatus.Active, [.. grants], [.. memberships], PilotPersonas.Directory);
            var actor = new ActorContext(persona.AccountId, PilotBoundary.IssuerFor("review"), "local-pilot|" + persona.AccountId, 1, now, FactorAssurance.MultiFactor);
            foreach (var grant in grants)
            {
                Assert.True(ScopePolicy.IsGrantActive(grant, persona.AccountId, now), persona.Name + " holds a grant its scope cannot carry");
                foreach (var permission in grant.Permissions)
                    Assert.True(ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed, persona.Name + " cannot use " + permission);
            }
        }
        string[] Held(string name) => PilotPersonas.All.Single(persona => persona.Name == name).Grants.SelectMany(grant => grant.Permissions).ToArray();
        Assert.Contains(Permissions.OrderSubmit, Held("pemohon"));
        Assert.Contains(Permissions.OrderSpendApprove, Held("penyetuju-cabang"));
        Assert.Contains(Permissions.OrderAccept, Held("peninjau-toko"));
        Assert.Contains(Permissions.CatalogManage, Held("petugas-katalog"));
        Assert.Contains(Permissions.PricingApprove, Held("peninjau-katalog"));
        Assert.Contains(Permissions.CatalogPublicApprove, Held("peninjau-katalog"));
        Assert.Contains(Permissions.ShipmentPrepare, Held("petugas-gudang"));
        Assert.Contains(Permissions.ShipmentDispatch, Held("petugas-kirim"));
        Assert.Contains(Permissions.ShipmentAssign, Held("petugas-kirim"));
        Assert.Contains(Permissions.CatalogPublicPropose, Held("petugas-katalog"));
        Assert.Contains(Permissions.InvoiceReleasePropose, Held("keuangan-tagihan"));
        Assert.Contains(Permissions.InvoiceReleaseApprove, Held("pemeriksa-pembayaran"));
        Assert.Contains(Permissions.PaymentVerify, Held("pemeriksa-pembayaran"));
        Assert.Contains(Permissions.ProcurementInspect, Held("petugas-gudang"));
        Assert.Contains(Permissions.ProcurementSupplierDocumentRecord, Held("pengadaan"));
        Assert.Contains(Permissions.ProcurementQualityAcceptanceDecide, Held("penyetuju-pengadaan"));
        // Maker/checker stays split across people: no persona holds both sides of a reviewed change.
        foreach (var (maker, checker) in new[] { (Permissions.PricingManage, Permissions.PricingApprove), (Permissions.CatalogPublicPropose, Permissions.CatalogPublicApprove),
            (Permissions.ProcurementPrepare, Permissions.ProcurementApprove), (Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove),
            (Permissions.SupplierBankPropose, Permissions.SupplierBankApprove), (Permissions.InvoiceReleasePropose, Permissions.InvoiceReleaseApprove),
            (Permissions.ReceivingAccountPropose, Permissions.ReceivingAccountApprove), (Permissions.PaymentRecord, Permissions.PaymentVerify),
            (Permissions.DisbursementRecord, Permissions.DisbursementVerify), (Permissions.ProcurementStockValuationPropose, Permissions.ProcurementStockValuationDecide),
            (Permissions.StockReleasePropose, Permissions.StockReleaseApprove), (Permissions.StockAdjustPropose, Permissions.StockAdjustApprove),
            (Permissions.FundingRequest, Permissions.FundingApprove), (Permissions.ProcurementPayablePropose, Permissions.ProcurementPayableDecide),
            (Permissions.ProcurementAdvanceReturnPropose, Permissions.ProcurementAdvanceReturnDecide),
            (Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementQualityAcceptanceDecide), (Permissions.AccessPropose, Permissions.AccessApprove),
            (Permissions.MembershipPropose, Permissions.MembershipApprove), (Permissions.CreditRequest, Permissions.CreditApprove), (Permissions.RefundRequest, Permissions.RefundApprove) })
            Assert.All(PilotPersonas.All, persona => Assert.False(Held(persona.Name).Contains(maker) && Held(persona.Name).Contains(checker),
                persona.Name + " holds both " + maker + " and " + checker));
        Assert.Contains(Permissions.DeliveryAttempt, Held("kurir"));
        Assert.Contains(Permissions.InvoiceIssue, Held("keuangan-tagihan"));
        Assert.Contains(Permissions.PaymentReviewRead, Held("pemeriksa-pembayaran"));
        Assert.Contains(Permissions.ProcurementPrepare, Held("pengadaan"));
        Assert.Contains(Permissions.ProcurementApprove, Held("penyetuju-pengadaan"));
        Assert.Contains(Permissions.AccessPropose, Held("admin-akses"));
        Assert.Contains(Permissions.OrderSubmit, Held("staf-pembeli"));
        Assert.Contains(Permissions.ShipmentPrepare, Held("staf-pembeli"));

        // One Playwright storage state per signed-in persona, only under tmp/website-ready/sessions, never echoed to output.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var issued = new List<(string Persona, string Token)>(); var revoked = new List<string>();
            Task<MintedSession> Issue(PilotPersona persona, CancellationToken _)
            {
                var token = "synthetic-token-" + persona.Name + "-" + Guid.NewGuid().ToString("N");
                issued.Add((persona.Name, token));
                return Task.FromResult(new MintedSession(token, now.AddHours(8)));
            }
            Task Revoke(string token, CancellationToken _) { revoked.Add(token); return Task.CompletedTask; }
            Assert.Throws<InvalidOperationException>(() => PilotSessions.Create(root, [new IPEndPoint(IPAddress.Any, 8443)], Issue, Revoke));
            Assert.Throws<InvalidOperationException>(() => PilotSessions.Create(root, [new IPEndPoint(IPAddress.Parse("192.168.1.20"), 8443)], Issue, Revoke));
            Assert.Empty(Directory.GetFileSystemEntries(root));
            Assert.Throws<ArgumentException>(() => PilotSessions.FilePath(root, "../outside"));
            Assert.Throws<ArgumentException>(() => PilotSessions.FilePath(root, "C:\\outside"));

            var sessions = PilotSessions.Create(root, review, Issue, Revoke);
            var console = new StringWriter(); var previous = Console.Out; var previousError = Console.Error;
            Console.SetOut(console); Console.SetError(console);
            try { await sessions.MintAllAsync(CancellationToken.None); }
            finally { Console.SetOut(previous); Console.SetError(previousError); }

            var directory = Path.Combine(root, "tmp", "website-ready", "sessions");
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.All(files, file => Assert.StartsWith(directory + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Personas.Skip(1).Select(name => name + ".json").Append("personas.json").Order(StringComparer.Ordinal),
                files.Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal));
            Assert.Equal(Personas.Skip(1), issued.Select(item => item.Persona));
            foreach (var (persona, token) in issued)
            {
                Assert.Equal(PilotSessions.FilePath(root, persona), Path.Combine(directory, persona + ".json"));
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, persona + ".json")));
                var cookie = Assert.Single(state.RootElement.GetProperty("cookies").EnumerateArray());
                Assert.Equal(OpaqueSessionCookie.Name, cookie.GetProperty("name").GetString());
                Assert.Equal(token, cookie.GetProperty("value").GetString());
                Assert.Equal("localhost", cookie.GetProperty("domain").GetString());
                Assert.Equal("/", cookie.GetProperty("path").GetString());
                Assert.True(cookie.GetProperty("secure").GetBoolean() && cookie.GetProperty("httpOnly").GetBoolean());
                Assert.Equal("Lax", cookie.GetProperty("sameSite").GetString());
                Assert.Equal(now.AddHours(8).ToUnixTimeSeconds(), cookie.GetProperty("expires").GetInt64());
                Assert.Empty(state.RootElement.GetProperty("origins").EnumerateArray());
                Assert.DoesNotContain(token, console.ToString());
            }
            var index = File.ReadAllText(Path.Combine(directory, "personas.json"));
            Assert.All(issued, item => Assert.DoesNotContain(item.Token, index));
            using (var parsed = JsonDocument.Parse(index))
                Assert.Equal(Personas, parsed.RootElement.GetProperty("personas").EnumerateArray().Select(item => item.GetProperty("name").GetString()));

            // A refresh replaces each file in place: still one file and one new session per persona.
            await sessions.MintAllAsync(CancellationToken.None);
            Assert.Equal(28, issued.Count);
            Assert.Equal(15, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
            using (var latest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pemohon.json"))))
                Assert.Equal(issued.Last(item => item.Persona == "pemohon").Token, latest.RootElement.GetProperty("cookies")[0].GetProperty("value").GetString());

            // Stopping revokes every minted session and removes the files.
            await sessions.StopAsync(CancellationToken.None);
            Assert.Equal(issued.Select(item => item.Token).Order(StringComparer.Ordinal), revoked.Order(StringComparer.Ordinal));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests")) + Path.DirectorySeparatorChar;
            if (root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
