using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;

namespace UnitToko.Modules.Fulfillment;

// Whole packages and an explicit loose remainder preserve the accepted package meaning.
// Each entered field is bounded; the derived base-unit total can exceed one million.
public sealed record ReceiptPackageAmount(int Packages, int LooseUnits);
public sealed record ReceiptDispatchedLine(LongId SkuId, LongId DefinitionId, int Packages, int BaseUnitsPerPackage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long LineRevision = 0);
public sealed record ReceiptLineDeclaration(LongId SkuId, ReceiptPackageAmount Accepted, ReceiptPackageAmount Rejected, ReceiptPackageAmount Missing, string? ExceptionReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long LineRevision = 0);
public sealed record ReceiptConfirmedLine(ReceiptLineDeclaration Declaration, LongId DefinitionId, long DispatchedBaseUnits,
    long AcceptedBaseUnits, long RejectedBaseUnits, long MissingBaseUnits) {
    public bool HasException => RejectedBaseUnits > 0 || MissingBaseUnits > 0;
}

public static class DeliveryReceiptPolicy
{
    public const string ConsentVersion = "delivery-receipt-v1";
    public const string ConsentText = "Saya telah memeriksa kiriman dan menyatakan jumlah yang diterima, ditolak, serta belum diterima sesuai keadaan sebenarnya.";
    public const int MaximumFailedAttempts = 5, MaximumChallengesPerHour = 20;
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(15);
    public static ImmutableArray<ReceiptConfirmedLine> Require(ImmutableArray<ReceiptDispatchedLine> dispatched, ImmutableArray<ReceiptLineDeclaration> declarations) {
        if (dispatched.IsDefaultOrEmpty || dispatched.Length > 100 || declarations.IsDefault || declarations.Length != dispatched.Length ||
            dispatched.Any(line => line is null || line.SkuId.Value <= 0 || line.DefinitionId.Value <= 0 || line.Packages is < 1 or > DomainValues.MaximumLineQuantity ||
                line.BaseUnitsPerPackage is < 1 or > DomainValues.MaximumLineQuantity) ||
            dispatched.Select(line => (line.SkuId, line.LineRevision)).Distinct().Count() != dispatched.Length || declarations.Any(line => line is null || line.SkuId.Value <= 0) ||
            declarations.Select(line => (line.SkuId, line.LineRevision)).Distinct().Count() != declarations.Length) throw Invalid();
        var result = ImmutableArray.CreateBuilder<ReceiptConfirmedLine>();
        foreach (var source in dispatched) {
            new OrderLineIdentity(source.SkuId, source.LineRevision).Validate();
            var line = declarations.SingleOrDefault(line => line.SkuId == source.SkuId && line.LineRevision == source.LineRevision) ?? throw Invalid();
            var accepted = Amount(line.Accepted, source.BaseUnitsPerPackage); var rejected = Amount(line.Rejected, source.BaseUnitsPerPackage); var missing = Amount(line.Missing, source.BaseUnitsPerPackage);
            var total = checked((long)source.Packages * source.BaseUnitsPerPackage);
            if (checked(accepted + rejected + missing) != total) throw Invalid();
            if (line.ExceptionReason is { } reason && (reason.Length is < 1 or > 1000 || reason != reason.Trim() || reason.Any(char.IsControl))) throw Invalid();
            if ((rejected != 0 || missing != 0) && line.ExceptionReason is null) throw Invalid();
            result.Add(new(line with { Accepted = line.Accepted with { }, Rejected = line.Rejected with { }, Missing = line.Missing with { } }, source.DefinitionId, total, accepted, rejected, missing));
        }
        return result.ToImmutable();
    }
    private static long Amount(ReceiptPackageAmount value, int contents) {
        if (value is null || value.Packages is < 0 or > DomainValues.MaximumLineQuantity || value.LooseUnits < 0 || value.LooseUnits >= contents) throw Invalid();
        return checked((long)value.Packages * contents + value.LooseUnits);
    }
    private static DomainValidationException Invalid() => new("invalid_delivery_receipt", "Declare exact received, rejected and missing quantities for every dispatched package definition");
}
