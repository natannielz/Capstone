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
        "petugas-gudang", "petugas-kirim", "kurir", "keuangan-tagihan", "pemeriksa-pembayaran", "pengadaan", "penyetuju-pengadaan", "admin-akses", "penyetuju-akses",
        "staf-pembeli"];

    /// <summary>Every maker/checker pair: the proposing side, then the deciding side. No persona may hold both. Same list as
    /// MakerCheckerPairs.All in UnitToko.Tests/Authorization/RoleTemplateJobTests.cs (a separate project, so it is kept in step by hand).</summary>
    private static readonly (string Maker, string Checker)[] MakerChecker = [
        (Permissions.OrderSubmit, Permissions.OrderSpendApprove), (Permissions.OrderSubmit, Permissions.OrderApprove),
        (Permissions.BranchOrderRequestSubmit, Permissions.BranchOrderRequestDecide), (Permissions.SubstitutionPropose, Permissions.SubstitutionAccept),
        (Permissions.OrderSpendingPolicyPropose, Permissions.OrderSpendingPolicyApprove), (Permissions.OrderCommercialPolicyPropose, Permissions.OrderCommercialPolicyApprove),
        (Permissions.PricingManage, Permissions.PricingApprove), (Permissions.CatalogPublicPropose, Permissions.CatalogPublicApprove),
        (Permissions.ProcurementPrepare, Permissions.ProcurementApprove), (Permissions.SupplierChangePropose, Permissions.SupplierChangeApprove),
        (Permissions.SupplierBankPropose, Permissions.SupplierBankApprove), (Permissions.SupplierClaim, Permissions.SupplierClaimApprove),
        (Permissions.FundingRequest, Permissions.FundingApprove), (Permissions.ExpenseRequest, Permissions.ExpenseApprove),
        (Permissions.ProcurementPayablePropose, Permissions.ProcurementPayableDecide), (Permissions.ProcurementAdvanceReturnPropose, Permissions.ProcurementAdvanceReturnDecide),
        (Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementQualityAcceptanceDecide),
        (Permissions.ProcurementStockValuationPropose, Permissions.ProcurementStockValuationDecide),
        (Permissions.StockAdjustPropose, Permissions.StockAdjustApprove), (Permissions.StockReleasePropose, Permissions.StockReleaseApprove),
        (Permissions.StockSourcePropose, Permissions.StockSourceApprove), (Permissions.StockSourcePolicyPropose, Permissions.StockSourcePolicyApprove),
        (Permissions.StockSourceReconciliationPropose, Permissions.StockSourceReconciliationDecide),
        (Permissions.InvoiceReleasePropose, Permissions.InvoiceReleaseApprove), (Permissions.PaymentRecord, Permissions.PaymentVerify),
        (Permissions.PaymentAllocate, Permissions.PaymentVerify), (Permissions.DisbursementRecord, Permissions.DisbursementVerify),
        (Permissions.CreditRequest, Permissions.CreditApprove), (Permissions.RefundRequest, Permissions.RefundApprove),
        (Permissions.ReceivingAccountPropose, Permissions.ReceivingAccountVerify), (Permissions.ReceivingAccountPropose, Permissions.ReceivingAccountApprove),
        (Permissions.PeriodSubmit, Permissions.PeriodApprove), (Permissions.PeriodReopenRequest, Permissions.PeriodReopenApprove),
        (Permissions.AccountingBookPropose, Permissions.AccountingBookApprove), (Permissions.AccessPropose, Permissions.AccessApprove),
        (Permissions.MembershipPropose, Permissions.MembershipApprove), (Permissions.PersonReviewPropose, Permissions.PersonReviewApprove),
        (Permissions.TechnicalConfigure, Permissions.OrganizationApprove),
        // Independence the services and ApprovalPolicy enforce from recorded history (RequestedBy / RecordedBy / ApprovedBy / SubmittedBy ...):
        (Permissions.BankStatementStage, Permissions.BankReceiptConfirm),          // BankReceiptService.PeopleAsync: the statement importer
        (Permissions.BankStatementStage, Permissions.DisbursementVerify),          // ProcurementSettlementService, RefundPayoutService.Settlement: the importer
        (Permissions.FundingApprove, Permissions.DisbursementRecord),              // ProcurementDisbursementService, ApprovalPolicy: the funding approver
        (Permissions.RefundApprove, Permissions.DisbursementRecord),               // RefundPayoutService, ApprovalPolicy: the refund approver
        (Permissions.ProcurementSupplierDocumentMatch, Permissions.ProcurementStockValuationDecide), // ProcurementStockValuationService: the matcher
        (Permissions.CaseRequest, Permissions.CaseManage),                         // ApprovalPolicy (AfterSalesCaseService): the case requester
        (Permissions.CaseRequest, Permissions.CreditRequest), (Permissions.CaseRequest, Permissions.RefundRequest), // ReturnEntitlementService: the case requester
        (Permissions.CaseRequest, Permissions.CreditApprove), (Permissions.CaseRequest, Permissions.RefundApprove), // ReturnFinancialApprovalService: every maker so far
        (Permissions.PaymentEvidence, Permissions.PaymentVerify),                  // PaymentVerificationService: the payment submitter
        (Permissions.PaymentEvidence, Permissions.PaymentAllocate),                // PaymentAllocationService (RecordedBy): the payment submitter
        (Permissions.PaymentEvidence, Permissions.PaymentReviewManual),            // manual payment review: the payment submitter
        (Permissions.PaymentEvidence, Permissions.PaymentReviewManage),            // payment review actions (RecordedBy): the payment submitter
        (Permissions.PaymentEvidence, Permissions.CreditApprove), (Permissions.PaymentEvidence, Permissions.RefundApprove), // ReturnFinancialApprovalService
        (Permissions.PaymentAllocate, Permissions.CreditApprove), (Permissions.PaymentAllocate, Permissions.RefundApprove), // ReturnFinancialApprovalService
        (Permissions.PaymentVerify, Permissions.CreditApprove), (Permissions.PaymentVerify, Permissions.RefundApprove),     // ReturnFinancialApprovalService
        (Permissions.StockCount, Permissions.StockAdjustApprove),                  // ApprovalPolicy: the counter (CountedBy)
        (Permissions.PeriodSubmit, Permissions.PeriodClose), (Permissions.PeriodApprove, Permissions.PeriodClose), // ApprovalPolicy: submitter and approver
        (Permissions.PeriodClose, Permissions.PeriodReopenApprove),                // ApprovalPolicy: ClosedBy
    ];

    /// <summary>The copy above must not drift from the backend's own list: MakerCheckerPairs.All, read from its source in this checkout (a
    /// separate test project, so it cannot be referenced). A pair added there and not here would let a persona hold both sides unnoticed.</summary>
    [Fact]
    public void The_pair_list_is_the_backends_MakerCheckerPairs()
    {
        string? file = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null && file is null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "tests", "UnitToko.Tests", "Authorization", "RoleTemplateJobTests.cs");
            if (File.Exists(candidate)) file = candidate;
        }
        Assert.NotNull(file);
        var text = File.ReadAllText(file); var start = text.IndexOf("class MakerCheckerPairs", StringComparison.Ordinal);
        Assert.True(start >= 0, "MakerCheckerPairs is no longer in RoleTemplateJobTests.cs; point this test at its new home.");
        var body = text[start..text.IndexOf("];", start, StringComparison.Ordinal)];
        string Value(string name) => (string)typeof(Permissions).GetField(name)!.GetRawConstantValue()!;
        var canonical = System.Text.RegularExpressions.Regex.Matches(body, @"\(Permissions\.(\w+),\s*Permissions\.(\w+)\)").Select(match => (Value(match.Groups[1].Value), Value(match.Groups[2].Value))).ToArray();
        Assert.NotEmpty(canonical);
        Assert.Equal(canonical.Distinct().Order().ToArray(), MakerChecker.Distinct().Order().ToArray());
        Assert.Equal(canonical.Length, MakerChecker.Length);
    }

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
            {
                // A persona is an explicit grant set: its template, packs and additions, minus template permissions it must not hold.
                var template = RoleTemplates.Materialize(grant.TemplateId, grant.FinancePacks, grant.Additions).Permissions;
                Assert.All(grant.Removed, permission => Assert.Contains(permission, RoleTemplates.Templates[grant.TemplateId].Permissions));
                Assert.Empty(grant.Removed.Intersect(grant.Additions));
                Assert.Equal(template.Except(grant.Removed).ToArray(), grant.Permissions.ToArray());
            }
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
        // Maker/checker stays split across people: no persona holds both sides of any reviewed change. No exceptions.
        foreach (var (maker, checker) in MakerChecker)
        {
            Assert.NotNull(Permissions.Find(maker)); Assert.NotNull(Permissions.Find(checker));
            Assert.All(PilotPersonas.All, persona => Assert.False(Held(persona.Name).Contains(maker) && Held(persona.Name).Contains(checker),
                persona.Name + " holds both " + maker + " and " + checker));
        }
        Assert.DoesNotContain(Permissions.OrderSubmit, Held("penyetuju-cabang"));
        Assert.DoesNotContain(Permissions.SupplierClaim, Held("penyetuju-pengadaan"));
        // G-035: the payment checker reads the bank pages, which also require receiving-account details.
        Assert.Contains(Permissions.ReceivingAccountDetailsRead, Held("pemeriksa-pembayaran"));
        Assert.Contains(Permissions.BankStatementStage, Held("keuangan-tagihan"));
        // F-027: each persona holds only its role's grants, so its workspace shows only its own work.
        Assert.Equal(new[] { Permissions.CatalogPublicApprove, Permissions.CatalogRead, Permissions.OrderRead, Permissions.PricingApprove }, Held("peninjau-katalog").Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Permissions.CaseRead, Held("keuangan-tagihan"));
        // F-007 (owner decision 1 Nov): keuangan-tagihan allocates payments pemeriksa-pembayaran verified; neither holds the other's side.
        Assert.Contains(Permissions.PaymentReviewRead, Held("keuangan-tagihan"));
        Assert.Contains(Permissions.PaymentAllocate, Held("keuangan-tagihan")); Assert.DoesNotContain(Permissions.PaymentVerify, Held("keuangan-tagihan"));
        Assert.Contains(Permissions.PaymentVerify, Held("pemeriksa-pembayaran")); Assert.DoesNotContain(Permissions.PaymentAllocate, Held("pemeriksa-pembayaran"));
        Assert.DoesNotContain(Permissions.StockSourceRead, Held("petugas-gudang")); Assert.DoesNotContain(Permissions.StockSourceReconciliationRead, Held("petugas-gudang"));
        Assert.DoesNotContain(Permissions.OrderSpendingPolicyRead, Held("penyetuju-cabang"));
        // The access checker approves what admin-akses proposes and cannot propose itself.
        Assert.Contains(Permissions.AccessApprove, Held("penyetuju-akses"));
        Assert.Contains(Permissions.MembershipApprove, Held("penyetuju-akses"));
        Assert.DoesNotContain(Permissions.AccessPropose, Held("penyetuju-akses"));
        Assert.DoesNotContain(Permissions.MembershipPropose, Held("penyetuju-akses"));
        // kurir is on the shop's courier roster: a courier-assignment membership and grant for any warehouse of shop 101, with the
        // shipment.read and delivery.attempt the dispatch courier picker requires (ShipmentCourierEligibility).
        var kurir = Assert.Single(PilotPersonas.All.Single(persona => persona.Name == "kurir").Grants);
        Assert.Equal(new GrantScope(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), kurir.Scope);
        Assert.Contains(Permissions.ShipmentRead, kurir.Permissions); Assert.Contains(Permissions.DeliveryAttempt, kurir.Permissions);
        var (kurirMemberships, _) = PilotPersonas.Records(PilotPersonas.All.Single(persona => persona.Name == "kurir"), [9001], [9101], now);
        Assert.Equal(kurir.Scope, Assert.Single(kurirMemberships).Scope);
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
            var failRevoke = "";
            Task Revoke(string token, CancellationToken _)
            {
                if (token == failRevoke) throw new TimeoutException("synthetic revoke failure");
                revoked.Add(token); return Task.CompletedTask;
            }
            // personas.json reports the grants as stored in the database, not the code's intent.
            Task<IReadOnlyList<PersonaGrantView>> Describe(PilotPersona persona, CancellationToken _) => Task.FromResult<IReadOnlyList<PersonaGrantView>>(
                [new PersonaGrantView(persona.Grants[0].TemplateId, "stored-scope-" + persona.Name, ["stored.only." + persona.Name])]);
            Assert.Throws<InvalidOperationException>(() => PilotSessions.Create(root, [new IPEndPoint(IPAddress.Any, 8443)], Issue, Revoke, Describe));
            Assert.Throws<InvalidOperationException>(() => PilotSessions.Create(root, [new IPEndPoint(IPAddress.Parse("192.168.1.20"), 8443)], Issue, Revoke, Describe));
            Assert.Empty(Directory.GetFileSystemEntries(root));
            Assert.Throws<ArgumentException>(() => PilotSessions.FilePath(root, "../outside"));
            Assert.Throws<ArgumentException>(() => PilotSessions.FilePath(root, "C:\\outside"));

            var sessions = PilotSessions.Create(root, review, Issue, Revoke, Describe);
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
            {
                var listed = parsed.RootElement.GetProperty("personas").EnumerateArray().ToArray();
                Assert.Equal(Personas, listed.Select(item => item.GetProperty("name").GetString()));
                var pemohon = listed.Single(item => item.GetProperty("name").GetString() == "pemohon");
                var stored = Assert.Single(pemohon.GetProperty("grants").EnumerateArray());
                Assert.Equal("stored-scope-pemohon", stored.GetProperty("scope").GetString());
                Assert.Equal(new[] { "stored.only.pemohon" }, stored.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()!).ToArray());
                Assert.Empty(listed.Single(item => item.GetProperty("name").GetString() == "tamu").GetProperty("grants").EnumerateArray());
            }

            // A refresh replaces each file in place: still one file and one new session per persona.
            var signedIn = Personas.Length - 1;
            await sessions.MintAllAsync(CancellationToken.None);
            Assert.Equal(2 * signedIn, issued.Count);
            Assert.Equal(signedIn + 1, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
            using (var latest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pemohon.json"))))
                Assert.Equal(issued.Last(item => item.Persona == "pemohon").Token, latest.RootElement.GetProperty("cookies")[0].GetProperty("value").GetString());

            // Stopping removes the files and revokes every minted session; one failed revocation neither stops the rest nor hides the count.
            failRevoke = issued[3].Token;
            var stopped = await sessions.StopAsync(CancellationToken.None);
            Assert.Equal((2 * signedIn - 1, 1), (stopped.Revoked, stopped.Failed));
            Assert.Equal(issued.Select(item => item.Token).Where(token => token != failRevoke).Order(StringComparer.Ordinal), revoked.Order(StringComparer.Ordinal));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnitToko-LocalPilot-Tests")) + Path.DirectorySeparatorChar;
            if (root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
