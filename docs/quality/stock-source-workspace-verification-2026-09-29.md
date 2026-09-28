# Stock-source staff workspace verification — 29 September 2026

Source base: 58bc371de6b38e3604324ee8178547efad5285f8. This checkpoint completes the new-pool source-setup staff surface; it does not complete CAT02 or authorize stock activation.

## Delivered behavior

The staff page at /inventory/sources supports separate proposal, review and history permissions. It loads currently authorized warehouse/channel choices from reviewed organization profiles, requires explicit source/control/freshness/buffer/owner/handoff settings, and shows the immutable proposed configuration before a deliberate confirmation. External-ledger proposals also require an explicit external namespace and reservation protocol reference. There are no invented operational defaults.

Approvers use the existing fresh-MFA path. A proposal must be read through a separately granted history permission before the UI can prepare a decision. The backend still checks current scope, assurance and person-level separation for every operation; the original maker alone may withdraw. Approval visibly creates an inactive pool and does not change available stock, balances or movements.

The additional protected warehouse query admits three closed purposes, returns at most 256 active authorized locations, and reads canonical names and actual ancestry. Shop assignments expand only within their organization/shop/channel. Concurrent profile rename, grant revocation or warehouse suspension cannot commit stale choice results through the final authority fence. A missing or malformed reviewed profile fails explicitly; no synthetic display names are inserted into production data.

Account task discovery now includes the three explicit source permissions without admitting source-only users to the administrative permission catalog. An authorized source reviewer whose MFA has aged can still reach verification; actual review queries/decisions remain denied until verification is fresh. Revoked grants remove the task capability.

## Recovery and privacy

The shared operation journal now allowlists only source propose/approve/reject/withdraw. It persists actor, operation, key and creation time only. Settings, reasons, source policies and identity evidence remain out of browser storage.

The client and editor preserve exact Int64 strings, freeze reviewed input, abort obsolete reads, guard the mounted account/session, prevent duplicate dispatch and retain the original key after an uncertain response. Reload recovery requires a deliberate authorized warehouse/channel choice. Missing, malformed, denied, late or location-mismatched recovery never clears the reference or creates a new submission. Source operations cannot use access-administration recovery or its close path.

The source UI and parsers expose only the public projection. Expired or replaced account views hide previous details. Confirmation and result panels receive focus; selectors use stable labels that exclude their option text.

## Executed checks

- The first warehouse route checks failed before implementation; an initial strict-schema incompatibility was corrected before the passing 48-case warehouse/source regression.
- The account-discovery regression failed with an empty task list before the capability fix.
- The corrected navigation/MFA/provider/migration/contract run passed 16 tests, with zero failures or skips.
- Frontend regression passed 521 main cases, six future-date cases and seven preserved-behavior cases: 534 passes, no failures/skips. Thirty are focused source client/editor/workflow/recovery/navigation cases.
- Production build and TypeScript checks passed. Lint passed with nine existing warnings outside the new source files. Server OpenAPI and generated frontend drift checks passed.
- Both real-browser fixtures executed and passed against the built frontend, production-mode API and disposable MongoDB: two passes, no failures/skips, 36 seconds.

The new source browser scenario performs real sign-in and logout for two distinct synthetic people; CSRF/actor/foreign-scope rejection; explicit web and external proposals; self-approval denial; two lost successful deliveries with the same key; reload/receipt recovery without resubmission; withdrawal; independent approval and rejection; keyboard confirmation; and a 390-pixel mobile history check. C# then checks three proposals, three terminal decisions, one inactive pool, six matching audits/receipts, unchanged physical/accounting data, and no remaining active session.

The browser first revealed missing task discovery, then a selector-label issue. Both were corrected and the real flow rerun successfully. These intermediate failures remain in the evidence directory rather than being represented as passes. Desktop 1280-pixel review and 390-pixel mobile history screenshots were inspected; no overflow or overlapping content was observed. This is targeted accessibility verification, not full WCAG certification.

## Published predecessor failure and correction

GitHub workflow [36470752275](https://github.com/natannielz/Capstone/actions/runs/36470752275) belongs to the base commit. Frontend and the separate real count-browser job passed, but the backend had 1,917 passes, three failures and 72 explicit skips.

The three failures were historical migration-chain assertions that did not include migration 15's four new collections. Their explicit DDL allowlists now include stockSourceProposals, stockSourceDecisions, inventoryPoolRegistrations and inventoryPools. Majority write concern, stronger-validator preservation, previous migration history and alternate-index assertions remain intact. All three corrected interruption cases passed locally. The new publication requires its own complete GitHub check; the failed predecessor is not described as green.

## Artifacts and limits

Evidence is under tmp/production/stock-source-ui-results. The owned-file manifest and source hashes bind the checks to the candidate files. Intermediate/red artifacts are retained separately.

| Artifact | SHA256 |
| --- | --- |
| source-choices-races.trx | 70addc43030efe1b8d3bf4ebabc5c016c1c17a867f61230711872d29d7970f27 |
| source-navigation-migration-provider.trx | 87dc62d6be8b1204a41384630d4ad859d3f9c4ff2fcf06375bdf6c1652044bfa |
| source-browser-final.trx | 28d64b485f45a4a5032b022ca680a26eefba5791bda1109f4c91026e4f9fced2 |

See [the browser runbook](../runbooks/stock-source-browser-rehearsal.md) for reproduction. Only the provider and business fixtures are synthetic; application authorization, cookies, CSRF, transactions and persistence are real code paths.

Existing-pool revisions/aliases, accepted authority activation, governed observations/lots, full inventory storage deployment, receiving/conversion/reservation/valuation workflows, load/restore, independent review and branch-pilot acceptance remain open. Actual D01/D04 facts and other recorded business gates have not been invented. The full production/report goal remains active. GitHub/Vercel publication is authorized, but Vercel owning-account access is still unavailable; a prepared release archive is not a deployment.

## Final affected regression

The final source/setup/warehouse/access/stock-count API regression passed **80 cases**, zero failures/skips, in 6m17s. Combined with the 16-case navigation/migration/provider/contract run and the two real-browser cases, this is **96 distinct passing backend/browser tests across 98 executions**; the navigation and contract tests overlap. All three final TRX files used the same corrected backend assembly. Frontend checks add 534 passes.

The final source-workspace-backend-final.trx SHA256 is 2aa7559ba81a1aa53d1eedded908b603c5c660256516d4e354ddfc213f779968. Machine-readable counters, case overlap and final source hashes are retained alongside the owned-file manifest. The failed predecessor workflow log SHA256 is b63862cb52eb40bdb3f7069bbcdaec861793383cda298bd001518c1c69d24b43.
