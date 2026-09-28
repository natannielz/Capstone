# Stock-count Catalog adapter — 28 September 2026

Status: local regression passed; independent scoped source review accepted after a test-portability correction. This records the Catalog transaction boundary for the [stock-count observation contract](../requirements/stock-count-observations.md). The Inventory service and draft migration are under implementation and are not accepted by this result.

The adapter validates the exact caller database/client and transaction, requires current stock.count on a canonical warehouse location, and accepts only an active SKU/family/category whose common authoring owner contains that warehouse. It loads immutable packaging facts from the actual current definition. A warehouse counter does not need catalog.manage or buyer publication. New count commands must supply reviewed expected SKU and definition IDs.

The trusted callback writes only metadata in the same transaction. Final checks write-fence the mutable catalog heads, current identity, matching grants/memberships, organization topology and session, then recheck time and cancellation. A callback or final-check failure aborts all caller effects, including when its caller catches the failure. Existing authoring methods retain catalog.manage as the default; only the new internal adapter selects stock.count.

| Artifact | Observed result |
| --- | --- |
| adapter-red-build.log | Compilable test/adapter stubs; zero build warnings/errors |
| adapter-red.trx | All 17 initial adapter cases failed against NotImplementedException |
| adapter-green-inventory-red.trx | Adapter: 23 passed, 3 test expectation mismatches; inactive items correctly raised the existing DomainValidationException. Inventory: 21 expected missing-behavior failures and 2 passing contract cases |
| adapter-catalog-regression.trx | 97 passed, zero failed/skipped, 2m37s; all 26 adapter cases plus existing Catalog authoring/image-target/transaction-cleanup regression |
| inventory-migration-red.trx | 2 expected NotImplementedException failures, 4s; migration implementation not accepted |

Artifacts are under tmp/production/stock-count-results/ in the production worktree. The new tests use real Mongo and provider-session fixtures: unpublished shop-owned 1/12/24-unit packages; wrong permission/location; inactive hierarchy; stale revision/definition; foreign client/database/session; caught callback failure; concurrent definition change, session/grant/membership revocation and warehouse suspension; expiry after the last database response; and cancellation rollback.

This is a filtered local run, not a new full-suite, HTTP, browser, deployment or production result. It creates no stock ledger, adjustment, reservation, valuation, API or staff screen. Inventory persistence/audit/receipt/recovery and its ordered migration must pass their own integration review before acceptance. Full CAT02/F03, all remaining report requirements and the branch pilot remain open.

Independent review found that two test clients used a hardcoded Mongo address instead of the configured fixture. Both now clone the fixture client settings; the expiry monitor preserves any existing configurator before adding its event subscribers. The runtime source did not change. All six affected adapter cases passed in adapter-review-inventory-hardening-red.trx. That coordinated 17-case run also captured 11 expected Inventory historical-identity/definition hardening failures (still under implementation), so its overall result is correctly failing, not a full green run. The adapter's four-file digest manifest was refreshed after the test-only correction. No remaining concrete scoped source finding was reported; no independent runtime rerun is claimed.