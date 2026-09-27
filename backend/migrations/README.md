# Mongo collection migration v1

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

The migration creates strict validators and named indexes. Review existing records and resolve duplicate keys before applying it. Application startup does not seed or migrate. Schema DDL is not transactional, so run one schema deployment at a time. The `schemaMigrations` record has an applying lease and a final applied state; retry an interrupted migration after the lease expires or a handled failure releases it. Repeating an applied version is a no-op that preserves newer validators. API readiness uses `IsCurrentAsync` without changing schema.

Counter updates, migration history and nontransactional outbox lease transitions explicitly use majority write concern even if the connection string requests weaker writes. Business modules must pass the same Mongo session to every write in one transaction and insert outbox records in that transaction; its commit uses majority write concern. Do not make external calls inside the transaction callback.

Every business document written by later modules must contain `schemaVersion: 1`. Money fields must be BSON Decimal128 and pass the narrower `Money`/`SignedMoney` contract before persistence; IDs must be BSON Int64 and JSON strings at the HTTP boundary. Module migrations must add required fields and aggregate-specific validators as each workflow ships. The v1 registry establishes common type/range/precision rules for known fields and known uniqueness/query paths; it does not certify unimplemented business schemas or calculated-total equality.
