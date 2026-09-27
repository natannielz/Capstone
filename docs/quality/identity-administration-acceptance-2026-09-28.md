# Identity administration — local acceptance

Date: 28 September 2026. Baseline: beed231. Scope: ACC01.1–ACC01.2, the administration portion of ACC01.5, and the existing T03/T04 interfaces. The full ACC01 task and production goal remain open.

## Delivered behavior

The configured API exposes scoped profile queries/search, invitation queries/proposals/independent decisions and local recovery initiation. DTOs reject unknown members and invalid role-array values, preserve Int64 IDs/versions as strings, require the reviewed actor, and reuse existing authorization, version, audit and operation receipts. Every POST retains origin/antiforgery protection. Mixed-case routes require the same actor consistency header, and the generated contract declares it.

Search filters permitted memberships before matching/counting/paging. The existing identity.directory.read audit records purpose, bounded selection, count, scopes and correlation; raw search text and email are excluded. Applied migrations v1–v4 were preserved. Search supports 2–80 characters; page queries retain the existing maximum offset of 10,000.

The Indonesian /administration/accounts screen provides an audited account directory, invitation queue, explicit active scope choices, membership-only or compatible role requests, Finance-only optional finance packs, Jakarta expiry, complete review and confirmation, and a clearly blocked recovery intent. The directory displays server-returned verified account details. Scope names still use honest reference fallbacks where names are not supplied; complete human directory/owner recovery navigation remains in ACC01.5/U02.

Unknown outcomes retain only actor/operation/key/time in browser storage. Same-page retry reuses the exact actor, body and key. Receipt reconciliation after reload does not resend. Confirmed proposal retry/reconciliation resets the reviewed form. An actor_changed or 401 response, including receipt lookup, clears prior account data and drafts.

Approval is local approved state; no provider invitation is sent. Recovery initiation revokes local sessions and records a blocked intent; no password/factor is replaced or owner recovery completed.

## Verification and review

| Check | Result and scope |
| --- | --- |
| Backend full regression | 277/277 passed, zero skipped; 7m23s; .NET 10.0.401; real disposable Mongo replica-set databases on loopback 27028 |
| Focused administration / affected identity | 2/2 focused and 114/114 affected identity passed during development; full final run includes the review fixes |
| Frontend and legacy | 315/315 passed; 6/6 future-date repository checks; 7/7 golden business cases; Node 24.19.0 |
| Focused frontend | 7 identity administration transport/recovery cases and 4 invitation scope/finance/expiry cases passed |
| Contracts | Server OpenAPI export check and generated-client drift check passed |
| Compilation / boundaries / lint | Production frontend build including TypeScript passed; import boundaries passed; full lint zero errors and nine unchanged legacy warnings |
| Browser | Saved runner passed 9/9 groups against the production build in Edge, desktop 1365px and mobile 390px; synthetic API responses only |
| Visual / accessibility | Both screenshots inspected; no horizontal overflow or page errors in desktop/mobile workflows; stable reason labels, focused decision heading, 44px controls and explicit review |
| Independent source review | Backend and frontend reviewers rechecked their fixes and reported no remaining finding in this bounded slice |

Observed red-to-green cases include missing implementation, mixed-case actor-header omission, omitted required OpenAPI header, non-Finance template receiving a finance pack, completed uncertain proposal retaining its submit-ready draft, and receipt auth failures retaining prior account data. Browser checks also found and corrected a reason label that changed after editing. Malformed role arrays are rejected before service execution. A final 2/2 focused HTTP rerun additionally covered both Buyer and Finance templates with null/blank entries in all three arrays (12 variants), returning 400 with no invitation mutation. No claim is made that a live 500 was observed before the correction.

The committed browser runner is frontend/scripts/check-identity-administration.mjs. It uses a fixed loopback frontend at http://127.0.0.1:3118, blocks service workers/off-origin traffic and intercepts every /api/v1 response. Playwright may be installed locally or resolved from the explicit UNIT_TOKO_PLAYWRIGHT_PACKAGE path to its package.json. UNIT_TOKO_BROWSER_CHANNEL optionally selects an installed browser; Windows defaults to Edge. Run the preview from frontend, then run the script with Node 24. Results/screenshots are written only under tmp/production. This runner does not provision users or send messages.

Evidence: tmp/production/test-results/acc01-administration-full.trx; accounts-browser-result.json; accounts-desktop.png; accounts-mobile.png; identity-admin-backend-report.md; invitation-form-report.md; q08-report.md. Temporary previews are stopped after verification. Hosted CI was not run.

## Remaining gates and next work

- ACC01.3: authenticate actual supported provider event transports, persist deduplication/revocation and admission holds, prove transaction races and coverage gaps. Auth0 logs are supplemental monitoring evidence, not a guaranteed immediate revocation channel.
- ACC01.4–.5: recovery-only admission fencing and server-bound owner verification, blocked/uncertain dispatch, safe failure navigation and confirmed completion. A newly enrolled factor or generic login proof is not proof of the legitimate pre-reset owner.
- ACC01.6: combined browser → one-origin TLS gateway → maintained OIDC handler → actual API → disposable Mongo/isolated provider. Separate HTTP/real-Mongo and intercepted UI results do not close this gate.
- ACC01.7: actual approved Auth0 tenant, sender, MFA/recovery journey, external security-event coverage and protected deployment acceptance. No credentials, paid provisioning, external messages or production outcomes were invented.
- ACC02–ACC04 and remaining accounting/catalog/payment/OCR/business/operational modules continue in dependency order. Names and person-level financial independence are not inferred from account IDs.

See the reviewed provider findings in [Auth0 lifecycle research](../research/2026-09-28-auth0-lifecycle.md), the [access plan](../superpowers/plans/2026-09-28-report-access.md), and [Q08 local acceptance](legacy-conflict-acceptance-2026-09-28.md).
