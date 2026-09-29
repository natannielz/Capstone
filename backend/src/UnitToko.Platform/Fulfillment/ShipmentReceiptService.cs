using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

public sealed record ShipmentRecipientView(LongId Id, string Name);
public sealed record ShipmentRecipientPage(ImmutableArray<ShipmentRecipientView> Items, bool HasMore);
public sealed record ShipmentReceiptChallengeView(LongId Id, LongId ShipmentId, LongId DocumentId, LongId RecipientId, string RecipientName, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, string? Token);
public sealed record ShipmentReceiptReviewLine(LongId SkuId, string SkuCode, string ProductName, LongId DefinitionId, string PackageLabel, string BaseUnitCode, int BaseUnitsPerPackage, int Packages);
public sealed record ShipmentReceiptReview(LongId ChallengeId, LongId ShipmentId, LongId DocumentId, long DocumentVersion, string DocumentHash, string RecipientName,
    DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, string ConsentVersion, string ConsentText, ImmutableArray<ShipmentReceiptReviewLine> Lines);
public sealed record ShipmentReceiptView(LongId Id, LongId ShipmentId, LongId DocumentId, LongId RecipientId, string RecipientName, DateTimeOffset RecordedAt, bool HasException,
    ImmutableArray<ReceiptConfirmedLine> Lines);

/// <summary>Authenticated recipient consent. The courier only issues a challenge; possession of
/// a QR is neither identity nor acceptance. No stock, invoice or payment writes occur here.</summary>
public sealed partial class ShipmentReceiptService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly ShipmentQueryService shipments;
    private readonly ShipmentDocumentService documents;
    private readonly AuthorizationStore authorization;
    private readonly ShipmentReceiptRecords records;
    private readonly ShipmentRecipientEligibility recipients;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public ShipmentReceiptService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ShipmentQueryService shipments, ShipmentDocumentService documents) {
        if (new[] { shipments.SourceDatabase, documents.SourceDatabase }.Any(source => !ReferenceEquals(source.Client, database.Client) || !source.DatabaseNamespace.Equals(database.DatabaseNamespace)))
            throw new ArgumentException("Shipment receipt and source owners must share one transaction");
        this.database = database; this.ids = ids; this.clock = clock; this.shipments = shipments; this.documents = documents;
        authorization = new(database, unit, clock); records = new(database); recipients = new(database, authorization, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => records.Rows(name);
    private async Task StorageAsync(CancellationToken ct) { await shipments.StorageAsync(ct); await records.StorageAsync(ct); await DeliveryAttempts.StorageAsync(ct); }
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool TokenMatches(BsonDocument challenge, string token) => token is not null && token.Length == 64 && token.All(character => char.IsAsciiHexDigitLower(character)) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge["tokenHash"].AsString), Encoding.ASCII.GetBytes(Hash(token)));
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static object IssueBody(long shipment, long document, long recipient) => new { shipmentId = shipment, documentId = document, recipientId = recipient };
    private static object ConfirmBody(long challenge, string tokenHash, ImmutableArray<ReceiptLineDeclaration> lines) =>
        new { challengeId = challenge, tokenHash, consentVersion = DeliveryReceiptPolicy.ConsentVersion, consent = true, lines = lines.OrderBy(line => line.SkuId.Value).ToImmutableArray() };
    private static CommandAuthorization Allow(ActorContext actor, AuthorizationSnapshot snapshot, ShipmentDocumentSource source, string permission, DateTimeOffset now) =>
        new(ScopePolicy.Authorize(actor, snapshot, permission, source.Resource, now), ScopePolicy.RequiredNodes(source.Resource, snapshot.Directory), permission, new(source.Resource));
    private static void Courier(ActorContext actor, ShipmentDocumentSource source) { if (source.Snapshot["courierId"] != actor.UserId) throw ShipmentReceiptRecords.Invalid(); }
    private ShipmentReceiptChallengeView Challenge(BsonDocument row, string? secret = null) => new(new(row["_id"].AsInt64), new(row["shipmentId"].AsInt64),
        new(row["documentId"].AsInt64), new(row["recipientId"].AsInt64), row["recipientName"].AsString, ShipmentReceiptRecords.Time(row["expiresAt"]), Now, secret);
    private static BsonDocument Header(long id, ActorContext actor, ShipmentDocumentSource source, BsonDocument document, ShipmentRecipientTarget target,
        AuthorizationCommandMetadata meta, object body, long audit, DateTimeOffset now) => new() {
        { "_id", id }, { "schemaVersion", 1 }, { "shipmentId", source.Snapshot["shipmentId"] }, { "documentId", document["_id"] },
        { "documentVersion", document["version"] }, { "documentHash", document["contentHash"] }, { "courierId", source.Snapshot["courierId"] },
        { "recipientId", target.UserId }, { "recipientName", target.Name }, { "recipientProfileVersion", target.ProfileVersion },
        { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "requestKey", meta.IdempotencyKey }, { "bodyHash", AuthorizationStore.CommandHash(body, meta.Reason) },
        { "recordedAt", now.UtcDateTime }, { "auditId", audit }, { "reason", meta.Reason }, { "correlationId", meta.CorrelationId }
    };
    private async Task FenceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, ShipmentDocumentSource source,
        string permission, ShipmentRecipientTarget target, PersonAuthorityProof proof, CancellationToken ct) {
        await recipients.FenceAsync(actor, snapshot, tx, source.Resource, target, ct);
        await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, ct);
        await shipments.FenceAsync(actor, snapshot, tx, [source.Resource], ct, permission);
        recipients.RequireCurrent(snapshot, source.Resource, target); proof.RequireCurrent(tx, clock.UtcNow);
    }
    public async Task<ShipmentRecipientPage> RecipientsAsync(ActorContext actor, long shipmentId, long? beforeId = null, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var source = await shipments.DocumentSourceAsync(actor, snapshot, tx, shipmentId, Permissions.DeliveryEvidence, token); Courier(actor, source);
            var targets = await recipients.ListAsync(tx, snapshot, source.Resource, beforeId, token);
            foreach (var target in targets) await recipients.FenceAsync(actor, snapshot, tx, source.Resource, target, token);
            await shipments.FenceAsync(actor, snapshot, tx, [source.Resource], token, Permissions.DeliveryEvidence);
            foreach (var target in targets) recipients.RequireCurrent(snapshot, source.Resource, target);
            return new ShipmentRecipientPage(targets.Take(25).Select(target => new ShipmentRecipientView(new(target.UserId), target.Name)).ToImmutableArray(), targets.Length > 25);
        }, ct);
    }
    public async Task<ShipmentReceiptChallengeView> IssueChallengeAsync(ActorContext actor, long shipmentId, long documentId, long recipientId,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (shipmentId <= 0 || documentId <= 0 || recipientId <= 0) throw ShipmentReceiptRecords.Invalid();
        await StorageAsync(ct);
        var id = (await ids.NextAsync(ShipmentReceiptRecords.Challenges, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); var body = IssueBody(shipmentId, documentId, recipientId);
        ShipmentDocumentSource source = null!; BsonDocument document = null!, final = null!; ShipmentRecipientTarget target = null!; PersonAuthorityProof proof = null!;
        await authorization.ExecuteCommandAsync(actor, ShipmentReceiptRecords.Issue, metadata, body, async (snapshot, tx, token) => {
            source = await shipments.DocumentSourceAsync(actor, snapshot, tx, shipmentId, Permissions.DeliveryEvidence, token); Courier(actor, source);
            await DeliveryAttempts.HistoryAsync(tx, source, token);
            document = await documents.RequireReceiptDocumentAsync(tx, source, documentId, token);
            target = await recipients.ReadAsync(tx, snapshot, source.Resource, recipientId, token);
            proof = await recipients.IndependenceAsync(tx, actor.UserId, recipientId, token);
            var previous = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).SingleOrDefaultAsync(token);
            if (previous is not null) await VerifyChallengeAsync(tx, previous, source, token);
            var receipt = await records.FindReceiptAsync(tx, shipmentId, token);
            if (receipt is not null) await VerifyReceiptAsync(tx, receipt, source, token);
            return Allow(actor, snapshot, source, Permissions.DeliveryEvidence, clock.UtcNow);
        }, async (tx, token) => {
            if (await records.FindReceiptAsync(tx, shipmentId, token) is not null) throw new PersistenceConflictException("shipment_already_received", "Read the recorded receipt");
            var now = Now;
            var recent = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("shipmentId", shipmentId) & F.Gt("recordedAt", now.AddHours(-1).UtcDateTime)).Limit(DeliveryReceiptPolicy.MaximumChallengesPerHour).ToListAsync(token);
            if (recent.Count >= DeliveryReceiptPolicy.MaximumChallengesPerHour) throw new DomainValidationException("receipt_challenge_limit", "Too many receipt challenges; try later");
            var row = Header(id, actor, source, document, target, metadata, body, audit, now);
            row.AddRange(new BsonDocument { { "tokenHash", Hash(secret) }, { "expiresAt", now.Add(DeliveryReceiptPolicy.ChallengeLifetime).UtcDateTime },
                { "failedAttempts", 0 }, { "consumedReceiptId", BsonNull.Value }, { "consumedAt", BsonNull.Value } });
            await Rows(ShipmentReceiptRecords.Challenges).InsertOneAsync(tx, row, cancellationToken: token);
            await records.EffectsAsync(tx, row, ShipmentReceiptRecords.Issue, token); return ShipmentReceiptRecords.Result(row, ShipmentReceiptRecords.Issue);
        }, ct, async (snapshot, tx, token) => {
            final = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("requestKey", metadata.IdempotencyKey)).SingleOrDefaultAsync(token) ?? throw ShipmentReceiptRecords.Invalid();
            await VerifyChallengeAsync(tx, final, source, token);
            if (final["bodyHash"] != AuthorizationStore.CommandHash(body, metadata.Reason)) throw ShipmentReceiptRecords.Invalid();
            await FenceAsync(actor, snapshot, tx, source, Permissions.DeliveryEvidence, target, proof, token);
        });
        // Only the response that owns the freshly generated secret can disclose it. A retry never invents a replacement.
        return Challenge(final, final["tokenHash"] == Hash(secret) ? secret : null);
    }
    private async Task VerifyChallengeAsync(IClientSessionHandle tx, BsonDocument row, ShipmentDocumentSource source, CancellationToken ct) {
        var document = await documents.RequireReceiptDocumentAsync(tx, source, row["documentId"].AsInt64, ct);
        if (row["shipmentId"] != source.Snapshot["shipmentId"] || row["courierId"] != source.Snapshot["courierId"] || row["actorId"] != row["courierId"] ||
            row["documentHash"] != document["contentHash"] || row["documentVersion"] != document["version"] ||
            row["bodyHash"] != AuthorizationStore.CommandHash(IssueBody(row["shipmentId"].AsInt64, row["documentId"].AsInt64, row["recipientId"].AsInt64), row["reason"].AsString) ||
            ShipmentReceiptRecords.Time(row["expiresAt"]) != ShipmentReceiptRecords.Time(row["recordedAt"]).Add(DeliveryReceiptPolicy.ChallengeLifetime) ||
            ShipmentReceiptRecords.Time(row["recordedAt"]) < ShipmentReceiptRecords.Time(document["issuedAt"])) throw ShipmentReceiptRecords.Invalid();
        await records.VerifyEffectsAsync(tx, row, ShipmentReceiptRecords.Issue, ct);
    }
    private async Task<ImmutableArray<ReceiptConfirmedLine>> VerifyReceiptAsync(IClientSessionHandle tx, BsonDocument row, ShipmentDocumentSource source, CancellationToken ct) {
        await DeliveryAttempts.RequireUsableChallengeAsync(tx, source, row["challengeId"].AsInt64, ct);
        var challenge = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("_id", row["challengeId"])).SingleOrDefaultAsync(ct) ?? throw ShipmentReceiptRecords.Invalid();
        await VerifyChallengeAsync(tx, challenge, source, ct);
        var lines = DeliveryReceiptPolicy.Require(ShipmentReceiptRecords.Dispatched(source.Snapshot), ShipmentReceiptRecords.Declarations(row["lines"].AsBsonArray));
        foreach (var name in new[] { "shipmentId", "documentId", "documentVersion", "documentHash", "courierId", "recipientId", "tokenHash" })
            if (row[name] != challenge[name]) throw ShipmentReceiptRecords.Invalid();
        if (row["actorId"] != row["recipientId"] || row["courierPersonId"] == row["recipientPersonId"] || challenge["consumedReceiptId"] != row["_id"] || challenge["consumedAt"] != row["recordedAt"] ||
            row["consentVersion"] != DeliveryReceiptPolicy.ConsentVersion || row["consentText"] != DeliveryReceiptPolicy.ConsentText ||
            ShipmentReceiptRecords.Time(row["recordedAt"]) < ShipmentReceiptRecords.Time(challenge["recordedAt"]) || ShipmentReceiptRecords.Time(row["recordedAt"]) >= ShipmentReceiptRecords.Time(challenge["expiresAt"]) ||
            !CatalogSchemas.SemanticEquals(row["lines"], ShipmentReceiptRecords.Lines(lines)) || row["hasException"] != lines.Any(line => line.HasException) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(ConfirmBody(row["challengeId"].AsInt64, row["tokenHash"].AsString, lines.Select(line => line.Declaration).ToImmutableArray()), row["reason"].AsString)) throw ShipmentReceiptRecords.Invalid();
        await records.VerifyEffectsAsync(tx, row, ShipmentReceiptRecords.Confirm, ct); return lines;
    }
    private static ShipmentReceiptView Receipt(BsonDocument row, ImmutableArray<ReceiptConfirmedLine> lines) => new(new(row["_id"].AsInt64), new(row["shipmentId"].AsInt64), new(row["documentId"].AsInt64),
        new(row["recipientId"].AsInt64), row["recipientName"].AsString, ShipmentReceiptRecords.Time(row["recordedAt"]), row["hasException"].AsBoolean, lines);
}
