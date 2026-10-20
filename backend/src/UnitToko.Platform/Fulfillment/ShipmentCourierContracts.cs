using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Fulfillment;

// Assignment authority is separate from immutable departure/document provenance.
public sealed record ShipmentCurrentCourier(long Revision, LongId CourierId, string CourierName, DateTimeOffset EffectiveAt);
public sealed record ShipmentCourierChange(LongId Id, LongId ShipmentId, long Revision, LongId PreviousCourierId,
    LongId CourierId, string CourierName, DateTimeOffset RecordedAt, string Reason);
public sealed record ShipmentCourierTask(LongId ShipmentId, LongId OrderId, LongId SuborderId, LongId NumberEntity,
    LongId NumberChannel, int NumberYear, long Number, LongId DepartureCourierId, string DepartureCourierName,
    ShipmentCurrentCourier CurrentAssignment, bool Received, bool CanHandover, ImmutableArray<ShipmentCourierChange> History);
public sealed record ShipmentCourierPreview(ShipmentCourierTask Task, LongId CourierId, string CourierName, string Review);
