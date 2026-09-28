# Payment Evidence, Local OCR and Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Require secure payment images, assist independent bank review with local OCR, and reconcile each actual transfer once before allocating verified funds to invoices.

**Architecture:** Documents owns immutable originals, evidence versions and extraction records; Billing owns declarations, review decisions, canonical bank receipts and allocations; Accounting owns journal posting. A restricted local worker processes validated documents, while the C# service retains permission, money and approval decisions. Object storage and MongoDB have coordinated eligibility states rather than a fictitious shared transaction.

**Tech Stack:** Existing Next.js/React/TypeScript; C#/.NET 10 API/workers; official MongoDB driver/replica set; private object storage; generated OpenAPI contracts; xUnit and real browser/API/Mongo tests. Benchmark PaddleOCR PP-OCRv6 tiny/small against Tesseract Indonesian/English before choosing a pinned local inference runtime.

**Spec:** [Consolidated report requirements](../../requirements/unit-toko-report-2026-09-28.md), [production specification](../../requirements/unit-toko-production.md), [umbrella execution plan](2026-09-28-report-completion.md), and stable tasks T05A/T08/T09/T10/T11/T12/T13/T15/T16 in the [production plan](2026-09-27-unit-toko-production.md).

## Global Constraints

- Launch audience: BNI branches and approved business accounts only. No public self-registration or retail checkout in the first launch.
- Three main source directories: `frontend/`, `backend/`, `docs/`. Keep required repository/CI metadata at root.
- Preserve existing uncommitted work; review the diff before moving files or committing. Do not commit another task's changes.
- Raw manuals, credentials, production data, uploads and extracted source text stay out of public Git/static assets.
- Int64 internal IDs cross JSON as strings. Money crosses JSON as decimal strings; validate the precise limits in spec §5.
- No whole-company state API, no negative sellable stock, no duplicate invoice sources, no duplicate cash postings, no unbalanced journal and no unauthorized cross-branch access.
- Keep one authoritative stock/money writer. Switch all inventory, order, procurement, fulfillment, billing and accounting writes consistently.
- External production changes, paid service provisioning and data cutover are separate actions; this deliverable is a plan.
- A payment image is mandatory before review submission; a draft may exist without it. OCR confidence is never proof of received funds.
- An audited OCR failure exception never bypasses the required image, malware quarantine, content validation, independent bank review or actor independence.
- Payment images, OCR text and bank records stay local/private. No payment data goes to cloud OCR or a general model service.
- Original bytes are immutable. Rotation, thumbnails, corrections and later supplements retain their own provenance.
- No runtime, model, service identity, bank connection, test environment or production data is provisioned by writing this plan.

## Review Focus

1. Image replacement or amount correction races approval: reject the stale review, and prevent late OCR output from changing the current revision — PAY01/PAY03.
2. One transfer enters through a declaration and repeated statements: retain one canonical receipt and one cash journal across retries/concurrent allocation — PAY04.
3. Malformed/huge images or worker crashes reach processing: apply bounded resource controls and explicit failure, never silent approval — PAY01/PAY02.
4. Another branch requests derived text/thumbnail/duplicate details, or an OCR identity tries to approve funds: scope every surface and deny financial authority — PAY01/PAY02/PAY05.
5. Restore replays leased jobs or verified evidence gains a supplement: preserve original approval history and idempotent journals/results/notifications — PAY02/PAY03/PAY05.

---

## Baseline and dependency order

Reviewed source baseline: `e84faa6`. Source locations below describe that revision.

- Legacy payment record accepts optional evidence; verify lacks independent-recorder/image checks: `backend/legacy/domain/engine.ts:157–163`. Verified payments still accept attachments: `backend/legacy/domain/selectors.ts:91`; attachment addition rewrites the evidence label at `engine.ts:274`.
- Useful legacy behavior exists: pending funds do not reduce AR, and allocation checks verified available funds: `backend/legacy/domain/selectors.ts:21–22`, `engine.ts:170–173`.
- Uploads already bound actual stream bytes, recognize signatures and publish private immutable objects: `backend/legacy/server/attachment-upload.ts:20–55`, `:65–73`, `:102–137`. Quarantine, scanning, decoded-resource limits and OCR remain production work.
- Production `backend/src/UnitToko.Modules/Identity/ApprovalPolicy.cs:12–38` checks immutable recorder history/revisions; `Platform/Authorization/AuthorizationStore.cs:76–98` and `:108–155` implement server-loaded approval history, idempotency and authorization fences. The policy's term "evidence" does not prove an uploaded image exists.
- `backend/src/UnitToko.Platform/Mongo/OutboxStore.cs:27–75` supports lease tokens/stale-completion protection, but currently claims any event kind. `UnitToko.Worker/Program.cs:32` reports no registered processors.
- Production Billing/Documents workflows are pending; generic collections do not mean these modules are complete.

| Task | Requirement ownership | Prerequisites | Independently reviewable result |
| --- | --- | --- | --- |
| PAY01 | S05/S06/S09 foundations; P02/P07/P12 document controls | T03/T04/T05; coordinate with T05A | Private immutable evidence and derived-access contract |
| PAY02 | P03/P04/P08/P09 processing; S08; M03/M04/M05/M09/M10 | PAY01 | Bounded local jobs, extraction contract and measured model decision |
| PAY03 | P01/P05/P06/P07/P09/P11/P12; S07 receiving-account controls | ACC03 trusted person independence, PAY01/PAY02, T05A accounting, T10 invoices | Draft/submission/review; integrated approval calls PAY04 receipt service |
| PAY04 | P04/P08/P10/F08; S07 supplier payout-account integration | ACC03 trusted person independence, PAY03 contracts, T05A accounting, T10 balances | Canonical bank receipts, imports and atomic allocations |
| PAY05 | P02/P11/P12; operating acceptance for all IDs above | PAY01–PAY04; minimum shared queue/UI contracts pulled forward from T13/T14 | Buyer/reviewer UI, notifications and release evidence |

PAY03/PAY04 contracts can be designed together, but PAY03 cannot claim integrated completion using fabricated receipt/journal success. ACC03 in the [access plan](2026-09-28-report-access.md) is an explicit prerequisite for payment, receiving-account and supplier payout-account approval: different UserId values alone do not prove different people. PAY01 refines T05A's earlier Modules/EvidenceService sketch into domain contracts/policies in Modules and one storage/session-owning EvidenceService in Platform/Documents; it replaces that placement sketch, not a second parallel implementation. T05A accounting/period-fencing remains required. T10 retains invoice issuance and aging ownership. PAY02's M01/M02 consumers complete at T08/T09, respectively. PAY05 pulls minimum shared queue, notification, UI and real-browser contracts forward with its slice; full T13/T14 convergence remains later and does not block the PAY05 slice.

## Shared ownership and contracts

Use Modules for domain contracts and Platform for Mongo/storage/runtime adapters. The worker never writes Billing/Accounting collections. Extend the migration registry with new migration classes; never rewrite applied migrations. Read the current API/frontend instructions before route changes. Coordinate shared registry/Program/permission changes with their existing owner.

| Owner | Files below `backend/src/` | Responsibility |
| --- | --- | --- |
| PAY01 | `UnitToko.Modules/Documents/{EvidenceContracts,EvidencePolicy,EvidenceLimits}.cs` | Evidence states, purpose, immutable reference, owning-target binding and eligibility |
| PAY01 | `UnitToko.Platform/Documents/{EvidenceService,EvidenceAccessService,EvidenceCleanupJob}.cs` | Upload completion, transactional evidence pins, scoped bytes/derivatives and coordinated deletion |
| PAY01 | `UnitToko.Modules/Audit/AuditEvent.cs`; `UnitToko.Platform/Audit/AuditWriter.cs` | Safe immutable history in caller transaction |
| PAY02 | `UnitToko.Modules/Documents/{ExtractionContracts,ExtractionCorrection,DocumentQuality,DuplicateEvidenceSignal}.cs` | Immutable output, provenance, corrections, quality/duplicate signals |
| PAY02 | `UnitToko.Platform/DocumentProcessing/{ILocalOcrEngine,ProcessingStore,ExtractionService,DuplicateEvidenceService}.cs` | Local engine adapter, versioned jobs/results |
| PAY03 | `UnitToko.Modules/Billing/{PaymentContracts,PaymentPolicy,PaymentReview,ReceivingAccountVersion}.cs` | Declaration/review contracts |
| PAY03 | `UnitToko.Platform/Billing/{PaymentService,PaymentReviewQueries,ReceivingAccountService}.cs` | Fenced state transitions/queries |
| PAY04 | `UnitToko.Platform/Billing/{BankStatementImportService,BankReceiptService,PaymentMatchService,AllocationService}.cs` | Imported transactions, unique cash, confirmed matches and allocation |
| PAY05 | Frontend payment module and payment-specific workers below | Scoped presentation and notification delivery |

The braces above enumerate files, not a single literal filename. New internal C# contracts use long IDs; project them to HTTP DTOs using the existing LongId converter. Never expose raw long IDs through default numeric JSON serialization.

PAY01 and CAT01 share the CatalogImage association contract: Documents owns the immutable eligible asset; Catalog owns its association with a family/definition and current publication authority. Neither an asset ID nor the existing catalog metadata-only image reference grants file access. Freeze the exact target/pin interfaces with those owners before PAY01 implementation; this refinement does not implement or activate Documents, storage, scanning or catalog images.

## PAY01 implementation sequence and existing boundaries

The [reviewed private-evidence contracts](../../requirements/private-evidence-contracts.md) specify lineage/version-row identity, uploader-bound reservations, same-session pins, abort-on-failure, immutable storage recovery and current authorization after slow integrity inspection. Their initial slice boundaries do not close any PAY01 requirement; full association, replacement/supplement history, cleanup and operational acceptance remain required.

Code inspection at `0119a74`, with accounting v12 in progress, resolves the following implementation order. Allocate the evidence migration number after accounting integration; do not reuse v12.

1. Freeze the Catalog-owned target adapter, then implement durable reservations, immutable references, lifecycle fences/pins, receipts/audit and isolated create-only local storage. Start from an existing image-free family/SKU root; unavailable owning-purpose adapters reject.
2. Add bounded JSON reservation/status/recovery endpoints and a separate authenticated raw binary upload ending in quarantine. Only the dedicated upload metadata bypasses the existing 64 KiB JSON buffering. Require the CSRF header; avoid multipart/IFormFile and its early form parsing. Never reuse the provider machine-callback exemption.
3. Add kind-specific validation claims, bounded real JPEG/PNG decoding, explicit unavailable/scanner outcomes, stale-claim protection and idempotent verdict persistence. Commit the result before acknowledging the outbox job; a crash between these steps must replay safely.
4. Complete Catalog image association across API, domain/service, repository and current schema, then authorized original/derivative delivery. Bind/pin the image to the new immutable definition inside the existing authoring transaction.
5. Finish tombstone cleanup, generation reconciliation, read/processing leases, holds/retention, integrity checks and real object/Mongo race/recovery acceptance. Missing retention keeps deletion disabled.

Reuse the verified actor resolver and `ActorRequiredFilter`, adding evidence mutations to expected-actor checks and their OpenAPI coverage. Reuse `AccessRequestMetadata` and `AuthorizationStore.ExecuteAuthorizedAsync` for current-authority command replay. `AuthorizationStore.LoadAsync`, scope compilation, exact resource reads and final grant/membership/session fences remain necessary; generic idempotency replay alone does not reauthorize.

Catalog authoring graph/receipt helpers are private: add a narrow same-session Catalog adapter instead of copying a weaker ownership check into Documents. Buyer delivery can compose inside `CatalogVisibilityStore.ReadCurrentAsync<T>`; it already owns the authorization transaction, so put association validation and evidence pins/read leases inside its callback. Never nest a second transaction. The current Image-purpose catalog result is metadata only.

`OutboxStore.EnqueueAsync` already accepts the caller session. Its current claim selects every event kind; completion/retry are separate majority writes with token/expiry checks, and renewal/dead-letter/retry ceilings are absent. Resolve those bounded consumer contracts before activating an evidence worker. Object writes, decoding and external side effects stay outside retried Mongo callbacks; the approved existing monotonic ID allocator may reserve IDs outside the transaction and leave gaps.

## PAY01 — Immutable private evidence and security eligibility

**Requirements:** S05/S06/S09, P02/P07/P12 foundations.

**Files:** Own the PAY01 files above; create `backend/src/UnitToko.Platform/Storage/{IPrivateEvidenceStore,IContentScanner,BoundedContentValidator}.cs`; `backend/src/UnitToko.Platform/Mongo/Migrations/EvidenceMigration.cs`; `backend/src/UnitToko.Api/Documents/EvidenceEndpoints.cs`; `backend/tests/UnitToko.Tests/Documents/{EvidencePolicyTests,EvidenceSecurityTests,EvidenceStorageTests,EvidenceAccessTests}.cs`; `docs/runbooks/evidence-storage.md`. Register migration/endpoints in existing CollectionMigrations.cs/API Program.cs.

**Interfaces:** Define `EvidenceRef(long Id, long Version, string Sha256)` and EvidenceMetadata (reference, owning resource/scope, uploader, purpose, state, scan result, MIME, byte/dimension/page counts, immutable object key/generation, timestamps and lifecycle revision). EvidencePurpose values are PaymentImage, SupplierInvoice, DeliveryProof, BankStatement, SupplierInspection and CatalogImage. Content version is immutable; validation/lifecycle revision and concurrency fences are separate. Extend the proposed internal `LoadEligibleAsync` contract to accept the caller's expected owning-target identity as well as session, actor, reference and purpose. Resolve that target and its policy from the owning aggregate in the caller's Mongo session; never trust request-supplied scope. Return metadata or typed rejection only after exact target/purpose/reference/security/ownership checks. A workflow that attaches or relies on evidence must also persist an evidence pin/reference and touch the evidence lifecycle fence in that same transaction, so cleanup or revocation conflicts with consumption. A snapshot read alone is insufficient. AuditWriter inserts events with the caller's Mongo session.

**Purpose and access:** Resolve each purpose through an explicit owning-resource policy. PaymentImage binds to the exact declaration; other financial, delivery and supplier purposes bind to their actual owning aggregates. Reject unsupported purposes or unavailable target adapters until those modules exist; generic foundation collection rows are not proof of ownership. CatalogImage binds to an authorized Catalog family/definition association: authoring requires current `catalog.manage` on its server-loaded owner, while buyer delivery requires current catalog entitlement/publication plus the requested image's actual association with that visible SKU/family/definition. A matching organization or `catalog.read` grant alone is insufficient. EvidenceAccessService applies the owning policy at actual delivery for originals, thumbnails, rotations and extraction text. Every derivative stores its exact original reference, transform/model version and own hash; reject cross-original, cross-target or cross-branch substitutions. Historical access follows the historical owning-document policy. Do not expose raw object keys or treat a previously issued metadata reference as continuing authority.

**Production defaults:** Unconfigured storage, scanner or decoder adapters return unavailable/ineligible; normal production configuration must never select a fake clean scanner. A real isolated local object store, real local Mongo replica set and real bounded image decoder can exercise the integration with explicitly test-only scanner outcomes. Keep scanner/runtime/license selection, process isolation, storage ACLs, key ownership and operational acceptance explicit; this plan selects or provisions no paid service or real runtime. Cancellation tokens alone do not enforce a decoder's hard wall-time/memory limits: require an isolated process with enforceable limits and termination, or leave the adapter unavailable. Unsupported PDF/video decoding remains ineligible; never infer validation from a signature or successful header inspection.

**Catalog integration baseline (`0119a74`):** Coordinate the complete association slice with CAT01; no single-layer relaxation enables safe images.

| Layer | Existing files below `backend/src/` | Required change when PAY01 is implemented |
| --- | --- | --- |
| API | `UnitToko.Api/Catalog/CatalogDtos.cs` | Replace empty-only input validation with bounded eligible references and add explicit image output contracts; current outputs omit images. |
| Domain/service | `UnitToko.Modules/Catalog/CatalogAuthoringPolicy.cs`; `UnitToko.Platform/Catalog/CatalogAuthoringCommands.cs` | Replace the nonempty-list prohibition only alongside exact target/purpose checks and transactional association/pins; preserve draft creation without images. |
| Persistence | `UnitToko.Platform/Catalog/CatalogRepository.cs` | Serialize and read the actual association; current family/definition paths force empty arrays. An asset ID must resolve to one immutable version, never follow a mutable latest-image pointer. |
| Schema/query | `UnitToko.Platform/Mongo/Migrations/{CatalogSchemas,CatalogMigration}.cs`; `UnitToko.Platform/Catalog/CatalogDefinitionQueryService.cs` | Preserve frozen v11, add a new migration and separate current runtime validation from frozen migration schemas. v11 requires `imageAssetIds` maximum count zero. AND-ing it with a new permissive validator still prohibits images: recognize and replace only the supported old image constraint while preserving unrelated restrictions, rejecting unknown incompatible validators. Update repository and buyer-query runtime validation together. |

Buyer delivery must join the association inside the current authorization transaction, reusing `UnitToko.Platform/Catalog/CatalogVisibilityStore.cs` entitlement/publication fences. Its existing Image-purpose result is metadata only, not a storage capability. Coordinate the new migration, API composition and generated contracts with their owners; never edit applied migration history.

- [ ] **PAY01.1 — Write an eligibility test before implementation.** Add EvidencePolicyTests with this exact contract:

```csharp
[Theory]
[InlineData("validated", "clean", "image/jpeg", true, true)]
[InlineData("quarantined", "pending", "image/jpeg", true, false)]
[InlineData("validated", "clean", "application/pdf", true, false)]
[InlineData("validated", "clean", "image/jpeg", false, false)]
[InlineData("failed", "infected", "image/png", true, false)]
public void Payment_requires_present_validated_clean_image(
    string state, string scan, string mime, bool bytesPresent, bool expected)
{
    Assert.Equal(expected,
        EvidencePolicy.CanSubmitPayment(state, scan, mime, bytesPresent));
}
```

- [ ] **PAY01.2 — Run the test and record its expected failure.** `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter FullyQualifiedName~EvidencePolicyTests`. Record the actual missing-behavior diagnostic.
- [ ] **PAY01.3 — Implement eligibility and upload versions.** Use the policy core below; JPEG/PNG retain the existing initial format contract. Additional formats require tested decoders/policy; PDFs can supplement but cannot replace PaymentImage.

```csharp
public static bool CanSubmitPayment(
    string state, string scan, string mime, bool bytesPresent) =>
    state == "validated" && scan == "clean" && bytesPresent &&
    mime is "image/jpeg" or "image/png";
```

  Reserve an authorized durable upload/idempotency record, quota and bounded lease before staging. Count actual streamed bytes and compute the server hash; use server-generated create-only object keys and reject path/reparse escape. Publish an immutable generation, then transition staged → quarantined → validated/failed through expected lifecycle revisions. Resolve lost publish responses by checking exact length/hash/generation, not existence alone. Keep cosmetic filenames outside logical content identity; different bytes under one idempotency key conflict. Do not place non-idempotent object side effects inside Mongo transaction retry callbacks. Reauthorize completion/replay and never delete an object in an upload exception handler or after an uncertain commit.

  Decode and scan the exact preserved original outside long Mongo transactions under configured byte/dimension/pixel/page/time/memory limits. Header/content disagreement, scan timeout/error or incomplete bytes never become validated. Persist verdicts only against the same reference/generation and current claim token, atomically with required audit. Thumbnails/rotation are separately recorded derived transforms. Recheck object existence/integrity during completion, eligibility/access and health checks; enforce immutable object permissions so validated bytes cannot later be replaced.

  Implement cleanup as a durable deletion protocol: transactionally claim a tombstone with an expected lifecycle fence only when no references, in-flight leases or legal hold remain and a configured retention decision permits removal. All new pins, associations and access leases must write/conflict with that same lifecycle record and reject deleting evidence. Delete only the claimed immutable generation outside the transaction, then record completion or retry. Reconcile ambiguous metadata outcomes before deleting possible orphans. Missing retention policy disables deletion; it does not imply an invented duration.

  Add explicit endpoint metadata for the bounded streaming upload route. `UnitToko.Api/Program.cs` currently buffers every body to 64 KiB before authentication: retain that bound for ordinary JSON and exempt only the recognized upload route from buffering, applying its actual-stream cap after current actor, origin and CSRF checks. Preserve private response headers, request correlation and safe errors; add evidence mutations to expected-actor/OpenAPI coverage in `UnitToko.Api/Authorization/RequestActorResolver.cs` and Program.cs. Do not use a broad path/content-type exemption or raise every endpoint's body limit.
- [ ] **PAY01.4 — Add storage/access integration tests.** Real Mongo + isolated private objects must cover interrupted upload, lost completion response, renamed retry, absent/replaced bytes, exact-target mismatch, revoked access on replay, stale validation claims, cleanup racing referenced/in-flight metadata and malicious/truncated/excess-dimension files. Confirm original/thumbnail/OCR access rejects cross-branch and wrong-original substitutions; catalog images additionally require the actual current association and publication authority. Prove pin-versus-tombstone conflicts in caller transactions, retention/hold protection and uncertain-commit recovery. Exercise the annotated upload route's streaming cap/current actor/origin/CSRF plus the unchanged 64 KiB JSON limit. Use an explicitly test-only fake scanner with clean/infected/error outcomes and a real bounded decoder fixture; pending/error cannot satisfy proof or count as real scanner acceptance. Cover all four catalog layers and migration upgrades before enabling associations.
- [ ] **PAY01.5 — Add audit/privacy controls.** Audit upload, validation, download, derivative access and retention decisions with actor/service, resource/version, UTC time, reason and correlation ID. Financial/evidence mutations fail if required audit insertion fails. Ordinary application roles cannot edit/delete history; optional external sink delivery uses durable outbox retries and alerting. Restrict audit reads/exports, redact raw OCR/account values from normal logs, and expose configurable retention/legal-hold/key ownership without inventing durations.
- [ ] **PAY01.6 — Verify, independently review and commit owned files.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter FullyQualifiedName~Documents` against the replica set. Review every original/derived read and the storage/Mongo boundary.

**Exit gate:** No unvalidated bytes/unauthorized derivative enters a workflow; immutable eligible references and audit records support transactional consumers. T05A journal/period requirements remain separately required.

## PAY02 — Bounded local OCR and durable processing

**Requirements:** P03/P04/P08/P09, S08, M03/M04/M05/M09/M10; downstream M01/M02.

**Files:** Own PAY02 files above; create `backend/src/UnitToko.Worker/DocumentProcessing/{DocumentProcessingWorker,ProcessingLimits,ModelManifest}.cs`; `backend/src/UnitToko.Platform/Mongo/Migrations/DocumentProcessingMigration.cs`; `backend/tests/UnitToko.Tests/Documents/{ExtractionTests,ProcessingLeaseTests,DuplicateEvidenceTests,WorkerAuthorityTests}.cs`; `backend/tests/DocumentProcessing/{benchmark-manifest.schema.json,run-benchmark.py}`; `docs/quality/local-ocr-evaluation.md`; `docs/runbooks/document-processing.md`. Modify OutboxStore.cs for kind-filtered claim and worker Program.cs for consumers. Store only synthetic fixtures in Git; real evaluation data stays private.

**Interfaces:** Define these output contracts in Modules/Documents/ExtractionContracts.cs and engine interface in Platform/DocumentProcessing/ILocalOcrEngine.cs:

```csharp
public enum ExtractionPurpose { Payment, SupplierInvoice, DeliveryNote }
public sealed record FieldCandidate(
    string Field, string? Value, decimal? Confidence,
    decimal[]? BoundingBox, string[] Flags);
public sealed record ExtractionResult(
    string JobId, EvidenceRef Evidence, ExtractionPurpose Purpose,
    string ModelVersion, string ModelSha256, string PreprocessingVersion,
    FieldCandidate[] Fields, string[] QualityFlags, string Status);
public interface ILocalOcrEngine
{
    Task<ExtractionResult> ExtractAsync(
        string jobId, EvidenceRef evidence, ExtractionPurpose purpose,
        Stream validatedBytes, CancellationToken cancellationToken);
}
```

Only an assigned validated stream is provided. Bound all strings/arrays/coordinates; document normalized bounding-box coordinates. Unknown fields remain null. Confidence is engine output, not probability that funds arrived. ExtractionService exposes scoped results; correction records preserve old/new value, actor, reason and time separately from immutable model output.

- [ ] **PAY02.1 — Write failing extraction/job/security tests.** Encode the following vectors and assert actual persisted result counts/permissions:

```json
[
  {"case":"no_amount","fields":[],"expectedAmount":null,"mayApprove":false},
  {"case":"uncertain_digits","expectedVisibleFlag":true},
  {"case":"superseded_image","jobVersion":1,"currentVersion":2,"changesCurrentReview":false},
  {"case":"expired_lease","completion":"old-token","storesNewResult":false},
  {"case":"repeat_job","key":"evidence-version+purpose+model+preprocessing","resultCount":1},
  {"case":"ocr_identity_payment_verify","allowed":false},
  {"case":"ocr_claim_notification_job","allowed":false}
]
```

- [ ] **PAY02.2 — Confirm failure.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~ExtractionTests|FullyQualifiedName~ProcessingLeaseTests|FullyQualifiedName~WorkerAuthorityTests"`.
- [ ] **PAY02.3 — Implement bounded versioned jobs.** States are queued/running/succeeded/failed/dead. Claim queries restrict approved kinds; service credentials restrict assigned evidence/results and deny Billing/Accounting writes. Lease renewal/completion compares owner token and expiry. Persist one result per evidence-version + purpose + model-checksum + preprocessing-version; keep late superseded results as history only. Bound concurrency, decoded resources, wall time and memory; kill the isolated process on limits. Deny network egress except explicitly required local transport. Cap transient retries/backoff, surface permanent failures, and persist safe completion/failure events. This brings the required document-worker infrastructure forward from T13.
- [ ] **PAY02.4 — Benchmark before model selection.** Official [PP-OCRv6 documentation](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/algorithm/PP-OCRv6/PP-OCRv6.en.md) identifies tiny/small variants and an ONNX backend; [Tesseract model files](https://tesseract-ocr.github.io/tessdoc/Data-Files.html) provide Indonesian/English models. Compare all candidates on held-out annotated screenshots/phone photos with varied bank layouts, blur, rotation, clipping, compression, uncommon amounts and repeated references. Record target CPU/RAM, dependency/model checksums, cold/warm latency, peak memory, per-field exact match, unknown/error rates, false-match rate and reviewer correction effort. Keep private corpus separate from public aggregate results.
- [ ] **PAY02.5 — Verify portability explicitly.** [ONNX Runtime C# support](https://onnxruntime.ai/docs/get-started/with-csharp.html) does not establish full C# PP-OCRv6 pipeline parity. Compare preprocessing, detection, recognition and postprocessing against the reference runtime on identical held-out inputs. If parity/suitability fails, retain a pinned isolated local reference runtime behind ILocalOcrEngine; .NET remains financial authority. Set and record acceptance thresholds with the business/technical owners before release selection; never claim unmeasured accuracy/hardware suitability. Write actual benchmark invocation/results and decision in local-ocr-evaluation.md.
- [ ] **PAY02.6 — Implement explainable extraction/quality/duplicates.** Extract amount, currency, transfer date/time, recipient, visible account fields, bank and reference; unknown stays unknown. Add blur/orientation/clipping/type flags and exact SHA-256 duplicates. Calibrate image similarity using actual distinct transfers versus crops/recompression of the same image; ambiguous flags require human disposition. Test false positives and restrict cross-scope duplicate detail. Preserve extracted locations, uncertain digits and correction history.
- [ ] **PAY02.7 — Define downstream M01/M02 consumers.** With T08, create `backend/src/UnitToko.Modules/Procurement/SupplierInvoiceComparison.cs` and `backend/src/UnitToko.Platform/Procurement/SupplierInvoiceExtractionConsumer.cs` to suggest fields/matches against exact PO/receipt versions; authorized staff confirm discrepancies and unknown lines. With T09, create `backend/src/UnitToko.Modules/Fulfillment/DeliveryNoteComparison.cs` and `backend/src/UnitToko.Platform/Fulfillment/DeliveryExtractionConsumer.cs` to compare references/quantities against exact shipment versions; OCR cannot certify receipt. Add `backend/tests/UnitToko.Tests/Procurement/SupplierInvoiceExtractionTests.cs` and `backend/tests/UnitToko.Tests/Fulfillment/DeliveryExtractionTests.cs` covering wrong supplier/PO, duplicate references, packaging ambiguity, partial quantities and superseded shipments. Complete each consumer only with real owning-workflow interfaces.
- [ ] **PAY02.8 — Verify, review and commit.** Run Documents tests and applicable downstream tests; execute recorded offline benchmark and preserve aggregate metrics plus private result manifest. Review worker privilege, job/model provenance, version consistency and false-match handling.

**Exit gate:** A measured pinned local configuration produces bounded versioned suggestions/unknowns; retries/crashes cannot approve money or overwrite current results. M01/M02 acceptance remains attached to T08/T09.

## PAY03 — Payment draft, submission and independent review

**Requirements:** P01/P05/P06/P07/P09/P11/P12; S07 receiving accounts; M09 review provenance.

**Files:** Own PAY03 files above; create `backend/src/UnitToko.Api/Billing/PaymentEndpoints.cs`; `backend/src/UnitToko.Platform/Mongo/Migrations/PaymentReviewMigration.cs`; `backend/tests/UnitToko.Tests/Billing/{PaymentSubmissionTests,PaymentReviewTests,ReceivingAccountTests}.cs`. Register endpoints/migration. With the T04 owner, extend explicit permissions for review assignment/correction/rejection/exception and receiving-account proposals/approval.

**Interfaces:** PaymentService consumes ActorContext, AuthorizationCommandMetadata and expected server resource revisions. Define:

```csharp
public sealed record ReviewBinding(
    long PaymentId, long PaymentVersion, EvidenceRef Evidence,
    long ExtractionRevision, long ReceivingAccountId,
    long ReceivingAccountVersion, long BankMatchVersion);
public sealed record PaymentDecisionRequest(
    ReviewBinding Binding, string Decision, string Reason,
    string? ManualOcrExceptionReason);
public sealed record PaymentCommandResult(
    long PaymentId, long Version, string Status, long? BankReceiptId);
```

Client binding fields are compared to server-owned records. States: draft/submitted/replacement_requested/rejected/verified. Security/OCR job states are separate. "Menunggu verifikasi" is the submitted display, with visible processing/failure substates. Before results exist, extraction revision 0 is explicitly pending; a completed/corrected extraction revision is required for normal approval. A manual exception binds its own recorded review revision.

- [ ] **PAY03.1 — Write state/race tests.** PaymentSubmissionTests covers absent/pending/invalid/cross-scope evidence, numeric boundaries and repeated submissions. PaymentReviewTests uses real Mongo barriers to race approval against replacement, amount correction, person-equivalence late binding, person dispute/suspension and access revocation. In PaymentReviewTests and ReceivingAccountTests, create maker account 101 and approver account 102 mapped to verified person 900: deny both payment and receiving-account self-approval; a verified account 103/person 901 succeeds with the other prerequisites. Unknown/disputed approver or historical maker person evidence denies approval. A late reviewed equivalence link of previously distinct people before commit must conflict/reload and deny without changing original account/person snapshots. Pin these additional state vectors:

```json
[
  {"case":"draft_no_image","status":"draft","invoiceOutstanding":"100000.00"},
  {"case":"submit_no_image","error":"payment_image_required","cash":"0.00"},
  {"case":"clean_image_submit","status":"submitted","display":"Menunggu verifikasi","cash":"0.00"},
  {"case":"recorder_changes_role","error":"independent_actor_required"},
  {"case":"two_accounts_one_verified_person","approval":"denied","cash":"0.00"},
  {"case":"unknown_or_disputed_maker_person","approval":"denied","cash":"0.00"},
  {"case":"late_person_equivalence_before_commit","approval":"conflict_then_denied","cash":"0.00"},
  {"case":"approve_v2_after_replace_v3","error":"conflict","cash":"0.00"},
  {"case":"ocr_outage_without_exception","verify":"denied"},
  {"case":"verified_supplement","originalApprovalBinding":"unchanged"}
]
```

- [ ] **PAY03.2 — Confirm failures.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~PaymentSubmissionTests|FullyQualifiedName~PaymentReviewTests|FullyQualifiedName~ReceivingAccountTests"`.
- [ ] **PAY03.3 — Implement draft/revision/submission.** Use existing Money/LongId contracts; complete upload before submission. Submission requires server-validated eligible evidence and schedules processing atomically. Replacement or decision-relevant correction increments the review version and invalidates prior pending review. Preserve original uploader, recorder, submitter and correcting account IDs separately, together with the trusted person/version snapshots known when each action occurred. Append later reviewed person relationships without rewriting historical account/person snapshots. Buyer/account/invoice scope comes from trusted records, never supplied labels.
- [ ] **PAY03.4 — Implement versioned receiving-account changes.** Immutable versions record proposer/approver account IDs and person snapshots, effective state/time and trusted verification reference/reason. Integrate ACC03 PersonRegistryService.ResolveAsync and PersonIndependencePolicy.AreIndependent using the original proposer account IDs; deny unknown/disputed mappings and same-person approval across different accounts. Touch ACC03 PersonAuthorizationFence in the same transaction as approval, in addition to existing access fences, so late equivalence, dispute, suspension or revocation forces reload/rejection. Use a business-approved verification channel; comparing the same submitted request twice is not trusted verification. Existing transactions retain their approved version. A later account change requires explicit reevaluation of affected pending reviews, never silent retargeting. Restrict full account values. Tests cover self-approval, unapproved version, historical preservation and stale verification.
- [ ] **PAY03.5 — Implement independent bank review.** In AuthorizationStore's fenced transaction, load immutable RecordedBy account history, original person snapshots, current payment/evidence/correction/account/bank-match revisions and eligibility. Resolve the reviewer and immutable historical account IDs through ACC03 PersonRegistryService.ResolveAsync in the same session; evaluate current reviewed equivalence with PersonIndependencePolicy.AreIndependent. Deny unknown/disputed actor or maker evidence and same-person approval even when UserIds differ. Touch PersonAuthorizationFence for all resolved evidence/equivalence versions before the financial write, alongside access/session fences; late binding, person dispute/suspension or revocation conflicts and reauthorizes. Preserve old snapshots; a relationship discovered after a completed approval triggers independent exception review, never automatic alteration/reversal of financial facts. Require actual bank transaction corroboration. Save exact ReviewBinding and audit decision; call PAY04 ConfirmReceiptAsync in that same Mongo session. Assignment is workflow ownership, not an authorization grant. Rejection/replacement requires reason and durable scoped notification event.
- [ ] **PAY03.6 — Implement explicit OCR exception/corrections.** Permit re-upload or an authorized documented manual OCR exception only against a clean mandatory image. Preserve failed-job/outage provenance and manually reviewed fields. It never waives scan/bank corroboration/independence. Verified records receive separately labeled supplements or correction proposals; original approval stays unchanged. Money corrections use PAY04/T05A linked reversal and period rules.
- [ ] **PAY03.7 — Verify, independently review and commit.** Pass state/race/account tests plus real PAY04 approval-to-receipt integration; no stubbed cash success. Inject audit/journal failure and assert no partial money/decision/outbox commit. Review identity, version races, bank corroboration and exception permissions.

**Exit gate:** Every verified transfer has clean mandatory image, exact approval snapshot and independent bank corroboration; stale/failed checks leave money unchanged.

## PAY04 — Bank reconciliation and allocation

**Requirements:** P04/P08/P10/F08; S07 supplier payout accounts; financial P06/P07/P12.

**Files:** Own PAY04 Platform files above; create `backend/src/UnitToko.Modules/Billing/{BankReceipt,BankStatementRow,PaymentMatch,Allocation,ReconciliationPolicy}.cs`; `backend/src/UnitToko.Platform/Integrations/BankStatements/{IBankStatementParser,BankImportProfile}.cs`; `backend/src/UnitToko.Api/Billing/ReconciliationEndpoints.cs`; `backend/src/UnitToko.Platform/Mongo/Migrations/PaymentReconciliationMigration.cs`; `backend/tests/UnitToko.Tests/Billing/{StatementImportTests,PaymentMatchingTests,AllocationConcurrencyTests,ReceiptPostingTests}.cs`; `backend/contracts/imports/bank-statement.schema.json`; `docs/runbooks/bank-reconciliation.md`. With T08 owner, create `backend/src/UnitToko.Modules/Procurement/SupplierPayoutAccountVersion.cs`, `backend/src/UnitToko.Platform/Procurement/SupplierPayoutAccountService.cs` and `backend/tests/UnitToko.Tests/Procurement/SupplierPayoutAccountTests.cs`.

**Interfaces:** BankReceiptService exposes `ConfirmReceiptAsync(IClientSessionHandle session, ActorContext reviewer, ReviewBinding binding, long canonicalBankTransactionId, CancellationToken cancellationToken)` returning BankReceipt. It validates confirmed match itself and invokes real T05A PostJournal with stable source event `bank-receipt:{receiptId}`. BankReceipt contains Id, Version, Currency, Money Amount/Allocated/ReservedForRefund, RecordedBy and source references. AllocationService consumes expected receipt version, typed invoice/amount lines, actor and command metadata, returning allocated/unapplied balances. No browser balance is authoritative. Confirmation and supplier-account approval consume ACC03 PersonRegistryService/PersonIndependencePolicy and PersonAuthorizationFence in the same Mongo session; they use immutable maker account history and current reviewed person equivalence, not UserId inequality or user-supplied names/email.

- [ ] **PAY04.1 — Write failing import/cash-once tests.** Preview malformed rows/unknown format; preserve raw source privately; test renamed/repeated/overlapping imports, repeated reference belonging to distinct transfers and uncertain identities. Race declaration approval against statement confirmation; assert one canonical receipt and one cash journal. ReceiptPostingTests and SupplierPayoutAccountTests must deny account 102/person 900 approving account 101/person 900, deny unknown/disputed maker/approver evidence, and race a late equivalence link or revocation against commit; assert no receipt, cash journal or approved payout-account version is created after denial. Preserve original person/account snapshots when current equivalence changes. Use:

```json
{
  "bankReceipt":"100000.00",
  "invoiceAAllocation":"60000.00",
  "invoiceBAllocation":"30000.00",
  "expectedUnapplied":"10000.00",
  "duplicateStatementImports":3,
  "expectedCashJournals":1,
  "concurrentExtraAllocations":["10000.00","10000.00"],
  "expectedAdditionalSuccessfulAllocationCount":1
}
```

- [ ] **PAY04.2 — Confirm failures.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~StatementImportTests|FullyQualifiedName~PaymentMatchingTests|FullyQualifiedName~AllocationConcurrencyTests|FullyQualifiedName~ReceiptPostingTests"`.
- [ ] **PAY04.3 — Implement staged imports/canonical identity.** Parse only approved bank profiles under file/row limits; show actionable errors, totals and identity preview. Enforce account-version + stable bank source transaction identity where available. File hash alone cannot identify overlapping statements; free-form reference alone is not globally unique. For sources without stable IDs, version a reviewed deduplication policy and route collisions to explicit reconciliation; create Mongo uniqueness only for confirmed canonical identities.
- [ ] **PAY04.4 — Implement suggested/confirmed matches.** Compare extracted/corrected declaration details with invoice, approved receiving-account version and bank row. Keep amount/currency/date/recipient/account/bank/reference discrepancies distinct. Unknown payer remains suspense/unidentified. A canonical receipt may have several supporting declarations and many invoice allocations; declaration creation never creates new cash. Exact hash, image similarity and reference reuse flag human review without leaking another branch's details.
- [ ] **PAY04.5 — Implement atomic receipt/allocation accounting.** Confirmation, unique receipt/source claim, journal, audit, idempotency and outbox share one Mongo session. Allocation conditionally consumes receipt balance and invoice remaining balances with expected versions, then posts unallocated/AR journal through T05A; failure rolls back everything. Allocations plus refund reservations cannot exceed verified funds. Preserve partial/multiple-invoice/unapplied funds, scope/currency checks and T10 overdue remaining-balance behavior. Same key/body retries return original result; changed body conflicts. Unallocation/correction uses explicit linked reversal under period rules.
- [ ] **PAY04.6 — Integrate supplier payout account changes.** Require independent proposal/approval plus trusted verification; settlements reference approved account versions. Resolve current reviewed person equivalence for proposer and approver through ACC03 using immutable account history; different UserIds are insufficient, unknown/disputed evidence denies approval, and person/access fences participate in the same approval transaction. Retain original person snapshots and append late relationship history; do not silently rewrite prior settlements. Existing T11 refund approval/payout-recording/independent payout-verification remains required; do not introduce a transfer API. Tests reject self-approval, stale/unapproved supplier account, historical account overwrite and paid status from OCR alone.
- [ ] **PAY04.7 — Verify, review and commit.** Run named tests, supplier account tests and T05A failure/closed-period cases. Inject uncertain commit/lost response and retry confirmation; assert one cash journal and no over-allocation. Reconcile customer/finance/Accounting totals. Review statement/declaration convergence and uniqueness/fences.

**Exit gate:** A transfer becomes cash once across intake routes; ambiguity is visible; simultaneous allocation cannot exceed funds or invoice balance.

## PAY05 — Buyer/reviewer UI, notifications and operating acceptance

**Requirements:** P02/P11/P12; end-to-end acceptance for P01–P12/S05–S09/F08/M03–M05/M09/M10.

**Files:** Create `frontend/src/modules/payments/{payment-submission,payment-review,payment-reconciliation}.tsx`, `frontend/src/modules/payments/{payment-api,payment-state}.ts`, `frontend/src/modules/payments/components/{evidence-picker,evidence-comparison,discrepancy-list,review-history,allocation-editor}.tsx`; `frontend/tests/payment-workflow.test.ts`; `frontend/tests/e2e/{payment-evidence,payment-review}.spec.ts`; `backend/src/UnitToko.Worker/Payments/{PaymentNotificationWorker,PaymentReviewReminderJob}.cs`; `backend/tests/UnitToko.Tests/Billing/PaymentNotificationTests.cs`; `docs/quality/payment-ocr-acceptance.md`. Extend prior runbooks and generated API contracts through the established generator; coordinate route integration with T14 owner.

**Interfaces:** UI uses generated scoped endpoints, EvidenceRef and ReviewBinding; balances/permissions stay server-owned. Comparison shows original, locations/confidence/unknowns, corrections and individual discrepancies. Notifications consume committed payment events with stable event/recipient/version keys, durable leases and provider acknowledgements; they perform no financial transition.

- [ ] **PAY05.1 — Add user-state tests before components.** Camera/gallery, preview, rotation, progress, replacement, errors, cancellation and lost-response recovery must preserve a draft until submission. Keep operation identity during uncertain outcome; query server status before duplicate creation. Add:

```typescript
import assert from "node:assert/strict";
import test from "node:test";
import { paymentStatusLabel } from "../src/modules/payments/payment-state";

test("pending review remains understandable", () => {
  assert.equal(paymentStatusLabel("submitted"), "Menunggu verifikasi");
  assert.equal(paymentStatusLabel("draft"), "Draf");
  assert.equal(paymentStatusLabel("replacement_requested"), "Perlu bukti pengganti");
});
```

- [ ] **PAY05.2 — Confirm failures.** Use `npm --prefix frontend test` and ensure discovery of the new workflow test. Reuse T14's real browser harness when present; otherwise pull its minimum isolated browser/API/Mongo setup into this slice with the T14 owner. Minimum queue/UI contracts are shared or pulled forward; completing all later T13/T14 convergence work is not a PAY05 prerequisite. Intercepted payment fixtures do not prove financial acceptance.
- [ ] **PAY05.3 — Implement buyer/reviewer pages.** Buyer image controls preserve originals and show upload/security/OCR/submission status separately. Queue shows assignee, age, discrepancies, reason and next action. Detail shows original beside extracted/corrected values and actual bank corroboration. Buttons follow server permissions; stale approval conflicts refetch and require renewed review. Mask sensitive details when full fields are unnecessary. Build loading/empty/denied/validation/offline/uncertain-result states and safe draft recovery.
- [ ] **PAY05.4 — Implement reasons/notifications/operating ownership.** Replacement/rejection/approval events send scoped links without raw images/account/OCR text. Store queued/provider-accepted/failed accurately; provider acceptance does not prove reading. Add deduplicated retries and tests for worker death after acknowledgement. Apply named reminder/escalation owners/intervals only after business decision. Monitor scans/OCR backlog/dead jobs, waiting reviews, unmatched funds, failed notifications and audit delivery with actionable runbooks.
- [ ] **PAY05.5 — Run real browser/API/Mongo acceptance.** In the established isolated identity/API/frontend/replica-set environment, complete image → scan → OCR → correction/discrepancy → independent bank review → receipt → partial/multiple allocations. Exercise phone/tablet/desktop, keyboard/screen-reader labels, focus and validation summaries. Test cross-branch original/thumbnail/OCR/duplicate/export access and stale-review replacement. Record per-ID results; no mocked cash transitions.
- [ ] **PAY05.6 — Rehearse restore/failure behavior.** Under T15's isolated restore procedure, restore Mongo, original objects, derived metadata and required encryption keys. Pause outbound workers, reconcile acknowledgements, then resume; confirm original approved hashes/version links, result deduplication, one cash posting and one acknowledged notification. Exercise dead OCR, failed scan/audit delivery and staff recovery. No production restore is authorized by this plan.
- [ ] **PAY05.7 — Verify, independently review and commit owned changes.** Run `npm --prefix frontend run typecheck`, `npm --prefix frontend run lint`, `npm --prefix frontend test`, scoped Billing/Documents tests and the established real-browser command. Record exact commands, observed counts, skipped checks/reasons and artifacts in payment-ocr-acceptance.md. Review privacy, money invariants, stale approval and recovery. T16 pilot requires trained independent operators and a real evidence-to-reconciliation cycle before expansion.

**Exit gate:** Real integrated evidence proves all launch payment requirements; measured OCR acceptance and operator recovery procedures exist. Every unfinished business/test gate is named rather than marked passed.

## Bounded business gates and release evidence

These constrain affected production actions, not synthetic development or unrelated work. Record actual owner/decision/version; do not invent approval policies, retention periods or performance claims.

| Gate | Required decision/evidence | Needed by |
| --- | --- | --- |
| Receiving/payout accounts | Approved accounts, operating owner, trusted verification channel, independent approvers/backups and version-change procedure | PAY03/PAY04 activation |
| Bank statements | Actual formats/identifiers/access, import owner/cadence, overlap/duplicate policy and suspense handling | PAY04 adapter acceptance |
| Financial policy | Supported currencies, allocation ownership scope, unallocation/correction/refund/closed-period procedure | PAY03/PAY04 real money writes |
| OCR exception | Explicit permission, acceptable reasons, manual checks and escalation owner; required image/security/bank independence remain mandatory | PAY03 exceptions |
| Evidence privacy | Media/resource quotas, retention/legal holds, derived-data handling, key owner and full bank-detail access | PAY01 production storage |
| Model acceptance | Approved representative corpus, target hardware, field/false-match/review-effort/latency/memory thresholds and actual measurements | PAY02 model release |
| Work queue | Assignees/backups, waiting targets, notifications and failed-delivery escalation owner | PAY05 operations |
| Supplier/delivery extraction | Exact PO/receipt/shipment contracts and authorized human confirmation | M01 at T08; M02 at T09 |

Completion record: immutable source revision; schema/model/preprocessing versions; scoped test results; security/resource-limit results; OCR benchmark; cash/allocation reconciliation vectors; restore results; trained operator/backup acceptance; remaining business gates. Planned controls and unexecuted tests are not passes.

## Official sources and limits of inference

- [OWASP file-upload guidance](https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html) supports validation, private storage and resource controls.
- [OWASP transaction authorization guidance](https://cheatsheetseries.owasp.org/cheatsheets/Transaction_Authorization_Cheat_Sheet.html) supports server-owned state and approval binding.
- [PP-OCRv6 documentation](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/algorithm/PP-OCRv6/PP-OCRv6.en.md), [Tesseract model files](https://tesseract-ocr.github.io/tessdoc/Data-Files.html) and [ONNX Runtime C# documentation](https://onnxruntime.ai/docs/get-started/with-csharp.html) establish candidates/capabilities, not this application's accuracy or full C# compatibility.
- OCR reads the image; bank corroboration and independent human authorization establish usable funds. Advanced forecasting/anomaly models remain outside this focused plan.
