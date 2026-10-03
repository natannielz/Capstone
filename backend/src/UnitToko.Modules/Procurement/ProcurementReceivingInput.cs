using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementArrivalNoticeInput(ProcurementLocation Location, long OrderId, long OrderVersion, long OrderApprovalId,
    DateOnly NoticeDate, string SupplierDocumentReference, string? DdoRequestReference, string? DdoTelephoneReference, int DeclaredContainers, string Note);
public sealed record ProcurementContainerIntakeInput(DateOnly ReceivedDate, string IntakeReference, int MatchedContainers, int ExcessContainers,
    int HeldContainers, string Condition, string? Discrepancy);

public static class ProcurementReceivingRules
{
    public static void Text(string? value, int maximum, bool optional = false) {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    public static ProcurementArrivalNoticeInput Copy(ProcurementArrivalNoticeInput input) {
        if (input is null || input.OrderId <= 0 || input.OrderVersion != 1 || input.OrderApprovalId <= 0 || input.NoticeDate.Year < 2000 ||
            input.DeclaredContainers is < 1 or > DomainValues.MaximumLineQuantity) throw Invalid();
        ProcurementRequestRules.Location(input.Location); Text(input.SupplierDocumentReference, 128); Text(input.DdoRequestReference, 128, true);
        Text(input.DdoTelephoneReference, 256, true); Text(input.Note, 1000); return input with { Location = input.Location with { } };
    }
    public static ProcurementContainerIntakeInput Copy(ProcurementContainerIntakeInput input) {
        if (input is null || input.ReceivedDate.Year < 2000 || input.MatchedContainers is < 0 or > DomainValues.MaximumLineQuantity ||
            input.ExcessContainers is < 0 or > DomainValues.MaximumLineQuantity || input.HeldContainers is < 0 or > DomainValues.MaximumLineQuantity ||
            input.Condition is not ("intact" or "opened" or "damaged" or "mixed" or "unverified")) throw Invalid();
        var total = (long)input.MatchedContainers + input.ExcessContainers;
        if (total is < 1 or > DomainValues.MaximumLineQuantity || input.HeldContainers < input.ExcessContainers || input.HeldContainers > total ||
            input.Condition is "opened" or "damaged" or "unverified" && input.HeldContainers != total || input.Condition == "mixed" && input.HeldContainers == 0) throw Invalid();
        Text(input.IntakeReference, 128); Text(input.Discrepancy, 1000, true);
        if ((input.Condition != "intact" || input.HeldContainers > 0 || input.ExcessContainers > 0) && input.Discrepancy is null) throw Invalid();
        return input with { };
    }
    public static void Route(ProcurementArrivalNoticeInput input, ProcurementRoute route) {
        Copy(input);
        if (route == ProcurementRoute.Ddo ? input.DdoRequestReference is null || input.DdoTelephoneReference is null :
            input.DdoRequestReference is not null || input.DdoTelephoneReference is not null) throw Invalid();
    }
    public static DomainValidationException Invalid() => new("invalid_procurement_receiving", "Record the actual supplier notice and observed container custody without inferring item stock");
}
