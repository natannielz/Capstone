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
| T02 | Accepted foundation | SDK 10.0.401/MongoDB 8.2.12 verified. Backend review fixes, typed client, generated schema checks and CI integration accepted; no production identity or business migration claimed |
| T05 | Independent primitive implementation in progress | Worker owns new Mongo unit-of-work/idempotency/outbox/ID/migration files, Money/LongId and persistence tests; does not change API or T02 files |
| T04, T03, T05A–T16 | Not started | Follow dependency order after foundation and persistence acceptance |

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
