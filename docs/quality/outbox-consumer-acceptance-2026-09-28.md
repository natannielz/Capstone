# Outbox consumer continuation — 28 September 2026

Status: locally verified and independently reviewed at 1e98aa2. The change is a bounded PAY01/PAY02/T13 queue prerequisite. No actual validation, OCR, notification or external processor is enabled; the production worker still registers no processor.

Claims now require an immutable configured set of event kinds, maximum attempts, lease duration and maximum processing interval. One atomic transition retains the lower stored attempt limit, claims eligible work, or terminalizes exhausted work. Each call handles at most 16 terminal candidates. The final permitted attempt can complete; another retry becomes dead. Renewal cannot extend the fixed processing deadline, shorten a lease or accept a stale owner/token/kind. Mutating the returned claim cannot increase the persisted limit. Failure reasons are a closed safe-code set. All state writes retain explicit majority write concern.

Independent review identified one concrete issue: Claim and Renew could return a lease already expired during the database await. Four real-Mongo tests advanced the clock in the command-success callback and failed before the correction. The fix checks fresh time and cancellation after the response, withholds expired handles and leaves already-written state intact for normal recovery. It does not undo the claim, decrement attempts, issue another claim, or misreport a successful Complete/Fail response.

| Verification artifact | Result |
| --- | --- |
| outbox-red.trx | 10 expected missing-method failures, 7 configuration cases passed |
| outbox-green-first.log | Compile failure CS8752 in a params-array construction; corrected with explicit BsonDocument |
| outbox-green-second.trx | 17 passed, zero failed/skipped |
| outbox-regression.trx | 30 passed, zero failed/skipped; real process-kill/restart and weak-default-client durability included |
| outbox-late-response-red.trx | All 4 delayed-response cases failed with expired handles returned |
| outbox-final-regression.trx | 34 passed, zero failed/skipped, 36 seconds |
| Independent final source review | Accepted; no remaining scoped finding, no independent rerun claimed |

Artifacts and the seven-file digest manifest are under tmp/production/outbox-consumer-results/ in the production worktree. This filtered local result does not imply a new full GitHub workflow has passed. The existing process test still proves one simulated downstream effect after duplicate delivery; it does not establish a real integration or exactly-once delivery.

Four test/probe call sites now register only their expected event kind; previous recovery assertions remain intact. No applied migration, API route, UI, business permission or production configuration changed. The [runbook](../runbooks/outbox-consumers.md) documents the bounded behavior and remaining strict-schema, credential, processor, host-isolation, alerting and audited operator-recovery work. Full PAY01/PAY02/T13 and every production/pilot gate remain open.

Complete-workflow result: [run 36394655918](https://github.com/natannielz/Capstone/actions/runs/36394655918) at 9cb7357 failed with 1,433 backend passes, two provider-security invitation-fixture failures and 31 explicit Windows-only skipped methods. Frontend passed; later Auth0/contract stages were skipped. The two failures came from seeding authority at real UTC before rewinding the test clock to a fixed earlier time. The [fixture repair evidence](ci-clock-fixture-2026-09-28.md) records the unchanged security assertions, 19 passing affected tests and bounded diagnostic-output correction. A new complete workflow is required; this failed run must not be represented as full queue-checkpoint acceptance.

## Completed publication verification

[GitHub workflow 36398684816](https://github.com/natannielz/Capstone/actions/runs/36398684816) passed at exact commit d29bd09b0accacaac22fdeaeb39caa7dad3c6377. Backend: **1,461 passed, zero failed, 31 explicit Windows-only method skips on Linux**, 1,492 total, 23m11s; build zero warnings/errors. All five Auth0 Action fixtures and both compiled/generated contract checks passed. Frontend types, lint, 453+6+7 regressions and production build passed; nine existing lint warnings remain. The full log is retained at tmp/production/github-publication-results/run-36398684816-full.log, 231312 bytes, SHA-256 ff862d63e0a1687f5a895a590e41d5324cc6c739ce9f47e1549d79b14d50b7a4.

This closes the previously observed security-fixture CI failure and verifies the bounded queue plus Catalog count adapter checkpoint. It does not cover the later uncommitted admission or Inventory implementations, prove positive private storage on Linux, or establish production readiness.
