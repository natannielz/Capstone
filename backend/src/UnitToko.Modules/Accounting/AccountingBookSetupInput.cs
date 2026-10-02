using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Accounting;

public sealed record AccountingBookMappingInput(string MappingKey, string AccountCode);
public sealed record AccountingBookSetupInput(OperationalBookKey Key, string InitialMonth, ImmutableArray<AccountingBookMappingInput> Mappings);
public enum AccountingBookSetupDecision { Approve, Reject }

public static class AccountingBookSetupRules
{
    public static OperationalBookKey Key(OperationalBookKey key) {
        if (key is null) throw Invalid();
        key.Validate(); if (key.Currency != "IDR") throw Invalid(); return key;
    }
    public static AccountingBookSetupInput Copy(AccountingBookSetupInput input) {
        if (input is null || input.Mappings.IsDefault || input.Mappings.Length is < 1 or > 1000) throw Invalid();
        Key(input.Key); AccountingCalendar.ValidateMonth(input.InitialMonth);
        var mappings = input.Mappings.Select(value => {
            if (value is null) throw Invalid(); AccountingValidation.Code(value.MappingKey); AccountingValidation.Code(value.AccountCode);
            return value with { };
        }).OrderBy(value => value.MappingKey, StringComparer.Ordinal).ToImmutableArray();
        if (mappings.Select(value => value.MappingKey).Distinct(StringComparer.Ordinal).Count() != mappings.Length) throw Invalid();
        return input with { Key = input.Key with { }, Mappings = mappings };
    }
    public static DomainValidationException Invalid() => new("invalid_accounting_book_setup", "Choose an explicit book, initial month and reviewed account mappings");
}
