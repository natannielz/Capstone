# Operating-policy staff workspace verification — 29 September 2026

Status: implementation and dedicated browser workflow verified locally; affected regression and publication results are recorded below. This stage extends API/client 7ea69dc and recovery journal 80aadfd. It does not complete CAT02 or the full production/report goal.

The dedicated /inventory/sources/policies screen offers deliberate physical-pool/proposal selection, explicit writer/tenant/feed/procedure inputs, frozen confirmation, separately scoped immutable history and original-reference receipt recovery. Source settings, the shared physical buffer and all channel mappings remain visible during review. An accepted policy definition leaves the source inactive. No activation control or implied inventory quantity is introduced.

Current-access discovery now includes the two existing policy permissions without granting general administration or history. Checker task discovery remains available when MFA ages so the user can reach verification, while actual review/decisions/recovery enforce current assurance. Exact frontend and Auth0 return-path allowlists include the policy route. Recovery navigation recognizes exactly four policy operations with distinct Indonesian labels.

The editor binds its private views to the mounted authenticated account. Changed selections, late reads, session expiry, malformed/foreign responses, lost authority and MFA expiry cannot resurrect stale views or consume another attempt. Proposal and original-maker withdrawal remain available without approval MFA. Each command needs an explicit checkbox and freezes the reviewed request. Only actor, operation, original key and timestamp persist. Missing/malformed receipts and lost responses keep the original reference; no close, retry or substitute operation exists.

## Verification

Artifacts are under tmp/production/stock-source-operating-policy-staff-results.

- Navigation initially failed three frontend cases and two backend cases: policy-only tasks were missing, the exact return path was not allowed, and recovery routed to the wrong desk. The implementation fixes those connections without adding grants.
- The editor's initial ten cases passed. Extended coverage brings the dedicated editor to 19 cases and navigation to three. The extended test initially expected the wire value not_confirmed from the journal, whose established public state is unconfirmed; corrected the test expectation without changing production recovery.
- All 713 frontend executions passed: 700 main cases, six future-date repository cases and seven preserved golden scenarios. Type checking, import boundaries, lint and production build passed. Nine existing lint warnings remain; no new warning or error.
- The first real browser run reproduced ambiguous select labels because option text became part of the accessible name. Both selects now have explicit label associations. The rebuilt production frontend passed the second browser run.
- The successful policy browser exercised 11 scenarios and 99 actual API responses. All four successful command responses were deliberately lost after the real commit and recovered after reload with their original references. It verifies separately scoped maker/checker/reader, signed provider-fixture MFA return, account/person independence, a real suspension invalidating frozen approval, stale rejection, original-maker withdrawal, exact large IDs, keyboard confirmation, mobile history, linked-channel revocation, MFA expiry and account suspension.
- Mongo assertions require four policy proposals, three decisions, one approved policy, seven exact policy audits and seven command receipts. Source activation, physical stock/lot balances, movements, reservations and accounting remain unchanged.
- Focused desktop approval and full mobile history screenshots were visually inspected. Labels, values, focus indication, confirmation, selected history and responsive stacking are readable without horizontal overflow. This is author inspection, not a complete independent accessibility audit.
- Affected backend/browser regression: 84 cases passed with zero failures/skips, including all five earlier real browser workflows. Together with the separate successful policy browser, 85 distinct backend/browser cases passed; all six browser workflows actually executed..
- Server/client generated-contract checks: Both checks passed unchanged against the compiled API and committed generated client..

| Artifact | SHA-256 |
| --- | --- |
| browser-second.trx | e7e9b393d060f5ca1fa764e1ae2a6027994a73f15e878731380f372cc3c172b6 |
| frontend-test.log | 60629a63110b064812931dbd1fd65e30c83279af340fb721c495ab9b135f4698 |
| affected-regression.trx | e1bda7fe5842cab3abcadda1b4b2587fab1d052818331a03c0b25c333fa15f54 |
| navigation-core.trx | 9a7615f25f425dbe485faa7785b5c6d51d4d8bfc7885032b0ca692b9b1f85c27 |

Author review covered final account/selection guards, separate permissions and assurance, frozen request/receipt matching, reference-only persistence, privacy, exact numbers, history transition checks, explicit confirmation and test-only fixture isolation. Independent review remains pending available agent capacity; author inspection is not substituted for that gate.

## Release and remaining scope

GitHub 80aadfdc9ee828d70233cfa9df20986afd0c46b5 is published; its exact Quality run 36538449822 is tracked separately. These local staff changes must not be attributed that predecessor's CI outcome. Publication and source-manifest evidence will record the staff commit explicitly. The browser CI job now requires six executed passes and zero skips, including the new policy workflow.

Vercel owning-account access is still unavailable. A prepared tracked-source archive is not a deployment, backend activation, production Auth0/Mongo configuration, source acceptance or a successful pilot. No production access, external provider facts, business decisions or independent review outcomes were invented.

Still required: accepted operational control and writer handoff, complete producer-backed drain/reactivation, observations/reconciliation/external holds, canonical stock/cost consumers, lot handling, remaining order-to-accounting modules, ergonomic selector convergence and full security/accessibility/load/restore/operations/pilot acceptance. Internal history validation is not bounded by response paging. The original production plan and all 97 report requirements remain active.