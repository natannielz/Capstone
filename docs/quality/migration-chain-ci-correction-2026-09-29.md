# Interrupted migration chain CI correction — 29 September 2026

Base: staff workflow commit 66e1a6863c152d0a84c827dbb5a9c298e40b85d7. This correction changes three tests and their verification record; application and applied migration code are unchanged.

The complete [GitHub run for predecessor 8c3a6eb](https://github.com/natannielz/Capstone/actions/runs/36506700321) finished with 2,140 backend passes, three failures and 74 explicit skips (2,217 total; 46 minutes 55 seconds). Frontend and the separate three-browser job succeeded. Auth0 Action and contract checks did not execute after the backend failure. This run is not full-green evidence.

All three failures were explicit DDL collection allowlists in the interrupted v5 provider, v6 catalog visibility and v7 historical-read migration tests. Each test resumes the entire registered chain, which now includes migration 17. The observed rejected names were exactly stockSourceAliasProposals, stockSourceAliasDecisions and stockSourceAliases, the three collections installed by the already registered mapping migration.

The correction adds those exact names to each allowlist and independently requires observed DDL for all three mapping collections. Unknown collection names remain disallowed. Existing majority write concern, cancellation and resumable lease, preservation of applied history, stronger validators and alternate-index assertions remain intact. No production schema, migration, permissions or application behavior changed.

The final local replica-set run executed the three previously failing tests: three passed, zero failed, zero skipped, in approximately 11 seconds including test startup. The project rebuilt successfully. This bounded rerun does not relabel the failed full CI run as successful; the next published commit requires its own GitHub checks.

Artifacts: tmp/production/migration-chain-ci-results/final.log and migration-chain-final.trx; predecessor JSON and full log are retained in tmp/production/github-publication-results. TRX SHA-256: 3f523dc02f0b8ca8df616315d7cfa96ff3e37d0587081d952a9e75da1e6ed7b2. Predecessor full-log SHA-256: 0dcb638d1d24aae6896b6eddd71100521721cf8eb8513424da4b643b8c1f3a05.

The staff workflow verification remains in [its evidence record](stock-source-alias-workspace-verification-2026-09-29.md): 51 affected backend tests including four real browser workflows, and 633 frontend executions, passed locally before this test-only correction. The committed frontend and legacy backend trees are unchanged by this correction.

GitHub publication and Vercel deployment are authorized. Vercel owning-account access remains unavailable; preparing a committed-source archive is not deployment. Independent review, the full production/report scope and external acceptance gates remain open.
