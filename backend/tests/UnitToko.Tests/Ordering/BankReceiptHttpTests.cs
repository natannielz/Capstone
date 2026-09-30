using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class BankReceiptHttpTests(MongoFixture fixture)
{
    [Fact] public async Task Actual_private_routes_confirm_cash_once_with_actor_csrf_and_scoped_recovery() {
        await using var data=await BankReceiptTestData.CreateAsync(fixture); var catalog=data.Statements.Accounts.Catalog;
        using var keys=new IdentityRuntimeTests.RuntimeKeys(); var settings=IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"]=catalog.Database.DatabaseNamespace.DatabaseName;
        using var factory=IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web=>web.ConfigureTestServices(services=>{
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(catalog.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
        }));
        const string origin="https://portal.unit-toko.co.id"; using var client=factory.CreateClient(new(){ BaseAddress=new(origin),AllowAutoRedirect=false }); var actor=data.Reviewer;
        async Task<string> SignIn() { client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie",OpaqueSessionCookie.Name+"="+catalog.Issued[actor.UserId].OpaqueToken);
            return (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!; }
        var csrf=await SignIn(); var access=await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current"); Assert.Contains("bank.receipt.confirm",access.GetProperty("allowedActions").EnumerateArray().Select(value=>value.GetString()));
        async Task<HttpResponseMessage> Send(string path,object body,string? fault=null) {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/billing/bank-receipts/"+path) { Content=JsonContent.Create(body) };
            request.Headers.Add("Origin",fault=="origin"?"https://outside.invalid":origin);
            if(fault!="actor") request.Headers.Add("X-Expected-Actor-Id",actor.UserId.ToString());
            if(fault!="csrf") request.Headers.Add("X-CSRF-Token",csrf);
            if(fault!="key") request.Headers.Add("Idempotency-Key","http-confirm");
            if(fault!="version") request.Headers.Add("If-Match","\"0\""); return await client.SendAsync(request);
        }
        using var preview=await Send("preview",new { importId=data.ImportId.ToString(),rowNumber=2 }); Assert.True(preview.IsSuccessStatusCode,await preview.Content.ReadAsStringAsync());
        Assert.True(preview.Headers.CacheControl!.NoStore); var shown=await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1000.25",shown.GetProperty("row").GetProperty("amount").GetString());
        var observed=new { accountNumber="0000123456789",transactionId="0001",bookedDate="2026-09-27",amount="1000.25" };
        var body=new { importId=data.ImportId.ToString(),rowNumber=2,binding=shown.GetProperty("binding").GetString(),observed,sourceReference="synthetic-bank-portal",attested=true,reason="Synthetic independently observed credit" };
        using var missing=await Send("confirmations",new { importId=data.ImportId.ToString(),rowNumber=2,binding=shown.GetProperty("binding").GetString(),observed=(object?)null,sourceReference="synthetic",attested=true,reason="Missing observation" });
        Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);
        foreach(var fault in new[]{"origin","actor","csrf","key","version"}) { using var denied=await Send("confirmations",body,fault); Assert.Equal(fault is "origin" or "csrf"?HttpStatusCode.Forbidden:HttpStatusCode.BadRequest,denied.StatusCode); }
        using var confirmed=await Send("confirmations",body); Assert.True(confirmed.IsSuccessStatusCode,await confirmed.Content.ReadAsStringAsync()); var result=await confirmed.Content.ReadFromJsonAsync<JsonElement>(); var id=result.GetProperty("receiptId").GetString()!;
        using var recovery=await Send("operations/query",new { purpose="operation-recovery",operation="billing.bank-receipt.confirm",key="http-confirm" }); Assert.True(recovery.IsSuccessStatusCode,await recovery.Content.ReadAsStringAsync());
        Assert.Equal(result.GetRawText(),(await recovery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetRawText());
        actor=data.Reader; csrf=await SignIn(); using var detail=await Send(id+"/query",new { purpose="bank-receipt-review" }); Assert.True(detail.IsSuccessStatusCode,await detail.Content.ReadAsStringAsync());
        var json=await detail.Content.ReadAsStringAsync(); Assert.DoesNotContain("personId",json); Assert.DoesNotContain("originalBase64",json); Assert.DoesNotContain("requestKey",json); Assert.True(detail.Headers.CacheControl!.NoStore);
        using var forbidden=await Send("confirmations",body); Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
        actor=data.Other; csrf=await SignIn(); using var outside=await Send(id+"/query",new { purpose="bank-receipt-review" }); Assert.Equal(HttpStatusCode.Forbidden,outside.StatusCode);
    }
}
