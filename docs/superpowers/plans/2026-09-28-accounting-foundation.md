# Transactional accounting foundation

Status: local implementation and scoped independent review accepted; publication pending. See [recorded verification](../../quality/accounting-foundation-acceptance-2026-09-28.md). Part of T05A, not completion of T05A or T12.

## Goal and binding scope

Implement real persisted balanced journal posting and cumulative period fencing that future procurement/billing commands can call in their own Mongo transaction. Preserve the complete production plan and all 97 report requirements. The private evidence workflow, genuine supplier-receipt integration, complete reconciliation/close UI and operational acceptance remain explicit dependent work.

Binding references: [production requirements](../../requirements/unit-toko-production.md), [T05A and T12](2026-09-27-unit-toko-production.md), [report execution](2026-09-28-report-completion.md), [PAY01](2026-09-28-report-payments-ocr.md).

## Constraints and resolved contracts

- Modules owns immutable contracts and pure validation; Platform owns Mongo implementations. No new frontend, provider, live finance grant, chart policy, legal issuer mapping or production activation is implied.
- Work only in the managed production worktree. Preserve unrelated ignore/layout/mentor-mockup edits. Do not modify applied migrations. No automatic seeding or production data changes.
- All monetary values use existing Money/Decimal128 and decimal-string boundaries; amounts and each debit/credit total are at most 999999999999999.99 with at most two fractional digits. IDs use positive Int64 internally and strings externally. Do not silently round.
- Canonical operational book identity is fulfillment organization + shop + channel + currency. Exclude warehouse, buyer branch, actor and grant shape. This is an operational partition, not an approved legal issuer.
- Every new post and every period transition writes the same book fence. Retain legacy cumulative behavior: closedThrough rejects posting in any month at or before it; backdated new entries invalidate all later review/approval snapshots. Future postings do not invalidate an earlier month's snapshot.
- Posting dates are explicit valid Jakarta business dates. A UTC-to-business-date helper must use Asia/Jakarta (including midnight tests), not machine local time. Do not infer invoice terms or tax rounding.
- Books and chart/account mappings must be explicitly configured, versioned and server-owned; absent/inactive/unknown configuration denies. Synthetic test fixtures may configure them. Do not add a permissive production provisioning endpoint.
- Each immutable journal has a source namespace, source document identity, event kind and occurrence/revision, canonical fingerprint, actor/grant provenance, original resource dimensions, explicit date/currency, 2–100 lines and balanced positive totals. Each line has one positive debit or credit and a server-resolved valid account reference. Caller arrays cannot mutate a validated entry.
- Namespaced source uniqueness is independent of HTTP idempotency. Same command key/body returns its original outer-command receipt after current authorization. A source duplicate discovered by the low-level posting writer aborts the whole caller transaction (same or changed content, with distinct typed conflict codes); it must never return success after another business mutation has already run. A future whole-command source replay may short-circuit before every effect. Document this contract prominently.
- PostJournal joins the supplied active Mongo transaction; it never opens/nests a transaction. The owning business command authorizes its effect; automatic receipt postings do not require granting staff discretionary journal.post. A manual journal wrapper, when later exposed, must require journal.post.
- Original entries and lines are append-only through application APIs. Reversal is a separately linked immutable entry; preserve original, require a new unique source and prevent a second reversal. It must use the original accounting content and current permitted posting period, not arbitrary replacement lines. Full independent manual-correction workflow remains T12.
- Book/period closure storage is a trusted internal boundary, not a public approval bypass. It requires exact approved data revision and matching snapshot plus the shared book fence; authorization/person proof/reconciliation prerequisites belong to the future approved outer workflow. No public close/reopen route or fabricated reconciled flag may be added in this substage. Tests clearly distinguish this storage proof from full T12 acceptance.
- Required journal audit and accounting outbox records share the caller session. Audit/outbox failure aborts journal/period/business changes. No external storage operations, notifications or other nontransactional side effects run inside transaction retry callbacks.

## Task 1 — Domain and caller contracts

Files: new bounded files under `backend/src/UnitToko.Modules/Accounting/`; tests under `backend/tests/UnitToko.Tests/Accounting/`.

- [x] Write behavior tests first for line shape, balance, aggregate bounds, exact precision, malformed/default values, immutable copies, source identities, currency, book canonicalization and Jakarta month boundaries.
- [x] Capture the expected missing-behavior failure, then implement pure contracts/policies.
- [x] Define exact posting, reversal, period and chart snapshot interfaces before persistence. Use actual server-owned accounts, never silently accept arbitrary browser account codes.

## Task 2 — Strict persisted schema and migration

Files: new `AccountingSchemas.cs`, `AccountingMigration.cs` in Platform/Mongo/Migrations; register next version in CollectionMigrations; migration/accounting tests.

- [x] Add immutable chart versions, canonical books, strict periods and journals with required source, nested line precision/bounds/balance, date/month parity, scope and revision validation; enforce unique canonical book/month/source/reversal identities and lookup indexes.
- [x] Preflight existing journal/period/configuration data and exact supported indexes/validators before any target DDL; malformed ambiguous records fail without silent backfill or policy invention. Preserve unrelated restrictions and the existing source uniqueness index when its semantics remain equivalent. Applied v1–v11 remain unchanged.
- [x] Handle interrupted/resumed migration and allocator high-water marks. Preserve historically scoped fixture tests at their intended migration version; do not weaken financial assertions.

## Task 3 — Real transactional posting and period protection

Files: Platform/Accounting session-taking writer/repository/period guard and accounting audit support; integration tests.

- [x] Load the book, current chart and exact source/resource inside the caller session; require active supplied transaction and matching database/session ownership.
- [x] Validate/re-resolve account lines and source fingerprint before writes; fence the canonical book and period, reject locked dates, append immutable journal, invalidate affected later period snapshots and persist audit/outbox together.
- [x] Implement linked exact reversal and duplicate prevention without mutating original entries.
- [x] Implement the internal period transition/fence boundary required to prove close/post concurrency, retaining immutable history. Do not expose operational approval until T12's real reconciliation/person-authority workflow exists.
- [x] Exercise the writer inside an actual AuthorizationStore outer command with synthetic current actor/grants and a controlled business mutation. Show accounting/audit failure rolls all effects back, including receipt/outbox. This proves composition but is explicitly not a real supplier-receipt acceptance claim.

## Task 4 — Verification, review and recorded handoff

- [x] Run focused domain tests and real local replica-set integration tests: duplicate source across keys/actors, changed-source fingerprint, exact request replay, uncertain commits, downstream failures, missing chart/book, wrong source scope, reversal duplicate, absent/closed periods, late/backdated posting and both deterministic close/post orderings.
- [x] Include authorization revocation/final-session expiry coverage for any new outer command wrapper; do not introduce an unauthenticated production caller to make tests easier.
- [x] Test strict nested BSON numeric validation and migration preflight/resumption. Verify applied migrations and prior numeric constraints remain compatible.
- [x] Run appropriate existing persistence/numeric/authorization regressions once; record failed fixtures separately before corrections. Do not claim the whole suite passed from filtered results.
- [x] Obtain independent review of transaction composition, source replay, cumulative periods, authority boundary and migration. Resolve material findings with regression evidence.
- [ ] Commit only owned files and publish the verified checkpoint to the existing GitHub branch. Vercel remains separately pending authorized account access and does not host the new .NET runtime.

## Completion evidence and remaining gates

This substage requires callable persisted accounting primitives and real transaction/race evidence. It does not satisfy full T05A until private evidence and genuine procurement composition are proven. T12 retains source reconciliation, reporting, full independent close/reopen/correction workflows and UI. Approved chart/currency/finance policy, actual reviewer appointments, production grants, hosting, data cutover and pilot remain unproven external gates. No requirement is removed to close a checkbox.

## Frozen implementation refinements after independent review

- Trusted namespace-specific `IAccountingSourceReader` resolves source revision, canonical book, exact stored resource dimensions, explicit business date and immutable mapping-key lines in the supplied session. Unknown source adapters deny; no generic browser source/lines adapter is registered. Synthetic persisted source fixtures prove composition without claiming procurement exists.
- Ordinary posts load the active book's current immutable chart version and active accounts. Entries retain chart version, stable account IDs and historical account snapshots. Configuration activation/chart-pointer changes must share the book fence; missing posting periods deny rather than silently provisioning production periods.
- Exact reversals swap original debit/credit lines and retain original immutable chart/account snapshots even after an account is retired. They still need an active book, permitted current date, trusted source and unique original-entry reversal link. No remapping through today's chart.
- A duplicate-source path aborts the supplied transaction before throwing the typed `journal_source_duplicate` or `journal_source_conflict` error. Test an owner that catches the exception: its earlier business effect must not be committable. Posting/audit failures must fail closed with the same atomicity; preserve driver transient labels for the outer bounded retry mechanism.
- Period closure storage transition is internal, loads approved revision/stage history from stored records, and verifies a deterministic journal-only cumulative snapshot/digest inside the fenced transaction. The digest is a storage snapshot, not evidence of complete reconciliation or person approval. T12 supplies those real outer gates.
- ID reservation uses the existing monotonic IdAllocator outside the business transaction (caller preallocation is also valid). This is the explicit permitted retry-side-effect exception: aborted attempts may leave gaps, but allocated IDs are never rolled back/reused. No financial state, notification or object side effect receives that exception; results expose IDs only after commit.
- Trusted source resolution must pin the authoritative source revision with a conditional write in the same session, or require an equivalent existing caller-owned version write. A read-only snapshot of the source cannot establish that later concurrent edits are compatible. Include a deterministic source-change race; adapters must preserve this contract before activation.
