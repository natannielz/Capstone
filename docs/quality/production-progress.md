# Production implementation progress

Plan: `docs/superpowers/plans/2026-09-27-unit-toko-production.md`

Implementation workspace: `C:/Users/User/.codex/worktrees/unit-toko-production/Capstone`
Branch: `codex/unit-toko-production`
Starting commit: `09265cc65b47f26ce3bec8c652fde380dcdcce86`

## Baseline and preservation

- The preceding goal turn created the active goal. This turn begins implementation.
- Original checkout remains at `C:/Users/User/Documents/Capstone`.
- Existing changes copied into the isolated worktree: `.gitignore`, `website/app/layout.tsx`, `website/public/mentor-mockup.html`, `docs/mentor-mockup/`, and the production planning documents. Preserve these; do not commit unrelated UI work as part of backend changes.
- No environment files, private database, uploaded evidence or production credentials copied.
- Planning baseline: 266 tests and TypeScript passed; recheck current implementation before claiming further completion.

## Execution rulings

- Use the explicit dependency order: T00, T01, T02, T05, T04, T03, T05A, T06–T16.
- On 28 September 2026 the user confirmed there is no existing approved identity provider and selected preparation of the planned Auth0 integration. Continue local T03 implementation against Auth0's OIDC boundary; do not infer tenant credentials, paid provisioning, regional-policy approval or successful provider acceptance.
- Independent preparation may run concurrently. Ownership boundaries prevent conflicting edits. The user's approved parallel-agent execution takes precedence over the skill's blanket serial-implementer guidance.
- Native managed worktree provides isolation. Sandbox-approved commands may operate in it; original source remains untouched.
- No artificial implementation approvals are needed for authorized local fixes. Provider credentials, business policy and production cutover remain actual external gates.
- Task IDs do not imply all subtasks are complete; record evidence and outstanding requirements explicitly.
- T02 source scaffolding may proceed in parallel with T01 because it owns only new `backend/src`, .NET tests/contracts/deploy files. Integration acceptance still waits for T01 and a clean frontend/backend build.

## Task states

| Task | State | Evidence / next step |
| --- | --- | --- |
| T00 | Baseline and golden slices reviewed; overall partial | Seven scenarios/84 commands/65 assertions pass both normal and reordered replay; independent reviewer accepted the legacy fixture. C# parity and defect red tests remain open |
| T01 | Accepted and committed | `86f0fc5`: clean Node 24 install/build, transitive browser boundary, typecheck/lint, regression/golden tests, native/SQL tracing, production boot and all three isolated HTTP smoke suites pass |
| T02 | Accepted and committed foundation | `fecbd85`: SDK 10.0.401/MongoDB 8.2.12 verified. Backend review fixes, typed client, generated schema checks and CI integration accepted; no production identity or business migration claimed |
| T05 | Persistence foundation accepted; business parity remains with module ports | Clean backend build and 52 tests pass with no skips; independent final review accepts migration, numeric, durability and recovery fixes; no production business writer enabled |
| T04 | Local authorization/organization foundation accepted; integration gates remain with owning modules | Six templates/packs, scoped queries/fences, reviewed organization/access lifecycle, audited history and durable request recovery; verification below |
| T03 | Auth0 preparation researched; implementation not started | User selected Auth0 preparation; verified-factor evidence and external-change revocation require actual tenant acceptance |
| T05A–T16 | Not started | Follow dependency order after authorization and identity |

## Dependency review

| Producer → consumer | Check / ruling |
| --- | --- |
| T00 → T01 | Capture baseline before paths change; T01 must preserve golden behavior |
| T01 → T02 | Legacy API adapters keep existing UI running while .NET is built |
| T02 → T05/T04/T03 | Shared ActorContext/clock/contract types exist before their implementations |
| T05 → T04 → T03 | Persistence before grants, grants before identity integration |
| T05A → T08/T09/T12 | Accounting and evidence foundations precede consumers |
| T06 → T07/T08/T09 | Inventory is sole physical stock writer |
| T09 → T10 → T11/T12 | Actual accepted receipt precedes financial completion |
| T05/T12 → T13/T15 | Outbox/source identity persists through retries and restores |
| T15 → T16 | Rehearsal and external business gates precede live pilot |

No task authorizes simulated success for unavailable services. Earlier tasks may use isolated fakes for unit tests, but production readiness requires the specified real integration tests.

## Baseline task review

Independent reviewer found no critical defect in the clock/dependency/smoke guard slice. Spec gaps: descriptive golden manifest is not executable, missing acceptance register, and A02/A11/A15 red tests were not added. The register now exists; manifest is explicitly marked nonexecutable. Ruling: carry the exact remaining test gates to their owning implementation tasks, before replacing the affected behavior. T00 remains partial and T01 proceeds against the verified 269-test baseline. Real audit timestamps remain a known nondeterministic legacy field, not an asserted golden value.

Commit `858423b`: baseline hardening and production planning documents. Existing UI changes were not staged or committed.

## Foundation integration in progress

- Root owns `.github/workflows/quality.yml`: separate clean frontend/legacy checks and .NET checks against an isolated replica set. Official GitHub action release commits are pinned. YAML parses locally; no hosted CI run is claimed.
- Root owns API contract tooling in `backend/contracts/{package.json,package-lock.json,generate-client.mjs}` and `frontend/src/api/`. The Node 24 clean tool install passes; typed client generation and schema matching pass, and the runtime client passes success/denial behavior tests. Independent integration review accepted the client and CI boundary.
- System Node is 25.7.0; the bundled Node at `C:/Users/User/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe` is 24.19.0 and is used explicitly for pinned-runtime checks. Invoking system `npm.ps1` can select its colocated Node even after a PATH change; use the explicit Node executable with the npm CLI when checking Node 24.

Independent review accepted the corrected C# foundation: all request bodies are bounded, money parsing/formatting rejects precision/range violations, explicit domain validation maps to 400 while unexpected errors remain redacted 500, readiness 503 has a matching problem schema, and contract export owns and terminates the directly launched API process. Root also verified export/check from a temporary checkout path containing spaces. The worker's 15-test run included one empty template test; that file has now been removed. Fourteen substantive foundation cases remain, with additional persistence tests under construction.

All temporary dependency junctions at root, `website/`, and `frontend/` were removed after checking their paths and targets. Original checkout dependencies were preserved. Clean frontend, legacy and contract-tool installs succeeded. Root's pinned Node 24 checks pass: typecheck, lint (zero errors/nine existing warnings), 271 tests including two generated-client behavior cases, two future-date tests and seven golden scenarios. Clean production build initially failed because separate libSQL installations were bundled; a Node-only external handler, matching-lock guard and native-binary trace resolved it. The clean build and production-mode boot now pass. All three HTTP smoke suites passed against one newly created, attested disposable database (`smoke-api`, `smoke-customer`, `smoke-access-v8`); the server process tree was stopped afterward. The ignored runner/logs are under `tmp/production/` and contain only isolated test activity.

T05 work may prepare independent primitives alongside the final T01/T02 integration gate. No new business writer or production route is enabled by this parallel work, and persistence acceptance waits for the foundation gate. Root keeps `tmp/production/t02-owned-files.json` to prevent new T05 files or unrelated UI work from entering the foundation commit.

Commit `86f0fc5` preserves tracked renames and adds the executable golden baseline. User changes remain unstaged: `.gitignore`'s npm-cache entry, the relocated layout's development-only capture script, and mentor mockup files. T02's API dependency/client changes were deliberately held out of this structural commit.

Commit `fecbd85` records the reviewed API/worker foundation, real Mongo fixture, generated client/contracts and CI gates. No business writes or provider identity were enabled.

## Persistence foundation acceptance

The initial 24 backend cases passed (14 foundation and 10 persistence), but independent review found blockers in migration replay, decimal round trips, expired outbox acknowledgements and invalid default values. These were not accepted as production-ready primitives. Fixes now add explicit migration history/leases and read-only readiness, consistent numeric boundaries and lease checks. The API maps known conflicts to 409 and uncertain commits to a safe same-key recovery response; a worker command explicitly applies migrations.

Root-owned checks inject actual Mongo commit reply failures and transient errors, including cancellation after an uncertain reply. Another worker added an owned test process that is killed after a simulated downstream acknowledgement, then restarted to recover its expired lease. The simulated downstream uses durable idempotency; these checks do not claim that the production notification worker or any external provider exists. The disposable loopback-only Mongo server enables test commands, and CI/compose match that setup.

Final result: clean solution build with zero warnings/errors; 52 backend cases pass, zero skipped. This includes the real Worker `--migrate` process, repeated migration without reseeding or history changes, storage/HTTP numeric contracts, concurrent writes, uncertain commits, bounded abort cleanup and actual process death/recovery. Abort fault coverage verifies a server-side hit and a roughly five-second cleanup deadline against a ten-second blocked response, preserving the original business rejection. Explicit majority writes override a deliberately weak client configuration for counters, migration history and outbox lease transitions; this is command-monitor evidence, not a multi-node failover rehearsal. Idempotency actor IDs persist as BSON Int64. Independent final review found no unresolved issue in this foundation. API export drift and generated frontend contract checks pass; the production OpenAPI file is unchanged because numeric business DTOs are not exposed yet.

The v1 collection registry establishes common numeric/version constraints and planned unique/query indexes. Full aggregate schemas, query explain/load evidence and C# replay of the seven legacy business scenarios remain tied to the business modules that will introduce those writes. Do not infer stock, accounting or production data migration from foundation acceptance.

T04 may now consume the reviewed persistence boundary. Its policy infrastructure can precede provider login, while all production routes remain fail-closed until T03. T05's legacy parity requirement stays open per owning business port; accepting primitives does not waive that gate or move the authoritative writer.

## Next: scoped authorization preparation

Previous goal turn made verified progress: commit `3f03f7b` records the accepted persistence foundation, 52 passing backend cases and final independent review. The next goal turn revalidated that commit and the unchanged unrelated user files before starting T04. New authorization migration history version 2 must preserve version 1 business-document formats and must not replay its validators over stronger later rules.

A read-only T04 map confirms that the legacy single-role actor and broad staff state cannot supply the new authorization boundary. Implement the fixed six templates, four explicit finance packs and separate `payment.verify`; keep public demo customers outside approved B2B migration. Preserve original actor identity across role changes through a durable import mapping.

Policy contracts must evaluate OR across complete grants and AND within one grant, avoiding accidental branch/channel combinations. Ordinary buyers remain self/assigned; `branch.pic` names one branch; organization-wide access requires a separate explicit grant. A null scope never means all. Courier shipment assignment, active hierarchy relationships, immutable maker/checker actor IDs and record revisions remain mandatory.

The transaction boundary must write an actor authorization fence matching active status and current grant version; revocation/suspension writes the same fence. Scope deactivation needs its own coordinated invalidation/fence. Queries apply authorized scope before pagination/counts, and detail/file/export/event access uses the same semantics. Keep Mongo session types out of domain contracts, version template definitions, and snapshot effective grants so a template edit cannot silently widen existing access.

Suggested ownership after shared contracts are agreed: policy/domain and matrix tests; Mongo/API/query/fence concurrency integration; frontend forms and permission documentation. Parent owns composition and generated-contract refresh. New administration remains protected by the fail-closed API until T03 supplies verified identity; no legacy-cookie bridge or test login may enable it. Named live approvers, business memberships and scope assignments remain later operational inputs, not invented seed data.

## T04 core implementation and review

Three independent work streams delivered pure domain policies, sequential Mongo migrations and transactional authorization persistence. Parent integrated versioned HTTP endpoints, generated contracts and regression checks; a worker then built the standalone Indonesian administration page. Independent source reviews covered policy boundaries, query/fence/replay behavior, access lifecycle and API metadata. Production authentication and actor resolution still return no authenticated actor until T03.

Implemented behavior:

- Six materialized role templates, four individually selected finance packs, explicit `payment.verify`, approved scope membership, exact grant alternatives, self/assignment visibility and named branch/organization breadth. Maker/checker compares immutable actors and revisions. No production MFA bypass is configurable.
- Mongo migration v2 preserves v1 history and stronger validators, preflights incompatible records, recovers interrupted upgrades, uses explicit majority writes and seeds no accounts. Authorization documents remain schema version 1.
- Scoped list/count/direct-ID reads, canonical BSON shape checks and active hierarchy checks. Authorized commands fence both actor/grant version and the concrete resource's ancestors; concurrent grant, membership, identity or branch removal invalidates stale work. Trusted same-key results still require current access; new work also requires current revision and immutable approval evidence.
- Independent grant/membership proposals, designated approval, revocation/suspension, bounded delegation and transactional change history. Suspended identities/scopes can still have authority removed. Approvers see delegated powers, membership, template version and effective/expiry dates, not only ordinary permissions.
- Strict HTTP DTOs, nullable Int64 string schemas, declared retry/version headers, server-derived command hashes and `no-store` on both success and rejection. Generated-client arrays use the generator's mutable wire types because the installed openapi-fetch helper cannot infer readonly array methods correctly; UI views still use readonly types where appropriate.
- Administration forms consume server previews and per-resource action hints. Editing invalidates stale previews; uncertain outcomes lock the exact request and retain its retry key. Approval confirmation repeats the complete authority and validity details. The page uses scoped styling, a mobile layout, noindex metadata and stable accessible reason labels.

Verification: full backend run passed 135 cases without skips, followed by one additional organization-wide descendant-fence race and the affected nine query/fence tests after tightening resource projections. There are now 136 distinct backend cases. Frontend full regression passed 281 cases plus two future-date repository cases and seven golden scenarios. TypeScript, production build, import boundaries and lint passed (zero errors, nine unchanged legacy warnings). Server OpenAPI export/check and generated-client drift check passed. The final reason-label accessibility correction passed a fresh production build and both browser viewport workflows. Independent rereview accepts canonical resource projections and the new descendant-fence regression.

Local Edge browser checks used fully intercepted synthetic API responses against a production frontend build at loopback, with desktop 1365px and mobile 390px widths. They exercised server preview, same-key retry after unknown commit, locked uncertain forms, complete review/confirmation, versioned approval, action removal after completion, 401/403 refusal, no page errors and no horizontal overflow. This is UI evidence only; it does not claim a working Auth0 session or real frontend-to-C# origin routing. Screenshots and the disposable runner are under ignored `tmp/production/access-*`.

Remaining work recorded at core commit `de5c6f1` (local lifecycle items 1–3 are addressed in the next section; later integration gates remain):

1. Reviewed organization/branch/shop/warehouse/channel creation, changes and reactivation; no direct database or unchecked status-toggle administration.
2. Proposal rejection/withdrawal and an authorized operation/history view for reconciling uncertain outcomes after a forced browser close. Current in-page retries and unload warnings do not persist a closed-tab command.
3. Purpose-limited privileged-read/export audit and complete organization/access administration navigation. T03 supplies verified directory/profile context and account recovery; reference-based forms are not the final human directory.
4. Each later business module must bind server-owned resource/history plus target/version to this transaction boundary for queries, details, commands, exports, files and events. Raw BSON store results are internal; every endpoint needs its own purpose-limited DTO and allow/deny cases.
5. Named initial approvers and approved real business scopes remain operational inputs. Nothing in the local fixture creates live BNI accounts or grants.

The goal remains active. T04's core can be committed as a reviewable foundation while these lifecycle items stay open; it does not replace legacy authority or authorize rollout. Auth0 research is in `docs/research/2026-09-28-auth0-integration.md`; paid entitlements, closed signup, actual factor timestamps and externally initiated security-change coverage are explicit later acceptance gates.

## T04 lifecycle acceptance — 28 September 2026

This goal turn made verified implementation progress. Three workers delivered organization lifecycle/policy, transactional access decisions/receipts/audit and migration v3; parent integrated HTTP contracts, browser recovery, navigation and history. Independent cross-review found and corrected self-beneficiary reactivation, ancestor-wide access omission, unsafe legacy history projection, and stale-tab account-switch behavior. No blocking finding remains in the bounded source reviews.

- Organization profiles keep reviewed names/codes separate from existing scope identities. Missing legacy names remain explicitly unset. Create, profile update and reactivation proposals preserve immutable kind/parent, expected revisions, active ancestors and independent decisions. `organization.approve` is an explicit fresh-MFA permission in no default template. Suspension remains a separately authorized removal of access.
- Reactivation displays potential descendant and ancestor-wide access impact. Approval rejects changed impact or an affected beneficiary acting as approver, fences relevant scopes and bumps affected identity grant versions. Suspended descendants remain suspended. No live organization, approver or account was seeded.
- Grant/membership requests support independent rejection and requester-only withdrawal, including rejection of overbroad requests without granting their powers. Current authority, immutable actors, reason, expected revision, audit and idempotency remain transactional.
- History filters current authorized scope before count/page and returns only purpose-limited metadata. Organization history projects nested resource revisions without profile snapshots. Unrecognizable immutable legacy scope is `null`, displayed as unknown; recognized operation allowlists prevent an arbitrary legacy event from masquerading as a scoped access/organization event. Privileged reads log defined purpose, correlation, bounded selection/count and relevant scopes in a separate unexposed audit collection.
- The browser stores only actor/operation/key/time before mutation. Forced reload can recover a committed receipt without resending. Missing results remain unconfirmed. Explicit resolution races the original command on the same unique idempotency identity and shared actor fence: either its committed result wins, or a terminal closure prevents a delayed original write. Storage cleanup failure never erases a confirmed server outcome. Current authority is still required; a different actor cannot inspect or close the original actor's receipt.
- Mutations and receipt recovery require a reviewed actor reference checked against independently resolved authentication. Other administration reads send the same optional consistency header after current access is loaded. A changed account receives `actor_changed` without a mutation, receipt or privileged-read write; the browser clears actor-owned drafts and cached decisions without reassigning its stored recovery references.
- Migration v3 preserves frozen v1/v2 and document schema version 1, conjoins stronger validators, preflights incompatible rows/duplicate scoped codes, raises sequence high-water marks without lowering counters, and resumes interrupted upgrades. New organization profile/proposal and read-audit collections are strict and bounded.

Verification: full backend run **168/168 passed, zero skipped**. A final recognized-operation history adjustment then passed all **3 affected StoreHistoryTests**. Clean backend compilation has zero warnings/errors. Server OpenAPI export/check and generated-client drift checks pass. Frontend **290 tests**, **2 future-date repository checks**, and **7 golden scenarios** pass. TypeScript, production build, import boundaries and lint pass; lint retains the same nine legacy warnings and zero errors.

Production-built frontend checks passed in local Edge at desktop 1365px and mobile 390px for all three synthetic workflow groups: original preview/approval/unknown retry/401/403; closed-tab committed recovery, absent-result closure, rejection, purpose-limited history and actor-switch clearing; organization creation and reactivation impact/approval. All groups report no page errors or horizontal overflow. Screenshots were visually inspected. Browser API responses were fully intercepted synthetic fixtures; actual frontend-to-C# origin routing, provider sessions and real user acceptance are not claimed. Temporary browser server was stopped after verification; test Mongo remains the isolated loopback replica set. Evidence is under ignored `tmp/production/` (backend TRX, frontend logs, browser results/screenshots and three worker reports).

Next task is T03 using the user's selected Auth0 integration preparation. Preserve fail-closed production actor resolution until verified provider login, approved membership/invitations, MFA evidence, secure server sessions, CSRF/origin behavior, recovery and revocation are implemented and tested. Actual Auth0 tenant/tier/region, provider secrets and externally initiated factor-change acceptance remain external gates; no paid service was provisioned or external message sent.

T04 acceptance here covers the reusable local foundation. Actual old-role/identity imports and named business assignments remain T15/T16 inputs; each business module must still wire and test its own query/detail/write/export/file/event enforcement and immutable maker/checker evidence. Human directory context and invitations belong to T03; complete workflow accessibility/load and accepted pilot remain T14–T16. No legacy business writer was replaced and the overall goal remains active.
