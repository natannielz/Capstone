# Operating-policy recovery journal verification — 29 September 2026

Status: locally verified continuation of API/client commit 7ea69dc6da5f16069740fd7a3d14cb88b318b961. The policy staff screen, access/MFA/navigation and real browser acceptance remain required.

The existing operation journal now accepts exactly policy propose/approve/reject/withdraw and chooses a dedicated stock.source.policy adapter before generic source handling. It stores only the original actor, operation, key and creation time. A missing adapter cannot fall back to source creation, revision, mapping, suspension or legacy recovery. The adapter has receipt lookup only; no close or resubmit action exists.

Committed receipts must have the exact public shape, exact numeric strings, valid state-dependent policy/decision fields and a status matching the original operation. Missing or malformed receipts, transport failures and late context results retain the original reference. A valid result clears only its matching actor/operation/key; other attempts survive. The owning policy gateway separately binds lookup to the explicitly selected physical pool and original actor.

## Evidence

Artifacts are in tmp/production/stock-source-operating-policy-recovery-results.

- The initial test run had seven failures because valid policy references were not accepted; the fabricated-operation case already passed.
- All 29 affected policy/revision/mapping/suspension recovery cases passed after implementation.
- A subsequent malformed-key hypothesis was disproved: existing journal validation already rejected line-terminated keys on both recording and reload. The additional regression passed; no shared key-validation change was needed.
- All nine dedicated policy recovery cases and the final full frontend suite passed. Final full execution: 678 main cases, six future-date repository cases and seven preserved golden scenarios, totaling 691; zero failures or skips.
- Type checking, module boundaries, lint and the production build passed after the journal change. Lint retains nine preexisting warnings and no errors. No production source changed after those checks; only the additional passing key regression was added before the final full test execution.
- No backend contract or application behavior changed after the verified API/client checkpoint. No new policy browser or external provider acceptance is claimed.

| Artifact | SHA-256 |
| --- | --- |
| recovery-focused.log | db84d250a69c51d6a8a4cd83da1581b1c03cb101c26e72e315764cb69c26ae49 |
| frontend-test-final.log | 3d885d762c0ef63b7cd45395a4f2dd4e602b1048a926082cbb4e35e8733b5d3f |

Author review covered exact operation routing, absence of fallback/close, numeric and state consistency, private-field rejection, reference-only persistence and final context checks. Independent review remains pending available agent capacity.

Next integrate current-access and MFA eligibility, exact return paths, accessible staff selection/frozen confirmation/history and actual HTTPS/Auth0-fixture/Mongo browser recovery/revocation/mobile/keyboard workflows. Internal history scaling, accepted stock control/handoff/drain/reactivation, canonical stock/cost consumers and every original production/report requirement remain open.

The baseline GitHub workflow 36533011095 on 7aa4 remains separately tracked; this local continuation must not be attributed that run's outcome. Vercel owning-account access remains unavailable, and no source archive is represented as a deployment or production activation.