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
| Payment independence (A02) | T04/T11 | User A records a payment; A cannot verify it after a grant/role change; distinct authorized B verifies once; replay posts once | T04 policy/generic Mongo command tests enforce immutable recorder after role change; actual payment posting port and A02 business acceptance remain open; legacy still permits self-verification |
| Partially paid aging (A11) | T10 | Total 100000, allocated 40000, approved credit 10000, due before business date: outstanding 50000, payment partial and aging overdue | Open; legacy label hides overdue state |
| Conflict exhaustion (A15) | T05 | Force four conflicting writes; response is typed 409, no partial mutation and a fresh retry can succeed | Mongo primitive rollback/fresh-retry case and HTTP 409 mapping pass; each migrated business endpoint must consume this boundary; legacy path remains unchanged |
| Replayable golden scenarios | T02/T05 and module ports | Materialize initial state and full commands/references for each manifest case; compare balances and normalized outcomes against legacy runner | Legacy runner passes seven scenarios and independent review; C# parity remains open |
| Immutable documents | T09/T10 | Rename buyer/address after issuance; old document content/hash does not change | Open |
| Branch authorization | T04 and owning module ports | Cross-branch list/detail/write/export/file/event requests denied, including direct ID substitution | Core Mongo list/count/detail, tuple isolation, courier assignment, malformed-BSON denial and branch-revocation races pass; each business endpoint/export/file/event integration remains open |
| Stock/payment races | T05–T11 | Competing reservation/allocation and lost-response replay preserve exact balances and one result | Generic Mongo version/idempotency races and real uncertain-commit recovery pass; domain stock/payment workflows remain open |
| External source deduplication | T12 | Exported web sale reimported from cashier changes stock/revenue once | Open |
| Restore replay safety | T15 | Restored outbox remains paused until external acknowledgment reconciliation, then no duplicate effect | Open |

## Ruling on T00 sequencing

The independently reviewed baseline-hardening slice is complete; T00 as a whole remains partial. Creating failing tests against unchanged money/access behavior and then moving them between runtimes adds little preservation value at the structure-only step. Those exact regressions must be written and observed failing immediately before their named owning implementations, and executable golden parity must exist before replacing the corresponding legacy behavior. T01 may proceed with the preserved executable baseline and these explicit open gates. This changes sequencing, not the acceptance requirement. The golden payment flow now uses two named actors with the same legacy billing role; its financial invariants do not require preserving the known self-verification defect.

## Production release evidence still required

T01 is accepted locally: clean dependency installs and Node 24 build/typecheck/lint succeed; the build traces SQL and the native database binary; all three isolated HTTP smoke suites pass. T02's service/client/CI foundation passed independent review and local checks. Hosted CI and actual deployment have not run. The new API intentionally denies unauthenticated access and has no business data cutover or managed identity integration yet.

T05 persistence foundation is accepted locally after independent review: 52 backend tests pass without skips on a real local replica set. Evidence covers transactional rollback/concurrency, lost/uncertain commit recovery, exact numeric boundaries, explicit migration/readiness, forced-majority state writes, bounded cleanup and an actual killed/restarted test consumer. Its downstream acknowledgement is simulated with durable idempotency; production outbox delivery and real-provider behavior remain open. Complete aggregate schemas, C# golden parity, representative query/load measurements and multi-node failover/restore remain attached to their owning later gates. API and generated-client contract drift checks pass.

T04 local foundation is accepted: six role templates, explicit finance/delegation permissions, exact scope policies, sequential migration v3, transactional access fences, independent grant/membership and organization lifecycle, rejection/withdrawal, purpose-limited audited history, and durable uncertainty reconciliation. Reactivation includes descendant and ancestor-wide impact, rejects affected-beneficiary approval and invalidates old affected sessions. Stale-tab actor consistency never substitutes for authentication. Full backend verification passed 168 cases without skips; all three affected history tests passed after the final operation-allowlist adjustment. Frontend 290 regressions, two future-date checks and seven golden scenarios pass, along with typecheck/build/lint (nine unchanged legacy warnings) and schema drift checks. Three desktop/mobile synthetic browser workflow groups pass. These checks cover local code and fixture-driven UI, not actual identity, frontend/API origin integration or real user acceptance. T03 and each business module retain their identity/enforcement gates; T15/T16 retain actual import, named assignments and pilot acceptance. The production actor adapter still denies access; no live users, provider tenant or business data were provisioned.

Every requirement in the production specification must be linked to its relevant implementation and check. In particular: real managed identity MFA/verification/recovery; replica-set transaction tests; readable immutable evidence; complete supplier and customer financial settlement; actual origin/cookie behavior; responsive/accessibility and realistic load measurements; verified backup restore; reviewed data migration; staffed independent approvals; and accepted pilot/cutover. No local unit test substitutes for these release gates.

T03 local identity foundation now passes 234 backend cases (66 identity cases) without skips and five Auth0 Action fixtures. The prepared OIDC/session/admission/recovery code and migration v4 are independently reviewed. Atomic callback rollback, one-use state, encrypted payloads, explicit session expiry, scoped invitation/profile reads and actual Mongo in-flight revocation cases pass. API/client contract checks pass. This does not close T03: normal startup still denies authentication; identity routes, origin/CSRF/proxy protections, operator/UI integration, real provider event/delivery/owner-recovery behavior and actual tenant acceptance remain open. No provider resource or external message was created. See `docs/architecture/identity.md` and `docs/runbooks/account-recovery.md`.

T03 runtime/account integration now passes 275 backend cases with no skips, five Action fixtures, 300 frontend cases, two date guards and seven golden cases. Clean backend build, frontend typecheck/production build, contract drift and lint (nine unchanged warnings, zero errors) pass. The configured API uses persistent keys, exact origin/anti-forgery/trusted-proxy controls and verified session actors. Full-host OIDC/callback/step-up/logout is tested with an isolated provider and actual MongoDB; desktop/mobile identity browser fixtures verify native form Origin and user flows separately. Invitation/recovery operation reconciliation retains current authority and honest blocked delivery state. T03 remains open for account administration UI/routes, owner recovery/provider events/delivery, combined real-origin browser acceptance and actual tenant behavior; the deployment and full production release gates remain unchanged. See the latest production-progress entry and `docs/runbooks/identity-hosting.md`.
## Consolidated report acceptance — 28 September 2026

The [97-item acceptance register](../requirements/unit-toko-report-2026-09-28.md) and [execution plan](../superpowers/plans/2026-09-28-report-completion.md) extend these gates. Every row remains open for production acceptance until linked implementation/test/operator evidence is recorded. Existing foundation passes remain valid within their recorded scope.

| Added/refined gate | Owning work | Required evidence |
| --- | --- | --- |
| Eligible image and reviewed version | PAY01/PAY03 | No-image/quarantined submission denial, stale-review replacement conflict, immutable verified evidence history |
| Local OCR without financial authority | PAY02/PAY03 | Representative field-level evaluation; restricted worker; unknown digits/outage path; no approval or balance change from OCR |
| One canonical receipt | PAY04 | Concurrent statement import and declaration recognize cash once; partial/multi-invoice/unapplied balances reconcile |
| Explicit package selection | CAT01–CAT03 | Every entry point; close adds nothing; two packs of 12 show 24; stale pack/price requires review |
| Product identity and physical conversion | CAT01/CAT02/CAT04 | SKU/public-reference uniqueness, legitimate GTIN handling, exact atomic conversion, scans and historic-reference reconciliation |
| Person-level independent decisions | ACC02–ACC04 | Distinct accounts belonging to one maker denied; separate buyer/store/substitution/auditor authority; governed template upgrades |
| Connected deployment and recovery | OPS05/OPS06 | Real browser/API/Mongo/provider/proxy acceptance, object+key restore and no external-ack replay |
| Report coverage and business activation | All report tasks | All 97 rows have implementation/evidence or an explicit valid conditional disposition; D01–D08 resolved before affected activation |

These planning additions do not change the prior test counts or constitute new runtime test results. No production-ready declaration is made.
## Local report implementation checkpoint — 28 September 2026

ACC01 administration now has protected HTTP/search contracts, reviewed invitation/recovery-intent screens and durable request reconciliation. [Bounded acceptance](identity-administration-acceptance-2026-09-28.md) records 277 backend tests, 315 frontend tests and nine synthetic browser groups. Provider event/owner recovery/delivery and actual connected/live acceptance are still open; this does not close the identity release gate.

The still-active legacy Q08 conflict exhaustion bug is corrected in 1b000bd with distinct unknown-outcome handling and [regression evidence](legacy-conflict-acceptance-2026-09-28.md). New business-module concurrency/recovery and deployed operating gates remain with their owning tasks.
