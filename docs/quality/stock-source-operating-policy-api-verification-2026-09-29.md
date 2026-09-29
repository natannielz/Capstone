# Operating-policy API and client verification — 29 September 2026

Status: locally verified API/client checkpoint based on 7aa4e0887df8b4f5c307356e6561ef5cd9b80c37. This completes policy review access and API/client preparation; journal, task/MFA/navigation, staff and actual policy browser acceptance remain required. CAT02 and the full original production plan plus all 97 report requirements remain incomplete.

## Delivered behavior

Action-specific review requires the actual propose/approve capability across the owner and every historically linked channel. Approval/rejection require fresh MFA, distinct accounts and independently verified people, with person and access fences and final expiry checks. Withdrawal requires the original maker. A stale pending proposal can be rejected or withdrawn, while approval requires the current stopped source/settings/mappings/suspension/prior-policy binding. General latest/detail/page reads separately require stock.source.read. No template receives another permission automatically.

Ten protected POST routes expose proposer preview, proposal, selected-action review, latest policy, proposal page/detail, three decisions and original-actor receipt recovery. Proposals bind pool If-Match plus exact suspension/policy versions and configuration hash. Decision routes use the immutable proposal ID whose pool is resolved and checked inside the command transaction. History includes all states in descending exact proposal ID order; a response page is bounded, while internal complete-history validation still requires production load measurement.

Explicit projections omit stored actor/person/identity evidence, audit IDs, body hashes, correlation and schema internals. Reviewed source settings, shared buffer and channel snapshots remain read-only policy facts. Required and unknown JSON fields, explicit nullable tenant, writer/feed/source consistency, canonical Int64 strings, precise IDs and version/hash preconditions are enforced. Recovery accepts only the four policy operations and never creates a missing receipt or resubmits its command.

The typed frontend gateway validates complete response shapes, exact versions and selected pool/proposal/action. Commands freeze the reviewed snapshot, send once, check the response against it, and preserve uncertainty when the context changes or the response is lost. No source activation, quantity/financial effect, generic fallback or close operation is exposed.

Compiled OpenAPI adds ten paths and 17 schemas. Structural comparison against the baseline found no removed or changed preexisting paths or schemas. Generated frontend types match the compiled server.

## Verification evidence

Local artifacts: tmp/production/stock-source-operating-policy-api-results.

- Four initial action/history service cases failed against explicit stubs, then passed. All 82 policy service cases passed, including 26 new preview/history cases covering final grant/person/time/MFA changes, all-channel denial, original maker, stale bindings, terminal decisions, wrong pool and exact pagination.
- Three authenticated HTTP workflow cases initially failed because the routes did not exist. The generated-contract test then failed because operation enums were absent. Both failures are retained.
- The affected regression executed 129 cases: 128 passed and one existing contract assertion expected 31 routes rather than the new 41. Updating only that count retained every session, CSRF, actor, idempotency, If-Match and problem-response assertion. The corrected case then passed. Thus all 129 distinct affected cases have passing results, with no skipped cases; this is not represented as one entirely green 129-case run.
- Nine new authenticated HTTP cases cover all four operation receipts, large IDs, no history privilege escalation, private projections, all ten route guards, malformed/missing/numeric/unknown inputs, explicit external tenant/feed, original-actor recovery, grant/person/MFA loss, real linked-channel mapping and a real stop that makes approval stale while preserving withdrawal.
- All 682 frontend executions passed: 669 main cases, six future-date repository cases and seven preserved golden scenarios. Twelve new cases cover policy parsing, frozen facts, explicit external inputs, state/version/mapping consistency, scope/order/count validation, selected response identity, four-operation lookup, stale context and lost responses.
- Type checking, module-boundary checks, lint and the production build passed. Lint retains nine preexisting warnings and no errors. The generated Next environment file was restored after the build; unrelated layout, ignore and mentor mockup work is preserved.
- Compiled server contract drift check and generated frontend type check both passed.

Result SHA-256 values:

| Artifact | SHA-256 |
| --- | --- |
| access-service.trx | f122a4eed33e6427a361bab9fd3138e002d33c5fc9b82890eb3043bfd3a4e0ce |
| api-regression.trx | d3513705fe95aeb2e12c154198cd04f36929d4c3d5b56fff9f1a07bad96a6f51 |
| api-contract-correction.trx | 1375c7fb192cca5292cc914f40f912b91b894aafe19b2abaeb440bc422e49818 |

Author review covered the service authorization/fence sequence, explicit projections, parent-pool binding, source/input consistency, frozen client commands, exact result/receipt checks, generated contract differences and original scope. Independent review remains pending agent capacity. HTTP sessions, people and provider facts are synthetic fixtures against real Mongo transactions; no actual Auth0 tenant or policy staff browser acceptance is claimed.

## Publication and remaining work

The baseline 7aa4e0887df8b4f5c307356e6561ef5cd9b80c37 is on GitHub. Its full [Quality run 36533011095](https://github.com/natannielz/Capstone/actions/runs/36533011095) was still running when this checkpoint was prepared. Let that workflow reach a terminal result before another push cancels it; attach later outcomes to their exact commits. The earlier full success on 3064541 does not validate a newer checkpoint.

Vercel owning-account access remains unavailable. A prepared source archive is not a deployment or an activated .NET/Mongo production system.

Next: the dedicated four-operation journal adapter before generic stock.source dispatch; current-access and MFA task discovery; exact login/step-up return paths; accessible Indonesian staff selection, frozen confirmation and separately scoped history; real policy browser verification with loss/reload/recovery/revocation/mobile/keyboard flows.

Accepted source control, writer handoff, producer-complete drain/reactivation, trusted observations/reconciliation/external holds, canonical stock/cost consumers and lots, remaining business modules, operational restore/load evidence and an accepted branch pilot remain required. Source inactivity or an accepted policy definition establishes none of those outcomes.