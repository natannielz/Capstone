# Local development and foundation checks

For the usable Release 1 application and persistent requester/staff accounts, follow [the local pilot launcher](local-pilot-release1.md). The commands below are retained developer/regression tooling; disposable test databases and legacy demo accounts are not pilot access or production acceptance.

Use Node 24 and the .NET SDK pinned by `backend/global.json` (currently 10.0.401). Commands below run from the repository root unless stated otherwise. Keep real credentials, databases and uploaded evidence outside Git.

## Install and verify

```powershell
npm ci --prefix backend/legacy
npm ci --prefix frontend
npm ci --prefix backend/contracts --ignore-scripts
npm run typecheck --prefix frontend
npm run lint --prefix frontend
npm test --prefix frontend
npm run build --prefix frontend
```

The legacy package has its own runtime dependencies; neither a root `node_modules` directory nor dependencies from another checkout are required. The frontend test command runs the preserved suite, a future-date repository replay, and the executable legacy golden scenarios. These tests do not establish parity with the new C# business modules.

Start the disposable local MongoDB replica-set server before the backend tests:

```powershell
docker compose -f backend/deploy/compose.yaml up -d
Push-Location backend
try {
    dotnet build UnitToko.slnx --nologo
    dotnet test UnitToko.slnx --no-build --nologo
} finally { Pop-Location }
```

Docker is one supported local option. The implementation workspace also has checksum-verified project-local .NET and MongoDB binaries under ignored `.tools/`; those machine-specific downloads are not repository dependencies. MongoDB listens only on `127.0.0.1:27028`. The test fixture initializes `rs0`, creates a unique `unittoko_test_*` database, and drops only that database when finished. It refuses a remote address or a supplied database in the connection URI. It fails when MongoDB is absent instead of skipping transaction tests. Do not run another MongoDB process on the same port.

The disposable server must use `--setParameter enableTestCommands=1`; compose and CI set this explicitly. Commit-failure tests use MongoDB's [app-name-scoped failCommand hook](https://github.com/mongodb/mongo/wiki/The-failCommand-fail-point) and remove the fault in `finally`. They inject uncertain commit replies after the database writes have executed. This tests recovery with the original idempotency key, alongside ordinary rollback and concurrent-write tests. Never reuse this test server configuration for production. Run one backend test suite at a time against this port because failpoint configuration belongs to the server.

## Run the new API foundation

```powershell
$env:Mongo__Uri = 'mongodb://127.0.0.1:27028/?replicaSet=rs0'
$env:Mongo__Database = 'unittoko_development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Push-Location backend
try {
    dotnet run --project src/UnitToko.Worker -- --migrate
    dotnet run --project src/UnitToko.Api --no-launch-profile --urls http://127.0.0.1:5388
} finally { Pop-Location }
```

The explicit worker migration command creates validators/indexes and records its schema version. API startup never applies migrations or seeds business data. `/health/live` checks that the process is running. `/health/ready` additionally checks that MongoDB is a writable replica-set member and has the current completed schema migration. The protected `/api/v1/system/status` currently returns 401: production identity is a later implementation gate. The test authentication adapter exists only in the test assembly. Do not turn it into a development or production login bypass. The worker foundation deliberately has no external message processors yet.

The existing frontend continues through its legacy adapters during migration. It is not connected to the new service's stock or finance writes. Keep one authoritative writer until the coordinated migration gate passes.

## Regenerate API contracts

After building the backend:

```powershell
./backend/contracts/export-openapi.ps1
npm run generate --prefix backend/contracts
./backend/contracts/export-openapi.ps1 -Check
npm run check --prefix backend/contracts
```

The exporter reads only the local development schema; it does not need production secrets. Commit `backend/contracts/openapi.json` together with generated frontend types when endpoints change. The quality workflow checks both directions of drift. The generator and client follow the [OpenAPI TypeScript Node API](https://openapi-ts.dev/node) and [typed fetch client](https://openapi-ts.dev/openapi-fetch/) contracts.

## Verification limits

The quality workflow is a local/CI gate, not a production deployment. It uses read-only repository permissions and no deployment credentials. Its GitHub actions are pinned to official release commits: [checkout](https://github.com/actions/checkout/releases/tag/v7.0.1), [setup-node](https://github.com/actions/setup-node/releases/tag/v7.0.0), and [setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0). A passing local run does not prove that hosted CI, provider login, backups, a real branch pilot or cutover have run. Track those separately in `docs/quality/production-acceptance.md`.
