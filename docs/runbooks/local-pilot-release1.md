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

## Review stack and personas (website-ready program)

The `review` profile is a separate, loopback-only copy of the full release for screenshot, accessibility and function reviews. Nobody types a password: the stack mints one session per persona and writes it as a Playwright storage state.

| | review | full (for comparison) |
|---|---|---|
| Browser origin | `https://localhost:8443` (127.0.0.1 and ::1 only) | `https://portal.unit-toko-pilot.home.arpa` (443) |
| MongoDB | `127.0.0.1:27031`, data in `tmp/pilot/review/mongo-data` | `127.0.0.1:27030` |
| ClamAV | its own `clamd` on `127.0.0.1:3311` | `127.0.0.1:3310` |
| Frontend build | `frontend/.next-full` (same build as full) | `frontend/.next-full` |
| Sign-in | minted sessions in `tmp/website-ready/sessions/<persona>.json` | passwords in `tmp/pilot/full/ACCOUNTS-PRIVATE.txt` |

Start it (the launcher never builds; build `backend/tools/UnitToko.LocalPilot` in Release and `.next-full` first, as for the full profile). Run the first start through the heavy lock, because it seeds the 251-product catalog and prices:

```powershell
./backend/tools/UnitToko.LocalPilot/start-local-pilot.ps1 -Profile review `
  -DotnetPath './.tools/dotnet10/dotnet.exe' `
  -NodePath 'C:/Users/User/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' `
  [-ClamavSignatures '<folder with main/daily .cvd or .cld>']
```

- It is ready when it prints `Review stack (loopback only): https://localhost:8443/` and `tmp/website-ready/sessions/personas.json` exists.
- Stop it from another shell with `start-local-pilot.ps1 -Profile review -Stop` (or Ctrl+C in its own window). Stopping revokes every minted session, deletes the session files and shuts down the review MongoDB. Its data stays, so the next start skips seeding. Do not kill `dotnet` directly: that orphans `next start` and `clamd`. `-Stop` works for any profile started by this launcher version; a pilot started by an older build ignores it.
- The review stack never downloads anything. Product photos need ClamAV signatures that are already on the machine: pass `-ClamavSignatures` with a folder to copy them from (read-only, newer files only). Without it the review catalog runs without photos and says so.
- If the review stack runs from the main checkout while the full pilot runs, both serve the same `.next-full`. Do not rebuild it while either is running.

**Sessions.** One file per signed-in persona: `tmp/website-ready/sessions/<persona>.json`. `personas.json` lists every persona with its label, user id and grants, and never contains a token. Only `UnitToko.LocalPilot` mints these sessions, through the API's own session store (as the browser rehearsal fixtures do), and only when every edge bind address is loopback.
- The stack mints new sessions every 3 minutes and revokes each session 45 minutes after minting. So read the file right before you open a browser context; a fresh file carries a factor time under 3 minutes old, which also satisfies the 5-minute step-up rule.
- Never print, paste or log a session file's contents.
- In Playwright: `browser.newContext({ storageState: '<file>', ignoreHTTPSErrors: true })`, then open `https://localhost:8443/...`. The guest `tamu` uses a context without storage state.

**The edge.** The API keeps its production origin rules unchanged. The review edge accepts only `Host: localhost:8443`. It refuses any browser `Origin` other than `https://localhost:8443`, and translates that origin to the API's internal name `portal.unit-toko-review.home.arpa`, which is never resolved. It drops `Strict-Transport-Security`, so `localhost` is not pinned to HTTPS for other local work. The 240 requests per minute per IP limit applies to everything on loopback, so pace your scripts.

**Screenshots and accessibility.** `frontend/scripts/review/capture.mjs` (run with the Node 24 runtime above, from any folder):

```powershell
node frontend/scripts/review/capture.mjs --persona <name|all|a,b> --routes <group|all|/path,/path> --run <label> [--reachable-only] [--out <folder>] [--viewports 1280,390]
```

- **Output:** `tmp/website-ready/screens/<label>/<persona>/<route>@1280.png` and `@390.png`, plus `axe.json` (axe-core violations per route and width) and `metrics.json` per persona. The metrics are: status, final path, redirects, page scroll width against viewport width, every visible button, input, select, textarea and `role=button` with its height and width, the heading outline, `/api/v1` 4xx/5xx responses, console errors and load time. `run.json` records the run.
- **Routes:** groups come from `frontend/scripts/review/routes.json`: every page route, the personas expected to reach it, and a `sample` rule for dynamic routes. By default every selected route is captured for each persona, so denial screens show too; `--reachable-only` limits the run to expected routes.
- **Pacing:** the harness keeps to 200 API requests per minute and retries a rate-limited page once after a minute.

**Personas.** These are SYNTHETIC accounts in the pilot's organisation (buying organisation 10, branches 11/12, shop 101, warehouse 102, channel 103). Each persona is an explicit grant set: `RoleTemplates.Materialize(template, financePacks, additions)` minus the template permissions it must not hold (see `backend/tools/UnitToko.LocalPilot/PilotPersonas.cs`).
- **Additions** are the permissions a persona's job needs beyond its template; the role templates do not carry them yet (findings F-006/F-007).
- **Drops** remove the proposing side of a pair the persona approves.
- No persona holds both sides of any maker/checker pair, with no exceptions. The test `PilotSessionsTests` lists every such pair in `Permissions.cs` and asserts this, and checks every grant against `ScopePolicy`.

Every start reconciles the stored grants with these definitions:
- A stored grant with other permissions is rewritten.
- A grant or membership no longer defined is revoked, and a missing one is added.
- When anything changes, the persona's `grantVersion` moves, so older sessions and snapshots stop authorizing.
- `personas.json` reports the grants as stored.

The same personas also join the `full` profile, on its next start and whenever a persona is added. First `private-settings.json` and `ACCOUNTS-PRIVATE.txt` are copied to `*.before-upgrade-<UTC timestamp>` beside them; only then are the new passwords appended. Existing accounts and secrets stay unchanged, and no file content is printed.

| Persona | Id | Template (+packs) and scope | Added beyond the template (and dropped from it) |
|---|---|---|---|
| `tamu` | none | no session | none |
| `pemohon` | 3001 | `buyer`, branch 11 | `branch.pic`, `order.request.read`, `order.request.submit`, `substitution.accept`, `organization.address.read` |
| `penyetuju-cabang` | 3002 | `buyer`, branch 11 | `branch.pic`, `order.spend.approve`, `order.spending.policy.read`, `order.request.read`, `organization.address.read`; drops `order.submit` |
| `peninjau-toko` | 3003 | `store-head`, shop | `order.accept`, `order.request.read`, `order.request.decide`, `order.commercial.policy.read`, `operations.anomaly.read`, `organization.address.read` |
| `petugas-katalog` | 3004 | `store-head`, shop | `catalog.public.propose` |
| `peninjau-katalog` | 3005 | `store-staff`, shop | `pricing.approve`, `catalog.public.approve` |
| `petugas-gudang` | 3006 | `store-staff`, warehouse 102 | `stock.release.propose`, `stock.convert`, `substitution.propose`, `reservation.release`, `procurement.container.receive`, `procurement.inspection.record`, `procurement.inspection.evidence.read`, `procurement.qualityacceptance.read`, `procurement.qualityacceptance.propose`, `stock.source.read`, `stock.source.reconciliation.read`, `organization.address.read` |
| `petugas-kirim` | 3007 | `store-staff`, warehouse 102 | `shipment.assign` (the template already has `shipment.dispatch`), `organization.address.read` |
| `kurir` | 3008 | `courier`, courier assignment for shop 101, any warehouse: the shop's courier roster, a membership and grant in that scope, as the dispatch courier picker requires | none |
| `keuangan-tagihan` | 3009 | `finance` + `billing`, shop | `invoice.release.read`, `invoice.release.propose`, `payment.review.read`, `receiving.account.read`, `receiving.account.propose`, `procurement.stockvaluation.read`, `procurement.stockvaluation.propose`, `procurement.disbursement.read`, `procurement.disbursement.evidence.read`, `procurement.read`, `stock.read`, `case.read`, `order.read`, `organization.address.read` |
| `pemeriksa-pembayaran` | 3010 | `finance` + `accounting`, shop | `invoice.read`, `payment.review.read`, `payment.review.correct`, `payment.review.manual`, `payment.verify`, `bank.receipt.read`, `bank.receipt.confirm`, `bank.statement.read`, `bank.statement.stage`, `invoice.release.read`, `invoice.release.approve`, `receiving.account.read`, `receiving.account.verify`, `receiving.account.approve`, `procurement.stockvaluation.read`, `procurement.stockvaluation.decide`, `stock.release.approve`, `funding.read`, `funding.approve`, `procurement.disbursement.read`, `procurement.disbursement.evidence.read`, `procurement.settlement.read`, `procurement.read`, `stock.read`, `accounting.book.read`, `report.read`, `organization.address.read` |
| `pengadaan` | 3011 | `store-staff`, shop | `supplier.change.propose`, `procurement.supplier.document.record`, `procurement.supplier.document.match`, `procurement.supplier.document.evidence.read`, `supplier.bank.read`, `supplier.bank.propose`, `supplier.claim`, `funding.read`, `procurement.inspection.evidence.read`, `procurement.qualityacceptance.read`, `procurement.payable.read`, `procurement.payable.propose`, `procurement.advancereturn.read`, `procurement.advancereturn.propose`, `organization.address.read` |
| `penyetuju-pengadaan` | 3012 | `store-head`, shop | `supplier.change.approve`, `procurement.qualityacceptance.read`, `procurement.qualityacceptance.decide`, `procurement.supplier.document.evidence.read`, `procurement.inspection.evidence.read`, `supplier.bank.read`, `supplier.bank.approve`, `supplier.claim.approve`, `funding.read`, `procurement.payable.read`, `procurement.payable.decide`, `procurement.advancereturn.read`, `procurement.advancereturn.decide`, `organization.address.read`; drops `supplier.claim` |
| `admin-akses` | 3013 | `system-admin`, system | `person.review.read`. This persona proposes; `penyetuju-akses` approves |
| `penyetuju-akses` | 3015 | `system-admin`, system | `access.approve`, `access.revoke`, `membership.approve`, `organization.approve`, `person.review.read`; drops `access.propose`, `membership.propose`, `invite.manage`, `recovery.initiate`, `technical.configure` |
| `staf-pembeli` | 3014 | `store-staff`, shop **and** `buyer`, branch 12 | `branch.pic` on the buyer grant. `order.submit` is a buying-scope permission, so this persona has a second grant instead of an addition to the shop grant |

Maker/checker pairs: `petugas-katalog` proposes prices and the guest catalog, and `peninjau-katalog` approves them. `pengadaan` prepares, and `penyetuju-pengadaan` approves. `keuangan-tagihan` proposes invoice releases, valuations and receiving accounts and records payments, and `pemeriksa-pembayaran` approves and verifies them. `petugas-gudang` proposes stock releases, and `pemeriksa-pembayaran` approves them. Funding is requested by the `store-head` personas and approved by `pemeriksa-pembayaran`.
- `pemohon` orders, and `penyetuju-cabang` approves the spending.
- `pengadaan` raises supplier claims, and `penyetuju-pengadaan` decides them.
- `admin-akses` proposes access, memberships and organisation changes, and `penyetuju-akses` approves them.

FIX-RULES owns template changes. Until then, the drops keep each checker persona from also proposing.

**Guest samples.** The full and review profiles seed 12 approved guest sample products in `PilotBootstrap.SeedAfterCatalogAsync` (`PilotSamples.cs`), so `tamu` sees a catalog without prices on `/katalog`. The 12 are the first three demo products of each group; each has a published package and a demo photo.
- **How:** only through `CatalogPublicSampleService`. `petugas` (2001, `catalog.public.propose`) proposes, and `peninjau` (2002, `catalog.public.approve`, fresh factor) approves.
- **On every start:**
  - Samples that are already approved are left alone, including those an earlier assortment showcase brought in through migration 122.
  - A chosen product that is missing is added through a new proposal.
  - A proposal of ours left pending by an interrupted start is approved.
  - Nothing is added beyond the 200-product limit.
  - Over the limit, guests get `public_catalog_limit`. This happened when migration 122 converted a public assortment. In that case the set is narrowed to the 12 chosen products: `petugas` proposes removing all other samples (at most 200 per proposal, one catalog owner each) and `peninjau` approves. A set inside the limit is never touched.
  - Conflicts with a changed sample state are logged and skipped, so they never stop the start.
- Release1 has no guest samples.
