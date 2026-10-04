using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Accounting;
using D = UnitToko.Tests.Procurement.ProcurementAdvanceReturnTestData;

namespace UnitToko.Tests.Procurement;

// Alternate financial sources are created through the same real owners. Only
// verification/Finance policies are explicit synthetic local test inputs.
[SupportedOSPlatform("windows")]
internal static class ProcurementAdvanceReturnAlternateSources
{
    internal static async Task<BsonDocument> AdvanceAsync(D data, string key, bool differentSupplier)
    {
        var payables = data.Payables; var orders = payables.Receiving.Orders; var suppliers = orders.Suppliers;
        var procurement = orders.Procurement; var funding = payables.Funding!; var banks = payables.Beneficiaries!;
        var supplier = orders.Supplier; var mapping = suppliers.Mapping; var details = SupplierBankTestData.Details;
        var firstDraft = await data.Rows(ProcurementDisbursementBson.Drafts).Find(new BsonDocument("_id", payables.Recorded["draftId"])).SingleAsync();
        var disbursementInput = ProcurementDisbursementBson.Input(firstDraft["input"]);
        if (differentSupplier) {
            var identity = SupplierTestData.Details with { SubjectReference = "SYNTHETIC-ALTERNATE-SUPPLIER", LegalName = "PT Synthetic Alternate Supplier",
                DisplayName = "Synthetic alternate supplier", RegistrationValue = "000002" };
            var policyId = (await new IdAllocator(data.Database).NextAsync(SupplierTestData.Policies)).Value;
            await suppliers.Rows(SupplierTestData.Policies).InsertOneAsync(suppliers.Policy(policyId, identity));
            mapping = mapping with { SourceAccountReference = "000002", ExternalItemCode = "000022" };
            var supplierInput = suppliers.Input with { PolicyId = policyId, Details = identity, Mappings = [mapping] };
            var proposal = await suppliers.ProposeAsync(key + "-supplier", supplierInput);
            var observed = await suppliers.Service.ObserveAsync(suppliers.Checker, proposal["proposalId"].AsInt64, 1, identity,
                policyId, 1, SupplierTestData.TrustedChannel, "SYNTHETIC-ALTERNATE-INDEPENDENT-IDENTITY", true, SupplierTestData.Meta(key + "-supplier-observed"));
            supplier = await suppliers.DecideAsync(proposal, key + "-supplier-approved", observed["recordId"].AsInt64);

            details = new ReceivingAccountDetails("BNI", "000098765432", identity.LegalName, "IDR");
            var bankPolicyId = (await new IdAllocator(data.Database).NextAsync(SupplierBankTestData.Policies)).Value;
            var bankPolicy = banks.Policy(); bankPolicy["_id"] = bankPolicyId;
            bankPolicy["supplierId"] = supplier["supplierId"]; bankPolicy["supplierVersionId"] = supplier["versionId"];
            bankPolicy["supplierVersion"] = supplier["version"]; bankPolicy["subjectReference"] = identity.SubjectReference;
            await data.Rows(SupplierBankTestData.Policies).InsertOneAsync(bankPolicy);
            var bankInput = banks.Input with { SupplierId = supplier["supplierId"].AsInt64, SupplierVersionId = supplier["versionId"].AsInt64,
                SupplierVersion = supplier["version"].AsInt64, PolicyId = bankPolicyId, Details = details };
            var bankProposal = await banks.ProposeAsync(key + "-beneficiary", bankInput);
            var bankObservation = await banks.ObserveAsync(bankProposal, key + "-beneficiary-observed", details);
            var beneficiary = await banks.DecideAsync(bankProposal, key + "-beneficiary-approved", bankObservation["recordId"].AsInt64);
            disbursementInput = disbursementInput with { SupplierId = supplier["supplierId"].AsInt64,
                BeneficiaryVersionId = beneficiary["versionId"].AsInt64, BeneficiaryVersion = beneficiary["version"].AsInt64 };
        }
        var day = AccountingCalendar.BusinessDate(data.Clock.UtcNow);
        var needInput = procurement.Input(ProcurementRoute.Ddo, ProcurementRequestKind.PurchaseRequest) with {
            BusinessDate = day, Lines = [ProcurementRequestTestData.Line(procurement.Sku, 3)] };
        var need = await procurement.SubmitAsync(key + "-need", needInput);
        var needed = await procurement.DecideAsync(need, key + "-need-approved");
        var poInput = orders.Input() with { RequestId = need["requestId"].AsInt64, RequestDecisionId = needed["decisionId"].AsInt64,
            SupplierId = supplier["supplierId"].AsInt64, SupplierVersionId = supplier["versionId"].AsInt64, SupplierVersion = supplier["version"].AsInt64,
            BusinessDate = day, CommercialReference = new("quote", "SYNTHETIC-QUOTE-" + key, day, data.Clock.UtcNow.AddDays(1)),
            PaymentTerms = new("deposit", "Synthetic explicit deposit for another genuine purchase order", new(6000), "on_receipt", null),
            Lines = [orders.Line() with { SourceAccountReference = mapping.SourceAccountReference, ExternalItemCode = mapping.ExternalItemCode }] };
        var poProposal = await orders.ProposeAsync(key + "-order", poInput); var order = await orders.DecideAsync(poProposal, key + "-order-approved");
        var fundingInput = funding.Input(6000) with { OrderId = order["orderId"].AsInt64, OrderApprovalId = order["decisionId"].AsInt64 };
        var requested = await funding.RequestAsync(key + "-funding", fundingInput); var authorized = await funding.DecideAsync(requested, key + "-funding-approved");
        disbursementInput = disbursementInput with { FundingRequestId = requested["requestId"].AsInt64,
            FundingDecisionId = authorized["decisionId"].AsInt64, AuthorizationId = authorized["authorizationId"].AsInt64 };
        var settlement = await payables.CreateAdditionalVerifiedAdvanceAsync(key, 6000, disbursementInput, details);
        var read = await payables.Service.AdvanceAsync(payables.Reader, settlement["id"].AsInt64);
        Assert.Equal(order["orderId"], read["advance"]["orderId"]); Assert.Equal(supplier["supplierId"], read["advance"]["supplierId"]);
        Assert.Equal("6000.00", read["capacity"]["remainingAmount"]); return read;
    }

    internal static async Task<BsonDocument> ForeignBookPolicyAsync(D data)
    {
        var valuation = data.Payables.Valuation; var foreignShop = data.Payables.Receiving.Procurement.ForeignShopId;
        var scope = AccountingBookSetupTestData.Scope with { ShopId = foreignShop };
        await data.Base.AddGrantAsync(valuation.BookMaker, 73601, scope, [Permissions.AccountingBookRead, Permissions.AccountingBookPropose]);
        await data.Base.AddGrantAsync(valuation.BookChecker, 73701, scope, [Permissions.AccountingBookRead, Permissions.AccountingBookApprove]);
        var service = new AccountingBookSetupService(data.Database, data.Unit, new(data.Database), data.Clock);
        var input = new AccountingBookSetupInput(new(100, foreignShop, 103, "IDR"),
            AccountingCalendar.BusinessDate(data.Clock.UtcNow).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            [new("unidentified_cash", "L2900"), new("supplier_advance", "A1201")]);
        var preview = await service.PreviewAsync(valuation.BookMaker, input);
        var proposed = await service.ProposeAsync(valuation.BookMaker, input, preview["binding"].AsString, D.Meta("return-foreign-book"));
        var review = await service.PreviewDecisionAsync(valuation.BookChecker, proposed["proposalId"].AsInt64, AccountingBookSetupDecision.Approve);
        var approved = await service.DecideAsync(valuation.BookChecker, proposed["proposalId"].AsInt64, 1,
            AccountingBookSetupDecision.Approve, review["binding"].AsString, D.Meta("return-foreign-book-approved"));
        Assert.NotEqual(valuation.Book["bookId"], approved["bookId"]);
        var chart = await data.Rows("accountingChartVersions").Find(new BsonDocument("_id", approved["chartId"])).SingleAsync();
        var policy = data.Policy.DeepClone().AsBsonDocument; policy["_id"] = (await new IdAllocator(data.Database).NextAsync("procurementAdvanceReturnPolicies")).Value;
        policy["bookId"] = approved["bookId"]; policy["chartVersionId"] = approved["chartId"]; policy["fence"] = 0L;
        policy["approvalReference"] = "SYNTHETIC-INTENTIONALLY-FOREIGN-BOOK-POLICY";
        foreach (var (role, key) in new[] { ("suspense", "unidentified_cash"), ("supplierAdvance", "supplier_advance") }) {
            var account = Assert.Single(chart["mappings"].AsBsonArray, row => row["mappingKey"] == key);
            policy[role + "AccountId"] = account["accountId"]; policy[role + "AccountCode"] = account["accountCode"];
        }
        await data.Rows("procurementAdvanceReturnPolicies").InsertOneAsync(policy); return policy;
    }
}
