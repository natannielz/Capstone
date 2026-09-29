# Stock-source staff browser rehearsal

Run this only against disposable local replica-set databases. It uses synthetic people, organization names, approved fixture grants and source settings; it does not establish real source acceptance or production access.

Follow [the shared stock browser setup](stock-count-browser-rehearsal.md) to install the pinned Node 24 / Playwright 1.62.1 runtime and build the frontend. Use the same UNITTOKO_STOCK_BROWSER, UNITTOKO_BROWSER_NODE, UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE, UNITTOKO_BROWSER_CHANNEL, PLAYWRIGHT_BROWSERS_PATH and guarded UNITTOKO_TEST_MONGO_URI environment variables. Run all four fixtures together with the filter:

    FullyQualifiedName~StockCountBrowserTests|FullyQualifiedName~StockSourceBrowserTests|FullyQualifiedName~StockSourceRevisionBrowserTests|FullyQualifiedName~StockSourceAliasBrowserTests

All four must execute and pass; an opt-in skip is not evidence. The GitHub browser job checks that its TRX contains four executed passes and zero skipped tests.

The source fixture starts the built Next.js application and actual production-mode ASP.NET API behind a local HTTPS proxy. It signs in two distinct synthetic people through signed provider responses, state, nonce, PKCE, verified email and factor evidence. Only the provider front/back channel is synthetic; source requests, opaque cookies, CSRF, grants, person separation, transactions and persistence use application code.

Covered sequence:

1. Sign in as the source maker; verify protected host cookies and task discovery.
2. Reject missing/invalid CSRF, an unexpected actor and a foreign warehouse.
3. Review and submit an explicit web source; reject its maker's self-approval.
4. Submit an external source, deliberately lose two committed responses, verify the same key, reload, select the original warehouse and recover its receipt without resubmission.
5. Withdraw a third proposal through its explicit review.
6. Sign out, sign in as a separate reviewer, approve the web source and reject the external source.
7. Inspect mobile history, exclude private authority fields, then sign out.

Database assertions require three immutable proposals, three terminal decisions by the correct people, one inactive pool and physical-key registration, six corresponding audit events and receipts, and unchanged lot/balance/movement/reservation/accounting data. Approval must never activate stock.

Screenshots are written under tmp/production/stock-source-ui-results/browser/ and contain synthetic fixture information only. Inspect the desktop review and mobile history images before claiming visual verification. Bounded failure diagnostics include source-screen text and response statuses, never cookie/token bodies.

The fixture does not approve actual D01/D04 decisions, verify a live Auth0 tenant, accept OMI/S-Mart integration, deploy the .NET API, test full load/restore, or complete the overall production goal.

## Revision workspace rehearsal

The separate StockSourceRevisionBrowserTests fixture opens /inventory/sources/revisions. It uses two separately authenticated synthetic people, real signed-provider MFA return, opaque cookies, CSRF, the production API, and real Mongo transactions. Its Node script is frontend/scripts/verify-stock-source-revision-browser.mjs. Synthetic controls exist only in the test assembly, require a private random nonce passed through the child process pipe, and are never added to the application or exposed to browser JavaScript.

The sequence proposes precise settings (60.001 seconds and a quantity above JavaScript's safe integer boundary), loses a committed proposal response, reloads, and recovers without resubmitting. A competing proposal and a withdrawn proposal remain in history. The independent reviewer completes MFA, approves the first change, and recovers a separately lost approval response. The stale proposal is visibly blocked, its direct API approval is rejected, and a reviewed rejection records its terminal state. A second approval advances the same pool to version three without rewriting the earlier decision. Mobile comparison/history must fit a 390-pixel viewport. Expiry after frozen review rejects a subsequent approval; current grant revocation conceals previously loaded details.

Assertions require five revision proposals, two applied versions, four decisions (two approvals, one rejection, one withdrawal), nine revision audit events and receipts, unchanged original creation and physical registration, and unchanged lots, balances, movements, reservations and accounting. The last proposal remains pending after MFA denial. Both browser sessions are signed out; encrypted key material remains protected. A skipped test does not establish any of these outcomes.

Screenshots and logs are written under tmp/production/stock-source-revision-ui-results/. Inspect focused desktop review and mobile history screenshots as well as the full-page layout. For a disabled option, inspect the native HTMLOptionElement.disabled property; this pinned browser automation runtime can return false from locator.isDisabled() on an option inside a label even when its native disabled property is true. The fixture also verifies that rejection, rather than approval, is selected for stale history.

The new page is an exact allowlisted return path in the frontend and default Auth0 options. Deployments overriding Identity:Auth0:AllowedReturnPaths must include /inventory/sources/revisions while retaining their other reviewed paths. This does not require adding a second OAuth callback or permitting wildcard redirects. Approval still leaves the source inactive; this rehearsal does not establish genuine source ownership, accepted operational handoff or live stock authority.

## Channel mapping workspace rehearsal

StockSourceAliasBrowserTests opens /inventory/sources/aliases using frontend/scripts/verify-stock-source-alias-browser.mjs and the same isolated HTTPS/production API/replica-set harness. Two synthetic people hold separate grants on the canonical OMI channel and requested S-Mart channel in the same warehouse. An existing inactive shared source and a populated canonical balance are created only in the disposable fixture.

The workflow covers exact sign-in return and secure cookies; keyboard confirmation; a committed proposal response lost before delivery; reload and receipt recovery after the maker's read permission is temporarily removed; restoration of reviewed fixture permissions; self-approval denial; competing and withdrawn proposals; the independent reviewer's actual MFA round-trip; a separately lost approval response; stale mapping rejection in both screen and API; source-settings revision and explicit mapping reapproval; reviewed disablement; 390-pixel mobile history; MFA expiry after frozen review; and revocation of the requested channel grant concealing the prior view.

Six mapping proposals produce three approved mapping versions (enabled, reapproved against new settings, disabled), one rejection and one withdrawal. The final proposal stays undecided after MFA denial. Assertions require the exact maker/reviewer identities, eleven mapping audits and whole-command receipts, one source settings revision and one still-inactive physical pool. Original creation/decision/registration records, populated large exact stock quantities, lots, movements, reservations and accounting remain unchanged. Both real browser sessions sign out.

The compact automated sequence shares one local IP across both accounts. Its driver waits for the existing one-minute request window before the final scenarios; production rate-limit settings are unchanged. The first run deliberately retained in the evidence reached the real limit after nine successful scenarios. Rate-limit or transport failures never trigger command resubmission.

Screenshots are under tmp/production/stock-source-alias-ui-results/browser/. Inspect the focused desktop confirmation and mobile historical detail images. Only actor, operation, original key and time persist in the recovery journal; pool and requested-channel context are selected again after reload. Command permission does not grant configuration/history access, and receipt absence is not a failed or closed request.

Deployments overriding Identity:Auth0:AllowedReturnPaths must include the exact /inventory/sources/aliases path while preserving other reviewed paths. This adds no OAuth callback or wildcard redirect. The test-only settings/grant/clock controls require a private nonce passed over the child-process pipe; they are not application routes or available to browser JavaScript. These synthetic outcomes do not establish actual source acceptance, D01/D04 decisions, external reservation capability or readiness for live stock promises.
