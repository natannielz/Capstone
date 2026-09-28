using System.Globalization;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Accounting;
internal static class AccountingBson
{
    internal static OperationalBookKey ReadKey(BsonValue value) => new(value["organizationId"].AsInt64, value["shopId"].AsInt64, value["channelId"].AsInt64, value["currency"].AsString);
    internal static BsonDocument Key(OperationalBookKey key) => new()
    {
        {
            "organizationId",
            key.OrganizationId
        },
        {
            "shopId",
            key.ShopId
        },
        {
            "channelId",
            key.ChannelId
        },
        {
            "currency",
            key.Currency
        }
    };
    internal static JournalSource ReadSource(BsonValue value) => new(value["namespace"].AsString, value["documentId"].AsInt64, value["eventKind"].AsString, value["occurrence"].AsInt64);
    internal static BsonDocument Source(JournalSource source) => new()
    {
        {
            "namespace",
            source.Namespace
        },
        {
            "documentId",
            source.DocumentId
        },
        {
            "eventKind",
            source.EventKind
        },
        {
            "occurrence",
            source.Occurrence
        }
    };
    internal static long? NullableId(BsonValue value) => value.IsBsonNull ? null : value.AsInt64;
    internal static BsonValue Optional(long? value) => value.HasValue ? new BsonInt64(value.Value) : BsonNull.Value;
    internal static JournalDimensions ReadDimensions(BsonValue value) => new(value["organizationId"].AsInt64, value["shopId"].AsInt64, value["channelId"].AsInt64, NullableId(value["warehouseId"]), NullableId(value["buyingOrganizationId"]), NullableId(value["branchId"]));
    internal static BsonDocument Dimensions(JournalDimensions value) => new()
    {
        {
            "organizationId",
            value.OrganizationId
        },
        {
            "shopId",
            value.ShopId
        },
        {
            "channelId",
            value.ChannelId
        },
        {
            "warehouseId",
            Optional(value.WarehouseId)
        },
        {
            "buyingOrganizationId",
            Optional(value.BuyingOrganizationId)
        },
        {
            "branchId",
            Optional(value.BranchId)
        }
    };
    internal static ChartSnapshot ReadChart(BsonDocument row) => new(row["_id"].AsInt64, row["bookId"].AsInt64, row["version"].AsInt64, row["mappings"].AsBsonArray.Select(mapping => new AccountMapping(mapping["mappingKey"].AsString, mapping["accountId"].AsInt64, mapping["accountCode"].AsString, mapping["active"].AsBoolean)));
    internal static ResolvedJournalLine ReadLine(BsonValue line) => new(line["mappingKey"].AsString, line["accountId"].AsInt64, line["accountCode"].AsString, new(line["debit"].AsDecimal), new(line["credit"].AsDecimal));
    internal static BsonDocument Line(ResolvedJournalLine line) => new()
    {
        {
            "mappingKey",
            line.MappingKey
        },
        {
            "accountId",
            line.AccountId
        },
        {
            "accountCode",
            line.AccountCode
        },
        {
            "debit",
            new BsonDecimal128(line.Debit.Value)
        },
        {
            "credit",
            new BsonDecimal128(line.Credit.Value)
        }
    };
    internal static JournalDraft ReadDraft(BsonDocument row)
    {
        var draft = JournalDraft.FromResolved(ReadSource(row["source"]), row["sourceRevision"].AsInt64, ReadKey(row["bookKey"]), ReadDimensions(row["dimensions"]), DateOnly.ParseExact(row["businessDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture), row["chartId"].AsInt64, row["chartVersion"].AsInt64, row["lines"].AsBsonArray.Select(ReadLine), NullableId(row["reversalOf"]));
        if (draft.Fingerprint != row["fingerprint"].AsString || draft.Source.EventKey != row["sourceEvent"].AsString || draft.Total.Value != row["total"].AsDecimal || AccountingCalendar.Month(draft.Date) != row["month"].AsString || draft.Book.Currency != row["currency"].AsString)
            throw AccountingValidation.Invalid("journal_integrity_failure");
        return draft;
    }

    internal static BsonDocument Journal(long id, long bookId, JournalDraft draft, ActorContext actor, AccountingWriteMetadata metadata, DateTimeOffset now) => new()
    {
        {
            "_id",
            id
        },
        {
            "schemaVersion",
            1
        },
        {
            "bookId",
            bookId
        },
        {
            "bookKey",
            Key(draft.Book)
        },
        {
            "source",
            Source(draft.Source)
        },
        {
            "sourceEvent",
            draft.Source.EventKey
        },
        {
            "sourceRevision",
            draft.SourceRevision
        },
        {
            "fingerprint",
            draft.Fingerprint
        },
        {
            "actorId",
            actor.UserId
        },
        {
            "actorGrantVersion",
            actor.GrantVersion
        },
        {
            "dimensions",
            Dimensions(draft.Dimensions)
        },
        {
            "businessDate",
            draft.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        },
        {
            "month",
            AccountingCalendar.Month(draft.Date)
        },
        {
            "currency",
            draft.Book.Currency
        },
        {
            "chartId",
            draft.ChartId
        },
        {
            "chartVersion",
            draft.ChartVersion
        },
        {
            "total",
            new BsonDecimal128(draft.Total.Value)
        },
        {
            "lines",
            new BsonArray(draft.Lines.Select(Line))
        },
        {
            "reversalOf",
            Optional(draft.ReversalOf)
        },
        {
            "createdAt",
            now.UtcDateTime
        },
        {
            "reason",
            metadata.Reason
        },
        {
            "correlationId",
            metadata.CorrelationId
        }
    };
    internal static AccountingStorageSnapshot Snapshot(long bookId, BsonDocument period, IEnumerable<BsonDocument> journals)
    {
        using var snapshot = new AccountingSnapshotBuilder(bookId, period);
        foreach (var row in journals.OrderBy(row => row["businessDate"].AsString, StringComparer.Ordinal).ThenBy(row => row["_id"].AsInt64))
            snapshot.Add(row);
        return snapshot.Finish();
    }
}
