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
public sealed class BankStatementHttpTests(MongoFixture fixture)
{
    [Fact] public async Task Actual_private_routes_stage_exact_original_with_actor_csrf_and_immutable_recovery() {
        await using var data=await BankStatementTestData.CreateAsync(fixture); var catalog=data.Accounts.Catalog;
        using var keys=new IdentityRuntimeTests.RuntimeKeys(); var settings=IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"]=catalog.Database.DatabaseNamespace.DatabaseName;
        using var factory=IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web=>web.ConfigureTestServices(services=>{
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(catalog.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
        }));
        const string origin="https://portal.unit-toko.co.id"; using var client=factory.CreateClient(new(){ BaseAddress=new(origin),AllowAutoRedirect=false }); var actor=data.Maker;
        async Task<string> SignIn() { client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie",OpaqueSessionCookie.Name+"="+catalog.Issued[actor.UserId].OpaqueToken);
            return (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!; }
        var csrf=await SignIn(); var access=await client.GetFromJsonAsync<JsonElement>("/api/v1/access/current"); Assert.Contains("bank.statement.stage",access.GetProperty("allowedActions").EnumerateArray().Select(value=>value.GetString()));
        async Task<HttpResponseMessage> Send(string path,object body,string? fault=null) {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/billing/bank-statements/"+path) { Content=JsonContent.Create(body) };
            request.Headers.Add("Origin",fault=="origin"?"https://outside.invalid":origin);
            if(fault!="actor") request.Headers.Add("X-Expected-Actor-Id",actor.UserId.ToString());
            if(fault!="csrf") request.Headers.Add("X-CSRF-Token",csrf);
            if(fault!="key") request.Headers.Add("Idempotency-Key","http-stage");
            if(fault!="version") request.Headers.Add("If-Match","\"0\""); return await client.SendAsync(request);
        }
        var selection=new{ profileId="910",profileRevision="1",accountId=data.Selection.AccountId.ToString(),accountVersionId=data.Selection.AccountVersionId.ToString() };
        var bytes=BankStatementTestData.Original(); var originalBase64=Convert.ToBase64String(bytes);
        using var preview=await Send("preview",new{ selection,originalBase64 }); Assert.True(preview.IsSuccessStatusCode,await preview.Content.ReadAsStringAsync()); Assert.True(preview.Headers.CacheControl!.NoStore);
        var parsed=await preview.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("1000.25",parsed.GetProperty("preview").GetProperty("credits").GetString());
        var body=new{ selection,originalBase64,filename="synthetic.csv",binding=parsed.GetProperty("binding").GetString(),sourceReference="synthetic-bank-portal",attested=true,reason="Synthetic exact statement import" };
        using var missingBinding=await Send("imports",new{selection,originalBase64,filename="synthetic.csv",binding=(string?)null,sourceReference="source",attested=true,reason="Invalid binding"});
        Assert.Equal(HttpStatusCode.BadRequest,missingBinding.StatusCode);
        foreach(var fault in new[]{"origin","actor","csrf","key","version"}) { using var denied=await Send("imports",body,fault); Assert.Equal(fault is "origin" or "csrf"?HttpStatusCode.Forbidden:HttpStatusCode.BadRequest,denied.StatusCode); }
        using var staged=await Send("imports",body); Assert.True(staged.IsSuccessStatusCode,await staged.Content.ReadAsStringAsync()); var result=await staged.Content.ReadFromJsonAsync<JsonElement>(); var id=result.GetProperty("importId").GetString()!;
        using var recovery=await Send("operations/query",new{purpose="operation-recovery",operation="billing.bank-statement.stage",key="http-stage"}); Assert.True(recovery.IsSuccessStatusCode,await recovery.Content.ReadAsStringAsync());
        Assert.Equal(result.GetRawText(),(await recovery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetRawText());
        actor=data.Reader; csrf=await SignIn();
        using var detail=await Send($"imports/{id}/query",new{purpose="bank-statement-review"}); Assert.True(detail.IsSuccessStatusCode,await detail.Content.ReadAsStringAsync());
        var view=await detail.Content.ReadAsStringAsync(); Assert.DoesNotContain("personId",view); Assert.DoesNotContain("originalBase64",view); Assert.DoesNotContain("requestKey",view);
        using var original=await Send($"imports/{id}/original/query",new{purpose="bank-statement-review"}); Assert.True(original.IsSuccessStatusCode,await original.Content.ReadAsStringAsync());
        Assert.Equal(bytes,await original.Content.ReadAsByteArrayAsync()); Assert.True(original.Headers.CacheControl!.NoStore); Assert.Equal("attachment",original.Content.Headers.ContentDisposition!.DispositionType);
        actor=data.Other; csrf=await SignIn(); using var outside=await Send($"imports/{id}/original/query",new{purpose="bank-statement-review"}); Assert.Equal(HttpStatusCode.Forbidden,outside.StatusCode);
    }
}
