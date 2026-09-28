# Report Access and Identity Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the remaining account lifecycle and extend the existing authorization foundation with explicit business responsibilities, controlled historical reads, trusted person independence and reviewed access governance.

**Architecture:** Extend the current C# modules, Mongo transactions, persistent session and authorization fences, purpose-limited audit and actor-owned operation receipts. Keep provider credentials and factors outside the application, use generated API contracts and server-owned allowed actions, and introduce separate policies for catalog entitlement and historical reads instead of weakening ordinary business authorization. New person and grant changes must participate in the same transaction boundary as affected sensitive operations.

**Tech Stack:** Existing Next.js/React/TypeScript frontend, C#/ASP.NET Core .NET 10, official MongoDB driver with replica-set transactions, managed OIDC/Auth0 boundary, xUnit/real Mongo integration tests and real browser acceptance through the planned one-origin gateway.

**Spec:** [Report specification and requirement matrix](../../requirements/unit-toko-report-2026-09-28.md), [existing production specification](../../requirements/unit-toko-production.md), [completion umbrella](2026-09-28-report-completion.md), and [authoritative progress](../../quality/production-progress.md). This plan extends T03/T04 and supplies contracts to T06–T14; it does not replace the master task IDs.

## Global Constraints

- Launch audience: BNI branches and approved business accounts only. No public self-registration or retail checkout in the first launch.
- The November dates are reference dates; release follows readiness gates.
- Three main source directories: `frontend/`, `backend/`, `docs/`. Keep required repository/CI metadata at root.
- Six launch role templates with independently assigned finance packs. No template grants a blanket union of financial powers.
- Int64 internal IDs cross JSON as strings. Money crosses JSON as decimal strings; validate the precise limits in spec §5.
- Preserve the existing 30-minute idle timeout, 8-hour absolute lifetime and fresh MFA within 5 minutes for sensitive actions. A new cookie or provider primary-login time never manufactures fresh factor evidence.
- No email-based automatic account merge, production demo seed, administrator-selected password, fabricated provider completion, or rewriting immutable actor/history records.
- Unknown or disputed person identity fails closed for financial approval. A second account is not proof of a second person.
- Existing T03/T04 foundations remain authoritative: reuse approved invitations, exact issuer/subject binding, sessions, scope queries, fences, history, idempotency and operation recovery.
- Production identity remains unavailable until its configuration and acceptance gates pass. An unavailable external Auth0 tenant does not prevent unrelated local module implementation against tested authenticated-actor contracts.
- External provisioning, sender activation, live provider mutation, real account assignment and production cutover require the umbrella's explicit external action gates. Local fixtures are never production acceptance evidence.
- Read `frontend/AGENTS.md` and the relevant installed Next.js guide before changing routes or build behavior. Do not replace the maintained OIDC protocol handler with a custom JWT/login implementation.

## Review Focus

1. A delayed external security event or recovery dispatch races a sensitive commit: the old identity/session/person authority must not commit or silently regain access — ACC01/ACC03.
2. A buyer can browse an allowed shared product without acquiring access to another buyer's order; counts, search, file, export and event paths obey the same policy — ACC02.
3. A suspended branch needs authorized history review while new transactions stay blocked; history permission must not authorize settlement, edits or reactivation — ACC02.
4. Two accounts are later identified as one person, or a template changes during approval: unknown/stale evidence cannot authorize a financial decision and old records remain unchanged — ACC03.
5. A temporary delegate outlives the source authority or an access-review screen is stale: expiry/revocation must win without silent renewal or stale attestation — ACC04.

---

## Baseline, ownership and execution order

Baseline is managed worktree commit `e84faa6`. Progress records 275/275 backend and 300/300 frontend passing cases, plus existing identity API/isolated-provider and synthetic browser checks. T03 has configured login/session/step-up/logout, account screens, approved invitation services, protected sessions, and recoverable local recovery intents. T04 has six templates/packs, scoped grants, organization lifecycle, immutable account-actor approval checks, audited history and durable request reconciliation. These are inputs, not work to rebuild.

Baseline limitations were explicit: invitation/profile/recovery administration is not fully exposed; delivery/provider events/real-provider acceptance remain open; person independence is account-ID based; catalog ownership cannot serve as shared assortment entitlement; business ports remain unstarted. Existing access history already supports structurally valid past scopes through eligible current ancestor/global authority. ACC02 adds controlled business history, not a claim that all history is absent.

Order: ACC01 local account administration can run alongside ACC02 policy work once shared actor contracts are frozen. ACC03 person/conflict/version contracts must pass before new financial approval workflows are enabled. ACC04 depends on ACC02/ACC03 and the ACC01 administration interfaces. T06/T07 consume ACC02; T10–T12 consume ACC03; T16 consumes ACC04 operator evidence. External ACC01 acceptance is a release gate, not a reason to stop independent local T05A–T14 work.

One integrator owns shared `Program.cs`, permission/template catalogs, API generation and migration numbering at a time. Workers own the exact files named below and do not modify another task's files without a reviewed handoff. Applied migrations v1–v4 remain frozen; each new migration registers the next available deployment version after inspecting the current migration history, preflights incompatible data and never creates people, approvals or account links by inference.

During execution, use one backend build/test lease per worktree. Commands below run from repository root with the pinned .NET 10 SDK and Node 24 toolchain. Browser tests must refuse any environment without the disposable test identity/database attestation.

## ACC01 — Remaining account administration, recovery and provider-event integration

**Coverage:** S01–S04; R05/R11/R12; U01–U04/U06/U08. Extends T03 and the T14/T15 acceptance gates.

**Files and ownership:**
- Create API: `backend/src/UnitToko.Api/Identity/IdentityAdministrationEndpoints.cs`, `IdentityAdministrationDtos.cs`, `ProviderSecurityEventEndpoints.cs`.
- Create platform: `backend/src/UnitToko.Platform/Identity/IdentityDirectorySearch.cs`, `ProviderSecurityEventService.cs`, `ProviderSecurityEventVerifier.cs`, `RecoveryDeliveryService.cs`, `OwnerRecoveryCompletion.cs`.
- Create adapter contracts: `backend/src/UnitToko.Platform/Identity/ProviderLifecycleContracts.cs`; verified proof types remain internal.
- Modify existing platform: `IdentityDirectory.cs`, `AccountRecoveryService.cs`, `IdentityOperationReceipts.cs`, `MongoIdentitySessionStore.cs`, `OidcTransactionStore.cs` in `backend/src/UnitToko.Platform/Identity/`.
- Modify existing API: `backend/src/UnitToko.Api/Identity/IdentityRuntime.cs`, `IdentityBrowserSecurity.cs`, `VerifiedOidcSignInCompletion.cs`, `IdentityEndpoints.cs`; coordinator wires `backend/src/UnitToko.Api/Program.cs`.
- Create UI: `frontend/src/modules/identity/IdentityAdministration.tsx`, `IdentityRecovery.tsx`, `identity-administration-client.ts`; routes `frontend/src/app/administration/accounts/page.tsx`, `frontend/src/app/identity/recovery/page.tsx`.
- Modify UI: `frontend/src/modules/identity/IdentityAccount.tsx`, `identity-client.ts`, `identity-workflow.ts`, `identity.module.css`; `frontend/src/modules/administration/AdministrationNavigation.tsx`.
- Create migration: `backend/src/UnitToko.Platform/Mongo/Migrations/ProviderLifecycleMigration.cs`; coordinator registers it in `CollectionMigrations.cs`.
- Create tests: `backend/tests/UnitToko.Tests/Identity/IdentityAdministrationTests.cs`, `ProviderSecurityEventTests.cs`, `OwnerRecoveryTests.cs`; `frontend/tests/identity-administration.test.ts`, `identity-recovery.test.ts`; `frontend/tests/e2e/identity-live-stack.spec.ts`.
- Extend tests: `IdentityFullHostTests.cs`, `SessionAuthorizationFenceTests.cs`, `IdentityReceiptTests.cs` under the existing identity test directory.
- Update docs: `docs/runbooks/account-recovery.md`, `identity-hosting.md`; create `docs/quality/identity-live-acceptance.md`.

**Interfaces:**
- Reuse existing `IdentityOnboardingService.ProposeAsync`, `ApproveAsync`, `RejectAsync`, `WithdrawAsync`, `RevokeAsync`, `ListInvitationsAsync`, `ListProfilesAsync`, and `AccountRecoveryService.InitiateAsync`. DTOs map to these services rather than implementing a second invitation engine.
- Add `SearchProfilesAsync(ActorContext actor, string term, int offset, int limit, AuthorizationReadMetadata audit, CancellationToken ct) -> Task<AccountDirectoryPage>`. Apply current scoped membership and normalized bounded name/code search before count/page; return no provider subjects or unrelated personal attributes.
- Provider proof verification is a private adapter boundary. Browser JSON must never construct a verified security event or owner-recovery completion proof. The internal event record below lives in the platform verifier file; public enums/receipt DTOs live in `ProviderLifecycleContracts.cs`.

```csharp
public enum ProviderEventTransport { Auth0Events, Auth0Logs }
public enum ProviderSecurityChange { AccountDisabled, AccountDeleted, PasswordChanged, FactorChanged, FactorRemoved }
public sealed record ProviderEventBatchResult(int Applied, int Duplicates, int UnknownSubjects, int Ignored);
// Internal Platform proof, created only by the trusted transport verifier.
internal sealed record VerifiedProviderSecurityEvent(string Issuer, string EventId, string Subject,
    ProviderSecurityChange Change, DateTimeOffset OccurredAt, string Fingerprint);
// Planned dispatcher interfaces below remain unimplemented.
public enum ProviderIntentState { Blocked, Ready, Dispatching, Accepted, Failed, Completed }
public sealed record ProviderIntentReceipt(
    long Id, long UserId, long Version, ProviderIntentState State, DateTimeOffset? CompletedAt);
```

- Implemented adapter interface: `ProviderSecurityEventService.ProcessAsync(ProviderEventTransport transport, ReadOnlyMemory<byte> body, string authorization, CancellationToken ct) -> Task<ProviderEventBatchResult>`. It verifies a bounded raw native message before any database read; the API cannot construct its internal proof. Each recognized event commits separately with durable deduplication, subject/account fences and session revocation. HTTP always returns a non-disclosing 204 only after successful processing. Native source IDs and a safe semantic fingerprint distinguish exact replay from conflicting facts. Public adapter types live under `Platform/Identity/ProviderLifecycleContracts.cs`, not browser DTO binding.
- V5 adds private security-event evidence, known-account holds and provider-subject fences. Block/delete/factor loss restrict first/existing admission; password/factor change sets a monotonic received-time proof cutoff. Every ordinary admission/session write checks the same subject fence and requires newer primary/factor evidence. No provider notice releases a hold, creates an account or completes an owner-recovery intent.
- `RecoveryDeliveryService.DispatchAsync(long intentId, long expectedVersion, CancellationToken ct) -> Task<ProviderIntentReceipt>` loads approved durable authority and explicit environment/sender configuration; it never accepts arbitrary target email/provider subject. Accepted means provider acknowledgement, not completed owner recovery.
- `OwnerRecoveryCompletion.CompleteAsync(string recoveryState, ProviderIdentityProof proof, CancellationToken ct) -> Task<ProviderIntentReceipt>` consumes only server-held one-use state bound to the exact account/intent/security revision and provider-verified owner completion. It cannot create an ordinary business session unless existing admission/MFA/membership gates also pass.

- [x] **ACC01.1 — Pin missing administration behavior with HTTP tests.** Use the real host and existing synthetic actors: missing CSRF/origin/expected-actor headers fail before mutation; a branch-A approver cannot discover branch-B profiles by search/count/direct ID; same-key retries preserve the original result; changed body/version rejects; approval displays permissions, scope, duration and independent actor. Test invitation secrets/email excluded from broad audit and browser storage. Example acceptance assertion:

```csharp
var outside = await onboarding.SearchProfilesAsync(branchAActor, "branch-b-person", 0, 20,
    new("access-review", "directory-boundary"), ct);
Assert.Equal(0, outside.Total);
Assert.Empty(outside.Items);
```

- [x] **ACC01.2 — Run the focused tests red, then expose existing services.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~IdentityAdministrationTests"`. Expected initial failure is missing administration/search behavior, not missing Mongo. Implement bounded typed DTOs, current actor checks, server-owned allowed actions and audited search. Add exact identity operation names to existing recovery only when the new command is genuinely idempotent. Preserve body-free actor/operation/key browser references and `not_confirmed` for absent outcomes.
- [ ] **ACC01.3 — Test and implement verified external revocation.** Write real-Mongo cases for missing/wrong machine credentials, wrong issuer and unknown-subject events, duplicate delivery, reordered events, failed persistence and two hosts sharing sessions. Start a sensitive transaction, apply a verified password/factor/disable event, then resume: no stale business effect commits. Implementation order is authenticate exact raw provider message, map issuer+subject, insert unique issuer/event ID, increment identity security version/fence and revoke sessions in one transaction, then acknowledge. Unknown identities use a safe non-disclosing outcome and cannot be created. Repeated events do not duplicate audit or reduce versions. Verify the actual supported Auth0 event mechanism against official documentation during implementation; if reliable authenticated delivery is unavailable, retain an explicit activation blocker rather than claiming coverage.
> ACC01.3 local receiver checkpoint accepted: native bearer transports, permanent deduplication, shared subject/identity/session fencing, admission holds and proof freshness are implemented and independently reviewed. All 355 backend cases have passing evidence across the full run and targeted copied-contract recheck; see [exact acceptance](../../quality/provider-event-acceptance-2026-09-28.md). Actual source coverage/reconciliation and tenant acceptance remain open, so this parent step stays unchecked. Do not rebuild the accepted receiver while completing those gates.

- [ ] **ACC01.4 — Test recovery delivery and owner completion before implementing them.** Cases: disabled outbound configuration keeps `blocked`; provider timeout remains uncertain/pending reconciliation; duplicate acknowledgment does not resend; completed event does not accept a different owner; expired/replayed state fails; factor-loss flow cannot acquire a weak business session. Prove local revocation precedes the durable intent and transaction rollback leaves neither partial effect. The existing self-recovery intent deliberately predates its own security-epoch increment: authorize the reviewed transition by exact intent/before/after revisions, not by blindly requiring the old epoch to remain current. A later unrelated security change invalidates the dispatch/completion. No code path lets an administrator choose the new password/factor.
- [ ] **ACC01.5 — Implement owner recovery navigation and administration pages.** Show verified person/account names only when supplied by the scoped directory, explain remaining references, keep Indonesian pending/blocked/failed/completed states distinct, and obtain a fresh antiforgery token before native navigation. An owner without `recovery.initiate` may request an account-bound owner reauthentication challenge; it grants no System permission. Bind challenge purpose, expected account, session/security revision and return path on the server. Account switches clear drafts and never adopt another actor's receipt. Provider failures return to a safe local page without secrets in URL/logs. Preserve keyboard focus, visible error summaries and safe Back navigation.
- [ ] **ACC01.6 — Verify the complete local stack without interception.** Run focused identity tests, API export/client drift checks and affected frontend tests. Then drive the production-built frontend through an actual one-origin TLS test gateway, maintained OIDC handler, isolated provider and real disposable Mongo: invitation → verified admission → fresh MFA → scoped admin → step-up → logout → old-cookie denial. Include local provider recovery completion/event fixtures, account switching, CSRF rejection, unsupported proxies and multi-host revocation. Record this separately from unit fakes and intercepted UI fixtures. Use 360px/mobile and desktop, keyboard-only input and accessible factor labels; do not log cookies, tokens or invitation secrets.
- [ ] **ACC01.7 — Independent review and external acceptance gate.** Reviewer examines provider proof/state trust, transaction fences, same-account recovery, event deduplication, directory privacy, anti-forgery and deployment assumptions. Record focused/full test commands and exact artifacts; commit only reviewed ACC01 files when execution is authorized. Actual tenant/tier/region/client/connection/sender, real email/OTP/enrollment/recovery, protected key mounts/restore and provider/edge throttles require separate approved acceptance. An isolated provider pass does not close this gate. Keep production activation disabled while it is open; continue independent local modules.

## ACC02 — Distinct business permissions, catalog entitlement and historical readers

**Coverage:** R01–R05/R09/R14; U01–U03/U05. Extends T04 and supplies T06–T12 interfaces.

**Files and ownership:**
- Catalog domain: `backend/src/UnitToko.Modules/Catalog/CatalogVisibilityContracts.cs`, `CatalogVisibilityPolicy.cs`; historical domain remains `backend/src/UnitToko.Modules/Organizations/HistoricalReadPolicy.cs`.
- Catalog platform: `backend/src/UnitToko.Platform/Authorization/CatalogVisibilityQuery.cs`, `backend/src/UnitToko.Platform/Catalog/CatalogVisibilityStore.cs`, `CatalogVisibilityBson.cs`; v6 `CatalogVisibilityMigration.cs` / `CatalogVisibilitySchemas.cs`. Historical platform remains `Authorization/HistoricalReadQuery.cs` and `ScopedAuditQueries.cs`.
- Modify domain: `backend/src/UnitToko.Modules/Identity/Permissions.cs`, `RoleTemplates.cs`, `ApprovalPolicy.cs`; `backend/src/UnitToko.Modules/Organizations/ScopePolicy.cs` only for explicit integration points, preserving ordinary active-resource denial.
- Modify platform: `backend/src/UnitToko.Platform/Authorization/AccessAdministrationReads.cs`, `AccessReadAudit.cs`, `AccessGrantCommands.cs`; add the new read policies to owning business modules as those tasks port them.
- Modify UI: `frontend/src/modules/administration/permission-labels.ts`, `AccessAdministration.tsx`, `AccessHistory.tsx`; generated clients change only through the integrator.
- Create tests: `backend/tests/UnitToko.Tests/Authorization/BusinessPermissionTests.cs`, `CatalogVisibilityTests.cs`, `HistoricalReadTests.cs`, `ScopedReadSurfaceTests.cs`.
- Update docs: `docs/architecture/permissions.md`; create `docs/architecture/business-access-contracts.md`.

**Interfaces and non-overlapping authority:**

```text
order.spend.approve        BuyingDivision / BuyingBranch / BuyingOrganization
order.accept              FulfillmentShop / FulfillmentWarehouse
substitution.propose      FulfillmentShop / FulfillmentWarehouse
substitution.accept       BuyerOwned / BuyingDivision / BuyingBranch / BuyingOrganization
catalog.read              entitled assortment visibility; not order ownership
history.read              explicit bounded buying/fulfillment scope, read-only
report.read               explicit bounded buying/fulfillment scope, read-only
stock.convert             FulfillmentShop / FulfillmentWarehouse
reservation.release       FulfillmentShop / FulfillmentWarehouse
supplier.change.propose   FulfillmentShop / FulfillmentWarehouse
supplier.change.approve   FulfillmentShop / FulfillmentWarehouse, independent
```

Existing `order.approve` and `order.substitute` are ambiguous legacy catalog entries. Do not silently reinterpret materialized grants: register the new names, preserve old snapshots for audit, and require ACC03's explicit grant migration before accepting them for new commands. Existing period reopening and cancellation permissions remain; each T06–T12 command declares its exact permission and immutable evidence in the matrix. Receiving-bank change authority remains with the payment plan and must be coordinated before this catalog is finalized.

```csharp
// Actual catalog producer contracts retain full provenance and owner bindings.
public sealed record CatalogEntitlement(long Id, long UserId, long MembershipId, long AssortmentId,
    GrantScope AudienceScope, AuthorizationStatus Status, DateTimeOffset EffectiveAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, long Version, long RequestedBy,
    long ApprovedBy, long ApprovalId, long ApprovalVersion, DateTimeOffset ApprovedAt);
public sealed record CatalogAssortment(long Id, GrantScope OwnerScope, AuthorizationStatus Status, long Version);
public sealed record CatalogListing(long AssortmentId, GrantScope OwnerScope);
// CatalogVisibilityQuery carries complete clauses; AssortmentIds is only a projection.
public sealed record HistoricalReadRequest(string Permission, GrantScope Scope, string Purpose);
// Compile uses current actor, memberships and explicit entitlement/read grants.
// Query results retain complete source evidence, never a client-provided Mongo filter.
```

- `CatalogVisibilityPolicy.Compile(ActorContext actor, AuthorizationSnapshot snapshot, ImmutableArray<CatalogEntitlement> entitlements, ImmutableArray<CatalogAssortment> assortments, DateTimeOffset now) -> CatalogVisibilityQuery` rejects stale/inactive actor or ineligible membership; empty means deny. Catalog owner applies the query before count/page/search and returns authorized pricing fields only.
- `HistoricalReadPolicy.Authorize(ActorContext actor, AuthorizationSnapshot snapshot, HistoricalReadRequest request, DateTimeOffset now) -> AuthorizationDecision` evaluates an explicitly approved historical grant, structurally valid hierarchy, permitted purpose and record scope. It may tolerate the target branch's suspension solely for a historical read; it never authorizes a write, event carrying new business data, settlement or reactivation. Authorization and audit use current actor/permission fences.
- `history.read` and `report.read` do not imply `access.approve`, `access.revoke`, `technical.configure`, payment approval or all fields in a document. Access-history and business-history DTOs remain distinct; sensitive evidence and margin fields require their own approved visibility.

- [x] **ACC02.1 — Write failing role/permission separation tests.** Assert a buyer spending approver cannot accept a store order; store head cannot spend the buyer's budget; staff can propose but not accept a substitution; buyer/PIC can accept only their own authorized order's exact proposed version. Preserve existing legacy `substitution.propose`/`substitution.decide` semantics during the port. Assert read-only auditor grants have no mutation permissions. Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~BusinessPermissionTests"`; failures must identify missing explicit authority.
- [x] **ACC02.2 — Add the catalog entries and role suggestions.** Use explicit `PermissionDefinition` scope lists; store operational actions belong in fulfillment templates, buyer consent remains buying scoped, and spending approval requires a separately approved grant rather than every Buyer template. Add immutable requester/proposer/version evidence to corresponding approval policy inputs. Extend permission preview and Indonesian labels; server resource allowed actions remain authoritative. Update the matrix before T07 command implementation consumes it.
Local ACC02.1–.2 acceptance: [permission, grant and invitation evidence](../../quality/business-permissions-acceptance-2026-09-28.md). This is producer-policy and administration acceptance; business endpoint integration, catalog entitlements and suspended-history grants remain below.

- [x] **ACC02.3 — Write the shared-catalog isolation matrix, then implement entitlement queries.** Fixture: buyer A and buyer B in one branch share assortment X; only A created order OA. Both may read X, neither may read hidden assortment Y, and B cannot read OA. Assert identical entitlement filtering for list/count/page/search/detail/product-image/export/event metadata with direct-ID substitution and malformed scope arrays. Tests must verify a hidden result never changes total count. Implement separate catalog filters; do not mark every SKU as created/assigned to every buyer or widen BuyerOwned order policy.
> ACC02.3 local producer checkpoint accepted: explicit catalog policy, v6 persistence and canonical metadata reads pass the full 461-case backend suite. See [exact acceptance](../../quality/catalog-visibility-acceptance-2026-09-28.md). Product/price/entitlement issuance and real image/export/event delivery remain CAT01/PAY01 integration gates; ACC02.4–.6 and report production acceptance stay open.

- [x] **ACC02.4 — Write suspended-history tests, then implement bounded historical grants.** Fixture: suspended branch, active parent, active named reviewer with explicit historical scope. Approved history is visible; order submission, reservation, adjustment, invoice issue and payment action remain denied. A suspended user, revoked reviewer or unrelated sibling is denied. Test branch-bound historical approval instead of relying solely on global System audit; query shaping precedes count/page and download/event/export permission is reevaluated. Do not change `ScopePolicy.IsResourceActive` to make all inactive resources readable.
- [x] **ACC02.5 — Define and test all read surfaces.** Reuse `AuthorizationStore` and actual Mongo fixtures to compare list/count/detail/file/export/search/event results for each new policy, including revoked access between enumeration and file/event delivery. Keep DTOs purpose-limited and write privileged-read audit without exposing or recursively reading that collection. New business modules must add equivalent endpoint-specific cases before their own acceptance; foundation fixtures do not claim nonexistent business routes are complete.
- [x] **ACC02.6 — Verify, review and publish producer contracts.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~BusinessPermission|FullyQualifiedName~CatalogVisibility|FullyQualifiedName~HistoricalRead|FullyQualifiedName~HistoricalGrant|FullyQualifiedName~AccessHttpTests.Historical"`; expected all pass on a real replica set. Reviewer checks complete-grant OR logic, read/write separation, field privacy and role migration requirements. Document product assortment ownership, buyer approver/threshold applicability, historical readers and historical settlement policy as business gates. Until approved, historical read is narrowly explicit and settlement remains blocked; no new financial-write exception is inferred from `history.read`.

> ACC02.4–.6 local producer/administration checkpoint accepted: full backend 586/586, frontend 329+6+7, 12 historical browser groups and prior ordinary/lifecycle browser checks passed. See [exact acceptance](../../quality/historical-access-acceptance-2026-09-28.md) and [historical-read contracts](../../architecture/historical-reads.md). Actual business/file/export/event delivery, person-level independence, approved business operators and production report acceptance remain open. All six public role templates and ordinary active-only write rules are preserved.

## ACC03 — Trusted person independence, finance conflicts and template migrations

**Coverage:** R06–R08/R12/R13; S03; U02–U03. Extends T03/T04 and gates financial approvals in T08/T10–T12.

**Files and ownership:**
- Create domain: `backend/src/UnitToko.Modules/Identity/PersonIndependencePolicy.cs`, `PersonContracts.cs`, `FinanceConflictPolicy.cs`, `RoleTemplateVersions.cs`.
- Create platform: `backend/src/UnitToko.Platform/Identity/PersonRegistryService.cs`; `backend/src/UnitToko.Platform/Authorization/PersonAuthorizationFence.cs`, `TemplateMigrationService.cs`.
- Modify domain: `backend/src/UnitToko.Modules/Identity/ApprovalPolicy.cs`, `AuthorizationContracts.cs`, `RoleTemplates.cs`; `backend/src/UnitToko.Modules/Organizations/ScopePolicy.cs`.
- Modify platform: `backend/src/UnitToko.Platform/Authorization/AuthorizationStore.cs`, `AccessGrantCommands.cs`; `backend/src/UnitToko.Platform/Identity/IdentityAdmission.cs`, `IdentityInvitations.cs`.
- Create API/UI: `backend/src/UnitToko.Api/Identity/PersonReviewEndpoints.cs`; `frontend/src/modules/administration/PersonReview.tsx`, `TemplateMigrationReview.tsx`; extend existing access previews and directory results with safe verification state, never raw employee source evidence.
- Create migration: `backend/src/UnitToko.Platform/Mongo/Migrations/PersonAuthorityMigration.cs`.
- Create tests: `backend/tests/UnitToko.Tests/Authorization/PersonIndependenceTests.cs`, `PersonFenceTests.cs`, `FinanceConflictTests.cs`, `TemplateMigrationTests.cs`.
- Create docs: `docs/runbooks/person-verification.md`, `docs/architecture/finance-conflicts.md`, `docs/runbooks/role-template-migration.md`.

**Interfaces:**

```csharp
public enum PersonVerification { Unknown, Verified, Disputed }
public sealed record PersonEvidence(long UserId, long? PersonId,
    PersonVerification Verification, long Version);
public static class PersonIndependencePolicy
{
    public static bool AreIndependent(PersonEvidence actor, params PersonEvidence[] history) =>
        actor.Verification == PersonVerification.Verified && actor.PersonId is > 0 &&
        history.Length > 0 && history.All(previous =>
            previous.Verification == PersonVerification.Verified && previous.PersonId is > 0 &&
            previous.PersonId != actor.PersonId);
}
public enum ConflictSeverity { Warning, Prohibited }
public sealed record FinanceConflict(string Code, ConflictSeverity Severity,
    ImmutableArray<long> GrantIds, GrantScope Scope);
public sealed record TemplateMigrationPreview(long UserId, long GrantId, long GrantVersion,
    int FromVersion, int ToVersion, ImmutableArray<string> Added,
    ImmutableArray<string> Removed, ImmutableArray<FinanceConflict> Conflicts);
```

The pure person predicate assumes evidence has already been resolved through the current reviewed equivalence map. `PersonRegistryService.ResolveAsync(IClientSessionHandle tx, ImmutableArray<long> userIds, CancellationToken ct) -> Task<ImmutableArray<PersonEvidence>>` loads current verified mappings for actor and immutable historical account IDs. It cannot accept an email/name as identity proof. A reviewed late equivalence link may establish two formerly distinct accounts are one person; preserve old account/person snapshots and append the new relationship instead of rewriting transactions. Current equivalence and its version participate in new decisions. Completed financial facts are flagged for independent exception review, never automatically edited or reversed.

`PersonAuthorizationFence.TouchAsync(IClientSessionHandle tx, PersonAuthorityProof proof, CancellationToken ct) -> Task` accepts an internal server-resolved proof for that exact transaction and conditionally writes every complete typed authority record before the business write. Public `PersonEvidence` is only a safe projection, never a caller-supplied fencing proof. Person link/suspension/dispute updates the same fence and affected identity grant/security versions. Concurrent changes force reload and reauthorization.

`FinanceConflictPolicy.Evaluate(ImmutableArray<PermissionGrant> effectiveGrants, ScopeDirectory directory) -> ImmutableArray<FinanceConflict>` compares only intersecting effective scopes and periods. Classify prohibited grants separately from allowed multi-duty assignments requiring distinct transaction actors; never ban every combined finance pack merely because it contains two stages. The business-approved matrix supplies the classification; financial approval remains blocked for an unapproved/unknown conflict policy.

`TemplateMigrationService.PreviewAsync(ActorContext actor, long grantId, int targetVersion, CancellationToken ct) -> Task<TemplateMigrationPreview>` and `ProposeAsync(ActorContext actor, TemplateMigrationPreview reviewed, AuthorizationCommandMetadata metadata, CancellationToken ct) -> Task<AccessProposalResult>` reuse independent approval and exact request/version checks. The supported-version registry validates old materialized snapshots without automatically recomputing permissions.

- [x] **ACC03.1 — Pin same-person and unknown-person denial before integration.** Add these executable pure vectors:

```csharp
var one = new PersonEvidence(101, 900, PersonVerification.Verified, 1);
var otherAccount = new PersonEvidence(102, 900, PersonVerification.Verified, 1);
var otherPerson = new PersonEvidence(103, 901, PersonVerification.Verified, 1);
Assert.False(PersonIndependencePolicy.AreIndependent(one, otherAccount));
Assert.False(PersonIndependencePolicy.AreIndependent(one, otherPerson with { PersonId = null,
    Verification = PersonVerification.Unknown }));
Assert.True(PersonIndependencePolicy.AreIndependent(one, otherPerson));
```

- [ ] **ACC03.2 — Run red tests, then implement governed person evidence.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~PersonIndependenceTests"`. Register verified personnel evidence only from the approved staff/person source with independent reviewer, source reference/hash, validity, reason and version. Keep provider account binding distinct. Invitation review flags duplicate/ambiguous person candidates; it never silently merges them. Missing source approval leaves the person unknown and financial approval unavailable, while unrelated permitted read/order work can proceed.
- [ ] **ACC03.3 — Test late binding and transaction races before wiring financial approvals.** Create two accounts with different provider subjects and an existing request. Linking both to one verified person before approval must deny it, including after role change. Race approved mapping/equivalence/suspension against a paused sensitive transaction and assert no stale effect. Unknown maker/checker/beneficiary evidence denies all affected financial approval; a reviewed historical binding permits only genuinely distinct people. Keep immutable UserId and original recorded person evidence, and show the later link as a separate audit event. Resolve and fence both the current actor and historical participants inside `ExecuteCommandAsync`; account-ID checks remain additional safeguards, not replacements.
> ACC03.1 pure rules are locally accepted (43 new cases, independent review). The local producer/consumer portions of ACC03.2–.3 are accepted after 715/715 backend tests (zero skips) and independent review: five strict v8 collections, complete current-person resolution, exact transaction fences and guarded business-command/receipt integration. See [person authority contracts](../../architecture/person-authority.md) and [detailed acceptance](../../quality/person-authority-acceptance-2026-09-28.md). Their parent steps remain open: governed source/binding/equivalence writers, immutable review history and original person snapshots, invitation ambiguity review, affected-session invalidation, specialized administration integration and actual source/reviewer approval are not yet delivered. Finance matrix and real business-module activation also remain open. Test fixtures are not operational authority.

> Governed writer continuation: [staff identity plan](2026-09-28-person-governance.md). The new local service implements complete structural closure, independently reviewed group suspension, atomic all-alias invalidation, immutable history and guarded replay/private receipt reads with unavailable production trust. [Detailed service evidence](../../quality/person-governance-acceptance-2026-09-28.md) records final checks and limits. Governed discovery/preview, API/UI, source registration/binding/merge, invitation integration, specialized administration and actual source/reviewer authority remain open. This does not complete the ACC03 parent steps.

- [ ] **ACC03.4 — Write conflict preview/activation tests, then enforce the matrix.** Cases: warning-only preparer pack overlap displays clearly; prohibited same-scope combination cannot activate; non-overlapping scopes do not produce false conflicts; widened scope or changed finance matrix invalidates old preview; simultaneous grants cannot each pass against stale aggregate authority. Include bank evidence/margins field visibility in service DTO tests. Require independent reviewers for affected-person privilege changes and audit matrix version/reason. Business owner approves the hard-versus-warning classifications before finance grants are enabled.
- [ ] **ACC03.5 — Write template migration tests, then implement explicit version handling.** Existing v1 grant retains its exact effective permissions when v2 is registered. A migration preview lists precise additions/removals/delegable powers and conflicts; stale preview or removed approver authority rejects. New power requires independent approval, old sessions are invalidated when the change commits, and historical v1 records stay unchanged. Unsupported/withdrawn versions receive an explicit review state; never implement an unconditional `CurrentVersion++` that silently disables every old grant or an automatic materialization that silently grants new powers. Coordinate ACC02 permission renames through reviewed migrations with explicit old-to-new semantics.
- [ ] **ACC03.6 — Verify and independently review.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~PersonIndependenceTests|FullyQualifiedName~PersonFenceTests|FullyQualifiedName~FinanceConflictTests|FullyQualifiedName~TemplateMigrationTests"`, then affected access/session integration cases. Reviewer checks unknown-person denial, immutable history, late-equivalence concurrency, source trust, complete scopes and supported template migration. Document verified staff source, account-link adjudicators and conflict policy as explicit business gates. Publish the financial approval contract to each owning module before activation.

## ACC04 — Temporary delegation, periodic reviews and named operator readiness

**Coverage:** R10–R13; R05/R06; relevant U01–U04/U08. Extends T04/T13 and supplies T16 operating evidence.

**Files and ownership:**
- Create domain: `backend/src/UnitToko.Modules/Identity/AccessGovernanceContracts.cs`, `TemporaryDelegationPolicy.cs`.
- Create platform: `backend/src/UnitToko.Platform/Authorization/TemporaryDelegationService.cs`, `AccessReviewService.cs`, `OperatorReadinessService.cs`.
- Modify shared policies: `backend/src/UnitToko.Modules/Organizations/ScopePolicy.cs`, `backend/src/UnitToko.Platform/Authorization/AccessGrantCommands.cs`, `AccessSuspensionCommands.cs`, `AccessOperationReceipts.cs`; these changes are serialized with ACC02/ACC03 owners.
- Create API: `backend/src/UnitToko.Api/Authorization/AccessGovernanceEndpoints.cs`, `AccessGovernanceDtos.cs`.
- Create UI: `frontend/src/modules/administration/TemporaryDelegation.tsx`, `AccessReviews.tsx`, `OperatorReadiness.tsx`, `access-governance-client.ts`.
- Create migration: `backend/src/UnitToko.Platform/Mongo/Migrations/AccessGovernanceMigration.cs`.
- Create tests: `backend/tests/UnitToko.Tests/Authorization/DelegationLifecycleTests.cs`, `AccessReviewTests.cs`, `OperatorReadinessTests.cs`; `frontend/tests/access-governance.test.ts`.
- Create docs: `docs/runbooks/access-review.md`, `docs/runbooks/staff-lifecycle.md`, `docs/quality/operator-readiness.md`.

**Interfaces:**

```csharp
public sealed record TemporaryDelegationRequest(long SourceGrantId, long SourceVersion,
    long BeneficiaryUserId, long BeneficiaryMembershipId, GrantScope Scope,
    ImmutableArray<string> Permissions, DateTimeOffset EffectiveAt, DateTimeOffset ExpiresAt);
public sealed record AccessReviewItem(long Id, long UserId, long GrantId, long GrantVersion,
    DateTimeOffset DueAt, string Status, ImmutableArray<string> AllowedActions);
public sealed record AccessReviewPage(long Total, ImmutableArray<AccessReviewItem> Items);
public sealed record OperatorReadinessItem(string Duty, long? UserId, long? PersonId,
    string Status, ImmutableArray<string> BlockingReasons);
```

- `TemporaryDelegationService.ProposeAsync(ActorContext actor, TemporaryDelegationRequest request, AuthorizationCommandMetadata metadata, CancellationToken ct) -> Task<AccessProposalResult>` materializes a bounded grant; activation uses existing independent approval and ACC03 person checks. Requested expiry is mandatory and cannot exceed source/membership expiry. Source authority changes invalidate dependent authority and update affected beneficiary fences/versions; expiry is checked during authorization, never dependent on a reminder worker running.
- `AccessReviewService.ListAsync(ActorContext actor, int offset, int limit, AuthorizationReadMetadata audit, CancellationToken ct) -> Task<AccessReviewPage>` uses scope before pagination; total and items use the identical filter. `DecideAsync(ActorContext actor, long itemId, long expectedVersion, string decision, AuthorizationCommandMetadata metadata, CancellationToken ct) -> Task<AccessChangeResult>` accepts only `retain` or `revoke`; retain is an attestation of the exact current snapshot, not renewal or widened access.
- `OperatorReadinessService.ReadAsync(ActorContext actor, AuthorizationReadMetadata audit, CancellationToken ct) -> Task<ImmutableArray<OperatorReadinessItem>>` reports named duty coverage and independence deficiencies. It cannot create a grant or infer that a person is a suitable backup merely from their role label.

- [ ] **ACC04.1 — Write failing temporary-delegation boundary cases.** Test mandatory finite expiry, scope/permission non-expansion, separate beneficiary approval, same-person denial, source grant expiry/revocation, membership suspension, handover history and blocked self-renewal. Pause a delegate's sensitive command, revoke the source grant, then resume: no business write may commit. Add an exact-expiry clock case so expiry does not depend on a scheduled job.
- [ ] **ACC04.2 — Implement materialized temporary authority and immutable handover.** Persist source grant/version, beneficiary, scope, explicit permissions, approver, reason, validity and handover events. Check source dependency in authorization, update beneficiary versions/fences on source changes, and deny stale query/detail/file/export/event access. Never let derived permission become broader because the source template later grows. Further delegation requires separately reviewed authority; no automatic chaining or return-to-duty reactivation.
- [ ] **ACC04.3 — Write review-campaign tests, then implement exact-snapshot decisions.** Cases: reviewer sees only permitted subjects/counts; changed grant/membership/person invalidates a review decision; an actor cannot attest their own conflicting privileges; revoked/expired access is never revived by `retain`; duplicate decision keys return the original result. Create versioned due-review items and current scoped queue projections, append attest/revoke outcomes, and route overdue work/reminders through T13's durable job boundary. A failed email cannot count as a completed access review.
- [ ] **ACC04.4 — Add governed staff lifecycle and operator coverage.** Test joining without approved membership, branch transfer leaving old scope, suspension during pending approval, leaver session revocation, reinstatement requiring review and a backup who is the same person under another account. UI shows effective scope/expiry, source delegation, outstanding reviews and specific missing duties. Named launch roles include initial access proposers, independent access approvers, finance recorder/verifier, period submitter/approver/closer, recovery operators and backups. The readiness page reports missing names as blockers; no production seed or automatic all-powerful administrator is permitted.
- [ ] **ACC04.5 — Verify user recovery and accessibility.** Extend actor-owned operation receipts for governance decisions before exposing mutation buttons. Test forced tab close, absent outcome closure, expired session, account switch, stale review preview and unknown commit. Keep drafts minimal and private; show Indonesian action/status labels, scoped name/code selections, keyboard focus/error summaries and mobile tables. Run `npm --prefix frontend run typecheck`, `npm --prefix frontend test`, and targeted real-browser governance flows after starting the attested local stack.
- [ ] **ACC04.6 — Run focused tests and independent operational review.** Run `dotnet test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~DelegationLifecycleTests|FullyQualifiedName~AccessReviewTests|FullyQualifiedName~OperatorReadinessTests"`. Reviewer checks dependent-grant invalidation, original-person independence, review purpose/privacy and non-renewal. Obtain the business cadence, transfer/leaver owner, delegation limits and named independent personnel before T16 signoff. Training/pilot evidence closes readiness; a technically valid grant matrix alone does not.

## Integration verification and handoff

- [ ] Confirm every R01–R14 and S01–S04 row points to an ACC task and its owning later-module acceptance; update the report matrix with exact evidence, not blanket completion.
- [ ] Run the full backend suite once after focused suites pass, with no simultaneous builds against the same outputs. Preserve no-skip real Mongo requirements and the existing revocation/receipt/organization regressions.
- [ ] Regenerate `backend/contracts/openapi.json` and `frontend/src/api/generated/schema.ts` through existing tooling; run `npm --prefix backend/contracts run check`. Require exact string IDs/versions, strict input bodies, no-store errors, standard problem codes and mandatory request/actor/version headers on new mutations.
- [ ] Run frontend typecheck/test/lint/build and affected browser workflows. Record synthetic/intercepted UI, real local-stack browser, hosted CI and real Auth0 acceptance separately; none substitutes for another.
- [ ] Independent reviewer verifies the five Review Focus failures and ownership boundaries. Commit reviewed task slices only during execution; never include another worker's changes incidentally.
- [ ] Publish ACC02 business-access contracts to catalog/ordering/evidence owners and ACC03 person independence to every financial approval owner. Keep shared data/schema changes coordinated and applied migration history frozen.
- [ ] Keep open business gates explicit: verified personnel source/equivalence reviewers, finance conflict matrix, buyer approval thresholds, historical readers/settlement, delegation/review cadence and named operators. Missing decisions block affected activation, not unrelated local development.

## Coverage and completion boundary

| Requirements | Primary owner | Completion evidence |
| --- | --- | --- |
| R01–R04, R09, R14 | ACC02 | Distinct authority, shared catalog and controlled history deny/allow matrices; later business routes integrate them |
| R05 | ACC01/ACC02/ACC04 | Scope before list/count/page; current authorization for detail/write/file/export/search/event and privileged read audit |
| R06–R08, R13 | ACC03 | Same-person/unknown-person/late-binding denial, finance conflicts and reviewed template migrations |
| R10–R12 | ACC04 with ACC01/ACC03 | Expiring bounded delegation, current review attestations and named independently staffed duties |
| S01–S04 | ACC01 | Complete administration/recovery/provider events and separately documented local-stack/deployed-provider acceptance |
| U01–U04, U06, U08 | All ACC tasks | Scoped directory, focused decision pages, honest uncertainty states, accessible Indonesian account/access flows |
| U05 | ACC02 producer contract; T07–T14 consumer | Safe actor/event references feed the unified order timeline without broad financial/history exposure |

This plan is complete when the local account/access work and producer contracts pass their stated gates and remaining external/business gates are accurately recorded. Production identity, financial approval and rollout require their respective real acceptance; completion of this plan's local code is not permission to bypass those gates. The umbrella remains responsible for the execution goal, business module plans, OCR/payment/packaging work and final launch sequencing.

## Local implementation checkpoint — 28 September 2026

ACC01.1–.2 and the administration portion of .5 are implemented and independently reviewed. [Local acceptance evidence](../../quality/identity-administration-acceptance-2026-09-28.md) records 277 backend cases, 315 frontend cases, and nine fixture-browser groups. Owner recovery, provider events/delivery and connected/live acceptance keep ACC01.3–.7 open. Use the [provider lifecycle research](../../research/2026-09-28-auth0-lifecycle.md) before implementing those steps: native bearer-authenticated events, signed logout and supplemental logs are distinct, and owner proof cannot be inferred from a new factor.
