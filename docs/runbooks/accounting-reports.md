# Accounting reports

The maintained full-profile entry is `/reporting/accounts`. It reads posted Mongo journals through the .NET API. The retained Release 1 pilot does not enable this workspace. This report is a cumulative trial balance and journal listing; it is not an approved accounting close.

## Access and selection

An active approved account needs `report.read` on the exact fulfillment organization, shop and channel. The book selector returns only configured active operational books in that scope. Buying, warehouse, system and historical grants do not confer whole-book financial access. Book setup permissions alone do not grant reporting access. Omi and S-Mart remain separate book/channel selections, including currency.

Choose a book and an inclusive calendar end date in `Asia/Jakarta`, then select **Tampilkan laporan**. The report includes persisted journal entries from the beginning of that book through the selected business date. Original postings and reversal postings remain separate. Account codes come from their associated frozen charts, and amounts use exact decimal strings. It does not recalculate invoices, inventory or valuation.

**Jurnal berikutnya** changes the visible journal page only. Server totals cover the entire selected snapshot. Loading, no configured book, no journal, invalid input, changed account, unavailable storage and selection-limit states remain explicit.

## CSV and retained read evidence

**Unduh CSV lengkap** exports every selected account and journal line from the original report, including pages that are not currently visible. The server persists the actor, scope, journal selection and source hashes with the report read. A new backdated posting does not silently join that export. Export requires fresh authority and the original read ID/hash, rechecks immutable source facts and records a separate read audit. A reference expires after fifteen minutes; reload the report if it expires or its configuration changes.

CSV uses UTF-8 with BOM, CRLF records and escaped quoted text. Potential spreadsheet formulas in text are neutralized; identifiers retain their Int64 digits and trusted exact amounts remain numeric. The client verifies the matching snapshot headers, private/no-store response, content type, safe filename, byte ceiling and CSV framing before offering a download. Failed or stale operations cancel private streams.

The bounded cumulative contract allows at most 128 visible books, 500 selected journals, 10,000 journal lines, 2,000 accounts, 64 related charts and an 8 MiB CSV. An oversized selection fails explicitly rather than omitting older rows or returning partial totals. An earlier date may fit; larger approved bulk reports need a separate supported workflow. Browser journal pages contain twenty entries.

## Persistence and operation

Additive migration 84 installs `accountingReportReadEvents` and its strict indexes/validator, preserving the existing accounting, contact and private-evidence owners. Each read/export records scope and source integrity evidence. It creates no journal, invoice, stock movement, financial approval or period-close decision. Include the read owner in normal Mongo backup and restore verification.

The API routes are POST `/api/v1/accounting/reports/books/query`, `/query` and `/export`. All require an authenticated current actor, exact origin/CSRF, `X-Expected-Actor-Id`, explicit `accounting-reporting` purpose and strict input. Export returns a CSV with `X-Read-Id`, `X-Snapshot-Read-Id` and `X-Snapshot-Hash`; its audit ID differs from the original snapshot read ID.

Consult the [progress ledger](../quality/production-progress.md) for actual local, staging and production evidence. This guide does not accept real Auth0, hosted API/Mongo connections, business books or operator permissions. Full reporting, aging and period-print requirements remain in the unchanged [97-item register](../requirements/unit-toko-report-2026-09-28.md).
