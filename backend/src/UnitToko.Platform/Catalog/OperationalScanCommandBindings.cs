using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using C = UnitToko.Platform.Inventory.StockCountBson;
using P = UnitToko.Platform.Fulfillment.ShipmentPreparationBson;
using D = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;
using R = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Catalog;

internal sealed record OperationalScanEvidence(OperationalScanSelection Selection, BsonDocument? Assignment,
    BsonDocument? Sku, BsonDocument? Definition, BsonDocument? ApprovedSource);

/// <summary>Separate immutable command evidence. Existing business receipt/audit formats are never
/// extended. The owner still authorizes the operation and authenticates/fences current source data.</summary>
internal sealed class OperationalScanCommandBindings(IMongoDatabase database, IdAllocator ids, IClock clock)
{
    internal const string Collection = "operationalScanBindings";
    internal static readonly string[] Operations = [C.Operation, R.Operation, P.Operation, D.Operation];
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal sealed class Attempt(OperationalScanCommandBindings owner, IClientSessionHandle tx, long actorId,
        string operation, string key, string businessHash, BsonArray selections, BsonDocument? original)
    {
        internal OperationalScanCommandBindings Owner { get; } = owner;
        internal IClientSessionHandle Transaction { get; } = tx;
        internal long TransactionNumber { get; } = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal long ActorId { get; } = actorId;
        internal string Operation { get; } = operation;
        internal string Key { get; } = key;
        internal string BusinessHash { get; } = businessHash;
        internal BsonArray Selections { get; } = selections.DeepClone().AsBsonArray;
        internal BsonDocument? Original { get; } = original?.DeepClone().AsBsonDocument;
        internal bool IsReplay => Original is not null;
    }
    internal async Task RequireStorageAsync(CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids); // Kept in the owner contract; IDs reuse the globally allocated original audit ID.
        var definition = await AccountContactStorage.DefinitionAsync(database, Collection, ct);
        var validator = OperationalScanBindingSchemas.Validator();
        if (definition is null || !AccountContactStorage.SafeDefinition(definition, validator) ||
            !Same(definition["options"]["validator"], validator)) throw Invalid();
        var indexes = await (await Rows(Collection).Indexes.ListAsync(ct)).ToListAsync(ct);
        var keys = OperationalScanBindingSchemas.Keys;
        if (!AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => indexes.Count(index => AccountContactStorage.Matches(index, key)) != 1)) throw Invalid();
    }
    internal async Task<Attempt> BeginAsync(IClientSessionHandle tx, ActorContext actor, string operation, string key,
        string businessHash, IReadOnlyList<OperationalScanSelection>? selections, CancellationToken ct)
    {
        Context(tx); ValidateKey(actor.UserId, operation, key); RequireHash(businessHash);
        var normalized = Normalize(operation, selections);
        var receipt = await OneAsync(tx, "idempotencyRecords", Key(actor.UserId, operation, key, "key"), ct);
        var binding = await OneAsync(tx, Collection, Key(actor.UserId, operation, key, "requestKey"), ct);
        if ((receipt is null) != (binding is null)) throw Invalid();
        if (receipt is null) await RejectOrphansAsync(tx, actor.UserId, operation, key, ct);
        else {
            await RequirePairAsync(tx, binding!, receipt, ct);
            if (binding!["businessHash"] != businessHash || !Same(binding["selections"], normalized)) throw Changed();
        }
        return new(this, tx, actor.UserId, operation, key, businessHash, normalized, binding);
    }
    internal async Task CompleteAsync(IClientSessionHandle tx, Attempt attempt, BsonDocument receipt, BsonDocument ownerAudit,
        IReadOnlyList<OperationalScanEvidence> evidence, CancellationToken ct)
    {
        Context(tx);
        if (!ReferenceEquals(attempt.Owner, this) || !ReferenceEquals(attempt.Transaction, tx) ||
            attempt.TransactionNumber != tx.WrappedCoreSession.CurrentTransaction.TransactionNumber) throw Invalid();
        RequireReceipt(receipt);
        if (receipt["actorId"] != attempt.ActorId || receipt["operation"] != attempt.Operation || receipt["key"] != attempt.Key ||
            receipt["bodyHash"] != attempt.BusinessHash) throw Invalid();
        var actualReceipt = await OneAsync(tx, "idempotencyRecords", Key(attempt.ActorId, attempt.Operation, attempt.Key, "key"), ct);
        if (actualReceipt is null || !Same(actualReceipt, receipt)) throw Invalid();
        var original = await OriginalAsync(receipt, (name, filter) => OneAsync(tx, name, filter, ct));
        if (!Same(ownerAudit, original.Audit) || Time(receipt["createdAt"]) > clock.UtcNow || Time(original.Audit["createdAt"]) > clock.UtcNow) throw Invalid();
        if (attempt.IsReplay) {
            // Replay authenticates the original immutable evidence. Current mappings may have retired.
            var binding = await OneAsync(tx, Collection, new("_id", attempt.Original!["_id"]), ct) ?? throw Invalid();
            if (!Same(binding, attempt.Original)) throw Invalid();
            await RequirePairAsync(tx, binding, receipt, ct); await FenceAsync(tx, binding, receipt, original.Audit, ct); return;
        }
        if (await Rows(Collection).Find(tx, Key(attempt.ActorId, attempt.Operation, attempt.Key, "requestKey")).AnyAsync(ct)) throw Invalid();
        var captured = Evidence(attempt.Operation, attempt.Selections, evidence, original.Owner, receipt);
        await FenceIdentifierHistoryAsync(tx, captured, ct);
        var created = Create(receipt, original.Audit, attempt.Selections.Count == 0 ? "manual" : "scanned", attempt.Selections, captured);
        RequireRecord(created);
        await Rows(Collection).InsertOneAsync(tx, created, cancellationToken: ct);
        await FenceAsync(tx, created, receipt, original.Audit, ct);
    }
    internal async Task VerifyRecoveryAsync(IClientSessionHandle tx, long actorId, string operation, string key,
        BsonDocument? receipt, CancellationToken ct)
    {
        Context(tx); ValidateKey(actorId, operation, key);
        var actual = await OneAsync(tx, "idempotencyRecords", Key(actorId, operation, key, "key"), ct);
        var binding = await OneAsync(tx, Collection, Key(actorId, operation, key, "requestKey"), ct);
        if (actual is null) {
            if (receipt is not null || binding is not null) throw Invalid();
            await RejectOrphansAsync(tx, actorId, operation, key, ct); return;
        }
        if (receipt is null || binding is null || !Same(actual, receipt)) throw Invalid();
        var original = await RequirePairAsync(tx, binding, receipt, ct);
        await FenceAsync(tx, binding, receipt, original.Audit, ct);
    }
    private async Task<Original> RequirePairAsync(IClientSessionHandle tx, BsonDocument binding, BsonDocument receipt, CancellationToken ct)
    {
        RequireRecord(binding); RequireReceipt(receipt);
        if (binding["actorId"] != receipt["actorId"] || binding["operation"] != receipt["operation"] || binding["requestKey"] != receipt["key"] ||
            binding["businessHash"] != receipt["bodyHash"] || binding["receiptId"] != receipt["_id"] || binding["receiptHash"] != Digest(receipt)) throw Invalid();
        var original = await OriginalAsync(receipt, (name, filter) => OneAsync(tx, name, filter, ct));
        if (binding["ownerAuditId"] != original.Audit["_id"] || binding["ownerAuditHash"] != Digest(original.Audit)) throw Invalid();
        _ = Evidence(binding["operation"].AsString, binding["selections"].AsBsonArray,
            binding["evidence"].AsBsonArray.Select(ReadEvidence).ToArray(), original.Owner, receipt);
        await FenceIdentifierHistoryAsync(tx, binding["evidence"].AsBsonArray, ct);
        if (Time(binding["recordedAt"]) > clock.UtcNow || Time(original.Audit["createdAt"]) > clock.UtcNow) throw Invalid();
        return original;
    }
    private async Task FenceAsync(IClientSessionHandle tx, BsonDocument binding, BsonDocument receipt, BsonDocument audit, CancellationToken ct)
    {
        // A real exact-value write fences each immutable original; no business byte is changed.
        await CatalogIdentifierRecords.FenceAsync(Rows("idempotencyRecords"), tx, receipt, ct);
        await CatalogIdentifierRecords.FenceAsync(Rows("auditEvents"), tx, audit, ct);
        await CatalogIdentifierRecords.FenceAsync(Rows(Collection), tx, binding, ct); Context(tx); ct.ThrowIfCancellationRequested();
    }
    private void Context(IClientSessionHandle tx) => new InventoryRepository(database).RequireSession(tx);
    private async Task<BsonDocument?> OneAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct);
    private async Task RejectOrphansAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct)
    {
        var descriptor = OwnerDescriptor(operation);
        if (await Rows(descriptor.Collection).Find(tx, new BsonDocument { { descriptor.ActorField, actor }, { "requestKey", key } }).AnyAsync(ct) ||
            await Rows("auditEvents").Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key },
                { "$or", new BsonArray { new BsonDocument("kind", descriptor.AuditKind), new BsonDocument("operation", operation) } } }).AnyAsync(ct)) throw Invalid();
    }
    internal static (string Collection, string ActorField, string AuditKind) OwnerDescriptor(string operation) => operation switch {
        C.Operation => (C.Collection, "actorId", C.AuditKind), R.Operation => (R.Receipts, "actorId", R.Operation),
        P.Operation => (P.Versions, "preparedBy", "fulfillment.preparation.saved"), D.Operation => (D.Collection, "dispatchedBy", "fulfillment.shipment.dispatched"),
        _ => throw Invalid() };
    internal sealed record Original(BsonDocument Owner, BsonDocument Audit);
    internal static async Task<Original> OriginalAsync(BsonDocument receipt, Func<string, BsonDocument, Task<BsonDocument?>> load)
    {
        RequireReceipt(receipt); var operation = receipt["operation"].AsString; BsonDocument owner, audit, result;
        try {
            if (operation == C.Operation) {
                owner = await load(C.Collection, new("_id", C.ReceiptObservationId(receipt))) ?? throw Invalid();
                audit = await load("auditEvents", new("_id", owner["auditId"])) ?? throw Invalid();
                C.Graph(owner, audit, receipt); return new(owner, audit);
            }
            if (operation == R.Operation) {
                owner = await load(R.Receipts, WithSchema(receipt["result"]["receiptId"], ProcurementStockReceiptSchemas.Validator(R.Receipts))) ?? throw Invalid();
                var input = R.Input(owner["input"]);
                if (owner["snapshotHash"] != R.Hash(owner) || owner["bodyHash"] != AuthorizationStore.CommandHash(R.Body(input, owner["binding"].AsString), owner["reason"].AsString)) throw Invalid();
                audit = R.Audit(owner); result = R.Result(owner);
            } else if (operation == P.Operation) {
                owner = await load(P.Versions, WithSchema(receipt["result"]["versionId"], ShipmentPreparationSchemas.Validator(P.Versions))) ?? throw Invalid();
                var source = P.Source(owner["source"].AsBsonDocument); var lines = P.Lines(owner["lines"].AsBsonArray);
                _ = ShipmentPreparationPolicy.Require(source, source.Review, lines);
                if (owner["bodyHash"] != AuthorizationStore.CommandHash(new { suborderId = source.SuborderId.Value,
                    expectedRevision = owner["revision"].AsInt64 - 1, review = source.Review, lines }, owner["reason"].AsString)) throw Invalid();
                audit = P.Audit(owner); result = P.Result(owner);
            } else {
                owner = await load(D.Collection, WithSchema(receipt["result"]["shipmentId"], ShipmentDispatchSchemas.Validator())) ?? throw Invalid();
                if (owner["bodyHash"] != AuthorizationStore.CommandHash(D.Body(owner["preparationId"].AsInt64, owner["preparationRevision"].AsInt64,
                    owner["courierId"].AsInt64, owner["review"].AsString, D.Lots(owner["pickedLots"].AsBsonArray)), owner["reason"].AsString)) throw Invalid();
                audit = D.Audit(owner); result = D.Result(owner);
            }
            var actualAudit = await load("auditEvents", new("_id", audit["_id"])) ?? throw Invalid();
            var descriptor = OwnerDescriptor(operation);
            if (!Same(audit, actualAudit) || !Same(result, receipt["result"]) || owner[descriptor.ActorField] != receipt["actorId"] ||
                owner["requestKey"] != receipt["key"] || owner["bodyHash"] != receipt["bodyHash"] ||
                Time(receipt["createdAt"]) > Time(owner["recordedAt"])) throw Invalid();
            return new(owner, actualAudit);
        } catch (Exception error) when (error is InvalidCastException or KeyNotFoundException or ArgumentException or OverflowException) { throw Invalid(); }
    }
    private static BsonDocument WithSchema(BsonValue id, BsonDocument schema) => new() { { "_id", id }, { "$and", new BsonArray { schema } } };
    internal static BsonDocument CreateLegacy(BsonDocument receipt, BsonDocument audit) => Create(receipt, audit, "legacy-manual", [], []);
    private static BsonDocument Create(BsonDocument receipt, BsonDocument audit, string mode, BsonArray selections, BsonArray evidence)
    {
        var row = new BsonDocument { { "_id", audit["_id"] }, { "schemaVersion", 1 }, { "actorId", receipt["actorId"] },
            { "operation", receipt["operation"] }, { "requestKey", receipt["key"] }, { "businessHash", receipt["bodyHash"] },
            { "mode", mode }, { "selections", selections.DeepClone() }, { "selectionHash", SelectionHash(receipt["bodyHash"].AsString, selections) },
            { "receiptId", receipt["_id"] }, { "receiptHash", Digest(receipt) }, { "ownerAuditId", audit["_id"] }, { "ownerAuditHash", Digest(audit) },
            { "evidence", evidence.DeepClone() }, { "recordedAt", receipt["createdAt"] } };
        row.Add("fingerprint", Fingerprint(row)); return row;
    }
    internal static void RequireRecord(BsonDocument row)
    {
        try {
            if (!Fields(row, "_id", "schemaVersion", "actorId", "operation", "requestKey", "businessHash", "mode", "selections", "selectionHash",
                "receiptId", "receiptHash", "ownerAuditId", "ownerAuditHash", "evidence", "recordedAt", "fingerprint") || row["schemaVersion"] != 1 ||
                !row["schemaVersion"].IsInt32 || Positive(row, "_id") != Positive(row, "ownerAuditId") || !row["receiptId"].IsObjectId ||
                !row["selections"].IsBsonArray || !row["evidence"].IsBsonArray || row["mode"].AsString is not ("manual" or "scanned" or "legacy-manual")) throw Invalid();
            ValidateKey(Positive(row, "actorId"), row["operation"].AsString, row["requestKey"].AsString);
            foreach (var field in new[] { "businessHash", "selectionHash", "receiptHash", "ownerAuditHash", "fingerprint" }) RequireHash(row[field].AsString);
            _ = Time(row["recordedAt"]);
            var normalized = Normalize(row["operation"].AsString, row["selections"].AsBsonArray.Select(ReadSelection).ToArray());
            if (!Same(normalized, row["selections"]) || row["selectionHash"] != SelectionHash(row["businessHash"].AsString, normalized) ||
                (row["mode"] == "scanned") != (normalized.Count > 0) || row["evidence"].AsBsonArray.Count != normalized.Count ||
                row["fingerprint"] != Fingerprint(row)) throw Invalid();
            var parsed = row["evidence"].AsBsonArray.Select(ReadEvidence).ToArray();
            if (!Same(Normalize(row["operation"].AsString, parsed.Select(item => item.Selection).ToArray()), normalized)) throw Invalid();
        } catch (Exception error) when (error is InvalidCastException or KeyNotFoundException or ArgumentException or OverflowException) { throw Invalid(); }
    }
    internal static void RequireReceipt(BsonDocument receipt)
    {
        try {
            var operation = receipt["operation"].AsString;
            string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "createdAt", "result"];
            if (!Fields(receipt, operation == C.Operation ? [.. fields, "stockCount"] : fields) || !receipt["_id"].IsObjectId ||
                !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"] != 1 || !receipt["result"].IsBsonDocument || receipt["result"].AsBsonDocument.ElementCount == 0) throw Invalid();
            ValidateKey(Positive(receipt, "actorId"), operation, receipt["key"].AsString); RequireHash(receipt["bodyHash"].AsString); _ = Time(receipt["createdAt"]);
            if (operation == C.Operation) _ = C.ReceiptObservationId(receipt);
        } catch (Exception error) when (error is InvalidCastException or KeyNotFoundException or ArgumentException) { throw Invalid(); }
    }
    internal static BsonArray Normalize(string operation, IReadOnlyList<OperationalScanSelection>? selections)
    {
        if (!Operations.Contains(operation, StringComparer.Ordinal) || selections is { Count: > 100 }) throw Invalid();
        var normalized = (selections ?? []).Select(selection => Selection(operation, selection)).ToArray();
        var identifiers = normalized.Where(row => !row["identifier"].IsBsonNull).Select(row => row["identifier"]["skuId"].AsInt64).ToArray();
        var sources = normalized.Where(row => !row["source"].IsBsonNull).Select(row => row["source"]["inspectionId"].AsInt64 + ":" + row["source"]["inspectionLineNumber"].AsInt32).ToArray();
        if (identifiers.Distinct().Count() != identifiers.Length || sources.Distinct(StringComparer.Ordinal).Count() != sources.Length) throw Invalid();
        return new BsonArray(normalized.OrderBy(Digest, StringComparer.Ordinal));
    }
    private static BsonDocument Selection(string operation, OperationalScanSelection value)
    {
        if (value is null || (value.Identifier is null) == (value.Source is null)) throw Invalid();
        if (value.Identifier is { } item) {
            if (item.IdentifierId.Value <= 0 || item.IdentifierRevision <= 0 || item.SkuId.Value <= 0 || item.DefinitionId.Value <= 0 || item.DefinitionVersion <= 0 ||
                CatalogIdentifierPolicy.Canonical(item.Kind, item.CanonicalValue) != item.CanonicalValue ||
                CatalogIdentifierPolicy.ExactText(item.Namespace, 256) != item.Namespace || item.Kind == "skuCode" && item.Namespace != "internal") throw Invalid();
            return new() { { "identifier", new BsonDocument { { "identifierId", item.IdentifierId.Value }, { "identifierRevision", item.IdentifierRevision },
                { "skuId", item.SkuId.Value }, { "definitionId", item.DefinitionId.Value }, { "definitionVersion", item.DefinitionVersion },
                { "kind", item.Kind }, { "namespace", item.Namespace }, { "canonicalValue", item.CanonicalValue } } }, { "source", BsonNull.Value } };
        }
        var source = value.Source!;
        if (operation != R.Operation || source.OrderId.Value <= 0 || source.OrderVersion <= 0 || source.InspectionId.Value <= 0 || source.InspectionRevision != 1 ||
            source.InspectionLineNumber is < 1 or > 100 || source.SkuId.Value <= 0 || source.DefinitionId.Value <= 0 || source.DefinitionVersion <= 0 ||
            source.SourceSystem is not ("supplier" or "omi" or "s_mart")) throw Invalid();
        _ = CatalogIdentifierPolicy.ExactText(source.SourceAccount, 128); _ = CatalogIdentifierPolicy.ExactText(source.SourceCode, 128);
        return new() { { "identifier", BsonNull.Value }, { "source", new BsonDocument { { "orderId", source.OrderId.Value }, { "orderVersion", source.OrderVersion },
            { "inspectionId", source.InspectionId.Value }, { "inspectionRevision", source.InspectionRevision }, { "inspectionLineNumber", source.InspectionLineNumber },
            { "skuId", source.SkuId.Value }, { "definitionId", source.DefinitionId.Value }, { "definitionVersion", source.DefinitionVersion },
            { "sourceSystem", source.SourceSystem }, { "sourceAccount", source.SourceAccount }, { "sourceCode", source.SourceCode } } } };
    }
    private static OperationalScanSelection ReadSelection(BsonValue value)
    {
        if (!value.IsBsonDocument || !Fields(value.AsBsonDocument, "identifier", "source")) throw Invalid();
        var row = value.AsBsonDocument;
        if (!row["identifier"].IsBsonNull) {
            var item = row["identifier"].AsBsonDocument;
            if (!row["source"].IsBsonNull || !Fields(item, "identifierId", "identifierRevision", "skuId", "definitionId", "definitionVersion", "kind", "namespace", "canonicalValue")) throw Invalid();
            return new(new(new(Positive(item, "identifierId")), Positive(item, "identifierRevision"), new(Positive(item, "skuId")),
                new(Positive(item, "definitionId")), Positive(item, "definitionVersion"), item["kind"].AsString, item["namespace"].AsString, item["canonicalValue"].AsString), null);
        }
        var source = row["source"].AsBsonDocument;
        if (!Fields(source, "orderId", "orderVersion", "inspectionId", "inspectionRevision", "inspectionLineNumber", "skuId", "definitionId", "definitionVersion", "sourceSystem", "sourceAccount", "sourceCode") ||
            !source["inspectionLineNumber"].IsInt32) throw Invalid();
        return new(null, new(new(Positive(source, "orderId")), Positive(source, "orderVersion"), new(Positive(source, "inspectionId")), Positive(source, "inspectionRevision"),
            source["inspectionLineNumber"].AsInt32, new(Positive(source, "skuId")), new(Positive(source, "definitionId")), Positive(source, "definitionVersion"),
            source["sourceSystem"].AsString, source["sourceAccount"].AsString, source["sourceCode"].AsString));
    }
    private static BsonArray Evidence(string operation, BsonArray selections, IReadOnlyList<OperationalScanEvidence> evidence, BsonDocument owner, BsonDocument receipt)
    {
        if (evidence is null || evidence.Count != selections.Count || evidence.Count > 100 || evidence.Any(item => item is null)) throw Invalid();
        var normalized = Normalize(operation, evidence.Select(item => item.Selection).ToArray());
        if (!Same(normalized, selections)) throw Invalid();
        var records = new List<BsonDocument>();
        foreach (var item in evidence) {
            var selected = Selection(operation, item.Selection);
            if (item.Selection.Identifier is { } identifier) {
                if (item.Assignment is null || item.Sku is null || item.Definition is null || item.ApprovedSource is not null) throw Invalid();
                var assignment = CatalogIdentifierRecords.Read(item.Assignment);
                if (!CatalogSchemas.IsValid("skus", item.Sku) || !CatalogSchemas.IsValid("variantDefinitions", item.Definition) ||
                    assignment.Id != identifier.IdentifierId || assignment.Revision != identifier.IdentifierRevision || assignment.SkuId != identifier.SkuId ||
                    assignment.Kind != identifier.Kind || assignment.Namespace != identifier.Namespace || assignment.CanonicalValue != identifier.CanonicalValue ||
                    assignment.RetiredAt is not null || assignment.EffectiveAt > Time(receipt["createdAt"]) ||
                    item.Sku["_id"] != identifier.SkuId.Value || !item.Sku["active"].AsBoolean ||
                    item.Definition["_id"] != identifier.DefinitionId.Value || item.Definition["skuId"] != identifier.SkuId.Value || item.Definition["version"] != identifier.DefinitionVersion ||
                    item.Sku["organizationId"] != assignment.OrganizationId.Value) throw Invalid();
                RequireOwnerTarget(operation, owner, identifier.SkuId.Value, identifier.DefinitionId.Value, identifier.DefinitionVersion);
            } else {
                if (item.Assignment is not null || item.Sku is not null || item.Definition is not null || item.ApprovedSource is null ||
                    !Fields(item.ApprovedSource, "orderSource", "inspection") || !Same(item.ApprovedSource["orderSource"], owner["source"]) ||
                    !Same(item.ApprovedSource["inspection"], owner["inspection"])) throw Invalid();
                var source = item.Selection.Source!;
                if (owner["orderId"] != source.OrderId.Value || owner["input"]["orderVersion"] != source.OrderVersion || owner["inspectionId"] != source.InspectionId.Value ||
                    owner["inspectionLineNumber"] != source.InspectionLineNumber || source.InspectionRevision != 1) throw Invalid();
                var product = ReceiptProduct(owner);
                if (product["skuId"] != source.SkuId.Value || product["definitionId"] != source.DefinitionId.Value || product["definitionVersion"] != source.DefinitionVersion ||
                    product["sourceSystem"] != source.SourceSystem || product["sourceAccountReference"] != source.SourceAccount || product["externalItemCode"] != source.SourceCode) throw Invalid();
            }
            records.Add(new() { { "selection", selected }, { "assignment", Clone(item.Assignment) }, { "sku", Clone(item.Sku) },
                { "definition", Clone(item.Definition) }, { "approvedSource", Clone(item.ApprovedSource) } });
        }
        return new BsonArray(records.OrderBy(row => Digest(row["selection"]), StringComparer.Ordinal));
    }
    private static void RequireOwnerTarget(string operation, BsonDocument owner, long sku, long definition, long version)
    {
        if (operation == C.Operation) {
            if (owner["definition"]["skuId"] != sku || owner["definition"]["definitionId"] != definition || owner["definition"]["definitionVersion"] != version) throw Invalid();
        } else if (operation == R.Operation) {
            var product = ReceiptProduct(owner);
            if (product["skuId"] != sku || product["definitionId"] != definition || product["definitionVersion"] != version) throw Invalid();
        } else {
            if (!owner["lines"].AsBsonArray.Any(line => line["skuId"] == sku)) throw Invalid();
            var line = owner["source"]["lines"].AsBsonArray.SingleOrDefault(line => line["skuId"] == sku) ?? throw Invalid();
            if (line["definition"]["_id"] != definition || line["definition"]["version"] != version) throw Invalid();
        }
    }
    private static BsonValue ReceiptProduct(BsonDocument owner)
    {
        var physical = owner["inspection"]["input"]["lines"].AsBsonArray[owner["inspectionLineNumber"].AsInt32 - 1];
        if (physical["receivedPackages"].AsInt32 <= 0) throw Invalid();
        return PurchaseOrderService.FundingOrderView(owner["source"].AsBsonDocument)["lines"].AsBsonArray
            .SingleOrDefault(product => product["skuId"] == physical["requestSkuId"]) ?? throw Invalid();
    }
    private static OperationalScanEvidence ReadEvidence(BsonValue value)
    {
        var row = value.AsBsonDocument;
        if (!Fields(row, "selection", "assignment", "sku", "definition", "approvedSource")) throw Invalid();
        static BsonDocument? Maybe(BsonValue item) => item.IsBsonNull ? null : item.AsBsonDocument;
        return new(ReadSelection(row["selection"]), Maybe(row["assignment"]), Maybe(row["sku"]), Maybe(row["definition"]), Maybe(row["approvedSource"]));
    }
    private async Task FenceIdentifierHistoryAsync(IClientSessionHandle tx, BsonArray evidence, CancellationToken ct)
    {
        foreach (var item in evidence.Select(ReadEvidence).Where(item => item.Selection.Identifier is not null)) {
            var assignment = item.Assignment ?? throw Invalid(); var selected = item.Selection.Identifier!;
            var history = await OneAsync(tx, CatalogIdentifierRecords.History, new BsonDocument {
                { "identifierId", selected.IdentifierId.Value }, { "assignmentVersion", selected.IdentifierRevision } }, ct) ?? throw Invalid();
            CatalogIdentifierRecords.RequireHistory(history);
            if (!Same(history["assignment"], assignment)) throw Invalid();
            var audit = await OneAsync(tx, "auditEvents", new("_id", history["auditId"]), ct) ?? throw Invalid();
            if (history["schemaVersion"] == 1) {
                _ = CatalogReceiptRules.RequireAudit(audit);
                if (audit["operation"].AsString is not ("catalog.sku.create" or "catalog.sku.replace") || audit["actorId"] != history["actorId"] ||
                    audit["createdAt"] != history["createdAt"] || audit["after"]["sku"]["_id"] != assignment["skuId"] ||
                    audit["after"]["sku"]["organizationId"] != assignment["organizationId"] || audit["after"]["sku"]["sku"] != assignment["canonicalValue"]) throw Invalid();
            } else {
                _ = CatalogIdentifierEvidence.RequireAudit(audit);
                if (audit["actorId"] != history["actorId"] || audit["createdAt"] != history["createdAt"] ||
                    audit["identifiersAfter"].AsBsonArray.Count(value => Same(value, assignment)) != 1) throw Invalid();
                var receipt = await OneAsync(tx, "idempotencyRecords", new("catalogIdentifier.auditId", audit["_id"]), ct) ?? throw Invalid();
                CatalogIdentifierEvidence.RequireReceipt(receipt, audit);
                await CatalogIdentifierRecords.FenceAsync(Rows("idempotencyRecords"), tx, receipt, ct);
            }
            var definition = await OneAsync(tx, "variantDefinitions", new("_id", selected.DefinitionId.Value), ct) ?? throw Invalid();
            if (item.Definition is null || !Same(item.Definition, definition)) throw Invalid();
            // The owner may retain an older package label. Authenticate the immutable definition
            // current at capture, without replacing the owner's selected historical package.
            var capturedSku = item.Sku ?? throw Invalid();
            var capturedCurrent = capturedSku["currentDefinitionId"] == definition["_id"] ? definition :
                await OneAsync(tx, "variantDefinitions", new("_id", capturedSku["currentDefinitionId"]), ct) ?? throw Invalid();
            if (!CatalogSchemas.IsValid("variantDefinitions", capturedCurrent) || capturedCurrent["skuId"] != selected.SkuId.Value ||
                new[] { "packageKind", "baseUnitCode", "baseUnitsPerPackage", "attributes" }.Any(field => !Same(capturedCurrent[field], definition[field]))) throw Invalid();
            CatalogAuthoringService.RequirePair(capturedSku, capturedCurrent);
            if (capturedCurrent["_id"] != definition["_id"])
                await CatalogIdentifierRecords.FenceAsync(Rows("variantDefinitions"), tx, capturedCurrent, ct);
            var parsed = CatalogIdentifierRecords.Read(assignment);
            if (parsed.DefinitionId is { } assignedId && assignedId != selected.DefinitionId) {
                var assigned = await OneAsync(tx, "variantDefinitions", new("_id", assignedId.Value), ct) ?? throw Invalid();
                if (!CatalogSchemas.IsValid("variantDefinitions", assigned) || assigned["skuId"] != selected.SkuId.Value ||
                    new[] { "packageKind", "baseUnitCode", "baseUnitsPerPackage", "attributes" }.Any(field => !Same(assigned[field], definition[field]))) throw Invalid();
                await CatalogIdentifierRecords.FenceAsync(Rows("variantDefinitions"), tx, assigned, ct);
            }
            await CatalogIdentifierRecords.FenceAsync(Rows("variantDefinitions"), tx, definition, ct);
            await CatalogIdentifierRecords.FenceAsync(Rows(CatalogIdentifierRecords.History), tx, history, ct);
            await CatalogIdentifierRecords.FenceAsync(Rows("auditEvents"), tx, audit, ct);
        }
    }
    private static BsonValue Clone(BsonDocument? row) => row is null ? BsonNull.Value : row.DeepClone();
    internal static string SelectionHash(string businessHash, BsonArray selections) => Digest(new BsonDocument { { "businessHash", businessHash }, { "selections", selections } });
    internal static string Fingerprint(BsonDocument row) => Digest(new BsonDocument(row.Where(field => field.Name != "fingerprint")));
    internal static string Digest(BsonValue value) { if (!Unique(value)) throw Invalid(); return CatalogRepository.Digest(value); }
    internal static bool Same(BsonValue left, BsonValue right) => Unique(left) && Unique(right) && CatalogSchemas.SemanticEquals(left, right);
    private static bool Unique(BsonValue value) => value.IsBsonDocument ? value.AsBsonDocument.Names.Distinct(StringComparer.Ordinal).Count() == value.AsBsonDocument.ElementCount &&
        value.AsBsonDocument.All(item => Unique(item.Value)) : !value.IsBsonArray || value.AsBsonArray.All(Unique);
    private static bool Fields(BsonDocument row, params string[] fields) => Unique(row) && row.ElementCount == fields.Length && fields.All(row.Contains);
    private static long Positive(BsonDocument row, string field) => row.TryGetValue(field, out var value) && value.IsInt64 && value.AsInt64 > 0 ? value.AsInt64 : throw Invalid();
    private static DateTimeOffset Time(BsonValue value) => CatalogSchemas.ValidDate(value) ? CatalogRepository.Date(value) : throw Invalid();
    private static void RequireHash(string value) { if (value is null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigitLower(c))) throw Invalid(); }
    private static void ValidateKey(long actor, string operation, string key) { if (actor <= 0 || !Operations.Contains(operation, StringComparer.Ordinal) || !C.Key(key)) throw Invalid(); }
    private static BsonDocument Key(long actor, string operation, string key, string keyField) => new() { { "actorId", actor }, { "operation", operation }, { keyField, key } };
    internal static PersistenceConflictException Invalid() => new("operational_scan_binding_unavailable", "The original command and its selected scan evidence are unavailable");
    private static PersistenceConflictException Changed() => new("operational_scan_selection_changed", "The original command key belongs to a different manual or scanned selection");
}
