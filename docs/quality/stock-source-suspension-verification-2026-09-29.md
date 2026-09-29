# Source suspension service verification — 29 September 2026

Base commit: ee4a224124a9de78c7cc31255ff05819cf20d68e. This implements a backend lifecycle stop and advances CAT02. The original production plan and all 97 report requirements remain binding. Registered migrations remain 17; the suspension schema is a draft and no suspension API or staff screen is activated by this checkpoint.

## Behavior implemented

The explicit stock.source.suspend permission is limited to fulfillment shop/warehouse scopes and requires MFA within five minutes. No role template, technical administrator or finance pack receives it automatically. It is a restrictive single-actor stop, with a recorded reason; it does not approve settings, accept handoff, prove drain, reactivate a pool or change money. Actual named operators and business acceptance remain D04 inputs.

A minimal action preview supplies the physical pool, exact settings and suspension revisions, current active flag, every affected location and a hash of the reviewed source settings and latest mappings. It omits private configuration reasons and participants. The canonical owner and every historically mapped channel require current action authority before private source history is loaded. Disabled mappings remain included because disablement alone cannot resolve their pending obligations. General history requires the separate stock.source.read permission.

The frozen command rechecks the exact settings/mappings hash and suspension version. A whole-row conditional update sets inventoryPools.active=false and increments its existing fence in the same Mongo transaction as an immutable suspension event, audit and whole-command receipt. Settings and mapping revisions do not advance. The pool fence is excluded from the displayed configuration hash: a stock write that commits first may finish, while a writer using an older snapshot conflicts and re-evaluates the inactive pool. Neither an inactive configuration nor a suspension record is accepted drain or handoff.

The first explicit stop of an already inactive configured pool records that action. Another new command for the same stopped configuration is rejected; original receipt replay remains inert. A changed settings/mapping configuration needs another explicit review. Events retain the original source/settings/mapping IDs, versions, configuration, actor, time and reason. Runtime validation checks actual immutable source/mapping records, sequential suspension history, audit copies, exact actor/operation/body/reason/result receipts and reverse counts. It does not reconstruct historical person assurance or same-millisecond external event ordering.

Recovery looks up the original actor's key and selected pool, requires current suspension authority, validates the stored result, then uses the original body with a receipt-required replay. A command-only operator can recover without general history permission. Missing receipts, foreign actors/targets, corrupt evidence or removed current authority cannot dispatch a replacement command. Only the owning command receipt represents success.

Strict draft storage and physical-pool revision uniqueness are required. Dedicated nonunique indexes cover audit kind/pool/suspension revision and receipt operation/pool/suspension ID. Missing, hidden, sparse, partial or restrictive indexes deny use; equivalent reviewed index names work. The receipt lookup includes the pool predicate. This establishes indexed lookup shape, not production load/latency acceptance.

The administration UI now gives source read/propose/approve/suspend permissions clear Indonesian labels. No suspension page, navigation or new browser workflow is claimed.

## Verification

- Initial three cases reproduced the missing contract/service, then passed. The recovery expansion reproduced the missing recovery method plus a missing test namespace import; both were resolved, and all 21 cases passed.
- The first race run passed all 30 cases. One scope-revocation callback synchronously requested a scope row already fenced by its own transaction, taking roughly 90 seconds until the transaction ended. The identified test host had already completed when inspected for termination; no process was stopped. The fixture now changes scope before the first authorization fence. Grant/membership/MFA changes remain tested after the business audit is written, proving rollback of already-written effects.
- The broader affected regression passed **268 executed tests**, zero failures/skips, in approximately 9 minutes 23 seconds. It covers source setup/revision/mapping, suspension, physical inventory graph/repository and relevant permission policies.
- Review added two missing-index cases. Both failed because the service initially permitted the lookup without those indexes. The required-index gate and pool-qualified receipt query were added, with compatible-name and unsafe-index cases. The final complete suspension suite passed **39 executed tests**, zero failures/skips, in approximately 86 seconds.
- The union of the broad and final suites contains **275 distinct passing cases**. This is not a new whole-backend run. All 39 suspension cases ran against the final service/index code; the other 236 cases cover unchanged affected dependencies.
- Real replica-set races cover two simultaneous stops; stock writer first versus suspension first; actual source-settings and mapping approvals racing a reviewed stop; grant, membership, scope and MFA changes; and receipt deletion between recovery lookup and replay. No skipped browser test is counted as execution.
- A populated balance above the JavaScript safe-integer boundary retains its quantity/buckets and version; settings/mappings, lot ownership, movements and accounting are not changed by the stop. The canonical repository refuses new writes after suspension. These are synthetic local fixtures, not production source activation or fulfilled orders.
- Frontend type checking/import boundaries, production build and **633 existing test executions** (620 normal, six future-date, seven golden) passed. Only the four static permission labels changed. The build's two generated Next declaration-path changes were inspected and restored; unrelated user layout/mockups remain untouched.
- Compiled server OpenAPI and generated-client drift checks passed with no contract changes. No independent reviewer or new suspension browser acceptance is claimed.

Artifacts are under tmp/production/stock-source-suspension-results, including initial/intermediate failures, final logs/TRX, source and artifact hashes, manifests and final-results.json. Broad TRX SHA-256: 874dbf19eb0965afba10de5d541545b9d16c470f81883eadb6125007f7fe4e9a. Final suspension TRX SHA-256: 55dafcacddf2396566297518d6be4a21ecfd92d9a48f354fc03053747c37446e.

## Remaining gates

Next: forward migration 18 with complete graph/storage/counter preflight and resumable majority DDL, strict private-safe API/client, dedicated operation-journal recovery before the generic stock.source handler, staff frozen confirmation/history and real browser verification. Independent review remains pending reviewer capacity.

Accepted control/handoff, producer-complete drain checks, reactivation, persisted reviewed policy/observations/reconciliation, canonical availability/reservation/conversion/receiving/dispatch consumers, external hold acknowledgment, lots/costs and other owning business workflows remain required. A missing producer is never proof of zero obligations. Actual D01/D02/D04 and provider/operations/pilot acceptance remain unresolved. Vercel needs owning-account access; no deployment, live writer change or production acceptance is implied.
