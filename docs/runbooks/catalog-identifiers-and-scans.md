# Catalog identifiers and operational scans

The identifier registry, buyer code lookup and four operational scan screens have passed local owner and browser acceptance. All evidence below covers isolated synthetic fixture databases only. Neither migration 110 nor 111 has been run against operational or pilot data.

## Staff workflow

An authorized catalog manager opens the selected SKU's **Kode dan GTIN** panel. The panel shows its permanent SKU and current primary code separately. To record a supplied GTIN or an internal alias, enter the original value, the source reference and the reason, select **Tinjau pencatatan kode**, inspect the exact SKU and package, then select **Simpan kode**. Inputs start empty. GTIN checksum validation establishes format only; it does not establish ownership of an allocation or product authenticity.

**Koreksi kode utama** makes the supplied new code primary and retains the former primary as an alias. **Hentikan kode** retires an additional code while retaining its permanent reservation and history. Retired codes cannot be reassigned. Stored SKU identity, original SKU code, package definitions and historical commercial snapshots remain unchanged.

If delivery of a save response is uncertain, use **Periksa hasil kode** with the saved original operation reference. The reference journal survives browser restart and keeps separate references from multiple tabs. Recovery does not require reselecting an owner or SKU. An unconfirmed outcome remains unresolved; do not create a new operation to replace it.

## Buyer workflow

The entitled catalog and package detail show **Kode utama saat ini** separately from **Referensi SKU tetap pada catatan transaksi**. Correcting a primary code does not rewrite the original SKU code, accepted order, package definition or commercial snapshot. Search accepts active internal aliases only within the buyer's current catalog; retired or unentitled aliases do not reveal another product. GTIN and supplier namespaces are not accepted as internal buyer SKU aliases.

In **Masukkan banyak SKU**, enter the supplied internal code and explicit package count. The review shows the entered code, current primary and permanent transaction reference together. Applying a reviewed list changes only the private draft. The server and client bind every requested code to its exact identifier, current SKU, package and price. Missing entries, retired codes, contradictory bindings, duplicate identifiers, or two aliases for one SKU reject the complete batch. They never merge quantities implicitly. Saving and recovery use the existing original-key draft workflow.

## Operational workflow

Receiving, picking preparation, dispatch and stock count each expose **Periksa kode barang** within their existing scoped work. Select the code type and provide the exact code, either by typing it or by a scanner that enters text and Enter. Receiving additionally accepts an explicit source namespace and account from the approved purchase order and inspection.

1. Load the intended location and current work document first.
2. Select **Periksa kode**. A result must identify one eligible document choice, exact SKU and package definition.
3. Compare the package and select **Konfirmasi kemasan hasil pindai**.
4. Enter quantity, lot and expiry explicitly where required, then use the workspace's existing review and save action.

A scan never adds quantity or commits a business operation. Invalid, unknown, retired, out-of-context or ambiguous codes produce no automatic selection. **Gunakan pilihan manual** returns to the existing manual path and discards the previous scan selection. The confirmed package must remain part of the reviewed command; changing the work context requires a new confirmation. Final owner checks reject a mapping or source that changed before commit.

The same original key binds the command to its exact confirmed mapping revision or approved source tuple. Switching between manual and scanned input, or changing the mapping with that key, is refused. A committed operation can still be recovered after the code is retired because recovery validates its recorded historical evidence. Missing or corrupted receipt/binding pairs are integrity failures, not permission to recreate an operation.

## API and persistence

- `POST /api/v1/catalog/administration/skus/{id}/identifiers/query` reads the authorized registry; assign, retire and primary correction use the corresponding `/identifiers`, `/identifiers/{identifierId}/retire` and `/code` commands with the current revision and an original idempotency key.
- `POST /api/v1/catalog/administration/identifiers/operations/query` recovers a registry command.
- `POST /api/v1/catalog/scans/query` resolves the exact current operational context. Existing receipt, preparation, dispatch and count command endpoints accept an optional confirmed selection; their existing business snapshots remain unchanged.
- Migration 110 extends the registry while preserving schema-v1 reservation bytes and the permanent uniqueness of codes. Its validated partial primary index allows aliases while keeping one active primary per SKU. Unknown graph/index states fail preflight before metadata changes; the supported interrupted index transition resumes without rewriting old reservations.
- Migration 111 stores command bindings in `operationalScanBindings`. It validates the four existing operation families and classifies pre-binding receipts as `legacy-manual`, without claiming historical scan evidence. Backfill is deterministic and resumable, preserves original receipts/results/audits, and refuses orphan or inconsistent evidence. Every future manual or scanned write requires its binding in the same transaction.

Catalog authority comes from the existing catalog manager scope. Scan queries and commits retain each operational owner's current permissions, location/document scope and final revision checks. Supplier identifiers are not exposed through a general catalog search.

## Verified local evidence

The earlier `tmp/i08-scan-candidate/acceptance-native111-8.json` preserves the independent **31/31** registry/scan owner tests on build8. These prove the actual four command owners, concurrent GTIN uniqueness and final-fence retirement, immutable replay/recovery, rejection of missing/corrupted evidence, legacy index crash/resume, and deterministic migration 111 backfill without rewriting business bytes. Its **40/40** client checks and original registry/count browser3 remain separate historical evidence; they were not relabeled as tests of the later combined binary.

The combined I08/I07 candidate's exact source, DLL, contract, log, TRX and screenshot hashes are recorded in `tmp/i08-i07-combined-candidate/acceptance-combined111-2.json`. Backend build2 passed with zero warnings/errors; the independent full frontend build1 passed full TypeScript and preserved main/pilot outputs. Its build ID is `fReF53dIL0NjWkZ5etRGF`. Overlay-bound checks passed **56/56**, scoped TypeScript checked all 27 candidate files with zero diagnostics, and lint reported zero findings.

| Actual check | Result | Evidence name |
| --- | --- | --- |
| Current primary/active alias/retirement; label-only versus material package change; GTIN/private provenance boundaries; atomic duplicate/unentitled batch refusal | 4/4 | `i08-i07-buyer-native111-2` |
| UI registry authoring; old/current/GTIN confirmation; one count; genuinely lost committed reply and actual browser-restart recovery; retired/unknown refusal | 1/1 | `i08-i07-count-browser111-2-1` |
| Wrong supplier account refusal; approved source/inspection tuple; explicit package/lot/expiry; one held receipt and original-key recovery | 1/1 | `i08-i07-receiving-browser111-2-1` |
| Unknown scans stop; explicit picking and dispatch; both committed replies lost/recovered; one stock decrement and correct reservation/COGS effects | 1/1 | `i08-i07-fulfillment-browser111-2-1` |
| Actual owner correction/retirement; current buyer search/detail and alias bulk; stable original order/SKU; one draft recovered after real browser restart | 1/1 | `i08-i07-buyer-browser111-2-1` |

The dispatch command returned HTTP 200 in **25.530 seconds**, below the unchanged 30-second limit. Representative desktop/mobile screenshots were visually inspected for registry/count, receiving, picking, dispatch, current buyer code, alias bulk and recovered draft; existing styling and readable layouts were retained without horizontal overflow. Pending sibling reads visible in a few captures are not claimed as completed by the image; the passing driver and native assertions establish the stated workflow results.

Earlier failures remain preserved: original count-driver scope/selector assumptions; a new fulfillment fixture used the DTO property name instead of the domain property; the first client invocation used the pilot profile. Corrections changed test/tooling only, with no relaxed business assertion or deadline. The passing client invocation explicitly used the full release profile.

## Explicit remaining limits

- This closes the local scanning and buyer identifier behavior described above. It does not establish CAT04 real-data import/cutover acceptance or the unrelated downstream delivery/invoice/return requirements.
- These are synthetic fixtures and keyboard/text-code input paths. No physical scanner hardware or optional GS1 carrier parsing was certified.
- Public hosting, real Auth0/provider access, operational catalog provenance and production migration approval remain separate gates. Pilot data and deployment are unchanged by these tests.
