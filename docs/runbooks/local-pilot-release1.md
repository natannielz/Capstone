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

Release 1 uses `frontend/.next`; full-workflow browser validation uses `frontend/.next-full` when built with `NEXT_PUBLIC_UNIT_TOKO_RELEASE=full`. The launcher always starts the Release 1 profile and checks its `.next/BUILD_ID`. Do not rebuild or clean `.next` while the pilot is serving it. A full validation build may run while the existing pilot stays online, but serialize frontend builds and type checks because Next still updates shared `next-env.d.ts` and generated TypeScript includes. A build that predates this separation must be rebuilt with `release1` during a coordinated pilot restart; the presence of `BUILD_ID` alone does not prove its profile.

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

## Full-workspace demo profile

To see every maintained workspace locally (grouped sidebar, Ctrl+K palette, Pekerjaan saya, order drafts with discard and restore, payment forms), build the full frontend once and start the launcher with `-Profile full`:

```powershell
$env:NEXT_PUBLIC_UNIT_TOKO_RELEASE = 'full'
npm run build --prefix frontend
dotnet build backend/tools/UnitToko.LocalPilot/UnitToko.LocalPilot.csproj -c Release
./backend/tools/UnitToko.LocalPilot/start-local-pilot.ps1 -Profile full `
  -DotnetPath './.tools/dotnet10/dotnet.exe' `
  -MongoPath './.tools/mongodb/mongodb-win32-x86_64-windows-8.2.12/bin/mongod.exe' `
  -NodePath 'C:/Users/User/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
```

The full profile keeps its own state in `tmp/pilot/full`, its own database `unittoko_local_pilot_full` on `127.0.0.1:27030`, and its own browser profile. It never touches the Release 1 pilot. Its synthetic accounts additionally receive the following:
- **Requesters:** ordering, invoice and payment-evidence rights.
- **`petugas`:** the operational workspace rights.
- **`peninjau`:** price approval.

The three sample packages receive prices through the real maker/checker price service, so requesters can prepare drafts. Everything stays SYNTHETIC. Only one profile can run at a time, because both use port 443.

### Product photos (full profile)

The catalog's 251 products include the first demo's 200, and their photos go through the real private-evidence path, not a public folder:
- **Scanner:** ClamAV for Windows, unpacked once under `.tools/clamav/clamav-<version>.win.x64` (official Cisco-Talos release zip, SHA-256 checked against the release). The launcher writes `tmp/pilot/full/photos/clamd.conf`, then starts `clamd` on `127.0.0.1:3310` only, with `TZ=UTC` so its version reply carries the UTC signature time the scanner requires. Signatures live in `tmp/pilot/full/photos/clamav-db`. ClamAV's own `freshclam` refreshes them at start-up and every 20 hours while the pilot runs. A failed update keeps the old signatures, and the scanner refuses signatures older than a week.
- **Decoder:** on first use the launcher copies the built `UnitToko.ImageDecoder` into `tmp/pilot/full/photos/decoder` and pins its files in `installation.json`. Later rebuilds do not change it. To install a newer decoder deliberately, delete that folder and its installation file. The worker approvals then move to a new revision, and the photos are rescanned.
- **Store and approvals:** a private store at `%LOCALAPPDATA%\UnitToko.LocalPilot\full\evidence` (owner: this user; access: this user, SYSTEM and Administrators). It lives outside the checkout because the store refuses any root whose ancestor folders grant write access to other identities, and checkout folders can carry such grants from other tools. The launcher, acting as operator for this synthetic store only, provisions:
  - the store policy (2 MiB maximum);
  - the read policy (a verdict counts for 24 hours, signatures for 7 days);
  - one worker approval per pipeline fingerprint, valid for 30 days and extended automatically.

  The API receives `Evidence:PrivateWindows` and `Evidence:ImageRevalidation`, so payment proofs and staff photo uploads use the same store.
- **Photos:**
  - Once, the launcher converts each `frontend/public/images/products/generated/*.webp` to an 800 px baseline JPEG and uploads it as `petugas` through reserve, upload, scan, decode and associate.
  - Every hour it requests a rescan of any photo whose verdict is older than 12 hours, so buyers keep seeing photos.
  - After the pilot has been stopped for more than a day, photos reappear a few minutes after start-up, once the rescan finishes.

Without `.tools/clamav` the full profile runs without photos and says so at start-up.
