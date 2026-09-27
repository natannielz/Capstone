# Mongo collection migrations

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

The runner applies registered steps in order. Version 1 establishes the storage foundation; version 2 adds authorization collections and strengthens identity/membership records. Review existing records and resolve duplicate keys before applying it. Application startup does not seed or migrate.

Pause writers, drain active transactions, verify the backup, and run one schema deployment at a time. Mongo schema DDL is not transactional: recoverable leases coordinate cooperating runners, but cannot fence an already-issued DDL operation from an expired process. Do not overlap deployments or forcibly take over a live deployment. Each `schemaMigrations` step has an applying lease and a final applied state. Retry an interrupted step after its ten-minute lease expires or a handled failure releases it; cleanup is bounded to five seconds. Completed steps are never replayed. Upgrading from version 1 therefore preserves its original `appliedAt` and unrelated stronger validators. An interrupted version 2 resumes version 2 only.

The runner refuses unknown newer, gapped, malformed, or out-of-order history. API readiness reads history without changing it and requires every known step to be applied. Restoring or editing history to make an old binary accept a newer database is not a supported rollback. Restore the coordinated database/application backup or deploy a reviewed forward migration.

Counter updates, migration history and nontransactional outbox lease transitions explicitly use majority write concern even if the connection string requests weaker writes. Business modules must pass the same Mongo session to every write in one transaction and insert outbox records in that transaction; its commit uses majority write concern. Do not make external calls inside the transaction callback.

Deployment history version and aggregate format version are separate. Version 2 does **not** change existing business, idempotency, or outbox writers to `schemaVersion: 2`; those formats remain `CollectionMigrations.FoundationDocumentSchemaVersion = 1`. The first authorization document format is also `AuthorizationDocumentSchemaVersion = 1`. A future format change requires its own migration and compatible writer change. An older version-1 application must fail readiness once version 2 is applied, even where an individual old aggregate format remains accepted.

Money fields must be BSON Decimal128 and pass the narrower `Money`/`SignedMoney` contract before persistence; IDs must be BSON Int64 and JSON strings at the HTTP boundary. Module migrations must add required fields and aggregate-specific validators as each workflow ships. The v1 registry establishes common type/range/precision rules for known fields and known uniqueness/query paths; it does not certify unimplemented business schemas or calculated-total equality.

Version 2 owns `identities`, `memberships`, `authorizationGrants`, `authorizationScopes`, `authorizationProposals`, and `authorizationHistory`. It requires explicit status, typed scopes, approval metadata, and long version/fence values as applicable. Before any version-2 schema DDL, it checks existing records against the new formats. Incompatible legacy records cause an actionable failure naming the collection; the migration never invents membership approval, grants, provider binding, or privileged accounts. Reconcile these records through an approved data migration before retrying. No accounts, scopes, or business names are seeded.

The authorization service enforces parent relationships, permission catalogs, ownership, maker/checker independence, and transaction fences. Collection validators provide type/shape constraints; they cannot enforce cross-collection policy. History is inserted through the service's append-only path. Deployment database privileges and audit retention must also restrict direct modifications; this collection alone is not a tamper-proof external audit service.
