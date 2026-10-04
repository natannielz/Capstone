# Release 1 deployment prerequisites

The [release checklist](../releases/release-1.md) is the acceptance record. A frontend deployment alone is not a working hosted pilot.

The application keeps Next.js/TypeScript, .NET 10, MongoDB and Auth0. Release 1 never writes stock reservations, payments or invoices. Private financial evidence remains in its existing architecture and is not exposed by this release.

## Required external inputs

1. An existing authorized .NET host with persistent encrypted identity keys, approved TLS/ingress and restart operations. Its `Release__Profile` must be `release1`.
2. An authorized MongoDB replica-set URI and dedicated database, credentials in provider secret storage. Apply the compiled migration worker explicitly before startup; preserve a backup and one writer during deployment. The separate local synthetic database must never be migrated into production.
3. An Auth0 confidential application/database connection: reviewed issuer, client ID/secret, connection ID/name, controlled claims namespace and installed assurance Actions. Configure the exact public callback `/auth/callback` and sign-out callback `/auth/signed-out`; validate verified email, MFA, revocation and secure approved-user provisioning against the real tenant.
4. Approved branch/business records, stable provider subjects and requester/staff assignments, plus the approved catalog. Email text alone is not an identity binding. Synthetic local identities and products are not acceptable production inputs.
5. Vercel `unit-toko-bni`, project root `frontend`, Node 24. `NEXT_PUBLIC_UNIT_TOKO_RELEASE=release1`; set `UNIT_TOKO_API_ORIGIN` only to the approved HTTPS API ingress. Remove obsolete demo secrets after checking that no retained deployment uses them.

## Routing and verification

Follow [identity-hosting.md](identity-hosting.md): one public HTTPS origin, sanitized forwarded headers, an exact directly connected trusted proxy and backend network isolation. A generic external rewrite does not prove that this network boundary is satisfied. The actual Vercel/API topology must pass origin, cookie, callback and spoofed-forwarding checks before admitting real accounts.

After deploying the pinned GitHub commit, verify the requester submission, staff decision and requester history in the real browser against the hosted API and MongoDB. Check refresh/retry and application restart, a second branch's denial, mobile layout, catalog edits, API/host logs and readiness. Record the deployed frontend and API versions, database migration version and measured outcomes in the release checklist. Do not substitute local fixture results for hosted acceptance.

## Inspected environment, 4 October 2026

- GitHub `natannielz/Capstone`: main has no branch protection or applicable rules returned by the API. Normal verified main pushes are permitted; no force push is needed.
- Connected Railway account: no projects found. No paid resource was provisioned.
- Vercel project exists, but inspected environment names are the old demo/Turso settings; there is no configured Release 1 API origin or supplied Auth0/API/Mongo host configuration.
- No Auth0 tenant credentials or approved provider subjects/catalog were supplied. User delegated preparation choices; the local fallback therefore uses explicit synthetic data.

Until the above inputs and real hosted checks are complete, the working deliverable is the persistent local pilot. A published frontend candidate must remain labelled as a candidate with hosted sign-in unavailable.
