# Production acceptance gates

Implementation baseline: `09265cc65b47f26ce3bec8c652fde380dcdcce86`.
Workspace: `C:/Users/User/.codex/worktrees/unit-toko-production/Capstone`.
These gates remain open until the corresponding implementation and meaningful checks pass. This file is not a declaration of production readiness.

## Baseline evidence

- T00 current checks: 269 passing TypeScript regression tests; repository replay at the frozen business date 2031-09-27 passes 2 tests; typecheck passes; lint has zero errors and nine pre-existing warnings.
- Test runner declares `esbuild` directly. Business date is injectable in tests and demo stock expiry is relative to initialization date.
- All three HTTP smoke scripts now require server attestation for the same disposable run/database before mutation. A localhost URL alone is insufficient.
- `backend/tests/Fixtures/legacy-scenarios.json` is now an independently reviewed executable version 2 golden fixture: full initial state, symbolic references, command sequences and fixed expected balances. Its runner passes seven scenarios on Node 24.19.0 (84 commands, eight unchanged-state rejections, 65 final assertions), in normal order and with every collection reordered after each command. The existing TypeScript suite remains the broader regression baseline.
- Real wall-clock audit timestamps in the legacy engine are not yet frozen. Current normalization does not treat them as golden values; new backend uses its injected clock throughout.

## Required regression cases before affected workflows migrate

| Gate | Owning task | Required executable failure/recovery case | Current status |
| --- | --- | --- | --- |
| Payment independence (A02) | T04/T11 | User A records a payment; A cannot verify it after a grant/role change; distinct authorized B verifies once; replay posts once | Open; baseline permits self-verification |
| Partially paid aging (A11) | T10 | Total 100000, allocated 40000, approved credit 10000, due before business date: outstanding 50000, payment partial and aging overdue | Open; legacy label hides overdue state |
| Conflict exhaustion (A15) | T05 | Force four conflicting writes; response is typed 409, no partial mutation and a fresh retry can succeed | Mongo primitive rollback/fresh-retry case and HTTP 409 mapping pass; each migrated business endpoint must consume this boundary; legacy path remains unchanged |
| Replayable golden scenarios | T02/T05 and module ports | Materialize initial state and full commands/references for each manifest case; compare balances and normalized outcomes against legacy runner | Legacy runner passes seven scenarios and independent review; C# parity remains open |
| Immutable documents | T09/T10 | Rename buyer/address after issuance; old document content/hash does not change | Open |
| Branch authorization | T04 | Cross-branch list/detail/write/export/file/event requests denied, including direct ID substitution | Open |
| Stock/payment races | T05–T11 | Competing reservation/allocation and lost-response replay preserve exact balances and one result | Generic Mongo version/idempotency races and real uncertain-commit recovery pass; domain stock/payment workflows remain open |
| External source deduplication | T12 | Exported web sale reimported from cashier changes stock/revenue once | Open |
| Restore replay safety | T15 | Restored outbox remains paused until external acknowledgment reconciliation, then no duplicate effect | Open |

## Ruling on T00 sequencing

The independently reviewed baseline-hardening slice is complete; T00 as a whole remains partial. Creating failing tests against unchanged money/access behavior and then moving them between runtimes adds little preservation value at the structure-only step. Those exact regressions must be written and observed failing immediately before their named owning implementations, and executable golden parity must exist before replacing the corresponding legacy behavior. T01 may proceed with the preserved executable baseline and these explicit open gates. This changes sequencing, not the acceptance requirement. The golden payment flow now uses two named actors with the same legacy billing role; its financial invariants do not require preserving the known self-verification defect.

## Production release evidence still required

T01 is accepted locally: clean dependency installs and Node 24 build/typecheck/lint succeed; the build traces SQL and the native database binary; all three isolated HTTP smoke suites pass. T02's service/client/CI foundation passed independent review and local checks. Hosted CI and actual deployment have not run. The new API intentionally denies unauthenticated access and has no business data cutover or managed identity integration yet.

T05 persistence foundation is accepted locally after independent review: 52 backend tests pass without skips on a real local replica set. Evidence covers transactional rollback/concurrency, lost/uncertain commit recovery, exact numeric boundaries, explicit migration/readiness, forced-majority state writes, bounded cleanup and an actual killed/restarted test consumer. Its downstream acknowledgement is simulated with durable idempotency; production outbox delivery and real-provider behavior remain open. Complete aggregate schemas, C# golden parity, representative query/load measurements and multi-node failover/restore remain attached to their owning later gates. API and generated-client contract drift checks pass.

Every requirement in the production specification must be linked to its relevant implementation and check. In particular: real managed identity MFA/verification/recovery; replica-set transaction tests; readable immutable evidence; complete supplier and customer financial settlement; actual origin/cookie behavior; responsive/accessibility and realistic load measurements; verified backup restore; reviewed data migration; staffed independent approvals; and accepted pilot/cutover. No local unit test substitutes for these release gates.
