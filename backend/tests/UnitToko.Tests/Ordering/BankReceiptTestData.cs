using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Accounting;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Tests.Ordering;

internal sealed class BankReceiptTestData(BankStatementTestData statements) : IAsyncDisposable
{
    internal BankStatementTestData Statements { get; }=statements;
    internal ActorContext Reviewer { get; private set; }=null!;
    internal ActorContext Reader { get; private set; }=null!;
    internal ActorContext Other { get; private set; }=null!;
    internal long ImportId { get; private set; }
    internal static BankReceiptObservation Observation(string id="0001",decimal amount=1000.25m)=>new("0000123456789",id,new(2026,9,27),new(amount));
    internal IMongoCollection<BsonDocument> Rows(string name)=>Statements.Rows(name);
    internal BankReceiptService Service=>NewService();
    internal BankReceiptService NewService(MongoClient? client=null) {
        var selected=client??Statements.Accounts.Catalog.Client; var db=selected.GetDatabase(Statements.Accounts.Catalog.Database.DatabaseNamespace.DatabaseName);
        return new(db,new(selected),new(db),Statements.Accounts.Catalog.Clock,Statements.NewService(selected));
    }
    internal static AuthorizationCommandMetadata Meta(string key)=>new(key,"Synthetic independent credit review","synthetic-bank-receipt");
    internal static async Task<BankReceiptTestData> CreateAsync(MongoFixture fixture) => await AttachAsync(await BankStatementTestData.CreateAsync(fixture));
    internal static async Task<BankReceiptTestData> AttachAsync(BankStatementTestData statements, bool configureAccounting = true, byte[]? original = null) {
        var data=new BankReceiptTestData(statements); var catalog=data.Statements.Accounts.Catalog;
        try {
            string[] read=[Permissions.BankReceiptRead,Permissions.BankStatementRead,Permissions.ReceivingAccountRead,Permissions.ReceivingAccountDetailsRead];
            data.Reviewer=await catalog.ActorAsync(7210,CatalogAuthoringTestData.Shop,[..read,Permissions.BankReceiptConfirm]);
            data.Reader=await catalog.ActorAsync(7220,CatalogAuthoringTestData.Shop,read);
            data.Other=await catalog.ActorAsync(7230,CatalogAuthoringTestData.OtherOwner,[..read,Permissions.BankReceiptConfirm]);
            await PersonTestData.SeedDistinctPeopleAsync(catalog.Database,catalog.Clock,data.Statements.Maker.UserId,data.Reviewer.UserId,data.Other.UserId);
            data.ImportId=(await data.Statements.StageAsync(original:original))["importId"].AsInt64;
            var account=await data.Rows(ReceivingAccountBson.Versions).Find(new BsonDocument("_id",data.Statements.Selection.AccountVersionId)).SingleAsync();
            await data.Rows(B.Policies).InsertOneAsync(new BsonDocument { { "_id",920L }, { "schemaVersion",1 }, { "revision",1L }, { "scope",account["scope"] },
                { "physicalIdentity",account["physicalIdentity"] }, { "bankCode","FIXTUREBANK" }, { "identityNamespace",BankStatementParserTests.Format.IdentityNamespace! },
                { "cashMapping","bank_cash" }, { "suspenseMapping","unidentified_cash" }, { "trustedSourceReference","synthetic-independent-bank-portal" },
                { "operatingOwnerReference","synthetic-finance-owner" }, { "approvalReference","synthetic-cash-policy-approval" },
                { "approvedAt",catalog.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt",catalog.Clock.UtcNow.AddDays(1).UtcDateTime }, { "active",true }, { "fence",0L } });
            if (configureAccounting) {
                await AccountingTestDocuments.ConfigureAsync(catalog.Database);
                await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id",20L),Builders<BsonDocument>.Update.Set("mappings",new BsonArray {
                    AccountingTestDocuments.Mapping("bank_cash",801,"A1101"),AccountingTestDocuments.Mapping("unidentified_cash",802,"A2101") }));
            } else await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id",20L),Builders<BsonDocument>.Update.PushEach("mappings",new[] {
                AccountingTestDocuments.Mapping("bank_cash",805,"A1102"),AccountingTestDocuments.Mapping("unidentified_cash",806,"A2102") }));
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal Task<BsonDocument> PreviewAsync(long? importId=null)=>Service.PreviewAsync(Reviewer,importId??ImportId,2);
    internal Task<BsonDocument> ConfirmAsync(BsonDocument preview,string key="confirm",BankReceiptObservation? observation=null)=>Service.ConfirmAsync(Reviewer,preview["importId"].AsInt64,2,preview["binding"].AsString,observation??Observation(),"synthetic-independently-observed-bank-credit",true,Meta(key));
    internal async Task<BsonDocument> StateAsync() {
        var result=await Statements.StateAsync();
        foreach(var name in new[]{B.Policies,B.Accounts,B.Receipts,B.Confirmations,"accountingBooks","periods","accountingAuditEvents","accountingPeriodHistory"})
            result[name]=new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id",1)).ToListAsync());
        return result;
    }
    public ValueTask DisposeAsync()=>Statements.DisposeAsync();
}
