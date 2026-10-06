using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementStockValuationSources(BsonDocument Receipt, BsonDocument Invoice, BsonDocument Match,
    BsonDocument MatchLine, BsonDocument Policy, BsonDocument Accounting, ProcurementStockValuationCapacity Capacity,
    SupplierDocumentHistory InvoiceHistory, BsonDocument? Quality, BsonDocument? QualityCapacity,
    BsonDocument? QualityHead);

internal sealed class ProcurementStockValuationProposalProof(IClientSessionHandle tx, BsonDocument proposal,
    ProcurementStockValuationSources source)
{
    private readonly IClientSessionHandle transaction = tx;
    private readonly long transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly BsonDocument proposalBytes = proposal.DeepClone().AsBsonDocument;
    private readonly BsonDocument receiptBytes = source.Receipt.DeepClone().AsBsonDocument;
    private readonly BsonDocument invoiceBytes = source.Invoice.DeepClone().AsBsonDocument;
    private readonly BsonDocument matchBytes = source.Match.DeepClone().AsBsonDocument;
    private readonly BsonDocument invoiceHeadBytes = source.InvoiceHistory.Head.DeepClone().AsBsonDocument;
    private readonly BsonDocument invoiceIdentityBytes = source.InvoiceHistory.Identity.DeepClone().AsBsonDocument;
    internal BsonDocument Proposal { get; } = proposal;
    internal ProcurementStockValuationSources Source { get; } = source;

    internal void Require(IClientSessionHandle current, ProcurementStockValuationInput input, BsonDocument? retainedProposal) {
        if (!ReferenceEquals(transaction, current) || !current.IsInTransaction ||
            current.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber ||
            Proposal != proposalBytes || Source.Receipt != receiptBytes || Source.Invoice != invoiceBytes || Source.Match != matchBytes ||
            Source.InvoiceHistory.Head != invoiceHeadBytes || Source.InvoiceHistory.Identity != invoiceIdentityBytes ||
            Proposal["input"] != ProcurementStockValuationBson.Input(input) ||
            retainedProposal is not null && retainedProposal != proposalBytes) throw ProcurementStockValuationBson.Broken();
    }
}

internal sealed class ProcurementStockValuationRecords(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
    CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources, ProcurementQualityProofGraph? proofGraph = null)
{
    private IMongoCollection<BsonDocument> Rows(string table) => database.GetCollection<BsonDocument>(table);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, ProcurementStockValuationInput input, DateTimeOffset now, CancellationToken ct) {
        var row = await Rows(B.Policies).Find(tx, new BsonDocument { { "_id", input.PolicyId }, { "$and", new BsonArray { ProcurementStockValuationSchemas.Validator(B.Policies) } } }).SingleOrDefaultAsync(ct)
            ?? throw B.Changed("procurement_stock_valuation_policy_unavailable");
        if (row["revision"] != input.PolicyRevision || !row["active"].AsBoolean || row["location"] != R.Location(input.Location) ||
            row["currency"] != "IDR" || row["sourceRecognition"] != "receipt_accrual" || row["approvedAt"].ToUniversalTime() > now.UtcDateTime ||
            row["expiresAt"].ToUniversalTime() <= now.UtcDateTime || row["proposedBy"] == row["approvedBy"] ||
            row["people"].AsBsonArray.Count != 2 || row["people"].AsBsonArray.Select(R.Person).Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0) ||
            row["people"].AsBsonArray.Select(R.Person).Select(person => person.UserId).Order().SequenceEqual(new[] { row["proposedBy"].AsInt64, row["approvedBy"].AsInt64 }.Order()) == false)
            throw B.Changed("procurement_stock_valuation_policy_unavailable");
        var people = row["people"].AsBsonArray.Select(R.Person).ToDictionary(person => person.UserId);
        if (!PersonIndependencePolicy.AreIndependent(people[row["proposedBy"].AsInt64], people[row["approvedBy"].AsInt64])) throw B.Changed("procurement_stock_valuation_policy_unavailable");
        foreach (var field in new[] { "operatingOwnerReference", "approvalReference" }) ProcurementStockReceiptRules.Text(row[field].AsString, 256);
        if (row["inventoryMapping"] == row["receiptAccrualMapping"] || row["inventoryAccountId"] == row["receiptAccrualAccountId"]) throw B.Changed("procurement_stock_valuation_policy_unavailable");
        if (!row["capitalizeTax"].AsBoolean && input.TaxAmount.Value > 0 || !row["capitalizeCharge"].AsBoolean && input.ChargeAmount.Value > 0)
            throw ProcurementStockValuationRules.Invalid("procurement_stock_valuation_noncapitalized_component");
        var capitalized = Money.Add(Money.Add(input.NetAmount, input.TaxAmount), input.ChargeAmount);
        if (capitalized != input.CapitalizedAmount) throw ProcurementStockValuationRules.Invalid("procurement_stock_valuation_amount_mismatch");
        return row;
    }
    internal async Task<BsonDocument> AccountingAsync(IClientSessionHandle tx, ProcurementStockValuationInput input, BsonDocument policy, CancellationToken ct) {
        var persistence = new AccountingPersistence(database, clock); var location = input.Location;
        var approved = await new AccountingBookSetupRecords(database).ApprovedAsync(tx,
            new(location.OrganizationId, location.ShopId, location.ChannelId, "IDR"), ct)
            ?? throw B.Changed("procurement_stock_valuation_accounting_changed");
        var book = await persistence.BookAsync(tx, new(location.OrganizationId, location.ShopId, location.ChannelId, "IDR"), ct);
        Equal(approved["key"], book["key"]);
        Equal(book["_id"], policy["bookId"]); Equal(book["chartVersionId"], policy["chartVersionId"]);
        var chart = await Rows("accountingChartVersions").Find(tx, new BsonDocument { { "_id", book["chartVersionId"] }, { "bookId", book["_id"] }, { "active", true } }).SingleOrDefaultAsync(ct)
            ?? throw B.Changed("procurement_stock_valuation_accounting_changed");
        var snapshot = AccountingBson.ReadChart(chart); var month = AccountingCalendar.Month(input.PostingDate);
        var period = await persistence.PeriodAsync(tx, book["_id"].AsInt64, month, ct); AccountingPersistence.RequireOpen(book, period, month);
        BsonDocument Account(string role) {
            var mapping = snapshot.Mappings.SingleOrDefault(value => value.MappingKey == policy[role + "Mapping"].AsString && value.Active)
                ?? throw B.Changed("procurement_stock_valuation_accounting_changed");
            if (mapping.AccountId != policy[role + "AccountId"].AsInt64 || mapping.AccountCode != policy[role + "AccountCode"].AsString)
                throw B.Changed("procurement_stock_valuation_accounting_changed");
            return new() { { "mappingKey", mapping.MappingKey }, { "accountId", mapping.AccountId }, { "accountCode", mapping.AccountCode } };
        }
        var inventory = Account("inventory"); var accrual = Account("receiptAccrual");
        if (inventory["accountId"] == accrual["accountId"] || inventory["mappingKey"] == accrual["mappingKey"]) throw B.Changed("procurement_stock_valuation_accounting_changed");
        // Delete and reinsert the exact immutable chart in this transaction: a competing
        // removal or replacement conflicts even when setting active=true would be a no-op.
        if ((await Rows("accountingChartVersions").DeleteOneAsync(tx, chart, cancellationToken: ct)).DeletedCount != 1)
            throw B.Changed("procurement_stock_valuation_accounting_changed");
        await Rows("accountingChartVersions").InsertOneAsync(tx, chart, cancellationToken: ct);
        await persistence.TouchBookAsync(tx, book, ct);
        return new() { { "bookId", book["_id"] }, { "chartVersionId", chart["_id"] }, { "periodId", period["_id"] },
            { "periodMonth", month }, { "inventory", inventory }, { "receiptAccrual", accrual } };
    }
    internal async Task<ProcurementStockValuationSources> SourcesAsync(IClientSessionHandle tx, ProcurementStockValuationInput input, CancellationToken ct,
        BsonDocument? retainedProposal = null, ProcurementStockValuationProposalProof? verifiedProposal = null) {
        verifiedProposal?.Require(tx, input, retainedProposal);
        var receipt = verifiedProposal is null
            ? await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources, proofGraph).ReadAsync(tx, input.ReceiptId, ct)
            : verifiedProposal.Source.Receipt;
        if (verifiedProposal is not null) {
            var currentReceipt = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument("_id", input.ReceiptId)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(currentReceipt, receipt);
        }
        Equal(receipt["location"], R.Location(input.Location)); Equal(receipt["revision"], input.ReceiptRevision);
        if (receipt["valuationState"] != "pending" || !receipt["valuation"].IsBsonNull || receipt["state"] != ProcurementStockReceiptBson.State)
            throw B.Changed("procurement_stock_valuation_receipt_unavailable");
        var capacityReader = new ProcurementStockValuationCapacityReader(database, unit, clock, catalog, orders, proofGraph);
        var invoice = verifiedProposal is null
            ? (await Rows(SupplierDocumentBson.Submissions).Find(tx, new BsonDocument { { "_id", input.InvoiceDocumentId },
                { "$and", new BsonArray { ProcurementQualityValuationSchemas.CurrentDocumentValidator(SupplierDocumentBson.Submissions) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken())
            : verifiedProposal.Source.Invoice;
        if (verifiedProposal is not null) {
            var currentInvoice = await Rows(SupplierDocumentBson.Submissions).Find(tx, new BsonDocument("_id", input.InvoiceDocumentId)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(currentInvoice, invoice);
        }
        Equal(invoice["location"], receipt["location"]); Equal(invoice["supplierId"], receipt["supplierId"]);
        if (invoice["input"]["kind"] != "invoice" || invoice["input"]["currency"] != "IDR" || input.InvoiceDocumentRevision != 1)
            throw B.Changed("procurement_stock_valuation_invoice_unavailable");
        // A complete HistoryAsync authenticates the submission, private original, all matches,
        // capacity head and effects. Share that exact proof with this transaction's allocation reader.
        var history = verifiedProposal is null
            ? await capacityReader.InvoiceHistoryAsync(tx, invoice, ct)
            : capacityReader.VerifiedProposalHistory(tx, verifiedProposal, input, retainedProposal, invoice);
        if (history.Identity["draftCount"].AsInt64 != 1 || history.Document is null || history.Document != invoice)
            throw B.Changed("procurement_stock_valuation_invoice_ambiguous");
        var match = history.Matches.SingleOrDefault(row => row["_id"] == input.InvoiceMatchId) ?? throw B.Changed("procurement_stock_valuation_match_unavailable");
        // HistoryAsync has already authenticated every match against this exact private invoice,
        // its inspected source, ordered capacity head, and retained operation effects.
        var currentMatch = await Rows(SupplierDocumentBson.Matches).Find(tx, new BsonDocument {
            { "_id", input.InvoiceMatchId },
            { "$and", new BsonArray { ProcurementQualityValuationSchemas.CurrentDocumentValidator(SupplierDocumentBson.Matches) } }
        }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(currentMatch, match);
        if (input.MatchLineNumber > match["lines"].AsBsonArray.Count) throw B.Changed("procurement_stock_valuation_match_unavailable");
        var line = match["lines"][input.MatchLineNumber - 1].AsBsonDocument;
        var link = match["links"][input.MatchLineNumber - 1];
        if (line["state"] != "matched" || link.IsBsonNull || link["kind"] != "inspection" ||
            line["inspectionId"] != receipt["inspectionId"] || line["inspectionLineNumber"] != receipt["inspectionLineNumber"] ||
            link["id"] != receipt["inspectionId"] || link["lineNumber"] != receipt["inspectionLineNumber"] ||
            link["hash"] != receipt["inspection"]["snapshotHash"] || link["orderId"] != receipt["orderId"] ||
            link["orderHash"] != receipt["source"]["approval"]["snapshotHash"] || line["packages"].AsInt32 < input.Packages ||
            line["order"]["id"] != receipt["orderId"] || line["inspectionLine"] != receipt["inspection"]["input"]["lines"][receipt["inspectionLineNumber"].AsInt32 - 1])
            throw B.Changed("procurement_stock_valuation_match_unavailable");
        var observed = receipt["inspection"]["input"]["lines"][receipt["inspectionLineNumber"].AsInt32 - 1];
        if (link["requestSkuId"] != observed["requestSkuId"] || line["inspectionLine"]["definitionId"] != observed["definitionId"])
            throw B.Changed("procurement_stock_valuation_match_unavailable");
        BsonDocument? quality = null, qualityCapacity = null, qualityHead = null;
        if (line.TryGetValue("quality", out var value)) {
            quality = value.AsBsonDocument;
            if (!line.TryGetValue("qualityAllocationId", out var qualityId) || quality["allocationId"] != qualityId ||
                !link.AsBsonDocument.TryGetValue("quality", out var linked) || linked != quality ||
                quality["receiptId"] != receipt["_id"] || quality["receiptHash"] != receipt["snapshotHash"] ||
                quality["acceptedPackages"].AsInt32 < input.Packages) throw B.Changed("procurement_stock_valuation_quality_unavailable");
            var accepted = await new ProcurementQualityAcceptanceCapacityReader(database, sources).ReadAsync(tx, receipt, ct);
            if (!accepted.Allocations.Any(allocation => allocation["_id"] == qualityId && allocation["snapshotHash"] == quality["allocationHash"]))
                throw B.Changed("procurement_stock_valuation_quality_unavailable");
            qualityCapacity = accepted.State; qualityHead = accepted.Head;
        } else if (observed["damagedPackages"].AsInt32 > 0 || observed["expiredPackages"].AsInt32 > 0 || observed["openedPackages"].AsInt32 > 0 ||
            !observed["discrepancy"].IsBsonNull || observed["declaredPackages"] != observed["receivedPackages"])
            throw B.Changed("procurement_stock_valuation_match_unavailable");
        var quote = receipt["source"]["proposal"]["input"]["lines"].AsBsonArray.SingleOrDefault(value => value["requestSkuId"] == observed["requestSkuId"])
            ?? throw B.Broken();
        var documentLineNumber = line["documentLineNumber"].AsInt32;
        var reviewedLines = match["input"]["reviewedInput"]["lines"].AsBsonArray;
        if (documentLineNumber <= 0 || documentLineNumber > reviewedLines.Count) throw B.Broken();
        var reviewed = reviewedLines[documentLineNumber - 1];
        if (quote["definitionId"] != observed["definitionId"] || link["requestSkuId"] != quote["requestSkuId"] ||
            reviewed["netPricePerPackage"].IsBsonNull ||
            Money.Multiply(Money.Parse(reviewed["netPricePerPackage"].AsString), line["packages"].AsInt32) != Money.Parse(line["netAmount"].AsString) ||
            input.NetAmount != Money.Multiply(Money.Parse(reviewed["netPricePerPackage"].AsString), input.Packages))
            throw B.Changed("procurement_stock_valuation_amount_mismatch");
        if (input.PostingDate < AccountingCalendar.BusinessDate(new DateTimeOffset(receipt["recordedAt"].ToUniversalTime())) ||
            input.PostingDate < AccountingCalendar.BusinessDate(new DateTimeOffset(match["recordedAt"].ToUniversalTime())) ||
            input.PostingDate > AccountingCalendar.BusinessDate(clock.UtcNow)) throw B.Changed("procurement_stock_valuation_posting_date_invalid");
        var policy = retainedProposal is null ? await PolicyAsync(tx, input, clock.UtcNow, ct) : retainedProposal["policy"].AsBsonDocument;
        var accounting = retainedProposal is null ? await AccountingAsync(tx, input, policy, ct) : retainedProposal["accounting"].AsBsonDocument;
        var capacity = await capacityReader.ReadAsync(tx, receipt, match, input.MatchLineNumber, ct);
        // The advance, supplier debt and physical-disposition owners are deliberately untouched.
        if (retainedProposal is null) {
            _ = ProcurementStockValuationCapacityReader.AfterReceipt(capacity.Receipt, input.Packages, 1, "preview");
            _ = ProcurementStockValuationCapacityReader.AfterMatch(capacity.Match, input.Packages, input.NetAmount, input.TaxAmount, input.ChargeAmount, 1, "preview");
            if (quality is not null && checked(capacity.Receipt["allocatedPackages"].AsInt64 + input.Packages) > qualityCapacity!["acceptedPackages"].AsInt64)
                throw B.Changed("procurement_stock_valuation_quality_capacity_exceeded");
        }
        return new(receipt, invoice, match, line, policy, accounting, capacity, history, quality, qualityCapacity, qualityHead);
    }
    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) =>
        (await ProposalWithSourcesAsync(tx, id, ct)).Proposal;
    internal async Task<ProcurementStockValuationProposalProof> VerifiedProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var (proposal, source) = await ProposalWithSourcesAsync(tx, id, ct);
        return new(tx, proposal, source);
    }
    internal async Task<(BsonDocument Proposal, ProcurementStockValuationSources Sources)> ProposalWithSourcesAsync(
        IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Proposals).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { ProcurementQualityValuationSchemas.CurrentValuationValidator(B.Proposals) } } }).SingleOrDefaultAsync(ct)
            ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["proposalId"], id); Equal(row["version"], 1L);
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Equal(row["location"], R.Location(input.Location));
        Equal(row["policy"]["_id"], input.PolicyId); Equal(row["policy"]["revision"], input.PolicyRevision);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input,
            new BsonDocument("snapshotHash", row["receiptHash"]), new BsonDocument("snapshotHash", row["invoiceHash"]),
            new BsonDocument("snapshotHash", row["matchHash"]), row["policy"].AsBsonDocument, row["accounting"].AsBsonDocument,
            row["receiptCapacity"].AsBsonDocument, row["matchCapacity"].AsBsonDocument,
            row.TryGetValue("quality", out var quality) ? quality.AsBsonDocument : null,
            row.TryGetValue("qualityCapacity", out var qualityCap) ? qualityCap.AsBsonDocument : null));
        var people = row["people"].AsBsonArray.Select(R.Person).ToArray();
        if (people.Length is < 1 or > 5 || people.Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0) ||
            people.Select(person => person.UserId).Distinct().Count() != people.Length || !people.Any(person => person.UserId == row["actorId"].AsInt64) ||
            !people.Any(person => person.UserId == row["matchActorId"].AsInt64)) throw B.Broken();
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.ProposeBody(input, row["binding"].AsString), row["reason"].AsString));
        var historical = await SourcesAsync(tx, input, ct, row);
        Equal(row["receiptHash"], historical.Receipt["snapshotHash"]);
        Equal(row["invoiceHash"], historical.Invoice["snapshotHash"]);
        Equal(row["matchHash"], historical.Match["snapshotHash"]);
        Equal(row["matchLine"], historical.MatchLine);
        if (row.Contains("quality") != (historical.Quality is not null)) throw B.Broken();
        if (historical.Quality is not null) {
            Equal(row["quality"], historical.Quality);
            if (!row.Contains("qualityCapacity") || row["qualityCapacity"]["acceptedPackages"].AsInt64 > historical.QualityCapacity!["acceptedPackages"].AsInt64)
                throw B.Broken();
        }
        Equal(row["matchActorId"], historical.Match["actorId"]);
        var bookKey = new OperationalBookKey(input.Location.OrganizationId, input.Location.ShopId, input.Location.ChannelId, "IDR");
        if (await new AccountingBookSetupRecords(database).ApprovedAsync(tx, bookKey, ct) is null) throw B.Broken();
        await EffectsAsync(tx, row, ct); return (row, historical);
    }
    internal async Task<BsonDocument?> DecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var raw = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).Limit(2).ToListAsync(ct);
        if (raw.Count > 1) throw B.Broken(); if (raw.Count == 0) return null;
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument { { "_id", raw[0]["_id"] }, { "$and", new BsonArray { ProcurementQualityValuationSchemas.CurrentValuationValidator(B.Decisions) } } }).SingleOrDefaultAsync(ct)
            ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["proposalHash"], proposal["snapshotHash"]);
        foreach (var field in new[] { "location", "input", "receiptHash", "invoiceHash", "matchHash", "policy", "accounting" }) Equal(row[field], proposal[field]);
        if (row.Contains("quality") != proposal.Contains("quality") || row.Contains("qualityCapacity") != proposal.Contains("qualityCapacity")) throw B.Broken();
        if (proposal.Contains("quality")) { Equal(row["quality"], proposal["quality"]); Equal(row["qualityCapacity"], proposal["qualityCapacity"]); }
        var decision = row["decision"] == "approve" ? ProcurementStockValuationDecision.Approve : ProcurementStockValuationDecision.Reject;
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, proposal,
            decision, row["receiptBefore"].AsBsonDocument, row["matchBefore"].AsBsonDocument,
            row["policy"].AsBsonDocument, row["accounting"].AsBsonDocument,
            row.TryGetValue("qualityCapacity", out var decisionQualityCap) ? decisionQualityCap.AsBsonDocument : null));
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.DecideBody(proposal["_id"].AsInt64, 1, decision, row["binding"].AsString), row["reason"].AsString));
        var people = row["people"].AsBsonArray.Select(R.Person).ToArray();
        if (people.Length is < 1 or > 5 || !people.Any(person => person.UserId == row["actorId"].AsInt64) ||
            people.Select(person => person.UserId).Distinct().Count() != people.Length) throw B.Broken();
        var maker = proposal["people"].AsBsonArray.Select(R.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
        var matcher = proposal["people"].AsBsonArray.Select(R.Person).Single(person => person.UserId == proposal["matchActorId"].AsInt64);
        var byUser = people.ToDictionary(person => person.UserId);
        if (!byUser.TryGetValue(maker.UserId, out var currentMaker) || currentMaker.PersonId != maker.PersonId ||
            !byUser.TryGetValue(matcher.UserId, out var currentMatcher) || currentMatcher.PersonId != matcher.PersonId ||
            !PersonIndependencePolicy.AreIndependent(byUser[row["actorId"].AsInt64], currentMaker, currentMatcher)) throw B.Broken();
        if (decision == ProcurementStockValuationDecision.Approve) {
            if (row["postingFence"] != 1L || row["journalId"].IsBsonNull || row["allocationId"].IsBsonNull) throw B.Broken();
            var allocation = await Rows(B.Allocations).Find(tx, new BsonDocument("_id", row["allocationId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(allocation["snapshotHash"], B.Hash(allocation)); Equal(allocation["decisionId"], row["_id"]);
            Equal(allocation["proposalHash"], proposal["snapshotHash"]); Equal(allocation["journalId"], row["journalId"]);
            var input = B.Input(proposal["input"]);
            foreach (var (field, expected) in new (string, BsonValue)[] { ("proposalId", proposal["_id"]), ("receiptId", input.ReceiptId),
                ("receiptHash", proposal["receiptHash"]), ("invoiceDocumentId", input.InvoiceDocumentId),
                ("invoiceMatchId", input.InvoiceMatchId), ("matchLineNumber", input.MatchLineNumber), ("matchHash", proposal["matchHash"]),
                ("packages", input.Packages), ("netAmount", input.NetAmount.ToString()), ("taxAmount", input.TaxAmount.ToString()),
                ("chargeAmount", input.ChargeAmount.ToString()), ("capitalizedAmount", input.CapitalizedAmount.ToString()),
                ("receiptSequence", row["receiptAfter"]["version"]), ("matchSequence", row["matchAfter"]["version"]),
                ("recordedAt", row["recordedAt"]) }) Equal(allocation[field], expected);
            foreach (var (left, right) in new[] { ("receiptBefore", "receiptBefore"), ("receiptAfter", "receiptAfter"), ("matchBefore", "matchBefore"), ("matchAfter", "matchAfter") })
                Equal(allocation[left], row[right]);
            Equal(row["receiptAfter"], ProcurementStockValuationCapacityReader.AfterReceipt(row["receiptBefore"].AsBsonDocument,
                input.Packages, allocation["_id"].AsInt64, proposal["snapshotHash"].AsString));
            Equal(row["matchAfter"], ProcurementStockValuationCapacityReader.AfterMatch(row["matchBefore"].AsBsonDocument,
                input.Packages, input.NetAmount, input.TaxAmount, input.ChargeAmount, allocation["_id"].AsInt64, proposal["snapshotHash"].AsString));
            var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var draft = AccountingBson.ReadDraft(journal); ProcurementStockValuationPostingSource.RequireAccounts(row, draft);
            Equal(journal["sourceEvent"], ProcurementStockValuationPostingSource.Source(row["_id"].AsInt64).EventKey);
            Equal(journal["fingerprint"], row["journalFingerprint"]); Equal(journal["bookId"], row["accounting"]["bookId"]);
            var expectedSource = ProcurementStockValuationPostingSource.Snapshot(row);
            var expectedLines = new[] {
                new ResolvedJournalLine(row["accounting"]["inventory"]["mappingKey"].AsString,
                    row["accounting"]["inventory"]["accountId"].AsInt64, row["accounting"]["inventory"]["accountCode"].AsString,
                    input.CapitalizedAmount, new Money(0m)),
                new ResolvedJournalLine(row["accounting"]["receiptAccrual"]["mappingKey"].AsString,
                    row["accounting"]["receiptAccrual"]["accountId"].AsInt64, row["accounting"]["receiptAccrual"]["accountCode"].AsString,
                    new Money(0m), input.CapitalizedAmount) };
            if (draft.Source != expectedSource.Source || draft.SourceRevision != 1 || draft.Book != expectedSource.Book ||
                draft.Dimensions != expectedSource.Dimensions || draft.Date != expectedSource.Date || draft.ReversalOf is not null ||
                draft.Total != input.CapitalizedAmount || !draft.Lines.SequenceEqual(expectedLines) ||
                journal["actorId"] != row["actorId"] || journal["actorGrantVersion"] != row["actorGrantVersion"] ||
                journal["reason"] != row["reason"] || journal["correlationId"] != row["correlationId"]) throw B.Broken();
            var audits = await Rows("accountingAuditEvents").Find(tx, new BsonDocument("journalId", row["journalId"])).Limit(2).ToListAsync(ct);
            if (audits.Count != 1 || audits[0]["kind"] != "journal.posted" || audits[0]["eventKey"] != $"accounting:journal:{row["journalId"].AsInt64}" ||
                audits[0]["bookId"] != row["accounting"]["bookId"] || audits[0]["fingerprint"] != journal["fingerprint"] ||
                audits[0]["actorId"] != journal["actorId"] || audits[0]["actorGrantVersion"] != journal["actorGrantVersion"] ||
                audits[0]["reason"] != journal["reason"] || audits[0]["correlationId"] != journal["correlationId"] || !audits[0]["periodId"].IsBsonNull) throw B.Broken();
        } else if (!row["allocationId"].IsBsonNull || !row["journalId"].IsBsonNull || row["postingFence"] != 0L ||
            row["receiptBefore"] != row["receiptAfter"] || row["matchBefore"] != row["matchAfter"]) throw B.Broken();
        await EffectsAsync(tx, row, ct); return row;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"], row["operation"]); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]);
        Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        var table = operation == B.ProposeOperation ? B.Proposals : operation == B.DecideOperation ? B.Decisions : throw ProcurementStockValuationRules.Invalid();
        var row = await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return operation == B.ProposeOperation ? await ProposalAsync(tx, row["_id"].AsInt64, ct) :
            await DecisionAsync(tx, await ProposalAsync(tx, row["proposalId"].AsInt64, ct), ct);
        foreach (var (effects, kind, field) in new[] { ("auditEvents", "kind", "requestKey"), ("outboxEvents", "kind", "requestKey"), ("idempotencyRecords", "operation", "key") })
            if (await Rows(effects).Find(tx, new BsonDocument { { "actorId", actor }, { kind, operation }, { field, key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (effects, operation, local, owner) in new[] {
            ("auditEvents", B.ProposeOperation, "result.proposalId", B.Proposals), ("auditEvents", B.DecideOperation, "result.decisionId", B.Decisions),
            ("outboxEvents", B.ProposeOperation, "payload.proposalId", B.Proposals), ("outboxEvents", B.DecideOperation, "payload.decisionId", B.Decisions),
            ("idempotencyRecords", B.ProposeOperation, "result.proposalId", B.Proposals), ("idempotencyRecords", B.DecideOperation, "result.decisionId", B.Decisions),
            (B.Allocations, "", "decisionId", B.Decisions), (B.Decisions, "", "proposalId", B.Proposals) }) {
            var filter = operation.Length == 0 ? new BsonDocument() : new BsonDocument(effects == "idempotencyRecords" ? "operation" : "kind", operation);
            var orphan = await Rows(effects).Aggregate<BsonDocument>(tx, new BsonDocument[] { new("$match", filter), new("$lookup", new BsonDocument { { "from", owner },
                { "localField", local }, { "foreignField", "_id" }, { "as", "owner" } }), new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (orphan.Count != 0) throw B.Broken();
        }
        var capacity = new ProcurementStockValuationCapacityReader(database, unit, clock, catalog, orders);
        using (var cursor = await Rows(B.ReceiptHeads).Find(tx, FilterDefinition<BsonDocument>.Empty).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var head in cursor.Current) {
                var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources)
                    .ReadAsync(tx, head["receiptId"].AsInt64, ct);
                var actual = await capacity.ReceiptAsync(tx, receipt, ct);
                if (actual.Head is null || actual.Head != head) throw B.Broken();
            }
        using (var cursor = await Rows(B.MatchHeads).Find(tx, FilterDefinition<BsonDocument>.Empty).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var head in cursor.Current) {
                var raw = await Rows(SupplierDocumentBson.Matches).Find(tx, new BsonDocument("_id", head["matchId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                var documents = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
                var invoice = await documents.SubmissionAsync(tx, raw["documentId"].AsInt64, ct);
                var match = await documents.MatchAsync(tx, head["matchId"].AsInt64, ct, invoice);
                var allocation = await Rows(B.Allocations).Find(tx, new BsonDocument("_id", head["lastAllocationId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                var receipt = await new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources)
                    .ReadAsync(tx, allocation["receiptId"].AsInt64, ct);
                var actual = await capacity.ReadAsync(tx, receipt, match, head["matchLineNumber"].AsInt32, ct);
                if (actual.MatchHead is null || actual.MatchHead != head) throw B.Broken();
            }
        using (var cursor = await Rows("journalEntries").Find(tx, new BsonDocument("source.namespace", B.DecideOperation)).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct)) foreach (var journal in cursor.Current) {
                var decision = await Rows(B.Decisions).Find(tx, new BsonDocument("_id", journal["source"]["documentId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                if (decision["decision"] != "approve" || decision["journalId"] != journal["_id"] ||
                    journal["sourceEvent"] != ProcurementStockValuationPostingSource.Source(decision["_id"].AsInt64).EventKey) throw B.Broken();
            }
    }
}
