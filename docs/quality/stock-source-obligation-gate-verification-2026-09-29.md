# Stock-source obligation gate verification — 29 September 2026

Status: private prerequisite implemented and locally verified. Accepted operational control, writer handoff and production readiness remain incomplete. Independent review remains pending agent capacity; the review below was performed by the author.

## Change

StockSourceObligationGate requires all seven operational owning domains before it can check a stopped canonical pool. Results must carry the exact physical-pool identity, owner location, settings revision and fence; a positive producer revision; complete coverage; and zero outstanding operations. Missing or malformed information never becomes zero work. It preserves Int64 values and does not sum overlapping counts.

Readers operate sequentially in the owning transaction and fence their actual producer records. The gate verifies strict actual pool storage, current stopped state and the original transaction identity, then conditionally advances the shared physical-pool fence. Failure aborts the owning transaction. The returned result is valid only inside that same transaction and is not a durable drain certificate, accepted control, activation or stock promise.

No production adapter, API, migration, UI, stock quantity, finance operation or automatic grant is introduced. Synthetic readers and their collections exist only in tests. Every genuine operational adapter must still validate its storage and lifecycle predicate, cover disabled/historical channels, and require all obligation-producing commands to fence this same pool, including creates with no previous row.

## Verification

| Run | Executed | Passed | Failed / skipped | Scope |
| --- | ---: | ---: | --- | --- |
| Initial obligation slice after fixture repair | 49 | 49 | 0 / 0 | Actual Mongo transaction gate |
| Affected regression | 172 | 172 | 0 / 0 | 49 gate cases plus 123 existing inventory graph, ledger and source-suspension cases |
| Final obligation slice after author-review fix | 51 | 51 | 0 / 0 | All gate cases, including ended/replaced transaction guards |
| Server OpenAPI check | 1 command | pass | no change | Public contract unchanged |

There are 174 distinct passing backend cases across the affected and final runs, not a single 174-case run. The 123 existing cases ran before the final isolated guard change; all 51 cases using that guard ran afterward. The .NET test command rebuilt the backend/test projects. Frontend application files and generated contracts are unchanged; no frontend or browser rerun is attributed to this backend-only checkpoint. Full exact-commit CI is tracked during publication.

The controlled race loads zero work, pauses before the pool write, then commits a second transaction that inserts a synthetic outstanding order while incrementing the actual pool fence. The older gate transaction conflicts and rolls back its caller/producer writes; a fresh check sees and rejects the winning obligation. Seven separate tests retain blocking work attributed to another, disabled channel mapping.

Failure cases cover every missing producer, incomplete producer state, missing storage, malformed/mismatched result, negative and maximum Int64 counts, foreign database/client/session, inactive transaction, cancellation, stopped-pool mismatches and exhausted fence. A downstream owner failure rolls back a successful gate's pool and producer changes. Existing regression covers real canonical stock writes versus suspension and rollback when the actual accounting boundary rejects.

## Failures observed and resolved

1. Initial red compilation failed because the new gate contract did not exist. The log is retained.
2. The first executable run failed 49 fixture setups because the generated isolated database name exceeded MongoDB's limit. Shortening only the test database suffix resolved this setup issue; no production rule was weakened.
3. Author review found that a reader ending or replacing the transaction could let the pool write continue outside the original transaction. Both regression cases failed with “No exception was thrown.” The gate now rechecks the original transaction number and active session after each reader and around its final write. Both cases and the complete 51-case slice pass.

## Evidence

Artifacts are private under tmp/production/stock-source-obligation-results.

| Artifact | SHA-256 |
| --- | --- |
| affected-regression.trx | d65ac7592321baf041ea3a8aa638a144a50d0acd6836c0de052b81ac5b5b8da1 |
| transaction-review-red.trx | 313c0357bb846d3b1b7132788bc851dfa5417a83b6fc1d96c1e4ac21f042a0df |
| obligation-final.trx | 19ae43a62b79d05976c0901c97a05566cbdd7ff5f4182d98977c131d3d2d1b79 |

The shared-pool write addresses the snapshot-read limitation documented in [MongoDB transaction considerations](https://www.mongodb.com/docs/manual/core/transactions-production-consideration/#in-progress-transactions-and-stale-reads). The tests prove this helper boundary with synthetic owning modules; they do not prove completeness of the real operational modules.

## Remaining acceptance

- Implement each genuine owning-module adapter and lifecycle predicate, with real source approval/revocation and create/update races. No absent collection or empty fallback is acceptable.
- Persist reviewed actual handoff/control evidence and accepted transitions with current policy/configuration/suspension, person/scope/MFA fences, audit, receipt and outbox; add forward migration, API/recovery, staff confirmation and real browser acceptance.
- Complete observations/reconciliation/external holds and the canonical lot/stock/cost consumers, then the remaining original/report modules and operating/pilot gates.
- Obtain the independent stage review when agent capacity returns. No author review is represented as independent.
- Vercel owning-account access remains unavailable. Preparing a source archive or a successful GitHub run is not a Vercel deployment or production acceptance.
