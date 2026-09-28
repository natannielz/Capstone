using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Accounting;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class AccountingMigrationGraph
{
    internal static void Validate(Dictionary<string, List<BsonDocument>> rows)
    {
        try
        {
            ValidateCore(rows);
        }
        catch (Exception exception)when (exception is DomainValidationException or FormatException or OverflowException or ArgumentException)
        {
            throw AccountingMigration.Invalid("accounting", "canonical content");
        }
    }

    private static void ValidateCore(Dictionary<string, List<BsonDocument>> rows)
    {
        var books = rows["accountingBooks"].ToDictionary(row => row["_id"].AsInt64);
        var charts = rows["accountingChartVersions"].ToDictionary(row => row["_id"].AsInt64);
        var periods = rows["periods"].ToDictionary(row => row["_id"].AsInt64);
        var journals = rows["journalEntries"].ToDictionary(row => row["_id"].AsInt64);
        var histories = rows["accountingPeriodHistory"].ToDictionary(row => row["_id"].AsInt64);
        var audits = rows["accountingAuditEvents"];
        void Require(bool valid, string collection)
        {
            if (!valid)
                throw AccountingMigration.Invalid(collection, "relationship/history");
        }

        foreach (var book in books.Values)
        {
            Require(charts.TryGetValue(book["chartVersionId"].AsInt64, out var chart) && chart["bookId"] == book["_id"], "accountingBooks");
            var closed = periods.Values.Where(period => period["bookId"] == book["_id"] && period["state"] == "closed").Select(period => period["month"].AsString).Order(StringComparer.Ordinal).LastOrDefault();
            Require(closed is null ? book["closedThrough"].IsBsonNull : book["closedThrough"] == closed, "accountingBooks");
        }

        foreach (var chart in charts.Values)
        {
            Require(books.ContainsKey(chart["bookId"].AsInt64), "accountingChartVersions");
            _ = AccountingBson.ReadChart(chart);
        }

        foreach (var journal in journals.Values)
        {
            _ = AccountingBson.ReadDraft(journal);
            Require(books.TryGetValue(journal["bookId"].AsInt64, out var book) && CatalogSchemas.SemanticEquals(journal["bookKey"], book["key"]), "journalEntries");
            Require(charts.TryGetValue(journal["chartId"].AsInt64, out var chart) && chart["bookId"] == journal["bookId"] && chart["version"] == journal["chartVersion"], "journalEntries");
            foreach (var line in journal["lines"].AsBsonArray)
                Require(chart!["mappings"].AsBsonArray.Any(mapping => mapping["mappingKey"] == line["mappingKey"] && mapping["accountId"] == line["accountId"] && mapping["accountCode"] == line["accountCode"]), "journalEntries");
            Require(periods.Values.Any(period => period["bookId"] == journal["bookId"] && period["month"] == journal["month"]), "journalEntries");
            if (journal["reversalOf"].IsInt64)
            {
                Require(journals.TryGetValue(journal["reversalOf"].AsInt64, out var original) && original["reversalOf"].IsBsonNull && original["bookId"] == journal["bookId"] && original["chartId"] == journal["chartId"] && original["chartVersion"] == journal["chartVersion"] && CatalogSchemas.SemanticEquals(original["dimensions"], journal["dimensions"]) && original["lines"].AsBsonArray.Count == journal["lines"].AsBsonArray.Count, "journalEntries");
                for (var i = 0; i < journal["lines"].AsBsonArray.Count; i++)
                {
                    var left = original!["lines"][i];
                    var right = journal["lines"][i];
                    Require(left["mappingKey"] == right["mappingKey"] && left["accountId"] == right["accountId"] && left["accountCode"] == right["accountCode"] && left["debit"] == right["credit"] && left["credit"] == right["debit"], "journalEntries");
                }
            }

            var linked = audits.Where(audit => audit["journalId"] == journal["_id"]).ToArray();
            Require(linked.Length == 1, "accountingAuditEvents");
            var audit = linked[0];
            Require(audit["eventKey"] == $"accounting:journal:{journal["_id"].AsInt64}" && audit["kind"] == (journal["reversalOf"].IsBsonNull ? "journal.posted" : "journal.reversed") && audit["fingerprint"] == journal["fingerprint"] && ProvenanceMatches(audit, journal), "accountingAuditEvents");
        }

        foreach (var history in histories.Values)
        {
            Require(periods.TryGetValue(history["periodId"].AsInt64, out var period) && period["bookId"] == history["bookId"] && period["month"] == history["month"] && period["version"].AsInt64 >= history["version"].AsInt64, "accountingPeriodHistory");
            BsonDocument? previous = null;
            if (history["previousHistoryId"].IsInt64)
            {
                Require(histories.TryGetValue(history["previousHistoryId"].AsInt64, out previous) && previous["periodId"] == history["periodId"] && previous["version"].AsInt64 + 1 == history["version"].AsInt64 && previous["createdAt"].ToUniversalTime() <= history["createdAt"].ToUniversalTime() && previous["stage"] != "closed", "accountingPeriodHistory");
            }
            else
                Require(history["version"] == 2L, "accountingPeriodHistory");
            switch (history["stage"].AsString)
            {
                case "invalidated":
                    Require(history["dataRevision"].AsInt64 == (previous is null ? 1 : checked(previous["dataRevision"].AsInt64 + 1)), "accountingPeriodHistory");
                    break;
                case "submitted":
                    Require(history["dataRevision"].AsInt64 == (previous is null ? 0 : previous["dataRevision"].AsInt64), "accountingPeriodHistory");
                    break;
                case "approved":
                    Require(previous is not null && previous["stage"] == "submitted" && previous["dataRevision"] == history["dataRevision"] && previous["snapshotHash"] == history["snapshotHash"] && previous["actorId"] != history["actorId"], "accountingPeriodHistory");
                    break;
                case "closed":
                    Require(previous is not null && previous["stage"] == "approved" && previous["dataRevision"] == history["dataRevision"] && previous["snapshotHash"] == history["snapshotHash"] && previous["actorId"] != history["actorId"] && previous["previousHistoryId"].IsInt64, "accountingPeriodHistory");
                    Require(histories.TryGetValue(previous!["previousHistoryId"].AsInt64, out var submitted) && submitted["stage"] == "submitted" && submitted["actorId"] != history["actorId"], "accountingPeriodHistory");
                    break;
            }

            if (history["stage"] == "invalidated" || history["stage"] == "closed")
            {
                var linked = audits.Where(audit => audit["eventKey"] == $"accounting:period:{history["periodId"].AsInt64}:{history["version"].AsInt64}").ToArray();
                Require(linked.Length == 1, "accountingAuditEvents");
                var audit = linked[0];
                Require(audit["periodId"] == history["periodId"] && audit["kind"] == (history["stage"] == "closed" ? "period.closed" : "period.invalidated") && audit["fingerprint"] == history["snapshotHash"] && ProvenanceMatches(audit, history), "accountingAuditEvents");
            }
        }

        foreach (var period in periods.Values)
        {
            Require(books.ContainsKey(period["bookId"].AsInt64), "periods");
            if (period["lastHistoryId"].IsInt64)
                Require(histories.TryGetValue(period["lastHistoryId"].AsInt64, out var last) && last["periodId"] == period["_id"] && last["version"] == period["version"] && last["dataRevision"] == period["dataRevision"] && last["stage"] == (period["state"] == "open" ? "invalidated" : period["state"]) && last["snapshotHash"] == period["snapshotHash"], "periods");
            else
                Require(period["state"] == "open" && period["version"] == 1L && period["dataRevision"] == 0L, "periods");
            if (period["state"] != "open")
            {
                var snapshot = AccountingBson.Snapshot(period["bookId"].AsInt64, period, journals.Values.Where(journal => journal["bookId"] == period["bookId"] && string.CompareOrdinal(journal["month"].AsString, period["month"].AsString) <= 0));
                Require(snapshot.Hash == period["snapshotHash"].AsString, "periods");
            }
        }

        foreach (var audit in audits)
        {
            Require(books.ContainsKey(audit["bookId"].AsInt64), "accountingAuditEvents");
            if (audit["journalId"].IsInt64)
                Require(journals.TryGetValue(audit["journalId"].AsInt64, out var journal) && journal["bookId"] == audit["bookId"], "accountingAuditEvents");
            if (audit["periodId"].IsInt64)
                Require(histories.Values.Any(history => history["periodId"] == audit["periodId"] && audit["eventKey"] == $"accounting:period:{history["periodId"].AsInt64}:{history["version"].AsInt64}" && history["bookId"] == audit["bookId"] && (history["stage"] == "invalidated" || history["stage"] == "closed") && audit["kind"] == (history["stage"] == "closed" ? "period.closed" : "period.invalidated") && audit["fingerprint"] == history["snapshotHash"] && ProvenanceMatches(audit, history)), "accountingAuditEvents");
        }
    }

    private static bool ProvenanceMatches(BsonDocument audit, BsonDocument original) => audit["bookId"] == original["bookId"] && audit["actorId"] == original["actorId"] && audit["actorGrantVersion"] == original["actorGrantVersion"] && audit["reason"] == original["reason"] && audit["correlationId"] == original["correlationId"] && audit["createdAt"].ToUniversalTime() >= original["createdAt"].ToUniversalTime();
}
