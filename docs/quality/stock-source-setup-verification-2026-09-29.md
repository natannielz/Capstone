# Stock-source setup verification — 29 September 2026

Status: local final regression passed; independent review and activation acceptance remain open. Source base: d233148cc67d9311e5fe5944744feef348dc1002.

## Result and boundary

The internal stock-source setup service persists an immutable proposal and exactly one approve/reject/withdraw decision. It provides scoped reads and lists, identical-request recovery and audit history. Approval requires an explicit source-approval grant, current scope, fresh MFA and independent reviewed person authority. It creates an inactive physical stock pool and reserves an organization-wide physical key; it does not create usable source authority or change stock.

No existing role default or finance pack is expanded. Three new capabilities are explicit additions. A source-propose grant permits only the original proposer to withdraw; a read grant alone cannot submit or decide. Approval and rejection share the independently reviewed decision permission.

Four strict draft collection contracts protect proposals, decisions, physical registrations and pools. A unique proposal decision prevents competing terminal outcomes. A unique organization/key registration prevents OMI and S-Mart channels from registering the same canonical physical key as two pools. Aliases of an already registered pool and changes to its setup remain separate work.

The authorization command boundary gained an optional owning-workflow completion hook, called after receipt persistence for both new work and replay. Source setup uses it to recheck time/session and fence actual grants, memberships and scope hierarchy. The database metadata checker now correctly recognizes the built-in unique _id index when that is the required identity key. Existing callers keep their existing behavior unless they supply the hook.

## Executed evidence

| Run | Observed result |
| --- | --- |
| Explicit capability tests before implementation | 4 expected failures: unknown capabilities |
| First workflow run | 25 passed, 1 failed; the test incorrectly expected MFA expiry at the inclusive five-minute boundary |
| Direct database semantic checks before constraint hardening | 5 expected failures: mode/protocol mismatch, noncanonical text/key, out-of-range BSON date |
| Source setup, business permissions and person-store regression | 72 passed, 1 failed; invalid-location fixture constructed a prohibited zero LongId before reaching its assertion |

The expiry case now checks five minutes plus one millisecond, preserving the existing assurance policy. The location fixture now uses the default invalid value so the actual input validator is exercised. The database semantic constraints were hardened and all five direct malformed-record cases passed in the later regression. Two initial build attempts also found test-only namespace/overload/analyzer errors; these were corrected before the first executed workflow run.

Final combined regression executed and passed 217 tests with zero failures or skips in 10m15s. A separate no-build run against the same assembly passed all 31 pure quantity/conversion tests, also with zero failures or skips. Total: 248 passed. The final TRX counters were checked directly. Only two trailing blank lines were removed after that build; no behavior changed. Tests use guarded local generated Mongo replica-set databases and synthetic person/source facts. There is no real business approval in this evidence.

Coverage includes durable proposals and decisions, independent people, same-account and equivalent-person denial, missing person evidence, stale MFA, stale revision, suspended scope, exact channel isolation before counts/paging, another proposer's withdrawal, read-only denial, changed-body retries, revoked-caller replay, audit-insert rollback after pool creation, conflicting reviewers, shared physical-key conflicts, missing/weakened storage, damaged audit/registration graph, complete external source settings, and Int64 policy limits. A completion-hook check proves final denial rolls back new writes/receipts and also blocks replay.

## Remaining delivery and acceptance

The service is internal preparation code. It has no activated HTTP route, DI registration, staff screen or forward migration. Migration registry remains 14. The final source/pool/lot migration must preflight immutable history and ownership, apply constrained storage under a migration lease, and add operational query indexes. Existing pool aliases, policy changes, suspension/reactivation, governed observations/lots, actual source adapters, cost/accounting, UI/API, realistic load and cutover remain incomplete.

D01 actual trusted-person authority and D04 actual physical/source/control/valuation decisions are unavailable; synthetic fixtures cannot satisfy them. OPS02/OPS06 source protocol, reconciliation and cutover acceptance must precede activation. Independent reviewers remain unavailable at the recorded account limit; root inspection and passing tests are not independent approval.

GitHub publication is authorized. Vercel publication is also authorized but remains blocked by access to the project's owning account. No deployment, real financial action, provider activation or data cutover is claimed.


## Final artifacts

| Coverage group | Passed |
| --- | ---: |
| New stock-source workflow / database constraints | 29 |
| New source-setup input rules | 20 |
| New explicit source permissions | 4 |
| Existing business permissions and person approval store | 20 |
| Inventory graph, ledger and repository | 116 |
| Source-authority policy | 28 |
| Quantity and packaging conversion rules | 31 |
| Total, no failures or skips | 248 |

| Artifact | SHA256 |
| --- | --- |
| stock-source-setup-final.trx | 6eac25a403eede8f1e2a72644084bfe20fb03c85361033fec43ca37c26b1c34c |
| stock-source-quantity-regression.trx | a4a8e0115626afca10d10c1798b9e233e6acf014332e3c21dd74357bf22c1e81 |

Artifacts and the exact test/source hash manifest are stored locally under tmp/production/stock-source-setup-results. All 53 new cases are included in the passing final evidence. The actual inventory graph was also tested with a valid lot/balance reference and a newly approved inactive pool; it rejected the stock write.

The previous d233148 GitHub workflow 36458019726 was still running its full backend suite when these local results were recorded; its frontend and real HTTPS/browser/Mongo jobs had passed. The latest completed full-green workflow at this checkpoint remains 309888d / 36453112905. Publication of this substantive change requires its own GitHub workflow; local selected tests are not a substitute for that full result.
