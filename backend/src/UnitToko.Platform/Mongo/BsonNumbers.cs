using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Mongo;

public static class BsonNumbers
{
    public static BsonInt64 Id(LongId id)
    {
        if (id.Value <= 0) throw new DomainValidationException("invalid_id", "ID must be a positive Int64");
        return new BsonInt64(id.Value);
    }
    public static LongId ReadId(BsonValue value) => value.IsInt64 && value.AsInt64 > 0
        ? new LongId(value.AsInt64)
        : throw new DomainValidationException("invalid_id", "Stored ID must be a positive BSON Int64");
    public static BsonDecimal128 Money(Money value)
    {
        _ = new Money(value.Value);
        return new BsonDecimal128(value.Value);
    }
    public static Money ReadMoney(BsonValue value)
    {
        if (!value.IsDecimal128)
            throw new DomainValidationException("invalid_money", "Stored money must be BSON Decimal128");
        return UnitToko.Modules.Accounting.Money.Parse(value.AsDecimal128.ToString());
    }
    public static BsonDecimal128 JournalAmount(SignedMoney value)
    {
        _ = new SignedMoney(value.Value);
        return new BsonDecimal128(value.Value);
    }
    public static SignedMoney ReadJournalAmount(BsonValue value)
    {
        if (!value.IsDecimal128)
            throw new DomainValidationException("invalid_money", "Stored journal amount must be BSON Decimal128");
        return SignedMoney.Parse(value.AsDecimal128.ToString());
    }
    public static BsonInt32 Quantity(int value) => new(DomainValues.ParseQuantity(value));
    public static int ReadQuantity(BsonValue value) => value.IsInt32
        ? DomainValues.ParseQuantity(value.AsInt32)
        : throw new DomainValidationException("invalid_quantity", "Stored quantity must be BSON Int32");
    public static BsonDecimal128 Rate(string value) => new(NumericContracts.ParseRate(value));
    public static decimal ReadRate(BsonValue value) => value.IsDecimal128
        ? NumericContracts.ParseRate(value.AsDecimal128.ToString())
        : throw new DomainValidationException("invalid_rate", "Stored rate must be BSON Decimal128");
}
