using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace UnitToko.Platform.Accounting;
// One bounded journal at a time. The same encoding is used by runtime cursors and migration graph checks.
internal sealed class AccountingSnapshotBuilder : IDisposable
{
    private readonly IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long bookId, periodId, revision;
    private decimal debit, credit;
    private int count;
    private string? lastDate;
    private long lastId;
    private bool finished;
    internal AccountingSnapshotBuilder(long bookId, BsonDocument period)
    {
        this.bookId = bookId;
        periodId = period["_id"].AsInt64;
        revision = period["dataRevision"].AsInt64;
        AddText("accounting-storage-snapshot-v1");
        AddText(bookId.ToString(CultureInfo.InvariantCulture));
        AddText(period["month"].AsString);
        AddText(revision.ToString(CultureInfo.InvariantCulture));
    }

    internal void Add(BsonDocument row)
    {
        if (finished)
            throw new InvalidOperationException("Snapshot already completed");
        var date = row["businessDate"].AsString;
        var id = row["_id"].AsInt64;
        if (row["bookId"].AsInt64 != bookId || lastDate is not null && (string.CompareOrdinal(date, lastDate) < 0 || date == lastDate && id <= lastId))
            throw new InvalidOperationException("Snapshot requires canonical book/date/id order");
        var draft = AccountingBson.ReadDraft(row);
        foreach (var line in draft.Lines)
        {
            debit = checked(debit + line.Debit.Value);
            credit = checked(credit + line.Credit.Value);
        }

        count = checked(count + 1);
        AddText(id.ToString(CultureInfo.InvariantCulture));
        AddText(draft.Fingerprint);
        AddText(date);
        lastDate = date;
        lastId = id;
    }

    internal AccountingStorageSnapshot Finish()
    {
        if (finished)
            throw new InvalidOperationException("Snapshot already completed");
        finished = true;
        AddText(count.ToString(CultureInfo.InvariantCulture));
        return new(bookId, periodId, revision, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), debit, credit, count);
    }

    private void AddText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        digest.AppendData(length);
        digest.AppendData(bytes);
    }

    public void Dispose() => digest.Dispose();
}
