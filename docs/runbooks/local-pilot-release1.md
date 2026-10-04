# Release 1 persistent local pilot

This Windows-only launcher is a **synthetic local pilot**, not Auth0 tenant acceptance or production bootstrap. Requester identities, independent-person evidence, branch names and sample catalog are explicitly synthetic. The password plus displayed factor code simulates provider MFA; it is not real OTP enrollment. No messages, purchases, reservations, stock or accounting transactions are provisioned.

The separate `backend/tools/UnitToko.LocalPilot` executable hosts the real API in Production with `Release:Profile=release1`, real OIDC code/PKCE validation, Mongo-backed protected sessions, CSRF and scoped authorization. Only this executable installs its synthetic provider. Production `Program.cs`, Auth0 validation and admission policy are unchanged by the launcher.

## Start

Prerequisites: Windows, PowerShell 7, installed .NET 10 SDK matching `backend/global.json`, Node 24, an already inspected `mongod.exe`, Microsoft Edge, installed frontend dependencies, and a Release 1 production frontend build. The launcher downloads nothing and does not silently build.

Build once from the repository root when the candidate is ready:

```powershell
$env:NEXT_PUBLIC_UNIT_TOKO_RELEASE = 'release1'
npm run build --prefix frontend
dotnet build backend/tools/UnitToko.LocalPilot/UnitToko.LocalPilot.csproj -c Release
```

Run in a terminal (pass explicit paths if tools are not on PATH):

```powershell
./backend/tools/UnitToko.LocalPilot/start-local-pilot.ps1 -MongoPath 'C:\path\to\mongod.exe'
```

On the prepared delivery computer, the inspected tools are already present. From `C:/Users/User/Documents/Capstone`, this command uses those exact versions:

```powershell
./backend/tools/UnitToko.LocalPilot/start-local-pilot.ps1 `
  -DotnetPath './.tools/dotnet10/dotnet.exe' `
  -MongoPath './.tools/mongodb/mongodb-win32-x86_64-windows-8.2.12/bin/mongod.exe' `
  -NodePath 'C:/Users/User/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
```

The launcher applies real migrations, explicitly bootstraps the marked pilot database on first use, and starts the API, Next and the HTTPS edge. When it prints “SYNTHETIC LOCAL PILOT”, run the generated `tmp/pilot/release1/open-browser.ps1` in another PowerShell 7 window. This opens an ordinary interactive Edge window with a dedicated profile. It maps only the two private `unit-toko-pilot.home.arpa` names to loopback and pins the pilot certificate key. It never changes the system hosts file, DNS, root certificate store or normal browser profile. Port 443 must be free; the tool does not evict another service.

Open `tmp/pilot/release1/ACCOUNTS-PRIVATE.txt` privately. Use `pemohon-a`, `pemohon-b` or `petugas` with that account's generated password and **simulation** factor code. Sign out before switching accounts. The extra `peninjau` identity is a synthetic setup participant with no request decision grant. All private settings, certificate, session keys, browser profile and Mongo files are confined to the current Windows user's ACL-protected `tmp/pilot/release1` directory, ignored by Git. Do not share this directory or its credentials.

## Persistence and scope

- Mongo listens on `127.0.0.1:27029`, replica set `unitTokoPilot`, database `unittoko_local_pilot_release1`. It is separate from the disposable test server on 27028. Test commands are not enabled. Startup refuses an unmarked existing database, mismatched state, wrong process/data directory, remote host, or another replica set.
- Requester A is scoped to buying organization 10 / branch 11; B to branch 12. Staff is scoped to fulfillment organization 100 / shop 101 / warehouse 102 / channel 103. Membership/grant and person evidence are local synthetic bootstrap records. They do not certify real approvals. No role text in the provider becomes application authority.
- Three labelled sample packages are created and published through actual catalog commands. Prices and photos are unavailable, not fabricated. Future edits and requests persist; normal restart does not reseed or overwrite them.
- Ctrl+C stops API and Next. Mongo remains running on loopback and all database files remain. Restart the same launcher to reuse accounts, certificate, session keys and requests. Keep the database and private launcher state together when backing up. No reset/delete command is supplied.
- Synthetic person evidence expires after 30 days and the local certificate after one year. The launcher never silently renews those approvals or replaces keys. This pilot is for a bounded local review; expired authority must remain denied until deliberately reprovisioned.

If a launch stops during setup, preserve its files and restart: schema migration and catalog command keys are resumable. A mismatched database/private state must be restored together rather than bypassed. The private `mongo.log` and `next.log` contain process diagnostics; the launcher never prints passwords, provider tokens or authorization query strings.

Actual Auth0 requires the reviewed provider and hosting inputs in `identity-hosting.md`; this synthetic host does not satisfy that external acceptance.

## Walkthrough

1. Open the generated browser shortcut and sign in as `pemohon-a` using its private generated values. The account page shows only the authorized synthetic branch.
2. Open **Katalog kemasan**, inspect the name, package contents, unit, minimum and increment, then choose **Pilih untuk permintaan**. Select the branch, add packages and quantities, and review the shop and notes before confirming.
3. Keep the **PT-…** reference. The request initially says **Menunggu peninjauan**. Refreshing reads the saved request; an uncertain submission is checked through **Periksa hasil** with its original key.
4. Sign out and sign in as `petugas`. Open **Permintaan masuk**, inspect the request and its packaging, then accept for processing or reject with a reason. Confirm the decision. Acceptance creates no reservation, price, payment or invoice.
5. Sign in as `pemohon-a` again to see the result and decision history. `pemohon-b` belongs to a separate synthetic branch and cannot read that request.
6. As `petugas`, use **Katalog toko** to maintain the pilot's product groups, package definitions and publishing state. Photos remain unavailable until the existing evidence/asset workflow is approved for that use; placeholders state this honestly.

Stop and restart the same launcher to check persistence. Keep `tmp/pilot/release1` intact; deleting it is not part of the launch procedure.
