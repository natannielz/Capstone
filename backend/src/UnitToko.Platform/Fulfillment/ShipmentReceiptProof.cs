using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Fulfillment;

public sealed record ShipmentReceiptProofView(LongId Id, LongId ShipmentId, LongId ReceiptId, long ShipmentRevision, long Version, string ContentHash, long ByteLength, DateTimeOffset IssuedAt, bool IssuedAsRecipient);
public sealed record ShipmentReceiptProofDownload(ShipmentReceiptProofView Document, string Content, string FileName);

public sealed partial class ShipmentReceiptService
{
    private sealed record ProofSource(ShipmentDocumentSource Shipment, BsonDocument Snapshot, string Permission, ShipmentRecipientTarget? Recipient, PersonAuthorityProof? People);
    private IssuedDocumentStore Proofs => new(database, receiptProof: true);
    private static object ProofBody(long shipmentId, long receiptId, bool asRecipient) => new { shipmentId, receiptId, asRecipient };
    private async Task<ProofSource> ProofSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long shipmentId, bool asRecipient, bool issuing, CancellationToken ct) {
        var permission = asRecipient ? Permissions.ReceiptAccept : issuing ? Permissions.ShipmentPrepare : Permissions.ShipmentRead;
        var shipment = await shipments.DocumentSourceAsync(actor, authority, tx, shipmentId, permission, ct);
        var receipt = await records.FindReceiptAsync(tx, shipmentId, ct) ?? throw ShipmentReceiptRecords.Invalid();
        await VerifyReceiptAsync(tx, receipt, shipment, ct);
        ShipmentRecipientTarget? target = null; PersonAuthorityProof? people = null;
        if (asRecipient) {
            if (receipt["recipientId"] != actor.UserId) throw ShipmentReceiptRecords.Invalid();
            target = await recipients.ReadAsync(tx, authority, shipment.Resource, actor.UserId, ct);
            people = await recipients.IndependenceAsync(tx, shipment.Snapshot["courierId"].AsInt64, actor.UserId, ct);
        }
        var copy = shipment.Snapshot.DeepClone().AsBsonDocument;
        var consent = new BsonDocument { { "id", receipt["_id"] }, { "recordHash", CatalogRepository.Digest(receipt) } };
        foreach (var field in new[] { "recipientId", "recipientName", "recordedAt", "consentVersion", "consentText", "hasException", "documentId", "documentVersion", "documentHash", "lines" }) consent.Add(field, receipt[field].DeepClone());
        copy.Add("receipt", consent);
        return new(shipment, copy, permission, target, people);
    }
    private Task ProofFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProofSource source, CancellationToken ct) => source.Recipient is { } target
        ? FenceAsync(actor, authority, tx, source.Shipment, source.Permission, target, source.People!, ct)
        : shipments.FenceAsync(actor, authority, tx, [source.Shipment.Resource], ct, source.Permission);
    private async Task ProofStorageAsync(CancellationToken ct) { await StorageAsync(ct); await Proofs.StorageAsync(ct); }
    private static ShipmentReceiptProofView ProofView(BsonDocument row) => new(new(row["_id"].AsInt64), new(row["sourceId"].AsInt64), new(row["snapshot"]["receipt"]["id"].AsInt64),
        row["sourceRevision"].AsInt64, row["version"].AsInt64, row["contentHash"].AsString, row["byteLength"].AsInt64, ShipmentReceiptRecords.Time(row["issuedAt"]), row["authority"] == "recipient");
    private async Task<BsonDocument> VerifyProofAsync(IClientSessionHandle tx, BsonDocument row, ProofSource source, CancellationToken ct) {
        if (row["authority"] != "staff" && row["authority"] != "recipient" || row["authority"] == "recipient" && row["issuedBy"] != source.Snapshot["receipt"]["recipientId"] ||
            row["bodyHash"] != AuthorizationStore.CommandHash(ProofBody(row["sourceId"].AsInt64, source.Snapshot["receipt"]["id"].AsInt64, row["authority"] == "recipient"), row["reason"].AsString) ||
            ShipmentReceiptRecords.Time(row["issuedAt"]) < ShipmentReceiptRecords.Time(source.Snapshot["receipt"]["recordedAt"])) throw ShipmentReceiptRecords.Invalid();
        return await Proofs.VerifyAsync(tx, row, source.Snapshot, ct);
    }
    public async Task<ShipmentReceiptProofView> IssueProofAsync(ActorContext actor, long shipmentId, long receiptId, bool asRecipient, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (shipmentId <= 0 || receiptId <= 0) throw ShipmentReceiptRecords.Invalid();
        await ProofStorageAsync(ct); var id = (await ids.NextAsync(IssuedDocumentStore.ProofCollection, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = ProofBody(shipmentId, receiptId, asRecipient); ProofSource source = null!; BsonDocument final = null!;
        await authorization.ExecuteCommandAsync(actor, IssuedDocumentStore.ProofOperation, metadata, body, async (snapshot, tx, token) => {
            source = await ProofSourceAsync(actor, snapshot, tx, shipmentId, asRecipient, true, token);
            if (source.Snapshot["receipt"]["id"] != receiptId) throw ShipmentReceiptRecords.Invalid();
            var existing = await Proofs.FindAsync(tx, shipmentId, token); if (existing is not null) await VerifyProofAsync(tx, existing, source, token);
            return Allow(actor, snapshot, source.Shipment, source.Permission, clock.UtcNow);
        }, async (tx, token) => {
            var now = Now;
            if (now < ShipmentReceiptRecords.Time(source.Snapshot["receipt"]["recordedAt"])) throw ShipmentReceiptRecords.Invalid();
            var content = ReceiptProofRenderer.Render(source.Snapshot, id, now);
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", IssuedDocumentStore.ProofKind }, { "sourceId", shipmentId }, { "sourceRevision", 1L }, { "version", 1L },
                { "renderer", "receipt-proof-html-v1" }, { "mediaType", "text/html; charset=utf-8" }, { "authority", asRecipient ? "recipient" : "staff" }, { "snapshot", source.Snapshot },
                { "content", content }, { "contentHash", IssuedDocumentStore.Hash(content) }, { "byteLength", (long)Encoding.UTF8.GetByteCount(content) },
                { "issuedBy", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "issuedAt", now.UtcDateTime }, { "auditId", auditId },
                { "requestKey", metadata.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } };
            await Proofs.InsertAsync(tx, row, token);
            await Rows("auditEvents").InsertOneAsync(tx, Proofs.CreateAudit(row), cancellationToken: token);
            await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", IssuedDocumentStore.ProofKind + ".issued" },
                { "sourceKey", IssuedDocumentStore.ProofKind + ".issued:" + id }, { "payload", IssuedDocumentStore.Result(row) }, { "createdAt", now.UtcDateTime }, { "dueAt", now.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: token);
            return IssuedDocumentStore.Result(row);
        }, ct, async (snapshot, tx, token) => {
            final = await Proofs.FindAsync(tx, shipmentId, token) ?? throw ShipmentReceiptRecords.Invalid();
            if (final["issuedBy"] != actor.UserId || final["requestKey"] != metadata.IdempotencyKey || final["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw ShipmentReceiptRecords.Invalid();
            await VerifyProofAsync(tx, final, source, token); await ProofFenceAsync(actor, snapshot, tx, source, token);
        });
        return ProofView(final);
    }
    public async Task<ShipmentReceiptProofView?> ReadProofAsync(ActorContext actor, long shipmentId, bool asRecipient, CancellationToken ct = default) {
        await ProofStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await ProofSourceAsync(actor, authority, tx, shipmentId, asRecipient, false, token);
            var row = await Proofs.FindAsync(tx, shipmentId, token); var result = row is null ? null : ProofView(await VerifyProofAsync(tx, row, source, token));
            await ProofFenceAsync(actor, authority, tx, source, token); return result;
        }, ct);
    }
    public async Task<ShipmentReceiptProofDownload> DownloadProofAsync(ActorContext actor, long shipmentId, long documentId, bool asRecipient, CancellationToken ct = default) {
        await ProofStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var source = await ProofSourceAsync(actor, authority, tx, shipmentId, asRecipient, false, token);
            var row = await Proofs.FindAsync(tx, shipmentId, token) ?? throw ShipmentReceiptRecords.Invalid();
            if (row["_id"] != documentId) throw ShipmentReceiptRecords.Invalid();
            await VerifyProofAsync(tx, row, source, token);
            var result = new ShipmentReceiptProofDownload(ProofView(row), row["content"].AsString, FormattableString.Invariant($"bukti-penerimaan-{shipmentId}-v1-{documentId}.html"));
            await ProofFenceAsync(actor, authority, tx, source, token); return result;
        }, ct);
    }
    public async Task<ShipmentReceiptProofView?> RecoverProofAsync(ActorContext actor, string key, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(new(key, "Recover original receipt proof", "receipt-proof-recovery")); await ProofStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var canPrepare = ScopePolicy.Compile(actor, authority, Permissions.ShipmentPrepare, clock.UtcNow);
            var canReceive = ScopePolicy.Compile(actor, authority, Permissions.ReceiptAccept, clock.UtcNow);
            AuthorizationStore.Require(canPrepare.IsAllowed ? canPrepare : canReceive);
            var rows = await Rows(IssuedDocumentStore.ProofCollection).Find(tx, F.Eq("issuedBy", actor.UserId) & F.Eq("requestKey", key)).Limit(2).ToListAsync(token);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", IssuedDocumentStore.ProofOperation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            if (rows.Count == 0 && receipt is null) { await shipments.FenceAsync(actor, authority, tx, [], token, canPrepare.IsAllowed ? Permissions.ShipmentPrepare : Permissions.ReceiptAccept); return null; }
            if (rows.Count != 1 || receipt is null) throw ShipmentReceiptRecords.Invalid();
            var row = rows[0]; var source = await ProofSourceAsync(actor, authority, tx, row["sourceId"].AsInt64, row["authority"] == "recipient", true, token);
            var result = ProofView(await VerifyProofAsync(tx, row, source, token)); await ProofFenceAsync(actor, authority, tx, source, token); return result;
        }, ct);
    }
}
