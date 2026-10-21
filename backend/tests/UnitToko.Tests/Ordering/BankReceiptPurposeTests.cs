using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class BankReceiptPurposeTests(MongoFixture fixture)
{
    [WindowsStorageFact(Skip = Pending.SharedWarehouseFixture)]
    public async Task Genuine_supplier_return_leaves_bank_history_readable_but_removes_credit_from_customer_picker()
    {
        await using var supplier = await ProcurementAdvanceReturnTestData.CreateAsync(fixture, receiptAmount: 4000, includeCustomerMappings: true);
        await using var customer = await BankReceiptPurposeCustomerData.AttachAsync(fixture, supplier);
        var before = await customer.Service.ReceiptCandidatesAsync(customer.Reviewer, customer.Read.DeclarationId);
        Assert.Contains(before.Items, row => row["_id"] == supplier.ReceiptId);
        var proposal = await supplier.ProposeAsync("purpose-picker-return-proposal", supplier.Input(4000));
        await supplier.DecideAsync(proposal["proposalId"].AsInt64, "purpose-picker-return-approved");
        var after = await customer.Service.ReceiptCandidatesAsync(customer.Reviewer, customer.Read.DeclarationId);
        Assert.DoesNotContain(after.Items, row => row["_id"] == supplier.ReceiptId);
        Assert.Equal(supplier.ReceiptId, (await customer.Bank.Service.ReadAsync(customer.Bank.Reader, supplier.ReceiptId))["_id"].AsInt64);
        Assert.Contains(await customer.Bank.Service.ListAsync(customer.Bank.Reader), row => row["_id"] == supplier.ReceiptId);
        Assert.Equal("bank_receipt_purpose_conflict", (await Assert.ThrowsAsync<PersistenceConflictException>(() => customer.PreviewAsync())).Code);
    }

    [WindowsStorageFact(Skip = Pending.SharedWarehouseFixture)]
    public async Task Real_customer_verification_and_supplier_return_compete_for_the_same_whole_confirmed_credit()
    {
        await using var supplier = await ProcurementAdvanceReturnTestData.CreateAsync(fixture, receiptAmount: 4000, includeCustomerMappings: true);
        await using var customer = await BankReceiptPurposeCustomerData.AttachAsync(fixture, supplier);
        var proposal = await supplier.ProposeAsync("purpose-race-return-proposal", supplier.Input(4000));
        var returnPreview = await supplier.Service.PreviewDecisionAsync(supplier.Checker, proposal["proposalId"].AsInt64, ProcurementAdvanceReturnDecision.Approve);
        var customerPreview = await customer.PreviewAsync();
        var receiptBefore = BankReceiptBson.Snapshot(await supplier.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", supplier.ReceiptId)).SingleAsync());
        var errors = await Task.WhenAll(
            Record.ExceptionAsync(() => customer.VerifyAsync(customerPreview, "purpose-race-customer")),
            Record.ExceptionAsync(() => supplier.Service.DecideAsync(supplier.Checker, proposal["proposalId"].AsInt64, 1,
                ProcurementAdvanceReturnDecision.Approve, returnPreview["binding"].AsString, ProcurementAdvanceReturnTestData.Meta("purpose-race-return"))));
        Assert.Single(errors, error => error is null);
        var conflict = Assert.IsType<PersistenceConflictException>(Assert.Single(errors, error => error is not null));
        Assert.Contains(conflict.Code, new[] { "version_conflict", "bank_receipt_purpose_conflict", "procurement_advance_return_changed", "payment_verification_changed" });
        var party = await supplier.Rows(PaymentVerificationBson.Parties).Find(new BsonDocument("_id", supplier.ReceiptId)).SingleAsync();
        var customers = await supplier.Rows(PaymentVerificationBson.Decisions).CountDocumentsAsync(new BsonDocument("receiptId", supplier.ReceiptId));
        var returns = await supplier.Rows("procurementAdvanceReturnDecisions").CountDocumentsAsync(new BsonDocument { { "receiptId", supplier.ReceiptId }, { "decision", "approve" } });
        Assert.Equal(1L, customers + returns);
        if (errors[0] is null) {
            Assert.Equal(1L, customers); Assert.Equal(1, party["schemaVersion"]);
            Assert.NotNull(await customer.Service.RecoverAsync(customer.Reviewer, "purpose-race-customer"));
            Assert.Equal("bank_receipt_purpose_conflict", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                supplier.Service.PreviewDecisionAsync(supplier.Checker, proposal["proposalId"].AsInt64, ProcurementAdvanceReturnDecision.Approve))).Code);
        } else {
            Assert.Equal(1L, returns); Assert.Equal(2, party["schemaVersion"]); Assert.Equal("supplier_advance_return", party["kind"]);
            Assert.Equal("bank_receipt_purpose_conflict", (await Assert.ThrowsAsync<PersistenceConflictException>(() => customer.PreviewAsync())).Code);
            Assert.False(await supplier.Rows("paymentReceiptBalances").Find(new BsonDocument("_id", supplier.ReceiptId)).AnyAsync());
        }
        Assert.Equal(receiptBefore, BankReceiptBson.Snapshot(await supplier.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", supplier.ReceiptId)).SingleAsync()));
    }

    [WindowsStorageFact]
    public async Task Genuine_customer_owner_survives_full_unallocation_and_missing_owner_cannot_reclassify_retained_money()
    {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        var source = data.Source; var db = source.Read.Database; var unit = new MongoUnitOfWork(source.Read.Catalog.Client);
        var owners = data.Rows(PaymentVerificationBson.Parties);
        var purpose = await owners.Find(new BsonDocument("_id", source.ReceiptId)).SingleAsync();
        Assert.Equal(1, purpose["schemaVersion"]); Assert.False(purpose.Contains("kind"));
        var immutable = PaymentVerificationBson.Snapshot(purpose);
        var receipt = await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", source.ReceiptId)).SingleAsync();
        var claimConflict = await Assert.ThrowsAsync<PersistenceConflictException>(SupplierClaimAvailableAsync);
        Assert.Equal("bank_receipt_purpose_conflict", claimConflict.Code);

        var allocationInput = data.Input(0, 1000m);
        var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, allocationInput);
        var allocation = await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, allocationInput,
            PaymentAllocationTests.Data.Meta("purpose-customer-allocate"));
        var checker = await PaymentAllocationCorrectionTests.Checker(data);
        var correctionInput = new PaymentAllocationInput(1, [new(new(data.InvoiceId), 1, new(1000m))]);
        var correctionPreview = await data.Service.PreviewCorrectionAsync(checker, allocation["allocationId"].AsInt64, correctionInput);
        await data.Service.CorrectAsync(checker, allocation["allocationId"].AsInt64, correctionPreview.Binding, correctionInput,
            PaymentAllocationTests.Data.Meta("purpose-customer-full-unallocation"));
        var current = await data.Service.ReadAsync(source.Reader, data.VerificationId, [data.InvoiceId]);
        Assert.Equal(0m, current.ReceiptBalance["allocated"].AsDecimal);
        Assert.Equal(immutable, PaymentVerificationBson.Snapshot(await owners.Find(new BsonDocument("_id", source.ReceiptId)).SingleAsync()));
        Assert.Equal("bank_receipt_purpose_conflict", (await Assert.ThrowsAsync<PersistenceConflictException>(SupplierClaimAvailableAsync)).Code);

        // Deliberate corruption probes retain genuine effects; they are not positive
        // setup data and never manufacture a replacement purpose or financial owner.
        var actualPurpose = await owners.Find(new BsonDocument("_id", source.ReceiptId)).SingleAsync();
        await owners.DeleteOneAsync(new BsonDocument("_id", source.ReceiptId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(SupplierClaimAvailableAsync);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => source.Service.ReadAsync(source.Reader, source.Read.DeclarationId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(source.Reader, data.VerificationId, [data.InvoiceId]));
        await owners.InsertOneAsync(actualPurpose);
        Assert.Equal(immutable, PaymentVerificationBson.Snapshot(await owners.Find(new BsonDocument("_id", source.ReceiptId)).SingleAsync()));
        Assert.Equal(allocation, await data.Service.RecoverAsync(data.Actor, "purpose-customer-allocate"));

        Task SupplierClaimAvailableAsync() => unit.ExecuteAsync(async (tx, ct) => {
            await new PaymentReceiptPurposeRecords(db).RequireUnclaimedForSupplierAsync(tx, receipt, ct); return true;
        });
    }
}
