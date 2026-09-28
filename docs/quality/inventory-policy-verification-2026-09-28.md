# Inventory quantity and authority verification — 28 September 2026

Status: pure domain rules verified locally; no inventory database writer/API/UI or external stock authority activated.

## Results

The final affected run passed 123 tests, zero failures/skips: 31 new quantity/conversion cases, 28 new source-authority cases and 64 existing Catalog/stock-count policy cases. The test command builds the .NET projects before executing the selected cases. This stage required no Mongo writer or deployed provider.

New coverage includes exact available-package partitions, Int64 limits, expected revisions, reservation-only dispatch, held-to-expired conservation, explicit 12/24-unit conversion ratios, incompatible physical products/organizations/units/attributes, historical descriptive definitions, malformed/reused definition identities and checked derived totals. Source checks cover exact scopes, reviewed policy/pool/observation versions, missing and unreconciled evidence, stale/future timestamps, the exact freshness deadline, shared-pool control and external holds.

The initial quantity implementation stubs failed all 26 selected cases as expected. Source-authority stubs failed all 28 selected cases. Strengthening conversion to include physical/current/historical identity produced 11 expected missing-implementation failures with 18 existing quantity cases passing. Those implementations then passed the affected groups.

A later integration check found a real contract mismatch: the balance helper applied the one-million user-input limit to a valid conversion's larger derived loose-piece total. The focused test failed with invalid_quantity. The repair keeps bounded user-entered steps and accepts checked positive Int64 derived totals in the internal arithmetic kernel. The final run includes the 24-million-piece case and an exact Int64 reservation/dispatch round trip.

## Scope and remaining gates

[The authority contract](../architecture/stock-authority-contract.md) records exact units, validation, plan refinements and persistence obligations. These are pure rules; passing them does not prove concurrent transaction safety, source approval, a physical balance, cost conservation, real source capability or a stock promise.

Independent implementation review is pending while the review agents remain account-limited. Durable pool/lot/source governance, forward migration, actual movements/reservations/conversion, costs, APIs/UI, external reconciliation, operations and pilot acceptance remain part of the full goal. Existing stock-count observations remain independent evidence.

The previously published browser commit 2e6b6727090c4c5a7930746338be71ce1a032527 has successful frontend and real HTTPS/browser/Mongo jobs in workflow 36442542166; its full backend workflow is still running at this record. The new pure inventory rules require their own subsequent exact-commit verification.

## Evidence

Paths are relative to tmp/production/inventory-policy-results unless marked source. Failed and passing artifacts are retained with synthetic inputs only.

| Artifact | SHA-256 |
| --- | --- |
| quantity-red.trx | e8375e0a47ebdb14fdbb838147dcc38c9c7cabedbfa05a08fd60e4777d0d28eb |
| quantity-green.trx | d26a9557a57d2ce2d42af49cbae8592d02c595e0f6efb9efb97d2aab2259fac2 |
| authority-red.trx | 58a476cc8fdb9e76e090bed24d6cfe91357530d861269aa6867c4c84bc9f759b |
| inventory-policy-green.trx | 9df1124af7693e4fc6113e9c260acf427b7ce658da911b448b61c2a674a9c156 |
| physical-compatibility-red.trx | eea019bfae197f05c4813998353e4d330254c106884533695928fc4a049cecd8 |
| inventory-policy-compatibility-green.trx | 9c441615ddb0f3075899409f479b437061f26e0f069405730b17ba548793c5eb |
| derived-quantity-red.trx | 08e5e5a59884684508f0b7293a21f3c88edc0c479110187ee0fae0290fda2bb6 |
| inventory-policy-final.trx | 4ce0a18d97019292dcc7e52c818b488fc77f6a7e2c9c3cbf9565a150837a0816 |
| source: backend/src/UnitToko.Modules/Inventory/StockContracts.cs | 1f5df69d8e7606910ffd46afb3f4fdfa3208d74d97fa97cf845955b0b164ff41 |
| source: backend/src/UnitToko.Modules/Inventory/PackConversionPolicy.cs | 928e9f94475ed7c86ea9f2cb9f6e4d2148d75420a564bd79f7c98eb4f29d8195 |
| source: backend/src/UnitToko.Modules/Inventory/StockAuthority.cs | 266fbc9dd37025e0bb93235eef3f7796e31eb6b4a3c480ce2abc5be753a7ac12 |
| source: backend/tests/UnitToko.Tests/Inventory/InventoryQuantityPolicyTests.cs | e307eef491b62e7f6de3ca12570cd9c5c6c3537201a01659efa9572e08abbb25 |
| source: backend/tests/UnitToko.Tests/Inventory/SourceAuthorityTests.cs | 1d8e4d44e55bf2c91c66092f553d12eff23086351a3b9986cb058b2727821c0d |
