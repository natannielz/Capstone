using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using C = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Billing;

/// <summary>Consumes approved historical order policy versions through their owning
/// graph. Finance never impersonates the buyer or acquires OrderSubmit permission.</summary>
internal sealed class InvoiceReleaseCommercial(IMongoDatabase database, ShipmentReceiptService receiptOwner)
{
    internal async Task<(BsonArray Sources, OrderCommercialRule Common)> ReadAndFenceAsync(ActorContext actor, IClientSessionHandle tx,
        ImmutableArray<InvoiceReceiptSource> receipts, CancellationToken ct)
    {
        var records = new OrderCommercialPolicyRecords(database); var found = new BsonArray(); OrderCommercialRule? common = null;
        foreach (var receipt in receipts.OrderBy(source => source.Snapshot["shipment"]["shipmentId"].AsInt64)) {
            var commercial = receipt.Snapshot["commercial"]; var terms = commercial["terms"];
            if (terms["policy"] != terms["taxPolicy"]) throw B.Broken();
            var reference = OrderSubmissionBson.Reference(terms["policy"]);
            var head = await records.HeadAsync(tx, reference.Id, ct);
            var version = await database.GetCollection<BsonDocument>(C.Versions).Find(tx, new BsonDocument {
                { "policyId", reference.Id }, { "version", reference.Revision }
            }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            version = await records.RowAsync(tx, C.Versions, version["_id"].AsInt64, ct);
            var proposal = await records.ProposalAsync(tx, version["proposalId"].AsInt64, ct);
            var decision = await records.DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
            if (decision["decision"] != "approve" || decision["versionId"] != version["_id"] || version["snapshotHash"] != C.Hash(version)) throw B.Broken();
            var acceptedAt = await receiptOwner.InvoiceCommercialApprovalTimeAsync(actor, tx, receipt.Snapshot["shipment"]["shipmentId"].AsInt64, ct);
            var rule = C.Rule(version["details"]["rule"]);
            if (B.Time(version["effectiveAt"]) > acceptedAt || B.Time(version["details"]["validFrom"]) > acceptedAt ||
                B.Time(version["details"]["validUntil"]) <= acceptedAt) throw B.Broken();
            var seller = C.Target(version["target"]).Seller; var resource = receipt.Resource;
            if (seller.OrganizationId != resource.FulfillmentOrganizationId || seller.ShopId != resource.ShopId || seller.ChannelId != resource.ChannelId) throw B.Broken();
            // Grouping may span branches with distinct policy identities, but the
            // reviewed payment/calendar/rounding terms must actually be compatible.
            if (common is not null && (C.Rule(common)["terms"] != C.Rule(rule)["terms"] || common.Rounding != rule.Rounding)) throw B.Changed();
            common ??= rule;
            found.Add(new BsonDocument { { "orderId", commercial["orderId"] }, { "shipmentId", receipt.Snapshot["shipment"]["shipmentId"] },
                { "acceptedAt", acceptedAt.UtcDateTime }, { "policy", terms["policy"] }, { "versionId", version["_id"] },
                { "versionHash", version["snapshotHash"] }, { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] },
                { "decisionId", decision["_id"] }, { "decisionHash", decision["snapshotHash"] }, { "rule", C.Rule(rule) } });
            foreach (var (name, row) in new[] { (C.Versions, version), (C.Proposals, proposal), (C.Decisions, decision) })
                await InvoiceReleaseRecords.FenceFrozenAsync(database, tx, name, row, ct);
            await InvoiceReleaseRecords.FenceOriginalCommandAsync(database, tx, proposal, ct);
            await InvoiceReleaseRecords.FenceOriginalCommandAsync(database, tx, decision, ct);
            if (head["fence"].AsInt64 >= long.MaxValue - 1 || (await database.GetCollection<BsonDocument>(C.Heads).UpdateOneAsync(tx, head,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        }
        return (found, common ?? throw B.Broken());
    }
}
