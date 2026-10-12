# Verified checkpoints (provenance seals) — design

Date: 2026-10-12 · Status: **draft for owner review** · Path: architectural

## 1. Intent

**What the owner asked for.** Reach the production latency targets in [unit-toko-production.md §233](../../requirements/unit-toko-production.md) — staging p95 scoped reads ≤500 ms and ordinary writes ≤1 s — by sealing immutable history once and verifying the seal later, instead of re-proving the whole provenance chain on every request. Full re-verification moves to a scheduled integrity job and on-demand audits.

**Constraints carried forward.**
- Each domain module owns its own checkpoints, schemas and verifiers (see [module boundaries](../../architecture/module-boundaries.md)); the shared part is only the sealing primitive.
- Fail closed. Anything missing, unsealed, unverifiable or changed is refused or falls back to the existing full proof — never silently accepted.
- Existing integrity assertions keep their meaning: tampering in the same transaction, or by someone with database write access but without the signing key, is refused **at request time** (owner decision assumed as option 1; see §9).
- Idempotency, original-key recovery and transaction semantics are unchanged.
- Forward-only migration after schema 114 (R02); pilot schema 78 untouched.

**Measured problem (local, 2026-10-12).** Each substitution read costs ~5 s (one order-graph replay = one shipment value verification); a buyer consent costs >30 s, of which one finite proof is 15–22 s. Inside that proof the same receipt origin was reconstructed four times (~1.75 s each) and re-checked ~88 times (~97 ms each). See the R02 row in the [ledger](../../quality/production-progress.md).

## 2. Approaches considered

1. **Keyed seals per owner record, verified on read (recommended).** At commit, the owner writes a checkpoint holding the record's canonical hash, its proof-relevant membership, the checkpoint hashes of its dependencies, and an HMAC under a server key held outside MongoDB. Reads verify the MAC, recompute the row hash, re-run membership as id+hash only, and verify dependency seals (each O(1), memoized per request). Tampering is caught immediately; cost is a few indexed reads per sealed record.
2. **Materialized verified projections.** Store denormalized "verified state" (e.g. current lot value head plus provenance summary) and trust it. Fastest reads, but trust shifts to the projection writer and tamper evidence becomes weaker unless the projection is sealed too — which collapses into approach 1.
3. **Keep full proofs, cache harder.** Transaction/operation-local reuse (already partly done). Cannot approach 1 s because the first proof per request still replays history.

Approach 1 keeps today's trust model (nothing accepted without a cryptographic or full proof) while removing ancestry replay from the request path.

## 3. Architecture

### 3.1 Shared primitive — `UnitToko.Platform/Integrity` (new, small)

Infrastructure like `Platform/Mongo`; contains no domain logic.

- `SealKeyRing` — holds 256-bit HMAC keys with a `keyId`, stored in the persistent protected key directory and encrypted through the existing certificate-protected ASP.NET Data Protection provider (`SessionPayloadProtector.Provider`, purpose `UnitToko.Integrity.Seals.v1`). Keys never live in MongoDB, configuration or environment variables as plain text. It supports one current signing key plus retained verify-only keys for rotation, and fails startup if no usable key exists.
- `ProvenanceSeal` — record type: `kind`, `ownerId`, `recordHash`, `membership[]` (`scope`, ordered `[{id, hash}]`), `dependencies[]` (`kind`, `ownerId`, `sealHash`), `sealedAt`, `keyId`, `mac`. Canonical BSON serialization (sorted, explicit types) is reused from the existing hashing helpers.
- `SealWriter.Seal(...)` / `SealVerifier.VerifyAsync(...)` — compute/verify the MAC, recompute `recordHash` from the live row, re-run each membership scope as a projection of `_id` plus the stored hash field only, and compare exactly. Verification of the same `(kind, ownerId)` is memoized per **unit-of-work operation** (same boundary as today's graph reuse: `MongoUnitOfWork.OwnsOperation`).

### 3.2 Module-owned checkpoints

Each owner adds one checkpoint collection, writes its seal **in the same transaction** as the immutable record (after its existing full verification succeeded), and gains a sealed read path. Phase 1 covers the measured hot path, in dependency order:

| Phase | Module | Sealed record | Replaces on read |
| --- | --- | --- | --- |
| 1a | Procurement | stock receipt origin (PO → custody → inspection → receipt → lot genesis) | `HeldReceiptOriginProof.CaptureAsync` reconstruction and repeated `RequireAsync` |
| 1b | Inventory | stock release decision; pack conversion; lot value transition | release-origin/value proof walks; `CompletedConversionProof` re-capture |
| 1c | Fulfillment | shipment dispatch (incl. value effects) | `ShipmentDispatchRecords.VerifyAsync` value replay |
| 1d | Ordering | order progress at each decision revision (spending/store/substitution/dispatch) | `DecisionGraphCoreAsync` full replay |

A seal depends only on seals of records its owner already proved (e.g. a dispatch seal lists the lot value-transition seals it consumed). Later phases extend the same pattern to payments/billing and after-sales.

### 3.3 Read path

`owner.ReadSealedAsync(id)`: read row + checkpoint → `SealVerifier` → on success return the row and its sealed facts. If the checkpoint is **absent** (pre-backfill history) the owner runs today's full proof unchanged. If it is **present but fails** (MAC, hash, membership, dependency), the read is refused (`*_unavailable`) and an integrity alert is raised — it does not fall back, so a tampered record cannot be "re-proven" around its seal.

### 3.4 Write path

Commands keep their current authorization, freshness fences and full verification of *new* inputs. Instead of re-proving the history those inputs depend on, they verify the dependencies' seals. The new immutable record is sealed before commit. A command that cannot seal (key unavailable) fails closed.

## 4. Integrity job and audits

- `UnitToko.Worker` gains a scheduled `ProvenanceAudit` consumer: for each sealed record it runs the existing full owner verifier and compares the derived facts with the sealed facts; any mismatch is recorded as an integrity incident and alerts the named responder (ledger Q10).
- An on-demand audit endpoint (scoped `integrity.audit` permission) re-verifies one order/lot/receipt chain fully.
- The job is resumable, bounded per run and never writes business data.

## 5. Migration and rollout

- Migration **115** (after R02's 114): creates the checkpoint collections, validators and indexes; registers the key ring requirement. It does not seal anything by itself.
- Backfill command (one writer, offline window): for each existing immutable record in dependency order, run the existing full verifier and write its seal. Resumable with a manifest; refusals are reported, never sealed.
- Until a record is sealed, reads use the full proof, so rollout is safe module by module.

## 6. Error handling

- Missing key or ring corruption: startup health fails; commands needing seals refuse.
- Key rotation: new seals use the current `keyId`; old seals verify with retained keys; the audit job may re-seal under the new key after a full proof.
- Clock: `sealedAt` is informational; freshness of *current* state (balances, grants, sessions) keeps using existing fences.

## 7. Testing

- Existing native tamper tests keep passing unchanged (row edit, delete, receipt/audit removal in the same transaction).
- New native tests per sealed kind: forged checkpoint (wrong MAC), changed row with recomputed unkeyed hash, removed or added membership row, dependency seal mismatch, unsealed-history fallback, key rotation, missing key.
- Latency tests against the requirement: fixture with sealed history; assert p95 of order read and substitution consent below budget on the local reference machine (budget recorded, not hard-coded to production numbers).
- Browser: the R02 substitution journey must pass its unchanged 30-second action bound with large margin.

## 8. Out of scope (this design)

Payments/billing, after-sales and accounting seals (later phases using the same primitive); hosted key custody/HSM (D07); staging load tests (M6).

## 9. Decisions recorded / open

- **Assumed (owner may override):** request-time tamper detection (option 1) rather than detection only by the scheduled job.
- **Assumed:** HMAC-SHA256 with keys outside MongoDB, rather than asymmetric signatures; asymmetric can be added later if third-party verification is needed.
- **Open:** R02 sequencing — recommended: finish R02's remaining local gates using phase 1a/1d where needed, then integrate; R02 must not ship with temporary diagnostics.
