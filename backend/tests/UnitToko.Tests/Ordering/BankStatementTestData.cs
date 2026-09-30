using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Billing.BankStatementBson;

namespace UnitToko.Tests.Ordering;

internal sealed class BankStatementTestData(ReceivingAccountTestData accounts) : IAsyncDisposable
{
    internal ReceivingAccountTestData Accounts { get; }=accounts;
    internal ActorContext Maker { get; private set; }=null!;
    internal ActorContext Reader { get; private set; }=null!;
    internal ActorContext Other { get; private set; }=null!;
    internal BankStatementSelection Selection { get; private set; }=null!;
    internal IMongoCollection<BsonDocument> Rows(string name)=>Accounts.Rows(name);
    internal static byte[] Original(string id="0001",string amount="1000.25")=>Encoding.UTF8.GetBytes(string.Join(',',BankStatementParser.Fields)+$"\r\n{id},2026-09-27,C,{amount},Synthetic buyer,SHARED-REF,0000123456789,IDR\r\n");
    internal BankStatementService Service=>NewService();
    internal BankStatementService NewService(MongoClient? client=null) {
        var selected=client??Accounts.Catalog.Client; var db=selected.GetDatabase(Accounts.Catalog.Database.DatabaseNamespace.DatabaseName);
        return new(db,new(selected),new(db),Accounts.Catalog.Clock,Accounts.NewService(selected));
    }
    internal static AuthorizationCommandMetadata Meta(string key)=>new(key,"Synthetic bank record staging","synthetic-bank-statement");
    internal static async Task<BankStatementTestData> CreateAsync(MongoFixture fixture) {
        var data=new BankStatementTestData(await ReceivingAccountTestData.CreateAsync(fixture)); var catalog=data.Accounts.Catalog;
        string[] read=[Permissions.BankStatementRead,Permissions.ReceivingAccountRead,Permissions.ReceivingAccountDetailsRead];
        data.Maker=await catalog.ActorAsync(7110,CatalogAuthoringTestData.Shop,[..read,Permissions.BankStatementStage]);
        data.Reader=await catalog.ActorAsync(7120,CatalogAuthoringTestData.Shop,read);
        data.Other=await catalog.ActorAsync(7130,CatalogAuthoringTestData.OtherOwner,[..read,Permissions.BankStatementStage]);
        var proposal=await data.Accounts.ProposeAsync(); await data.Accounts.ApproveAsync(proposal,await data.Accounts.ObserveAsync(proposal));
        var version=await data.Rows(ReceivingAccountBson.Versions).Find(new BsonDocument("accountId",proposal["accountId"])).SingleAsync();
        data.Selection=new(910,1,proposal["accountId"].AsInt64,version["_id"].AsInt64);
        var scope=(await data.Rows(ReceivingAccountBson.Policies).Find(new BsonDocument("_id",810L)).SingleAsync())["scope"];
        await data.Rows(B.Profiles).InsertOneAsync(new BsonDocument { { "_id",910L }, { "schemaVersion",1 }, { "revision",1L }, { "scope",scope },
            { "bankCode","FIXTUREBANK" }, { "name","Synthetic bank export profile" }, { "parser","bank-csv-v1" }, { "format",B.Format(BankStatementParserTests.Format) },
            { "trustedSourceReference","synthetic-authenticated-portal" }, { "operatingOwnerReference","synthetic-finance-owner" }, { "approvalReference","synthetic-bank-export-approval" },
            { "approvedAt",catalog.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt",catalog.Clock.UtcNow.AddDays(1).UtcDateTime }, { "active",true }, { "fence",0L } });
        return data;
    }
    internal async Task<BsonDocument> StageAsync(string key="stage",byte[]? original=null,string filename="synthetic.csv") {
        original??=Original(); var preview=await Service.PreviewAsync(Maker,Selection,original);
        return await Service.StageAsync(Maker,Selection,original,filename,preview.Binding,"synthetic-private-statement-reference",true,Meta(key));
    }
    internal async Task<BsonDocument> StateAsync() {
        var state=await Accounts.StateAsync();
        foreach(var name in new[]{ B.Profiles,B.Imports,"journalEntries","bankReceipts","paymentAllocations" }) state[name]=new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id",1)).ToListAsync());
        return state;
    }
    public ValueTask DisposeAsync()=>Accounts.DisposeAsync();
}
