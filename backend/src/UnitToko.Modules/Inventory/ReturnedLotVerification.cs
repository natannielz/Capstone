using System.Collections.Immutable;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;
public sealed record ReturnedLotVerificationLine(long ReceiptId, long SkuId, long OriginalLotId, long VerifiedBaseUnits);
public sealed record ReturnedLotVerificationInput(string VerificationReference, bool ConfirmPhysicalOriginalLotMatch,
    ImmutableArray<ReturnedLotVerificationLine> Lines);
public static class ReturnedLotVerificationPolicy
{
    public static ReturnedLotVerificationInput Copy(ReturnedLotVerificationInput input)
    {
        if (input is null || !ReceivingAccountPolicy.Text(input.VerificationReference, 160) || !input.ConfirmPhysicalOriginalLotMatch ||
            input.Lines.IsDefaultOrEmpty || input.Lines.Length > 100 || input.Lines.Any(line => line is null || line.ReceiptId <= 0 ||
                line.SkuId <= 0 || line.OriginalLotId <= 0 || line.VerifiedBaseUnits is <= 0 or > DomainValues.MaximumLineQuantity) ||
            input.Lines.Select(line => (line.ReceiptId, line.SkuId)).Distinct().Count() != input.Lines.Length) throw Invalid();
        return input with { Lines = [.. input.Lines.OrderBy(line => line.ReceiptId).ThenBy(line => line.SkuId).Select(line => line with { })] };
    }
    public static DomainValidationException Invalid() => new("invalid_return_lot_verification",
        "Verifikasi lot asli hanya untuk varian dan kemasan utuh yang cocok; satuan lepas atau identitas yang belum cocok memerlukan penanganan manual dan tetap dikarantina");
}
