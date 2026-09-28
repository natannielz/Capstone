# Stock-count persistence and ordered migration acceptance — 28 September 2026

The immutable observation service and registered v14 migration are accepted for this bounded foundation. The component previously passed 88 scoped cases and independent review. Root reviewed the subsequent registry and fixture integration, checked the exact source manifest, and verified every original integration failure passed after the fixture repairs. The staff choices, authenticated API and counting screen are separate unfinished work.

## Behavior

A new count records its canonical warehouse, actual immutable package definition, observed package/base-unit quantities, optional unverified lot label, actor provenance and reason in one transaction with audit and actor/key-bound receipt. The same unchanged request recovers its original observation; changed data under its key conflicts. Recovery requires current stock.count and preserves historical package facts even after later catalog changes. Stock.read independently authorizes history. Counts do not establish balances, available stock, valuation, source policy, accounting or approved adjustments.

V14 follows the actual v1-v13 chain. It installs strict owned observation validation/indexes and conditional shared audit/receipt constraints while preserving earlier applied history and existing shared constraints. Preflight rejects malformed graphs, duplicate keys, incompatible owned unique indexes and unsafe counters before target DDL. Tests exercise a real interrupted create, migration-lease expiry/resume and competing lease rejection.

Historical catalog identity validation proves the actual SKU's immutable owner/code/physical-item key/family and matching definition. It does not claim an exact historical family-to-category relationship after later family reclassification; such proof requires an additional immutable history contract.

## Verification and observed repairs

- Component: 88 passed, zero failed/skipped, 3m06s; inventory-final88-green.trx, SHA-256 495896e433f80a7d64d52f76d7c3d9ba4f753c731b9056049990b152ff1aba16.
- Registered migration cases initially failed against the v13-only registry; the three forward/resume/lease cases then passed with v14 appended. The seven-case initial integration set passed after narrowly updating the fresh-v13-history assertion and three exact DDL collection allowlists.
- Eight Windows replay cases then exposed fixtures deleting only history13 while retaining14. Reset now removes history at/after13 when intentionally replaying13.
- Protected combined run: **506 passed, 11 failed, one explicit platform skip; 518 total, 14m54s**. Ten failures were intentional old-migration replay setups retaining the future v14 shared audit index. One test could no longer seed malformed observations because registered v14 correctly enforced validation.
- Test-only repair removes exactly ux_stock_count_audit_observation when deliberately rewinding through v13, and explicitly bypasses validation only for the corruption seed. Applied v1-v13 production migrations were not changed.
- Final affected full classes: **58 passed, zero failed/skipped, 2m01s**. Root parsed both TRX files and confirmed all eleven original failing cases are included as passing. The 506 other passing cases were not redundantly rerun after test-fixture-only changes. This is not described as a fresh 518-case all-green execution.

Artifacts under tmp/production/stock-count-results/:

| Artifact | SHA-256 |
| --- | --- |
| inventory-v14-protected-affected.trx | c942ed692a0731f991c9d2dc7f777670651740b15bed4137b8d40d82aef58e7b |
| inventory-v14-repaired-fixtures.trx | 0cbae9921ea88167f5bf9ebe498d5a4cda722f9f1d18d86cf6e23393ccf6118e |
| v14-av2-repaired-source-manifest.json | 5eb9227f9a8b6e65a7a577e659a1638099aaaaf1722049236814ccffd0a914e4 |

The protected verification tree was C:/Users/User/.codex/worktrees/unit-toko-private-evidence/Capstone/tmp/av2/backend. Root verified all 357 manifest files against both that tree and the primary production worktree after the final run. The snapshot excludes later Catalog choice stubs/tests and the future HTTP tests. Storage ACL checks were not weakened; verification used the previously established protected ancestor location. The final repaired run was terminal with no remaining dotnet/testhost process.

The 12-file component had independent review before registration. Root independently reviewed the author's subsequent migration integration after the agents became unavailable due to account usage limits. A second agent review of that final integration did not finish; no such review is claimed.

A new full GitHub run must verify the committed v14 tree. Earlier successful 1b61c1a CI covers admission, not these later changes. Production deployment, real provider/hosting access, the full stock ledger, report completion, pilot and rollout remain open.
