# Stock-source revision recovery, API and client verification — 29 September 2026

Base commit: 835ed60d65f2d6db91269a9a442e429453df142d. This checkpoint connects the governed settings-revision service to receipt recovery, protected API queries/commands and a typed frontend client. Staff revision screens and their persistent recovery adapter are still required.

## Delivered behavior

RecoverRevisionAsync accepts only the four revision operation names and the actor's own original key under a currently authorized location. It checks the stored request facts, audit history, exact result/hash, retained approval participants and applied revision where applicable. It reconstructs the request from server records, then replays it through current session/scope/person/MFA checks with requireReceipt enabled inside the transaction. No history-read grant is needed for recovery of one's own command. An absent lookup returns not_confirmed; a receipt disappearing between lookup and replay fails without running a new business writer. Later approved versions do not rewrite the original recovered result.

Current-source and revision-history queries apply exact location filters before totals and bounded pagination, order by immutable IDs and perform final authorization fences. An optional pool filter cannot expose foreign rows. Private records are loaded only after the applicable scope checks. The existing creation workflow and all applied migration behavior remain unchanged.

Nine additional protected API paths supply pool listing/current settings, revision proposal/list/detail, approve/reject/withdraw and receipt lookup. Commands retain expected-account, session, same-origin CSRF, idempotency and quoted If-Match checks. Proposal If-Match binds the pool version; review If-Match binds the immutable proposal version. Typed responses show explicit current/base/proposed settings, decision facts and exact decimal-string references. Actor/person evidence, receipt hashes, audit internals and raw BSON do not enter those projections.

The generated contract adds nine paths and eleven schemas. A structural comparison ignoring JSON object/set ordering found zero changed or removed preexisting paths or schemas. Both server export and generated-client drift checks passed. The large generated diff includes serialization reordering.

The frontend workflow validates exact Int64 strings, physical identity/location continuity, source-mode combinations, chronology and approved version increments. It freezes separate current/proposed review snapshots, preserves fractional-second source ages in exact milliseconds, verifies scoped ordered pages and binds outcomes to the reviewed pool/proposal/version. The generated client sends one command attempt with the original key. Recovery sends only the actor/operation/key and selected location; it has no resubmit or close action. Late responses cannot confirm a replaced view. Existing source setup reuses the same settings parser while retaining its separate required reason.

## Verification

The complete affected backend suite passed 158 executed cases with zero failures or skips in approximately 12 minutes. It covers the original setup workflow/permissions/warehouse choices, revision rules and transactions, migration 16, receipt and authorization races, protected HTTP flows and OpenAPI contracts. The new work contributes 18 service/query cases, six HTTP cases and one additional contract case; these are included in the final total, not added again.

Receipt deletion is injected after the actual recovery read and before replay for all four operations. Other races revoke the grant, expire MFA or remove current person bindings at that boundary. Changed hashes/results/participants, missing audits/applied history, cross-location recovery, foreign malformed history, another actor's key, stale competing proposals, self-review and malformed requests fail without new business effects. HTTP fixtures exercise actual application middleware, opaque sessions, CSRF and Mongo with synthetic approved identities.

The full frontend suite passed 547 normal cases, six future-date replay cases and seven legacy golden scenarios: 560 passing executions, no failures or skips. The 26 new workflow/client cases are included. Type checking, import-boundary checks and the production build passed. Lint has zero errors and the same nine preexisting warnings in customer-account.tsx, workspace.tsx and domain.test.ts.

Development evidence includes expected failures for missing recovery/query behavior, missing HTTP routes and missing closed contract choices, followed by green checks. Two test-fixture compile errors and a generated-client request missing its declared pagination fields were corrected before final verification. The first contract-export wrapper misread a PowerShell script's absent native exit code; the exporter itself succeeded, and both explicit drift checks subsequently passed.

Artifacts are retained under tmp/production/stock-source-revision-api-results. Final regression, frontend, contract comparison, source manifest and artifact hashes identify the verified boundary. Independent review remains pending reviewer capacity; local tests and inspection are not independent approval.

## Remaining integration and release boundary

Connect the source staff workspace to the revision client: show current versus proposed settings, stale-base warnings, explicit independent review, immutable history and inactive/preparatory outcomes. Extend the allowlisted operation journal and its recovery adapter for the four revision operations while persisting references only. Add real browser coverage for successive versions, competing proposals, reload recovery, current access/MFA changes, mobile layout and keyboard review. No new revision screen or browser completion is claimed by this checkpoint.

The existing source setup browser rehearsal is checked separately because its parser is shared. That rehearsal cannot prove the missing revision screen. Approval still causes no quantity, operational stock authority or financial effect. Active-source suspension/drain/handoff/reactivation, aliases, genuine accepted observations, lot authoring, downstream inventory/cost workflows, actual business policies and operations/pilot acceptance remain open. The full 97-report production goal stays active.

GitHub publication is authorized. Vercel still needs owning-account access; preparing an exact tracked source package is not deployment. No live Auth0/Mongo/.NET activation, production data migration or real business acceptance is inferred.

The existing source-setup staff rehearsal executed and passed against the built frontend, actual Production API, local HTTPS and real Mongo: one test, zero skips, approximately 28 seconds. Its desktop frozen review and 390-pixel mobile history screenshots were inspected and copied into this checkpoint's browser artifact folder. The known source-setup behavior remained intact after sharing its parser; this is not a revision-screen test. Together with the 158-case backend run, this is 159 distinct backend/browser passes.

After the successful final runs, generated Next.js declaration paths were restored and one extra blank line at the end of a test fixture was removed. No executable behavior changed. Final-results.json, source-hashes.json, artifact-hashes.json and owned-paths.txt retain the tested source and exact publication manifest.
Final TRX SHA256: regression 706e358501ed807050f0dd39ee70e5293766a53d2b43620a03a4c0103bfba3f5; source browser 878d2bdaf4691ff920e3067f6b1661faec93bff83165d05b9f4979bc36f116b2.
