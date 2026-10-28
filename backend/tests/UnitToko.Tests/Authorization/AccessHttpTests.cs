using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed partial class AccessHttpTests(MongoFixture fixture)
{
    private static WebApplicationFactory<Program> Factory(StoreTestData data, ActorContext? actor = null,
        bool authenticated = true) => new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
    {
        web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0",
                ["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName
            }));
        web.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(data.Clock);
            if (authenticated) services.AddAuthentication("AccessTest").AddScheme<AuthenticationSchemeOptions,
                AccessTestAuthenticationHandler>("AccessTest", _ => { });
            if (actor is not null)
            {
                services.RemoveAll<IRequestActorResolver>();
                services.AddSingleton<IRequestActorResolver>(new TestResolver(actor));
            }
        });
    });

    [Fact]
    public async Task Authentication_and_verified_actor_are_both_required_and_rejections_are_never_cached()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        foreach (var authenticated in new[] { false, true })
        {
            using var factory = Factory(data, authenticated: authenticated);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Actor-Id", "1000");
            client.DefaultRequestHeaders.Add("X-Role", "system-admin");
            client.DefaultRequestHeaders.Add("Cookie", "__Host-ut_session=fabricated; session=legacy-demo");
            var denied = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl?.NoStore);
            var body = await Body(denied);
            Assert.Equal("unauthenticated", body["code"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(body["traceId"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task Current_access_keeps_large_ids_as_strings_and_does_not_list_other_organizations()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(9007199254740993L,
            new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        using var factory = Factory(data, actor);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/access/current");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await Body(response);
        Assert.Equal("9007199254740993", body["userId"]!.GetValue<string>());
        Assert.Equal("1", body["grantVersion"]!.GetValue<string>());
        Assert.Equal(new[] { "10", "11" }, body["scopes"]!.AsArray().Select(node => node!["id"]!.GetValue<string>()).Order().ToArray());
        Assert.Equal(new[] { Permissions.OrderRead }, body["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()));
        var catalog = await client.GetAsync("/api/v1/access/catalog");
        Assert.Equal(HttpStatusCode.Forbidden, catalog.StatusCode);
        Assert.True(catalog.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Current_access_discovers_only_granted_financial_tasks_and_keeps_step_up_entry_without_authorizing_expired_factor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var financialTasks = new[] { Permissions.CreditApprove, Permissions.RefundApprove,
            Permissions.DisbursementRecord, Permissions.DisbursementVerify };
        var checker = await data.AddActorAsync(7625, scope, Permissions.CaseRead, Permissions.InvoiceRead,
            Permissions.CreditApprove, Permissions.RefundApprove, Permissions.DisbursementRecord, Permissions.DisbursementVerify);
        var reader = await data.AddActorAsync(7626, scope, Permissions.CaseRead, Permissions.InvoiceRead);
        foreach (var permission in financialTasks)
            Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);

        async Task<string[]> Actions(UnitToko.Modules.Identity.Contracts.ActorContext actor)
        {
            using var factory = Factory(data, actor);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        }

        foreach (var actions in new[] { await Actions(checker), await Actions(reader) })
        {
            Assert.DoesNotContain(Permissions.CaseManage, actions);
            Assert.DoesNotContain(Permissions.CreditRequest, actions);
            Assert.DoesNotContain(Permissions.RefundRequest, actions);
        }
        var currentActions = await Actions(checker);
        var readerActions = await Actions(reader);
        foreach (var permission in financialTasks)
        {
            Assert.Contains(permission, currentActions);
            Assert.DoesNotContain(permission, readerActions);
        }

        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        var agedActions = await Actions(checker);
        var snapshot = await data.Store.ReadCurrentAsync(checker);
        foreach (var permission in financialTasks)
        {
            Assert.Contains(permission, agedActions);
            Assert.Equal("step_up_required", ScopePolicy.Compile(checker, snapshot, permission, data.Clock.UtcNow).DenialCode);
            Assert.True(ScopePolicy.CanRequestStepUp(checker, snapshot, permission, data.Clock.UtcNow));
        }
        foreach (var permission in financialTasks) Assert.DoesNotContain(permission, await Actions(reader));
    }

    [Fact]
    public async Task Current_access_discovers_only_granted_spending_policy_tasks_and_keeps_step_up_entry_without_authorizing_expired_factor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.BuyingBranch, 10, 11);
        var maker = await data.AddActorAsync(7661, scope, Permissions.BranchPic,
            Permissions.OrderSpendingPolicyRead, Permissions.OrderSpendingPolicyPropose);
        var checker = await data.AddActorAsync(7662, scope, Permissions.BranchPic,
            Permissions.OrderSpendingPolicyRead, Permissions.OrderSpendingPolicyApprove);
        var reader = await data.AddActorAsync(7663, scope, Permissions.BranchPic, Permissions.OrderSpendingPolicyRead);
        var assignments = new[]
        {
            (Actor: maker, Tasks: new[] { Permissions.OrderSpendingPolicyRead, Permissions.OrderSpendingPolicyPropose }),
            (Actor: checker, Tasks: new[] { Permissions.OrderSpendingPolicyRead, Permissions.OrderSpendingPolicyApprove }),
            (Actor: reader, Tasks: new[] { Permissions.OrderSpendingPolicyRead })
        };
        async Task<string[]> Actions(ActorContext actor)
        {
            using var factory = Factory(data, actor);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        foreach (var assignment in assignments)
        {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            foreach (var permission in assignment.Tasks)
                Assert.True(ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow).IsAllowed);
        }

        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var assignment in assignments)
        {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            Assert.True(ScopePolicy.Compile(assignment.Actor, current, Permissions.OrderSpendingPolicyRead, data.Clock.UtcNow).IsAllowed);
            foreach (var permission in assignment.Tasks.Where(value => value != Permissions.OrderSpendingPolicyRead))
            {
                Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)!.Assurance.MaximumAge);
                var compiled = ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow);
                Assert.False(compiled.IsAllowed);
                Assert.Equal("step_up_required", compiled.DenialCode);
                Assert.True(ScopePolicy.CanRequestStepUp(assignment.Actor, current, permission, data.Clock.UtcNow));
            }
        }
    }

    [Fact]
    public async Task Current_access_discovers_only_granted_accounting_book_tasks_and_keeps_step_up_entry_without_authorizing_expired_factor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var maker = await data.AddActorAsync(7671, scope, Permissions.AccountingBookRead, Permissions.AccountingBookPropose);
        var checker = await data.AddActorAsync(7672, scope, Permissions.AccountingBookRead, Permissions.AccountingBookApprove);
        var reader = await data.AddActorAsync(7673, scope, Permissions.AccountingBookRead);
        var assignments = new[]
        {
            (Actor: maker, Tasks: new[] { Permissions.AccountingBookRead, Permissions.AccountingBookPropose }),
            (Actor: checker, Tasks: new[] { Permissions.AccountingBookRead, Permissions.AccountingBookApprove }),
            (Actor: reader, Tasks: new[] { Permissions.AccountingBookRead })
        };
        async Task<string[]> Actions(ActorContext actor)
        {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            foreach (var permission in assignment.Tasks) Assert.True(ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow).IsAllowed);
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            Assert.True(ScopePolicy.Compile(assignment.Actor, current, Permissions.AccountingBookRead, data.Clock.UtcNow).IsAllowed);
            foreach (var permission in assignment.Tasks.Where(value => value != Permissions.AccountingBookRead)) {
                var policy = ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow);
                Assert.False(policy.IsAllowed); Assert.Equal("step_up_required", policy.DenialCode);
                Assert.True(ScopePolicy.CanRequestStepUp(assignment.Actor, current, permission, data.Clock.UtcNow));
            }
        }
    }

    [Fact]
    public async Task Current_access_discovers_only_granted_funding_tasks_and_keeps_step_up_entry_without_authorizing_expired_factor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var maker = await data.AddActorAsync(7701, scope, Permissions.FundingRead, Permissions.FundingRequest);
        var checker = await data.AddActorAsync(7702, scope, Permissions.FundingRead, Permissions.FundingApprove);
        var reader = await data.AddActorAsync(7703, scope, Permissions.FundingRead);
        var assignments = new[]
        {
            (Actor: maker, Tasks: new[] { Permissions.FundingRead, Permissions.FundingRequest }),
            (Actor: checker, Tasks: new[] { Permissions.FundingRead, Permissions.FundingApprove }),
            (Actor: reader, Tasks: new[] { Permissions.FundingRead })
        };
        async Task<string[]> Actions(ActorContext actor)
        {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            foreach (var permission in assignment.Tasks) Assert.True(ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow).IsAllowed);
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            Assert.True(ScopePolicy.Compile(assignment.Actor, current, Permissions.FundingRead, data.Clock.UtcNow).IsAllowed);
            foreach (var permission in assignment.Tasks.Where(value => value != Permissions.FundingRead)) {
                var policy = ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow);
                Assert.False(policy.IsAllowed); Assert.Equal("step_up_required", policy.DenialCode);
                Assert.True(ScopePolicy.CanRequestStepUp(assignment.Actor, current, permission, data.Clock.UtcNow));
            }
        }
    }

    [Fact]
    public async Task Current_access_discovers_only_granted_procurement_disbursement_tasks_and_keeps_step_up_entry()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var maker = await data.AddActorAsync(7721, scope, Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.DisbursementRecord);
        var reader = await data.AddActorAsync(7722, scope, Permissions.ProcurementDisbursementRead);
        var evidenceReader = await data.AddActorAsync(7723, scope, Permissions.ProcurementDisbursementEvidenceRead);
        var assignments = new[] {
            (Actor: maker, Tasks: new[] { Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.DisbursementRecord }),
            (Actor: reader, Tasks: new[] { Permissions.ProcurementDisbursementRead }),
            (Actor: evidenceReader, Tasks: new[] { Permissions.ProcurementDisbursementEvidenceRead })
        };
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        foreach (var assignment in assignments) Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor)); var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            foreach (var permission in assignment.Tasks) {
                var policy = ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow);
                if (permission == Permissions.DisbursementRecord) {
                    Assert.False(policy.IsAllowed); Assert.Equal("step_up_required", policy.DenialCode);
                    Assert.True(ScopePolicy.CanRequestStepUp(assignment.Actor, current, permission, data.Clock.UtcNow));
                } else Assert.True(policy.IsAllowed);
            }
        }
    }

    [Fact]
    public async Task Current_access_discovers_only_granted_supplier_bank_tasks_and_preserves_renewal_without_authorizing_expired_factor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var maker = await data.AddActorAsync(7711, scope, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose);
        var checker = await data.AddActorAsync(7712, scope, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankApprove);
        var reader = await data.AddActorAsync(7713, scope, Permissions.SupplierBankRead);
        var assignments = new[] {
            (Actor: maker, Tasks: new[] { Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankPropose }),
            (Actor: checker, Tasks: new[] { Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.SupplierBankApprove }),
            (Actor: reader, Tasks: new[] { Permissions.SupplierBankRead })
        };
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient(); using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor)); var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            foreach (var permission in assignment.Tasks) Assert.True(ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow).IsAllowed);
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor)); var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            foreach (var permission in assignment.Tasks) {
                var policy = ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow);
                if (permission is Permissions.SupplierBankPropose or Permissions.SupplierBankApprove) {
                    Assert.False(policy.IsAllowed); Assert.Equal("step_up_required", policy.DenialCode); Assert.True(ScopePolicy.CanRequestStepUp(assignment.Actor, current, permission, data.Clock.UtcNow));
                } else Assert.True(policy.IsAllowed);
            }
        }
    }
    [Fact]
    public async Task Current_access_discovers_only_granted_procurement_tasks_and_keeps_step_up_entry_without_authorizing_expired_factor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var maker = await data.AddActorAsync(7681, scope, Permissions.ProcurementRead, Permissions.ProcurementPrepare);
        var checker = await data.AddActorAsync(7682, scope, Permissions.ProcurementRead, Permissions.ProcurementApprove);
        var reader = await data.AddActorAsync(7683, scope, Permissions.ProcurementRead);
        var receiver = await data.AddActorAsync(7684, scope, Permissions.ProcurementRead, Permissions.ProcurementContainerReceive);
        var inspector = await data.AddActorAsync(7685, scope, Permissions.ProcurementRead, Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead);
        var evidenceReader = await data.AddActorAsync(7686, scope, Permissions.ProcurementRead, Permissions.ProcurementInspectionEvidenceRead);
        var stockReceiver = await data.AddActorAsync(7687, scope, Permissions.ProcurementRead, Permissions.StockReceive);
        var stockReader = await data.AddActorAsync(7688, scope, Permissions.ProcurementRead, Permissions.StockRead);
        var assignments = new[]
        {
            (Actor: maker, Tasks: new[] { Permissions.ProcurementRead, Permissions.ProcurementPrepare }),
            (Actor: checker, Tasks: new[] { Permissions.ProcurementRead, Permissions.ProcurementApprove }),
            (Actor: reader, Tasks: new[] { Permissions.ProcurementRead }),
            (Actor: receiver, Tasks: new[] { Permissions.ProcurementRead, Permissions.ProcurementContainerReceive }),
            (Actor: inspector, Tasks: new[] { Permissions.ProcurementRead, Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead }),
            (Actor: evidenceReader, Tasks: new[] { Permissions.ProcurementRead, Permissions.ProcurementInspectionEvidenceRead }),
            (Actor: stockReceiver, Tasks: new[] { Permissions.ProcurementRead, Permissions.StockReceive }),
            (Actor: stockReader, Tasks: new[] { Permissions.ProcurementRead, Permissions.StockRead })
        };
        async Task<string[]> Actions(ActorContext actor)
        {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            foreach (var permission in assignment.Tasks) Assert.True(ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow).IsAllowed);
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var assignment in assignments) {
            Assert.Equal(assignment.Tasks.Order(), await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            Assert.True(ScopePolicy.Compile(assignment.Actor, current, Permissions.ProcurementRead, data.Clock.UtcNow).IsAllowed);
            foreach (var permission in assignment.Tasks.Where(value => value != Permissions.ProcurementRead)) {
                var policy = ScopePolicy.Compile(assignment.Actor, current, permission, data.Clock.UtcNow);
                if (permission is Permissions.ProcurementInspectionEvidenceRead or Permissions.StockRead) { Assert.True(policy.IsAllowed); continue; }
                Assert.False(policy.IsAllowed); Assert.Equal("step_up_required", policy.DenialCode);
                Assert.True(ScopePolicy.CanRequestStepUp(assignment.Actor, current, permission, data.Clock.UtcNow));
            }
        }
    }
    [Fact]
    public async Task Current_access_discovers_only_granted_supplier_tasks_and_keeps_step_up_entry_without_promoting_warehouse_authority()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var maker = await data.AddActorAsync(7691, scope, Permissions.SupplierChangePropose);
        var checker = await data.AddActorAsync(7692, scope, Permissions.SupplierChangeApprove);
        var reader = await data.AddActorAsync(7693, scope, Permissions.ProcurementRead);
        var assignments = new[] { (Actor: maker, Task: Permissions.SupplierChangePropose), (Actor: checker, Task: Permissions.SupplierChangeApprove), (Actor: reader, Task: Permissions.ProcurementRead) };
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        foreach (var assignment in assignments) {
            Assert.Equal(new[] { assignment.Task }, await Actions(assignment.Actor));
            Assert.True(ScopePolicy.Compile(assignment.Actor, await data.Store.ReadCurrentAsync(assignment.Actor), assignment.Task, data.Clock.UtcNow).IsAllowed);
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        foreach (var assignment in assignments) {
            Assert.Equal(new[] { assignment.Task }, await Actions(assignment.Actor));
            var current = await data.Store.ReadCurrentAsync(assignment.Actor);
            var compiled = ScopePolicy.Compile(assignment.Actor, current, assignment.Task, data.Clock.UtcNow);
            if (assignment.Task == Permissions.ProcurementRead) Assert.True(compiled.IsAllowed);
            else { Assert.False(compiled.IsAllowed); Assert.Equal("step_up_required", compiled.DenialCode);
                Assert.True(ScopePolicy.CanRequestStepUp(assignment.Actor, current, assignment.Task, data.Clock.UtcNow)); }
        }
        // Warehouse-only procurement readers keep only their ordinary task; supplier governance is never discovered by promotion.
        var warehouse = await data.AddActorAsync(7694, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103), Permissions.ProcurementRead);
        Assert.Equal(new[] { Permissions.ProcurementRead }, await Actions(warehouse));
    }

    [Fact]
    public async Task Catalog_lists_every_role_template_and_separate_finance_packs_without_implicit_payment_verification()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        using var factory = Factory(data, actor);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/access/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Body(response);
        Assert.Equal(15, body["roles"]!.AsArray().Count);
        Assert.Equal(4, body["financePacks"]!.AsArray().Count);
        Assert.Empty(body["roles"]!.AsArray().Single(role => role!["id"]!.GetValue<string>() == "finance")!["permissions"]!.AsArray());
        Assert.DoesNotContain(body["financePacks"]!.AsArray(), pack => pack!["permissions"]!.AsArray()
            .Any(permission => permission!.GetValue<string>() == Permissions.PaymentVerify));
        Assert.Equal(new[] { Permissions.AccessPropose }, body["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Theory]
    [InlineData("numeric-id")]
    [InlineData("missing-kind")]
    [InlineData("numeric-kind")]
    [InlineData("unknown-field")]
    [InlineData("null-scope")]
    [InlineData("missing-packs")]
    public async Task Ambiguous_or_incomplete_json_is_rejected_before_proposal(string mutation)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        using var factory = Factory(data, actor);
        using var client = factory.CreateClient();
        var request = Grant(data.Clock.UtcNow);
        switch (mutation)
        {
            case "numeric-id": request["userId"] = 1001; break;
            case "missing-kind": request["scope"]!.AsObject().Remove("kind"); break;
            case "numeric-kind": request["scope"]!["kind"] = 0; break;
            case "unknown-field": request["approvedBy"] = "1000"; break;
            case "null-scope": request["scope"] = null; break;
            case "missing-packs": request.Remove("financePacks"); break;
        }
        var response = await client.PostAsync("/api/v1/access/grants/preview", Json(request));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("authorizationProposals").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Grant_preview_and_independent_approval_work_through_versioned_http_contract()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var technical = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose, Permissions.AccessAuditRead);
        var scope = new GrantScope(GrantScopeKind.BuyerOwned, 10, 11);
        await data.AddActorAsync(1001, scope, Permissions.OrderRead);
        var approver = await data.AddActorAsync(1002, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessApprove);
        await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 1002L),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray(RoleTemplates.Materialize("buyer", [], []).Permissions)));
        using var makerFactory = Factory(data, technical);
        using var maker = makerFactory.CreateClient();
        var grant = Grant(data.Clock.UtcNow);
        grant["delegablePermissions"] = new JsonArray(Permissions.OrderSubmit);
        grant["expiresAt"] = data.Clock.UtcNow.AddDays(1).ToString("O");
        var previewResponse = await maker.PostAsync("/api/v1/access/grants/preview", Json(grant));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await Body(previewResponse);
        Assert.True(preview["requiresIndependentApproval"]!.GetValue<bool>());
        Assert.Equal(Permissions.OrderSubmit, Assert.Single(preview["delegablePermissions"]!.AsArray())!.GetValue<string>());
        Assert.Equal(data.Clock.UtcNow.AddDays(1), preview["expiresAt"]!.GetValue<DateTimeOffset>());

        var body = new JsonObject { ["grant"] = grant.DeepClone(), ["reason"] = "Tugas pembelian cabang telah disetujui" };
        var submitted = await Send(maker, "/api/v1/access/grants/proposals", body, "propose-once");
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var proposal = await Body(submitted);
        var replay = await Send(maker, "/api/v1/access/grants/proposals", body, "propose-once");
        Assert.True(JsonNode.DeepEquals(proposal, await Body(replay)));
        var id = proposal["id"]!.GetValue<string>();
        var queue = await Body(await maker.GetAsync("/api/v1/access/proposals"));
        var review = Assert.Single(queue.AsArray())!;
        Assert.Equal("1001", review["membershipId"]!.GetValue<string>());
        Assert.Equal(1, review["templateVersion"]!.GetValue<int>());
        Assert.Equal(data.Clock.UtcNow, review["effectiveAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(data.Clock.UtcNow.AddDays(1), review["expiresAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(Permissions.OrderSubmit, Assert.Single(review["delegablePermissions"]!.AsArray())!.GetValue<string>());
        Assert.Equal("withdraw", Assert.Single(review["allowedActions"]!.AsArray())!.GetValue<string>());
        var reason = new JsonObject { ["reason"] = "Kewenangan dan lingkup telah diperiksa" };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(maker, $"/api/v1/access/grants/proposals/{id}/approve", reason, "self-approve", "1")).StatusCode);

        using var approverFactory = Factory(data, approver);
        using var checker = approverFactory.CreateClient();
        var checkerQueue = await Body(await checker.GetAsync("/api/v1/access/proposals"));
        Assert.Equal(new[] { "approve", "reject" }, Assert.Single(checkerQueue.AsArray())!["allowedActions"]!.AsArray()
            .Select(action => action!.GetValue<string>()).Order());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Send(checker, $"/api/v1/access/grants/proposals/{id}/approve", reason, "no-version")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await Send(checker, $"/api/v1/access/grants/proposals/{id}/approve", reason, "stale-version", "2")).StatusCode);
        var accepted = await Send(checker, $"/api/v1/access/grants/proposals/{id}/approve", reason, "approve-once", "1");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("2", (await Body(accepted))["grantVersion"]!.GetValue<string>());
        var changed = body.DeepClone().AsObject();
        changed["reason"] = "Alasan permintaan berubah";
        Assert.Equal(HttpStatusCode.Conflict,
            (await Send(maker, "/api/v1/access/grants/proposals", changed, "propose-once")).StatusCode);
    }

    [Fact]
    public async Task Openapi_requires_retry_and_version_headers_for_mutations()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        using var factory = Factory(data, authenticated: false);
        using var client = factory.CreateClient();
        var schema = await Body(await client.GetAsync("/openapi/v1.json"));
        foreach (var name in new[] { "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId" })
        {
            var field = schema["components"]!["schemas"]!["AccessScopeDto"]!["properties"]![name]!;
            Assert.Equal(new[] { "null", "string" }, field["type"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray());
            Assert.Equal(19, field["maxLength"]!.GetValue<int>());
            Assert.False(string.IsNullOrEmpty(field["pattern"]!.GetValue<string>()));
        }
        var paths = schema["paths"]!.AsObject();
        foreach (var path in paths.Where(item => item.Key.StartsWith("/api/v1/access/", StringComparison.Ordinal) ||
            item.Key.StartsWith("/api/v1/organizations", StringComparison.Ordinal)))
        {
            var operation = path.Value?["post"];
            if (operation is null || path.Key.EndsWith("/preview", StringComparison.Ordinal) ||
                path.Key.EndsWith("/query", StringComparison.Ordinal) || path.Key.EndsWith("/lookup", StringComparison.Ordinal) ||
                path.Key.EndsWith("/preview-decision", StringComparison.Ordinal)) continue; // a decision preview returns the binding to sign and changes nothing
            var parameters = operation["parameters"]!.AsArray();
            var key = parameters.Single(item => item?["name"]?.GetValue<string>() == "Idempotency-Key")!;
            Assert.Equal("header", key["in"]!.GetValue<string>());
            Assert.True(key["required"]!.GetValue<bool>());
            Assert.Equal(128, key["schema"]!["maxLength"]!.GetValue<int>());
            if (path.Key.Contains("{id}", StringComparison.Ordinal))
                Assert.True(parameters.Single(item => item?["name"]?.GetValue<string>() == "If-Match")!["required"]!.GetValue<bool>());
        }
    }

    private static JsonObject Grant(DateTimeOffset now) => new()
    {
        ["userId"] = "1001", ["membershipId"] = "1001", ["templateId"] = "buyer",
        ["financePacks"] = new JsonArray(), ["additionalPermissions"] = new JsonArray(),
        ["scope"] = new JsonObject { ["kind"] = "buyerOwned", ["organizationId"] = "10", ["branchId"] = "11" },
        ["effectiveAt"] = now.ToString("O")
    };
    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");
    private static async Task<JsonNode> Body(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, JsonObject body, string key, string? version = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        var current = await Body(await client.GetAsync("/api/v1/access/current"));
        request.Headers.Add("X-Expected-Actor-Id", current["userId"]!.GetValue<string>());
        request.Headers.Add("Idempotency-Key", key);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request);
    }
    private sealed class TestResolver(ActorContext actor) : IRequestActorResolver
    {
        public ValueTask<ActorContext?> ResolveAsync(HttpContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActorContext?>(actor);
    }
}

internal sealed class AccessTestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
        new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-only")],
            "AccessTest")), "AccessTest")));
}
