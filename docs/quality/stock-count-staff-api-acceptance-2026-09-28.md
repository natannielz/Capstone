# Stock-count choices, HTTP and browser client acceptance — 28 September 2026

This is a foundation for the [staff workflow](../requirements/stock-count-staff-workflow.md), not acceptance of the rendered staff screen, stock balances or production rollout. The [screen plan](../superpowers/plans/2026-09-28-stock-count-staff-screen.md) preserves the remaining work.

## Implemented boundary

Catalog-owned read services return actual named authorized warehouses and exact current package definitions. They distinguish stock.count from stock.read, include shop-owned or exact-warehouse-owned packages only within the chosen organization/shop/channel, validate active catalog joins before pagination, and fence identity, session, matched authority, topology, catalog heads and expiry through final awaited work. Invalid profiles fail closed instead of becoming guessed labels. No buyer publication or catalog.manage permission is borrowed.

Six session-protected POST endpoints provide choices, recording, history, nullable detail and lookup-only recovery. All use same-origin CSRF and the expected actor, including reads and case-variant paths. Public observations contain only the 18 agreed fields; internal issuer/subject, counter identity, physical-item key, request key and audit/provenance fields are excluded. Count-only staff can recover their own receipt without history permission. Empty receipts do not prove failure. No closure, update or delete endpoint exists.

The frontend client uses the generated same-origin transport and checks public response shapes, exact Int64 references, warehouse binding, explicit package facts and arithmetic. Blank quantity differs from explicit zero. A reviewed command is detached and frozen, including its warehouse and package definition; a late or mismatched write result remains uncertain. References-only recovery survives a lost response and a later denied retry. Local storage failure stops the business write before dispatch.

Server and browser contracts were regenerated. Semantic comparison against the prior committed OpenAPI found exactly six new paths and 13 new schemas; no existing path/schema was removed or changed. Additional JSON diff volume is property ordering. The generated-client consistency check passed.

## Verification evidence

- Before service implementation: 71 expected failures, including 66 unimplemented choice cases and five missing HTTP/capability cases.
- Catalog choices plus existing count-definition adapter: **92 passed, zero failed/skipped**, 6m50s. This contains 66 new choice cases and 26 existing adapter cases.
- HTTP/OpenAPI and existing access/catalog HTTP regressions: **37 passed, zero failed/skipped**, 2m54s.
- Additional named-choice, cross-warehouse/actor receipt and historical-definition HTTP cases: **three passed**. They ran alongside the unchanged concurrent-count case, which also passed locally, four total in 21s. This local pass did not erase the full-CI failure described below.
- Browser workflow: eight expected unimplemented-behavior failures, then eight passes. Browser transport: nine expected failures, then its stale-view assertion was corrected to inspect the existing typed RequestNotSentError.code; one additional mutable-source snapshot case was added.
- Full browser regression command: **478 passed**, then **six future-date** and **seven preserved-behavior** passes; zero failures/skips. Typecheck and import boundaries passed. Lint passed with nine preexisting warnings. The production build passed. The working tree's unrelated user layout/mentor changes were preserved and are not included in this publication; exact committed-source CI is a separate gate.
- A test-only unknown-response TypeScript annotation and a C# overloaded fixture constructor were corrected before successful checks. An initial edit command used the wrong frontend-relative directory and made no source change; its failing stale-view assertion remained visible until corrected.
- Bounded-conflict service regression: **75 passed, zero failed/skipped**, six minutes. This covers the entire StockCountTests partial class, including the corrected concurrent case and the deterministic exhausted-retry case. The server OpenAPI export check also passed.

Local evidence lives under tmp/production/stock-count-results:

| Artifact | SHA-256 |
| --- | --- |
| stock-count-choice-first-green.trx | dbe05e39ffdded4f440f4a91d3d3cdbd0abdaaa0bee9c1f9d8ed53ec8c80ba7b |
| stock-count-http-first-green.trx | 7dc4b1a2fe800838750f2ddfb5736354bc7afdec00b22b31e97af61a509ca656 |
| stock-count-http-and-race-reproduction.trx | 458d41f243b20deb1394a6a0d2f799595fa40abe1d66ee8bdc3f660a9f416a5f |
| stock-count-bounded-conflict-regression.trx | 1c1a3e0cc50ac439cdb407912df0fe3e1c7124e4699a83fad3bb9b51ec2bdbe0 |
| frontend-staff-client-full.log | d6de943dbed0c82a47c62d433f6974aac7512bada4bf0d85a55dfdb6d8507d5e |
| frontend-staff-client-build.log | 3ea934a536dd254c2c3bcfb5b47e2eb4381e359bb4599f9a992110d0f5ba8993 |

## Full-CI finding and bounded concurrency contract

[Workflow 36426923328](https://github.com/natannielz/Capstone/actions/runs/36426923328) at 2068905dc48b7e5d63837c693696e80e6b082604 completed with **1,580 backend passes, one failure, 71 explicit Windows-only method skips**, 1,652 total,28m44s. Frontend passed. Later backend Auth0/contract stages did not execute after the failed test. The prior complete green remains workflow 36402327435 at 1b61c1a343518d11778ad5005f98fb8a8b180f4b.

The one failure was Concurrent_same_key_creates_one_observation_and_audit. Four simultaneous same-key transactions contend on the existing authorization/session fences. MongoUnitOfWork intentionally stops after four transaction attempts and returns version_conflict; the test incorrectly required immediate success from every caller. The production retry limit and service were not changed.

The corrected test preserves four concurrent calls, requires at least one successful original observation, accepts only that specific bounded conflict for a loser, then requires unchanged-key replay and receipt recovery to return the same original. It still requires exactly one observation, audit and receipt and unchanged physical stock. A new deterministic real-Mongo failpoint test rejects all four update attempts, checks the exact retry bound and zero partial count/audit/receipt effects, then verifies successful same-key submission/recovery after contention is removed. Unexpected errors and unknown commit outcomes are not swallowed by the correction.

Failed CI log: tmp/production/github-publication-results/run-36426923328-failed.log, SHA-256 0c7d9a8a26a66ee9db7d8b522acc4fa080b32c15e6c071b3b26484e424575063. A fresh full workflow is required for the next commit; no inherited green result is claimed.

## Review, deployment and open gates

Root inspected the new API/reader/client implementation and exact existing-contract preservation. Independent implementation review of these new files has not completed: all three review agents reported account usage limits. Earlier independent acceptance of the persistence and browser-journal components does not cover this later code.

The actual /inventory/counts screen, its session/view lifecycle, protected login-return/navigation integration, keyboard/mobile/browser verification, and same-origin deployed API routing remain open. Vercel owning-account access is still unavailable; a clean release can be prepared but no new deployment is claimed. Hosting the frontend does not activate .NET, Mongo or Auth0.

This work changes no live finance policy, person-source decision, catalog-audience policy, production grant, scanner/decoder result or business transaction. All report requirements, external integration/hosting, operational readiness and the real branch pilot remain part of the active production goal.
