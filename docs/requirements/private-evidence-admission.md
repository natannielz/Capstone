# CatalogImage admission contract proposal — v13
Status: frozen implementation contract, 28 September 2026. Implementation is in progress; no production activation or PAY01 completion.
Scope: existing CatalogFamily/CatalogSku → new lineage/version 1 → quarantined original. No association, replacement, scanner success, buyer bytes, transfer, retry generation, deletion or quota release.
Anchors: primary Catalog/CatalogEvidenceUploadTarget.cs; Mongo/{MongoUnitOfWork,IdAllocator,OutboxStore,IdempotencyStore}.cs; reviewed docs/requirements/private-evidence-contracts.md; PAY01.3–.5; accepted isolated Storage/{EvidenceStorageContracts,WindowsLocalEvidenceStore,VerifiedOriginalHandle}.cs.

## Fixed identity and configuration
- Allocate lineageId, versionRowId, reservationId and attemptId by four independent IdAllocator.NextAsync("evidenceIds") calls; a shared sequence makes their numeric values distinct. Audit IDs use the existing auditEvents sequence. Allocation gaps are allowed.
- Configured policy rows allocate their own IDs through IdAllocator.NextAsync("evidenceStorePolicies"); no policy is auto-created by admission or migration.
- All persisted domain IDs/revisions are positive BSON Int64; schemaVersion is Int32=1, counters/fences are nonnegative Int64, hashes/tokens are canonical 64-lowercase-hex, dates are bounded BSON UTC millisecond dates.
- First slice reserves exactly one attempt and contentVersion=1 per lineage. No version increment/replacement/new-attempt API. Infrastructure receipt/outbox ObjectIds retain existing conventions.
- Uploader identity is userId + provider issuer/subject, stored internally; allow a fresh valid session of that same identity, never another CatalogManage actor. Admission actor grant/session snapshots are provenance, not enduring authority.
- Owner is the exact server-loaded canonical GrantScope, including warehouse when the actual Catalog owner has one; do not invent issuer/organization equivalence. Every later phase compares current owner to this fixed owner.
- Purpose must equal CatalogImage and target kind must be CatalogFamily or CatalogSku; reject every other combination before reservation. A stable SKU target never follows replacement/predecessor links.
- Configure store identity/activation and limits server-side before use. Default missing policy or unavailable activation denies reservation. Build/observe storage activation outside Mongo; it may later become unavailable, so admission is no promise that subsequent filesystem I/O succeeds.
- Store registration must bind the exact storeId used to create LocalPrivateEvidenceStore to its successful StoreActivation.Store; never infer availability from a non-null interface containing UnavailablePrivateEvidenceStore.
- No limits are request-selected or seeded by migration. A policy change/disable invalidates unfinished mutations until an explicit reviewed resolution; receipts/status remain readable under current target authority.

## Minimal v13 collections and exact fields
Common mutable rows have schemaVersion, revision, createdAt, updatedAt; immutable rows omit update fields. All identity/owner/purpose bindings are immutable after insertion.
| Collection | Fields beyond common fields; invariants |
| --- | --- |
| evidenceStorePolicies | _id positive policy ID, storeId unique, active, policyVersion, maximumContentBytes, maximumHeldReservations, maximumHeldBytes, reservationDurationMs, writerDurationMs, ioDurationMs, bufferBytes, chargedReservations, chargedBytes, fence. Config values positive/finite; writerDuration<=reservationDuration; maximumContentBytes<=Int64.MaxValue/3. One store supports only this registered purpose in this slice. |
| evidenceLineages | _id=lineageId, target:{kind,id}, purpose, owner, uploader:{userId,issuer,subject}, firstVersionRowId; immutable; contentVersion starts at 1. |
| documentVersions | _id=versionRowId, lineageId, contentVersion=1, reservationId, attemptId, state reserved/quarantined, lifecycleRevision, fence, contentRevision:null/1, length:null/positive, sha256:null/hash, storeId, generation, detectedMime:null, scanStatus:unavailable, decoderStatus:unavailable, quarantinedAt:null/date. Content tuple/generation immutable once frozen; no bytes-present boolean or clean verdict. |
| evidenceUploadReservations | _id=reservationId, lineageId, versionRowId, contentVersion=1, attemptId, target/purpose/owner/uploader, policyId/policyVersion, admittedTargetRevision, admittedDefinitionId/version nullable, admittedLimits snapshot, operation/key/bodyHash, expiresAt, chargedBytes, chargedReservations=1, state reserved/quarantined, reserveAuditId, completionAuditId nullable. |
| evidenceUploadAttempts | _id=attemptId, reservationId, ticketRevision=1, storeId, stageToken, generation, state fresh/writing/publish_pending/quarantined/interrupted, claimEpoch, claimToken nullable, claimKind null/stage/publication, claimUntil nullable, claimRequestId nullable, writerRequestId nullable, writerUntil nullable, contentRevision null/1, length/hash nullable, lastSafeFailure nullable, completionAuditId nullable. revision is mutable CAS; ticketRevision stays fixed and supplies StorageAttempt.Revision. |
| existing auditEvents | Append evidence.reserve, evidence.claim, evidence.freeze, evidence.interrupted, evidence.quarantine and evidence.status records with positive _id, actor/grant provenance, exact reservation/attempt/version IDs, target/purpose/owner, from/to revisions, safe reason/correlation/time, request fingerprint where relevant. No private tokens, addresses, filenames, bytes or raw exception text. |
| existing idempotencyRecords | Standard actorId/operation/key/bodyHash/result/createdAt plus evidenceAdmission:{schemaVersion:1,reservationId,auditId}. Reserve operation evidence.catalog-image.reserve; exact original safe result persists. Completion uses operation evidence.catalog-image.complete and the deterministic server key reservationId:attemptId:contentRevision, not a new browser key. |
| existing outboxEvents | On quarantine enqueue exactly one sourceKey=evidence.quarantined:{versionRowId}:{contentRevision}, kind=evidence.validation.requested; payload exact IDs/hash/lifecycle revision only. No scanner/consumer activation. |
- Policy counter initialization is an explicit configuration operation; migration inserts no policy. Policy and quota share one row so reservation admission serializes through one checked CAS/fence.
- Reserve checked(3*maximumContentBytes) bytes and one retained reservation before any object I/O; charged totals must remain <= configured caps. This covers .part + .ready + .original simultaneously.
- Charges are retained on interruption, expiry, unknown outcome AND quarantine because all three candidates may remain. No decrement/release without future proven deletion. This bounded slice can exhaust configured capacity; it is not production retention management.
- Shared audit/receipt/outbox collections retain every existing validator/index; add only conditional constraints for evidence-owned kinds/operations/markers.

## Indexes and migration preflight
- Unique: policy(storeId); version(lineageId,contentVersion); version(reservationId); reservation(attemptId); reservation(versionRowId); reservation(lineageId); attempt(reservationId); attempt(storeId,stageToken); attempt(storeId,generation).
- Keep existing receipt(actorId,operation,key), outbox(sourceKey), and legacy documentVersions(documentId,version) indexes. Add canonical lineage/version index; never silently reinterpret legacy documentId/version rows.
- Query indexes: reservation(uploader.userId,createdAt,_id), reservation(target.kind,target.id,createdAt,_id), attempt(state,claimUntil,_id), audit(evidence reservationId,createdAt,_id).
- Before any v13 target DDL/counter write: validate existing options/strict schemas, required/extra index compatibility and duplicate keys; reject TTL/capped/time-series/unsafe collation; preflight every owned record and cross-record relation.
- Reject unknown legacy documentVersions shapes; do not invent a lineage/owner/receipt mapping. Preserve stronger existing validators by conjunction rather than replacement.
- Graph checks: exact lineage/version/reservation/attempt bijection; fixed target/owner/uploader/store/tickets; allowed state/tuple/claim combinations; complete matching immutable audit/receipt provenance; one quarantine outbox event; no fabricated MIME/verdict.
- Recompute every policy charge total from its retained reservations using checked arithmetic; each charge must equal its admitted 3*maximumContentBytes. Policy caps cannot undercut charged totals.
- Use bounded cursors/indexed joins rather than unbounded ToList materialization. Reconcile evidenceIds high-water against all four identity collections ; reconcile the separate evidenceStorePolicies counter against policy IDs; auditEvents high-water considers its whole shared collection.
- Interrupted migration reruns must preserve IDs/data/stronger validators and finish missing DDL. Existing v1–v12 definitions remain unchanged; registry changes only when root integrates the reviewed v13 implementation.

## Same-session service boundary
One Platform/Documents EvidenceService owns these internal methods; tx and database/client binding are checked, no method below starts/commits/retries a transaction or performs storage I/O.
Signatures use CancellationToken last and existing AuthorizationCommandMetadata; returned internal claims/results are provisional until the owning UoW has a known successful commit.
```csharp
Task<ReservationResult> ReserveAsync(tx, actor, ReserveCatalogImage request, metadata, ct);
Task<StageClaim> ClaimStageAsync(tx, actor, reservationId, expectedReservationRevision, claimRequestId, invocationDeadline, metadata, ct);
Task<ExpectedPublication> FreezeContentAsync(tx, actor, StageClaim claim, SealedStage stage, metadata, ct);
Task<PublicationClaim> ClaimPublicationAsync(tx, actor, reservationId, expectedReservationRevision, claimRequestId, invocationDeadline, metadata, ct);
Task<EvidenceResult> CompleteUploadAsync(tx, actor, PublicationClaim claim, VerifiedOriginalHandle handle, metadata, ct);
Task RecordInterruptedAsync(tx, actor, StageClaim claim, safeFailureCode, metadata, ct);
Task<SafeStatus> GetStatusAsync(tx, actor, reservationId, readMetadata, ct);
Task<UploadOutcome> UploadAsync(actor, reservationId, expectedReservationRevision, Stream source, metadata, ct); // OUTSIDE tx
Task<UploadOutcome> RecoverAsync(actor, reservationId, metadata, ct); // OUTSIDE tx; never accepts replacement bytes
```
- ReserveCatalogImage contains target/purpose and optional positive expectedTargetRevision; filename is cosmetic, excluded from canonical body fingerprint. Hash target/purpose/expected revision + normalized reason, with an explicit versioned canonical framing.
- Invoke CatalogAuthoringService.WithCatalogImageUploadTargetAsync(database,tx,actor,target,null,callback,ct) around every phase and replay. Load reservation's actual target server-side for later operations; never use a caller scope.
- Inside Reserve callback, look up/validate receipt first; compare exact body hash and joined reservation/audit/uploader/target. Replay returns the original safe result with fresh authority, no new IDs/charge/audit/outbox.
- Only for a new reservation compare request.expectedTargetRevision to callback snapshot.Revision; passing it directly to the adapter would incorrectly reject old receipts after a later head revision.
- Later phases require current active family/category/SKU and exact current graph/owner, but do not require the admitted head revision forever: there is no pinned association yet. Definition changes require a fresh successful final graph fence.
- Do not call IdempotencyStore.ExecuteAsync or AuthorizationStore.ExecuteAuthorizedAsync inside these methods: both own a UoW. Reuse their schema/metadata/hash discipline through the caller-session receipt core and the existing Catalog adapter's authorization fences.
- All required writes/audits/outbox/CAS operations occur inside the Catalog callback. Any validation/CAS/callback/final-fence failure aborts caller effects with bounded independent cleanup; preserve original exceptions/labels and invalidate an unconfirmed-abort handle.
- For new/mutating work, after the adapter's final await recheck reservation/claim deadline, cancellation and captured policy validity; expiry failure also aborts. Exact known-success receipt replay/status may outlive the original reservation deadline. No further awaited work before owner commit.
- Every mutation touches documentVersions.fence and exact reservation/attempt revisions; all config checks touch the current policy fence. Match claim token+epoch+kind+deadline, not token alone.
- Status is safe metadata under current CatalogManage; it may be read by another currently authorized staff actor. Upload/recovery/mutation additionally require original uploader. No status result grants file access.

## Transaction / filesystem sequence
1. Reserve transaction: current Catalog authority + available registered store + exact active policy → capacity CAS → four identities, reservation/attempt/version/lineage, audit and reserve receipt. Hash/length null. Commit must be known successful.
2. Claim stage transaction: only fresh attempt; CAS to writing with fresh private claim token/request ID, epoch increment and fixed deadline <= reservation expiry. Same invocation can reconcile an uncertain claim by its original private request ID; another request never takes over staging.
3. After known claim commit, construct StorageAttempt(storeId,reservationId,attemptId,ticketRevision,stageToken,generation) from durable rows and call StageAsync once outside all Mongo retries. Source remains caller-owned.
4. Sealed outcome only: Freeze transaction verifies exact claim/attempt, server stage tuple, byte cap, unexpired current policy/reservation/claim and current Catalog authority; sets the same complete length/hash/contentRevision=1 in attempt+version, increments mutable revisions, moves attempt to publish_pending and clears stage claim; required audit.
5. Only after known freeze commit, rehydrate ExpectedPublication from the durable frozen tuple. Never derive an expected tuple from current .part/.ready bytes, names, EOF during recovery, client Content-Length or browser hashes.
6. Claim publication transaction: only publish_pending with frozen tuple; private publication token/epoch/deadline. Permit takeover only after prior publication claim expiry; storage operations remain on the same exact immutable candidate/tuple. This is not permission to replace bytes or assume an old writer stopped.
7. Outside Mongo call PublishAsync(expected,budget) on that exact generation. Published/ExistingIdentical are sufficient only with successful storage flush semantics; incomplete/conflict/unknown/unavailable leave metadata reserved and charges/candidates retained.
8. OpenVerifiedAsync(expected,budget) outside Mongo and retain the same verified read-only handle without emitting bytes. In fresh Complete transaction match handle.Attempt/Content to frozen tuple/current publication claim and reauthorize/fence current Catalog/actor/session.
9. Complete atomically CAS reserved→quarantined, increment lifecycle/reservation/attempt revisions, append immutable completion audit/receipt and validation-request outbox. MIME stays null and scanner/decoder unavailable. Dispose held handle after commit or failure; never delete.
10. Complete/Recover finding an already quarantined reservation the identical EvidenceRef from its authorized matching receipt without restaging or duplicating quota/audit/outbox; the receipt establishes past completion, not current byte availability or eligible/clean status.
- Raw UploadAsync accepts a source only for a fresh attempt; writing/pending/completed attempts reject without consuming it and direct callers to bodyless RecoverAsync. Never claim that an unexamined replacement body was accepted, even if a prior upload completed.
- Use one nonextending absolute deadline per orchestration invocation, constructed outside Mongo retries; each storage budget is <= remaining time and admitted maximum bytes/buffer. Capture the raw invocation start before its first authorized pre-read; derive its deadline once from admitted limits and pass that same invocationDeadline to ClaimStageAsync across every Mongo retry and reconciliation. The first stage claim fixes attempt.writerRequestId and writerUntil permanently. Raw Upload retains that ID/deadline through freeze/publication. Each bodyless Recover uses a new private request ID and its own fixed deadline bounded by admitted writerDuration and reservation expiry. Recovery may finish a known committed frozen tuple after the original raw window expires, but cannot renew/reopen staging, reset a generation or extend the old raw invocation. ClaimPublication receives the fixed invocationDeadline; the original writerRequestId must match original writerUntil. Publication takeover still waits for the prior claim expiry and fences token/epoch. Retain writerRequestId/writerUntil when clearing stage fields; they are provenance, not proof the old process stopped.
- Interrupted source or known Stage failure may record safe attempt interruption under the same live claim/current authority; if that metadata update fails, leave the old writing state unresolved. No exception handler creates a new attempt or removes/reclaims anything.

## Unknown-result and writer rules
| Boundary | Permitted next action |
| --- | --- |
| Reserve/claim commit unknown | Reauthorize and reconcile original receipt/private claim request ID; no source read until success is known. A missing read result is not proof a timed-out transaction aborted. |
| Source interrupted, stage result lost, or stage lease expired before frozen tuple | Return unresolved/interrupted; retain original attempt/quota/files. No stage takeover, inferred sealing, replacement input or automatic new generation. |
| Freeze commit unknown | Read/reconcile exact original frozen tuple under current authorization; only a positively committed exact tuple permits publication. A vanished process's local SealedStage cannot be reconstructed from files. |
| Publish response unknown | Recover only the same committed ExpectedPublication/generation; exclusive inspect/reflush may resolve it. Busy writer is unknown, not absent/stopped. |
| Completion commit unknown | Query exact original receipt+quarantined version/audit/outbox under current authority; reconcile same immutable candidate if still pending. Never allocate a second lineage or delete on ambiguity. |
| Stale stage/publication token resumes | CAS token+epoch+state+deadline denies metadata completion; it cannot overwrite CreateNew candidates. Lease expiry never authorizes byte replacement, cleanup or quota release. |

## Required real Mongo + isolated-storage integration cases
1. Family and SKU admission succeeds with active unpublished targets; wrong owner/read-only actor/inactive parent/unsupported purpose/missing policy/unavailable store creates nothing and consumes no stream.
2. New reservation has four distinct positive IDs, version=1, exact owner/uploader/policy, one receipt/audit and 3*M charge; BSON validators reject malformed IDs/tuples/states.
3. Concurrent same-key reservations return one identity/charge; changed target/purpose/reason conflicts; renamed retry preserves identity; later head revision permits authorized exact replay, revoked authority does not.
4. Different CatalogManage actor cannot claim/upload/freeze/complete/recover the reservation; the same uploader's fresh valid session can recover. Owner transfer or cross-kind/cross-SKU substitution rejects.
5. Real simultaneous reservations at byte/count caps admit only allowed totals, including Int64 overflow boundaries; failed required audit/receipt/final fence rolls back all quota and records.
6. No source reads before known reserve+claim commit; forced real unknown commits at each phase preserve original identity and prove no speculative storage call. Mongo callback retries never reread source or repeat physical writes.
7. EOF-at-cap succeeds; oversize/empty/throwing/cancelled/truncated source retains bounded .part, never freezes/publishes/quarantines; lost stage result cannot be recovered from matching files alone.
8. Freeze audit/CAS failure prevents publication; altered stage tuple, ticketRevision, store/generation, contentRevision or hash is rejected. Once committed, frozen tuple cannot change under any key.
9. Concurrent stage requests consume one source; expired stage claim cannot transfer; stale token cannot freeze. Expired publication claim can recover only the same committed generation and stale claimant cannot finalize.
10. Pause stage/publish/hash, revoke session/grant/membership or retire target, then resume: fresh final authority denies quarantine with retained candidate/charge and no completion receipt/outbox.
11. Replace SKU definition during finalization: conflict/retry must reload current valid graph; never commit against stale snapshot. Advance clock after final authorization await: expired claim/reservation aborts.
12. Lost publish response recovers full identical original; missing/partial/corrupt/busy candidate stays pending; a successful held-handle completion emits no bytes and ends quarantined/unavailable-verdict.
13. Catch a required evidence/audit failure inside caller after an earlier business write: commit must be impossible, including disposed-abort cleanup and labeled retry preservation.
14. Lost completion response returns the exact original reference with one audit/outbox/charge; status exposes no key/token/path/provenance secret; scanner/decoder/association/read/cleanup remain unavailable.
15. v13 upgrades empty and valid prepopulated v12, preserves stronger validators and legacy index, repairs allocator high-water, resumes interrupted DDL; malformed graph/audit/receipt/quota/index/counter fails before any target DDL.

## Freeze blockers / explicit limits
- Existing general authorization/idempotency wrappers are transaction-owning; a same-session receipt implementation is required, not nested calls or generic replay.
- documentVersions already exists with a legacy partial documentId/version index; v13 must preserve it and reject unexplained legacy records before DDL. Do not create a competing version store merely to avoid this check.
- The first raw-upload retry contract is reject-without-reading plus bodyless recovery; this resolves the tension between rejecting changed bytes under one key and never consuming replacement input for an existing attempt.
- Storage tickets and ExpectedPublication do not prove a Mongo commit. Only the orchestration's known-commit/reconciliation sequence establishes permission to perform subsequent I/O.
- Published/ExistingIdentical enum results do not themselves carry immutable proof; completion uses the retained VerifiedOriginalHandle matched to durable rows. No public route may construct proof/tickets.
- No automatic restart after lost stage EOF proof; no quota reclamation without cleanup. Those deliberate limits must appear in safe status/operator documentation rather than fabricated recovery success.
- The baseline outbox claims every kind and no processor is registered. Kind filtering and bounded claims/renewals/retries are being implemented independently. Persist validation requests, but do not enable a consumer before that reviewed integration and real decoder/scanner acceptance.
- Production policy values, storage host acceptance, full association/pins, scanning/decoding, endpoint security and cleanup remain PAY01 work; this proposal selects no production quotas or privileges.