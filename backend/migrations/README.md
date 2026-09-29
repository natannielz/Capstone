# Mongo collection migrations

Current migration 22 installs canonical order/reservation/decision storage. See the [order upgrade runbook](../../docs/runbooks/order-submission-migration.md) for preflight, preservation, interruption recovery and outstanding live acceptance. Existing generic order data is never silently converted.

Configure `Mongo__Uri` and `Mongo__Database` for the intended dedicated database, verify the replica set and backup, then run the explicit migration:

```powershell
$env:Mongo__Uri = "<replica-set connection string>"
$env:Mongo__Database = "<dedicated database>"
Push-Location backend
try {
    dotnet run --project src/UnitToko.Worker -- --migrate
}
finally {
    Pop-Location
}
```

The runner applies registered steps in order. Version 1 establishes the storage foundation; version 2 adds authorization collections and strengthens identity/membership records; version 3 adds access and organization lifecycle metadata. Review existing records and resolve duplicate keys before applying it. Application startup does not seed or migrate.

Pause writers, drain active transactions, verify the backup, and run one schema deployment at a time. Mongo schema DDL is not transactional: recoverable leases coordinate cooperating runners, but cannot fence an already-issued DDL operation from an expired process. Do not overlap deployments or forcibly take over a live deployment. Each `schemaMigrations` step has an applying lease and a final applied state. Retry an interrupted step after its ten-minute lease expires or a handled failure releases it; cleanup is bounded to five seconds. Completed steps are never replayed. Upgrading therefore preserves prior `appliedAt` values and unrelated stronger validators. An interrupted version 2 resumes there before version 3; an interrupted version 3 resumes version 3 only.

The runner refuses unknown newer, gapped, malformed, or out-of-order history. API readiness reads history without changing it and requires every known step to be applied. Restoring or editing history to make an old binary accept a newer database is not a supported rollback. Restore the coordinated database/application backup or deploy a reviewed forward migration.

Counter updates, migration history and nontransactional outbox lease transitions explicitly use majority write concern even if the connection string requests weaker writes. Business modules must pass the same Mongo session to every write in one transaction and insert outbox records in that transaction; its commit uses majority write concern. Do not make external calls inside the transaction callback.

Deployment history version and aggregate format version are separate. Versions 2 and 3 do **not** change existing business, idempotency, outbox or authorization writers to a new document `schemaVersion`; those formats remain `CollectionMigrations.FoundationDocumentSchemaVersion = 1` and `AuthorizationDocumentSchemaVersion = 1`. A future format change requires its own migration and compatible writer change. An older application must fail readiness once a newer migration is applied, even where an individual old aggregate format remains accepted.

Money fields must be BSON Decimal128 and pass the narrower `Money`/`SignedMoney` contract before persistence; IDs must be BSON Int64 and JSON strings at the HTTP boundary. Module migrations must add required fields and aggregate-specific validators as each workflow ships. The v1 registry establishes common type/range/precision rules for known fields and known uniqueness/query paths; it does not certify unimplemented business schemas or calculated-total equality.

Version 2 owns `identities`, `memberships`, `authorizationGrants`, `authorizationScopes`, `authorizationProposals`, and `authorizationHistory`. It requires explicit status, typed scopes, approval metadata, and long version/fence values as applicable. Before any version-2 schema DDL, it checks existing records against the new formats. Incompatible legacy records cause an actionable failure naming the collection; the migration never invents membership approval, grants, provider binding, or privileged accounts. Reconcile these records through an approved data migration before retrying. No accounts, scopes, or business names are seeded.

The authorization service enforces parent relationships, permission catalogs, ownership, maker/checker independence, and transaction fences. Collection validators provide type/shape constraints; they cannot enforce cross-collection policy. History is inserted through the service's append-only path. Deployment database privileges and audit retention must also restrict direct modifications; this collection alone is not a tamper-proof external audit service.

Version 3 keeps the applied v1/v2 migration source frozen. It adds `organizationProfiles`, `organizationProposals` and `authorizationReadEvents`; it conjoins lifecycle requirements with existing `authorizationProposals` and `authorizationHistory` validators, retaining stronger custom rules. All affected rows, duplicate scoped organization codes and relevant ID counters are checked before schema/index/counter changes. A failure names the affected collection without disclosing record contents. Reconcile incompatible records through an approved process; the migration does not drop rules or invent missing decisions.

Organization profiles use the existing scope's BSON Int64 ID and contain reviewed display code/name plus provenance. Profiles have a unique `(kind, parentId, normalizedCode)` key. No profile is invented for a legacy scope. Organization proposals record the exact intended parent/kind, scope/profile revisions, independent approval or rejection/withdrawal, and a bounded reactivation-impact snapshot. The service owns immutable parent/kind enforcement and recomputation of that snapshot; validators cannot verify cross-collection relationships.

Access withdrawal retains the compatible stored `status: rejected` and adds `resolutionKind: withdrawn`, resolver, time and reason together. Public responses/history distinguish withdrawal from rejection. Older rejected proposals without this metadata remain readable. History may include a typed scope; old immutable history is not rewritten. `authorizationReadEvents` accepts only purpose-limited read operations, bounded IDs/paging or a hashed receipt key, returned count and typed scopes. It does not accept request bodies, raw receipt keys or arbitrary selection properties. No public reader for this audit collection is added.

Before newly available scope creation, v3 raises `authorizationScopes`' ID counter to at least its largest persisted ID, using majority `$max`; an already larger counter is never reduced. The same rule applies to `organizationProposals`, `authorizationHistory` and `authorizationReadEvents`. Empty collections do not receive invented starting records. This is a high-water correction during the exclusive deployment window, not runtime seeding. Later approved imports must also advance the relevant counters before writers resume; deleting an entity must never make its ID reusable.

## Version 15: governed stock-source setup

Version 15 follows the actual applied steps 1–14 and installs only stockSourceProposals, stockSourceDecisions, inventoryPoolRegistrations and inventoryPools storage contracts. It validates all existing source records and their proposal/decision/audit/receipt/registration/pool relationships before target DDL or counter writes. Invalid or orphaned history, unsafe indexes, duplicate physical keys, exhausted/malformed counters or unaccepted active/revised pools stop deployment; the runner does not infer missing evidence or repair business history.

The migration retains stronger existing validators and equivalent safe index names, creates location query indexes, enforces one terminal decision per proposal and one canonical physical key per organization, and advances proposal/decision/pool/audit ID counters using majority high-water updates. Registration IDs share the pool allocator. Empty collections receive no business records or invented source settings. Current suspension of a historical actor/scope/person source does not erase a past decision; runtime commands still require current authority.

Use the pause/drain/backup procedure above. An interrupted step can resume under the real migration lease; the local verification includes interruption after the first actual collection DDL and a competing-runner case. Do not edit applied history to force readiness. Version 15 does not install complete lot/balance/movement or source-activation workflows, and does not activate any pool or external integration. See ../../docs/quality/stock-source-api-verification-2026-09-29.md for evidence and remaining acceptance.
