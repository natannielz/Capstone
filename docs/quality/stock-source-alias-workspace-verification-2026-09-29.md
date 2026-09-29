# Stock-channel mapping staff workspace verification — 29 September 2026

Base commit: 8c3a6eb22fc4054f60efca4d51d9a94a4cb35698.

This checkpoint connects the governed mapping service/API/client to a staff workflow at /inventory/sources/aliases. It advances CAT02; the full original production plan and all 97 report requirements remain binding.

## Implemented behavior

Staff select the canonical allocation and a different authorized channel in the same organization/shop/warehouse. Current configuration distinguishes missing, disabled, stale and current mappings. The screen shows one physical allocation, the source settings version and the separate mapping version. Historical proposals and terminal decisions retain their original version facts and reasons.

Proposal and decision review freeze the displayed allocation, both channels, versions, enabled/disabled setting and reason. Confirmation is explicit; edits and selection changes are blocked during review/submission. Stale source or mapping bases and active pools prevent approval while rejection/withdrawal remain available according to independent permissions. The server retains final current authorization on both scopes, distinct-person review and fresh MFA.

The editor cancels obsolete reads, rejects foreign pool/channel/physical-key detail, discards late responses and conceals data after revoked access, account replacement or expired sessions. Duplicate clicks cannot issue a second command. Storage failures stop submission before dispatch, and uncertain outcomes retain the original key through disposal and reload.

The four stock.source.alias operations have a separate recovery adapter resolved before stock.source. Only actor/operation/key/time persist. Recovery requires deliberate pool/requested-channel context again after reload; users with command-only permissions can supply their original allocation reference and an authorized channel without reading configuration/history. Missing, wrong, malformed or late receipts leave the reference unresolved. There is no close operation, resubmission or replacement-key path.

The screen is linked from staff navigation and account destinations. Frontend and default Auth0 options allowlist its exact login/MFA return path; changed/suffixed redirects are tested. Deployments with an overridden list must add the exact path, as documented in the browser runbook. No live Auth0 tenant is activated.

## Verification and corrected findings

Eight new recovery/navigation cases reproduced missing journal ownership and navigation before implementation. Eighteen editor cases cover frozen commands, same-warehouse channel selection, no-op/reapproval behavior, stale versions, independent read/command rights, MFA expiry, unknown results, storage failures, reload recovery, wrong actor/pool/key, session/scope changes, out-of-order detail and busy recovery context. The final 26 new cases passed.

Review added a failing physical-key substitution case: a valid-looking detail with the selected pool ID but a different physical key could enter the editor. The editor now compares the detail's key against the selected source; the reproduced case passes. A TypeScript failure caused by widening the parsed binding-state string was corrected to an explicit closed union. The final frontend passed 620 normal tests, six future-date replay executions and seven golden cases: 633 passed executions, zero failures/skips. Type checking, import boundaries, lint and the production build passed. Lint retains nine preexisting warnings and no errors.

The initial affected backend run executed 48 cases: 47 passed and the new browser case reached the unchanged 240-request/minute limit after nine successful scenarios. The test driver now respects the existing one-minute request window before its last scenarios; production limits and single-attempt command behavior are unchanged. The failed run is retained and is not called successful.

The final affected backend run passed all 51 executed cases with zero failures or skips in 2 minutes 28 seconds. All four real browser workflows actually executed and passed: stock counts, source creation, source revisions and the new channel mapping flow. The new mapping browser completed all ten scenarios in approximately 69 seconds. Existing identity and exact redirect checks also passed.

The new browser uses two synthetic identities through real HTTPS, signed-provider PKCE/MFA, secure cookies, CSRF, production API code and real replica-set Mongo transactions. It covers a lost proposal response, command-only reload recovery, self-approval denial, competing/withdrawn proposals, independent MFA and lost approval recovery, stale mapping refusal in screen/API, source settings change and explicit reapproval, disablement, retained mobile history, MFA expiry after frozen review and requested-channel revocation.

The database verifier requires six proposals, five terminal decisions by the correct people, three immutable mappings, eleven alias audits/receipts, one still-inactive pool and one independently reviewed source-settings revision. Original creation/decision/registration and populated stock quantities above the JavaScript safe-integer boundary, lots, movements, reservations and journal entries remain unchanged. No mapping creates stock or finance effects.

Focused desktop confirmation and 390-pixel mobile history screenshots were visually inspected. The browser checks keyboard confirmation/focus and horizontal overflow; private provider/person/audit fields must not appear. CI now requires four actually executed successful browser cases with no skips.

Artifacts, intermediate failures, final summaries and exact source hashes are retained under tmp/production/stock-source-alias-ui-results. See [browser runbook](../runbooks/stock-source-browser-rehearsal.md) and [source authority contract](../architecture/stock-authority-contract.md).

## Remaining requirements

An approved mapping is configuration, not accepted stock authority or promised availability. Authorized canonical inventory consumers, source suspension/drain/handoff/reactivation, accepted policy/observations, lots/costs and downstream owning workflows remain required. The unchanged pure authority evaluator and private ledger/repository are not live production workflows.

Actual D01/D02/D04 and other provider/business/operations/pilot decisions are unresolved external gates. Independent review remains pending reviewer capacity. Vercel owning-account access remains unavailable: a prepared exact-commit archive is not a deployed build. CAT02 and the overall production goal remain incomplete.
