# Stock-source revision staff workspace verification — 29 September 2026

Base commit: f817669030f5be35a8362d8f17923c994b7d5052. The staff workflow now connects the governed revision service, protected API and typed client to /inventory/sources/revisions. This checkpoint implements staff review and recovery; CAT02 and the full 97-report production goal remain incomplete.

## Delivered behavior

The new workspace is discoverable through authorized staff navigation and account destinations. It lists scoped physical allocations, loads current approved settings and immutable history, and compares base, proposed and current settings when versions differ. Changed fields have both a text label and color cue. Stable physical identity/location cannot be edited. Exact decimal-string identifiers and quantities remain intact; a stored age of 60001 milliseconds round-trips as 60.001 seconds.

Proposal, approve/reject and maker withdrawal each require a frozen explicit review and final confirmation. The editor prevents duplicate clicks, hides replaced or expired account views, rejects late reads/outcomes and blocks approval of a stale base or active source. Separate read/propose/approve permissions remain independent. An actor with command permission but no history-read grant can still choose the original authorized location to recover their own request. The server retains the authority to enforce scope, current grants, distinct verified people, fresh MFA and final transaction checks.

The operation journal explicitly recognizes the four stock.source.revision operations and routes them through a separate adapter before the original stock.source prefix. It persists only actor, operation, key and timestamp. Missing, malformed, foreign or late receipts retain the reference. An uncertain command has receipt lookup only: no automatic or manual resubmission, fresh replacement key, legacy adapter fallback or close action. An in-memory recovery also binds the returned result to the original reviewed command. Confirmed recovery forces fresh source data before another change.

The frontend and default Auth0 configuration allow the exact new return path. The browser verifies both login and MFA return to the revision page. No wildcard redirect, callback change or live provider activation is introduced.

## Verification

The final frontend run passed 574 normal cases, six future-date replay cases and seven golden scenarios: 587 executed passes, zero failures or skips. The 27 new editor/recovery/navigation cases are included. Type checking, import boundaries and the production build passed. Lint has zero errors and the same nine preexisting warnings in customer-account.tsx, workspace.tsx and domain.test.ts. The final browser fixture also passed a targeted lint check.

The final affected backend run passed 45 executed tests with zero failures or skips in approximately 3 minutes 54 seconds. These include identity runtime/endpoints, source HTTP/permissions and all three actual browser rehearsals: stock counting, initial source setup and source revisions. The new revision browser case passed in approximately 41 seconds; its nine scenarios are not counted as nine additional test cases.

A final focused rerun strengthened preservation assertions to compare full BSON snapshots of the original creation proposal, decision and physical registration, along with lots, balances, movements, reservations and journal entries. The same browser case passed again in approximately 2 minutes 35 seconds with zero skips. This is 45 distinct affected backend/browser tests and 46 executions across the combined and focused final runs, not 46 distinct tests.

The new rehearsal uses the built production frontend, production-mode ASP.NET API, local HTTPS, real opaque cookies/CSRF and a real disposable Mongo replica-set database. Only the provider and business identities/settings are explicitly synthetic. A test-only private nonce, passed through the child-process pipe, controls a six-minute clock advance and suspension of the actual fixture grant; these controls are absent from the application assembly and never given to browser code.

The browser exercises precise proposal review by keyboard; loss of an already committed proposal response; reload and receipt-only recovery; competing proposals and maker withdrawal; distinct-person login and MFA return; loss and recovery of an approval response; visible stale-base prevention plus an actual rejected direct approval; a second approved version; immutable earlier history on a 390-pixel viewport; MFA expiry after frozen review; current grant revocation; and sign-out of both accounts. Server assertions require five proposals, two approved settings versions, four terminal decisions, nine revision audits/receipts, unchanged creation and physical registration, an inactive pool at version three, preserved exact age/quantity values, and unchanged lots, stock balances, movements, reservations and accounting. The last proposal remains pending after MFA denial.

## Failures found and resolved

The initial browser run exposed select accessible names that included option text. Explicit label spans and aria-labelledby fixed the application fields; the production frontend was rebuilt and the real workflow passed afterward.

A separate browser probe demonstrated that this pinned automation runtime reports locator.isDisabled() as false for an option inside a label even when its native disabled property is true. The fixture now inspects the native option property and verifies that rejection is selected for a stale proposal. Another fixture initially revoked access before its first history read finished: the application correctly concealed the view on that read's 403. The fixture now waits for the history row before revoking access and deliberately reloads to verify concealment. These two fixture corrections did not change application authorization.

Recovery/navigation tests were observed failing before their implementation; editor tests initially failed for the missing editor. Subsequent targeted and complete checks passed. An initial targeted lint invocation from the repository root lacked the frontend configuration; rerunning from the correct frontend directory passed. No safeguards or test assertions were relaxed to obtain a successful application result.

## Evidence and remaining gates

Artifacts are under tmp/production/stock-source-revision-ui-results, including revision-ui-final.trx, backend-final.log, final frontend logs, intermediate failure evidence, final-results.json, source-hashes.json, artifact-hashes.json and the owned publication manifest. Focused and full-page desktop review/mobile history screenshots were inspected: precise values and all three historical/current snapshots remain readable, changed fields are explicit, keyboard focus is visible, and the narrow layout has no horizontal overflow. GitHub's browser job now requires all three rehearsals to execute and pass, with zero skips.

Final combined TRX SHA-256: be27b1e6c331c324d0d3994e7c3a95504c9968ed78c36d2af437d355002c9b33. Focused preservation TRX SHA-256: 893245b123e900255ac38a6b26dfd28962254a5bbc099d554378c679b31e2108.

Independent review is still pending reviewer capacity. This is tested implementation evidence, not independent approval or business acceptance. Settings approval has no quantity/financial effect and does not activate a source. Actual D01/D04 decisions, trusted-person/source acceptance, active-source suspension/drain/handoff/reactivation, shared-channel aliases, accepted observations, lot authoring, operational inventory migration/costs, wider owning workflows, load/restore/operations and pilot acceptance remain required.

GitHub publication is authorized. Vercel owning-account access remains pending; an exact tracked release archive is prepared separately and must not be reported as a deployment. No live Auth0, Mongo or .NET activation is inferred.
