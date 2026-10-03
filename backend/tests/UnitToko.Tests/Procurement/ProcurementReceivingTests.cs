using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using D = UnitToko.Tests.Procurement.ProcurementReceivingTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementReceivingTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(ProcurementRoute.OmiRegular, false)]
    [InlineData(ProcurementRoute.Ddo, false)]
    [InlineData(ProcurementRoute.Smart, true)]
    [InlineData(ProcurementRoute.PasarKering, true)]
    public async Task Actual_zero_advance_order_records_separate_partial_container_custody_without_item_stock_or_money(ProcurementRoute route, bool onReceipt)
    {
        await using var data = await D.CreateAsync(fixture, route, onReceipt);
        var originalOrder = await data.Orders.StateAsync(); var notice = await data.NoticeAsync(); var id = notice["noticeId"].AsInt64;
        Assert.Equal("notice_recorded", notice["state"].AsString);
        Assert.Equal(notice, await data.Service.LookupAsync(data.Maker, D.NoticeOperation, "receiving-notice"));
        var first = await data.IntakeAsync(id);
        Assert.Equal("pending_inspection", first["state"].AsString);
        Assert.Equal(first, await data.Service.LookupAsync(data.Receiver, D.IntakeOperation, "receiving-intake"));
        var view = await data.Service.ReadAsync(data.Reader, id); var capacity = view["capacity"];
        Assert.Equal(3, capacity["matchedContainers"].AsInt32); Assert.Equal(2, capacity["remainingContainers"].AsInt32);
        Assert.Equal(1L, capacity["excessContainers"].AsInt64); Assert.Equal(1L, capacity["heldContainers"].AsInt64); Assert.Equal(4L, capacity["receivedContainers"].AsInt64);
        var secondInput = data.IntakeInput("SYNTHETIC-CONTAINER-INTAKE-002") with { MatchedContainers = 2, ExcessContainers = 0, HeldContainers = 0, Condition = "intact", Discrepancy = null };
        await data.IntakeAsync(id, 1, "receiving-intake-second", secondInput);
        var full = await data.Service.ReadAsync(data.Reader, id);
        Assert.Equal(0, full["capacity"]["remainingContainers"].AsInt32); Assert.Equal(6L, full["capacity"]["receivedContainers"].AsInt64);
        Assert.All(full["receipts"].AsBsonArray, row => Assert.Equal("pending_inspection", row["state"].AsString));
        Assert.Equal(2, full["receipts"].AsBsonArray.Count);
        Assert.Equal(originalOrder, await data.Orders.StateAsync());
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.NoticeOperation)));
            Assert.Equal(2, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.IntakeOperation)));
        }
    }

    [Fact]
    public async Task Scoped_notice_and_custody_authority_are_separate_and_require_current_factor_and_grant()
    {
        await using var data = await D.CreateAsync(fixture); var notice = await data.NoticeAsync(); var id = notice["noticeId"].AsInt64;
        var before = await data.StateAsync();
        foreach (var actor in new[] { data.Reader, data.Receiver, data.Outsider })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewNoticeAsync(actor, data.NoticeInput()));
        foreach (var actor in new[] { data.Reader, data.Maker, data.Outsider })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewIntakeAsync(actor, id, 0, data.IntakeInput()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, id));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewNoticeAsync(
            data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, data.NoticeInput()))).Code);
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewIntakeAsync(
            data.Receiver with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, id, 0, data.IntakeInput()))).Code);
        var preview = await data.Service.PreviewIntakeAsync(data.Receiver, id, 0, data.IntakeInput());
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Receiver.UserId),
            Builders<BsonDocument>.Update.Pull("permissions", Permissions.ProcurementContainerReceive));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReceiveAsync(data.Receiver, id, 0, data.IntakeInput(), preview["binding"].AsString, D.Meta("revoked-intake")));
        Assert.Equal(before, await data.StateAsync()); await data.Orders.NoFinancialOrReceiptEffectsAsync();
        // Observed custody does not invent an independent-checker rule. The notice
        // recorder may also receive containers when that action is explicitly granted.
        await data.Base.AddGrantAsync(data.Maker, 889991, ProcurementRequestTestData.Scope, [Permissions.ProcurementContainerReceive]);
        var combined = await data.Service.PreviewIntakeAsync(data.Maker, id, 0, data.IntakeInput());
        Assert.Equal("pending_inspection", (await data.Service.ReceiveAsync(data.Maker, id, 0, data.IntakeInput(),
            combined["binding"].AsString, D.Meta("explicitly-granted-same-custodian")))["state"].AsString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Racing_intakes_and_same_key_retries_consume_the_container_head_once(bool sameKey)
    {
        await using var data = await D.CreateAsync(fixture); var notice = await data.NoticeAsync(); var id = notice["noticeId"].AsInt64;
        var input = data.IntakeInput(); var preview = await data.Service.PreviewIntakeAsync(data.Receiver, id, 0, input);
        async Task<(BsonDocument? Result, Exception? Error)> Attempt(string key) {
            try { return (await data.Service.ReceiveAsync(data.Receiver, id, 0, input, preview["binding"].AsString, D.Meta(key)), null); }
            catch (Exception error) { return (null, error); }
        }
        var outcomes = await Task.WhenAll(Attempt("intake-race"), Attempt(sameKey ? "intake-race" : "intake-other-key"));
        Assert.Contains(outcomes, outcome => outcome.Error is null);
        if (sameKey) { Assert.All(outcomes, outcome => Assert.Null(outcome.Error)); Assert.Equal(outcomes[0].Result, outcomes[1].Result); }
        else {
            Assert.Single(outcomes, outcome => outcome.Error is null);
            Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, outcome => outcome.Error is not null).Error);
        }
        Assert.Equal(1, await data.Rows(D.Intakes).CountDocumentsAsync(new BsonDocument()));
        var view = await data.Service.ReadAsync(data.Reader, id); Assert.Equal(2, view["capacity"]["remainingContainers"].AsInt32);
        Assert.Equal(1L, view["capacity"]["version"].AsInt64);
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Missing_recorded_intake_cannot_reopen_capacity_or_recover_a_fabricated_result()
    {
        await using var data = await D.CreateAsync(fixture); var notice = await data.NoticeAsync(); var id = notice["noticeId"].AsInt64;
        var result = await data.IntakeAsync(id); var filter = new BsonDocument("_id", result["receiptId"]);
        var actual = await data.Rows(D.Intakes).Find(filter).SingleAsync(); await data.Rows(D.Intakes).DeleteOneAsync(filter);
        try {
            var damaged = await data.StateAsync();
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Reader, id));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Receiver, D.IntakeOperation, "receiving-intake"));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewIntakeAsync(data.Receiver, id, 1, data.IntakeInput("new-after-loss")));
            Assert.Equal(damaged, await data.StateAsync()); await data.Orders.NoFinancialOrReceiptEffectsAsync();
        } finally { await data.Rows(D.Intakes).InsertOneAsync(actual); }
        Assert.Equal(result, await data.Service.LookupAsync(data.Receiver, D.IntakeOperation, "receiving-intake"));
    }

    [Fact]
    public async Task Expired_original_quote_does_not_rewrite_or_block_the_actual_approved_zero_advance_order()
    {
        await using var data = await D.CreateAsync(fixture);
        var original = await data.Orders.StateAsync(); data.Clock.UtcNow = data.Clock.UtcNow.AddDays(2);
        data.Maker = data.Maker with { AuthenticatedAt = data.Clock.UtcNow };
        data.Receiver = data.Receiver with { AuthenticatedAt = data.Clock.UtcNow };
        var notice = await data.NoticeAsync(); var intake = await data.IntakeAsync(notice["noticeId"].AsInt64);
        Assert.Equal("pending_inspection", intake["state"].AsString);
        Assert.Equal(original, await data.Orders.StateAsync()); await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Rejected_notice_or_intake_outbox_rolls_back_the_owner_head_and_effects_before_original_key_retry()
    {
        await using var data = await D.CreateAsync(fixture);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var original = definition["options"].AsBsonDocument.GetValue("validator", new BsonDocument()).DeepClone().AsBsonDocument;
        async Task GuardAsync(BsonDocument validator) => await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", "outboxEvents" }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        async Task RejectAsync(string operation, Func<Task<BsonDocument>> command) {
            var before = await data.StateAsync();
            try {
                await GuardAsync(new BsonDocument("$and", new BsonArray { original, new BsonDocument("kind", new BsonDocument("$ne", operation)) }));
                Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(command)).WriteError.Code);
                Assert.Equal(before, await data.StateAsync()); await data.Orders.NoFinancialOrReceiptEffectsAsync();
            } finally { await GuardAsync(original); }
        }
        var noticeInput = data.NoticeInput(); var noticePreview = await data.Service.PreviewNoticeAsync(data.Maker, noticeInput);
        Task<BsonDocument> Notice() => data.Service.RecordNoticeAsync(data.Maker, noticeInput, noticePreview["binding"].AsString, D.Meta("notice-rollback"));
        await RejectAsync(D.NoticeOperation, Notice);
        Assert.Null(await data.Service.LookupAsync(data.Maker, D.NoticeOperation, "notice-rollback"));
        var notice = await Notice(); Assert.Equal(notice, await Notice()); var id = notice["noticeId"].AsInt64;
        var input = data.IntakeInput(); var preview = await data.Service.PreviewIntakeAsync(data.Receiver, id, 0, input);
        Task<BsonDocument> Intake() => data.Service.ReceiveAsync(data.Receiver, id, 0, input, preview["binding"].AsString, D.Meta("intake-rollback"));
        await RejectAsync(D.IntakeOperation, Intake);
        Assert.Null(await data.Service.LookupAsync(data.Receiver, D.IntakeOperation, "intake-rollback"));
        var saved = await Intake(); Assert.Equal(saved, await Intake());
        Assert.Equal(saved, await data.Service.LookupAsync(data.Receiver, D.IntakeOperation, "intake-rollback"));
        Assert.Equal(1, await data.Rows(D.Notices).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(D.Intakes).CountDocumentsAsync(new BsonDocument()));
    }
}
