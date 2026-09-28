using MongoDB.Bson;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class StockCountIntegrityTests
{
    [Fact]
    public void Duplicate_audit_field_cannot_replace_required_original_provenance()
    {
        var shop = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
        var location = shop with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = 102 };
        var now = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        var actor = new ActorContext(1, "https://approved.example/", "subject-1", 1, now, FactorAssurance.MultiFactor);
        var definition = new StockCountDefinitionSnapshot(shop, 2, 1, 3, 1, 4, 1, 5, 1, "SKU", "physical", "Dus", "sealed", "piece", 12);
        var row = StockCountBson.Create(1, 2, actor, new(location, 2, 1, 5, 1), definition, new("key", "reason", "correlation"), now);
        var audit = StockCountBson.Audit(row);
        audit.AllowDuplicateNames = true;
        audit.Remove("subject");
        audit.Add("reason", audit["reason"]);
        var receipt = StockCountBson.Receipt(row, ObjectId.GenerateNewId());
        Assert.Throws<AuthorizationDeniedException>(() => StockCountBson.Graph(row, audit, receipt));
    }
}
