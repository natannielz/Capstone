# Stock channel mapping API and client verification — 29 September 2026

Base commit: 55a20272eb1223564e8750293cda426f4f427e2a.

This checkpoint connects governed mapping configuration to a protected API and typed frontend client. It advances CAT02 while retaining the full production plan and all 97 report requirements.

## Implemented behavior

Eight new routes expose current configuration, proposals, exact pool/channel history, scoped proposal detail, independent approval/rejection, maker withdrawal and receipt-only recovery. All use the existing authenticated session, expected-actor, CSRF, permission and transaction boundaries. Mutations require an original request key and the applicable expected version.

Proposal version, bound source-settings version and mapping version are separate exact decimal strings. Current projections explicitly distinguish absent, disabled, stale and current mappings. They show canonical ownership, the requested channel and the approved source settings; current means configuration binding, not accepted stock authority or promised availability. No route activates a source or changes stock, accounting or financial balances.

The new scoped detail service checks both channel permissions, filters the exact selected pool/channel before loading private history, validates retained source ownership and applies final current-authority checks even when a record is absent. Historical proposals remain readable after later settings versions. Revocation during either side of the read prevents returning the result.

Public DTOs exclude actor/provider/person evidence, raw audits, receipt hashes and internal fences. Required booleans cannot silently default from missing request fields. Closed purposes, operation/status choices and exact numeric strings are carried into the generated frontend contract. Comparison with the previous contract proves zero preexisting path/schema changes or removals, all security schemes unchanged, eight added paths and nine added schemas.

The frontend client validates response shape, source/pool/channel identities, physical ownership, chronology and version relationships. It freezes reviewed commands, refuses no-op changes, retains explicit reapproval after settings changes and sends each command once. Late or mismatched results cannot confirm another review. Recovery sends only the original actor/operation/key plus the deliberately selected pool/channel, requires the original server receipt and does not resubmit or close uncertain work. Recovery keeps command permissions separate from history-read permission.

## Verification

The initial two HTTP tests reproduced the missing routes, then passed after implementation. An intermediate compilation failure was a missing DTO import and was corrected before runtime tests. The new OpenAPI test first failed because the choices were not constrained; all seven HTTP cases and that contract case then passed.

The broader affected backend regression executed 145 cases: 144 passed and one failed because the existing source security-contract test still expected 17 routes. The count was updated to 25 while preserving required actor/CSRF/idempotency/version headers, authentication and documented error checks for every route. Application behavior was not changed to accommodate that assertion.

The final focused rerun passed all 13 corrected-contract, new HTTP and detail-read cases with zero failures/skips. Combined with the 144 passing cases from the broader run, all 145 distinct affected backend cases have passing evidence (157 passing executions across those two runs). The original one-failure run is retained; it is not represented as a completely green run. Coverage includes both scopes, mid-read grant revocation, current session/MFA, independent reviews, stale settings/mappings, privacy, exact large IDs, filtered paging, strict input, terminal decisions, rollback and receipt-only recovery.

All 20 new frontend cases passed after initial missing-module failures. The complete frontend verification passed 594 normal tests, six future-date replay executions and seven legacy golden cases, for 607 passed executions with zero failures/skips. Type checking, import boundaries, lint and the production build passed. Lint retains nine preexisting warnings and no errors. Both server and generated-client drift checks passed. Only generated Next development-path changes were restored after the build.

No new screen or browser workflow is claimed in this checkpoint. Existing UI behavior is covered by the full frontend suite; mapping journal/staff/browser integration is the next implementation step. Artifacts, failed runs, final summaries, exact owned-file manifest and source/artifact hashes are retained under tmp/production/stock-source-alias-api-results.

## Remaining work

Integrate an independent stock.source.alias recovery adapter before the generic stock.source namespace, then the staff mapping editor, frozen review/history, exact login/MFA navigation and real browser reload/revocation/lost-response/mobile coverage.

Authorized canonical inventory consumers, source suspension/drain/handoff/reactivation, accepted per-channel policy/observations, lots/costs and downstream workflows remain required. Actual D01/D02/D04 and other business/provider/operations/pilot acceptance are external gates; independent review is still pending reviewer availability. Vercel owning-account access remains pending, and a prepared archive is not a deployed build.

See [source authority](../architecture/stock-authority-contract.md), [migration evidence](stock-source-alias-migration-verification-2026-09-29.md) and [the migration runbook](../runbooks/stock-source-alias-migration.md).
