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

## Task states

| Task | State | Evidence / next step |
| --- | --- | --- |
| T00 | Baseline slice reviewed; overall partial | 269 tests + 2 future-date tests, typecheck and lint passed; executable golden parity and defect red tests assigned in production-acceptance.md |
| T01 | Starting | File ownership mapped; split browser projections from authoritative server domain before moving |
| T02 | Tooling prepared | Workspace .NET 10.0.401 and MongoDB 8.2.12 checksums verified; local-only Mongo PID 25108 port 27028 awaiting replica initiation |
| T05–T16 | Not started | Follow dependency order after earlier gates |

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
