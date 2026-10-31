# Capstone (Unit Toko)

.NET backend in `backend/`, Next.js frontend in `frontend/` (read `frontend/AGENTS.md` before frontend code), MongoDB.

## Program
- The production program (`docs/superpowers/specs/2026-10-19-production-completion-program.md`) runs one plan per phase with superpowers:executing-plans and Sonnet workers: test-first per task, no per-task reviews, one final review per phase on Opus, then commit, push and report. Don't ask for approval per phase.
- Hardening, performance, seals and rehearsal tooling count only when a readiness item or a named launch requirement needs them. Otherwise add a `shortcut:` comment or one line to `docs/debt.md` and go back to the slice.

## Progress
- Progress is features passing end-to-end in `docs/readiness.json` (`id`, `journey`, `steps[]`, `passes`).
- If `docs/readiness.json` doesn't exist yet, draft it from the program spec with every `passes: false` and ask me to approve the journeys and steps before using it.
- Session start: read `docs/progress.md`, `git log --oneline -10` and the board; state "X/Y passing" and pick one failing feature.
- Flip `passes` only after that feature's browser or integration test ran green in this session. Never edit `steps` or weaken a test without asking.
- Commit at every green check on the slice branch. Session end: state the new X/Y and put three lines (done / next / blockers) at the top of `docs/progress.md`.

## Checks
- Frontend: `npm --prefix frontend run typecheck`, `npm --prefix frontend run lint`, `npm --prefix frontend test`.
- Backend tests: `pwsh -NoProfile -File tmp/website-ready/wt-test.ps1 -Root <checkout> -Name <label> [-Build] -Filter "~<ExactClassName>"`. Use exact class names; broad filters like `~MigrationTests` hold the shared heavy lock for hours.
- `tmp/` is gitignored, so these scripts exist only on the PC. On a fresh clone, ask me before recreating them.
- Browser acceptance: no single-rehearsal command yet; ask me which one to run.

## Gotchas
- Run backend tests in small batches, never the full suite. Run substitution, stock-release and dispatch batches with nothing else heavy running; parallel builds cause false 60 s transaction timeouts. Never raise the 60 s limit.
- After a machine or session restart, restart the test mongod on port 27028.
- Stage explicit paths only; never `git add -A` or `git add docs/`. `docs/proposal-completion-plan.md` stays untracked.
- Remove worktrees with `tmp/website-ready/remove-worktree.ps1`, never `git worktree remove` (junctions inside).
