# Identity hosting and browser boundary

Status: local implementation, not a deployed Auth0 tenant or release approval. The API defaults to unavailable identity. Enable it only in an explicitly configured environment; missing or invalid provider/key settings stop startup.

## One public origin

Serve the frontend, `/api/v1` and `/auth` through one canonical HTTPS origin. Route `/api/v1` and `/auth` (including their descendants) to the ASP.NET API; route pages and frontend assets to Next.js. Preserve `/auth/callback` and `/auth/signed-out` exactly. The frontend uses relative URLs and same-origin cookies; provider tokens never enter its storage.

A reverse proxy must overwrite incoming forwarded headers, not append untrusted browser values. Supply exactly one `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` value. Configure only the directly connected proxy IP addresses in `Identity:TrustedProxies`; otherwise forwarded headers are ignored. The API accepts one hop and requires header symmetry. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. Restrict backend network access to the proxy/approved health network. A load balancer must preserve the one-hop contract or be reviewed with a corresponding explicit topology change.

The API checks the resulting scheme and host against the exact configured origin before reading sessions. It rejects path-base rewriting. Health probes remain independent of browser origin. API responses do not enable cross-origin credential sharing. Identity pages use a same-origin referrer policy for native forms; callback/API responses suppress referrers so authorization codes and state are not passed onward.

The local [real browser rehearsal](stock-count-browser-rehearsal.md) now exercises Next.js, HTTPS proxying, the actual API/Mongo/session middleware, browser cookies, anti-forgery, stock-count recovery and logout with a synthetic provider. Actual Auth0, trusted public TLS, deployed routing/network isolation and operations acceptance remain open. Local browser evidence is not a production hosting claim.

## Configuration

Supply these through environment-specific secret/configuration storage, never a committed real configuration file:

| Setting | Required value |
| --- | --- |
| `Identity:Auth0:Enabled` | Explicit `true` for the configured runtime; default `false` |
| `Identity:Auth0:Issuer` | Exact reviewed HTTPS provider origin with trailing slash |
| `Identity:Auth0:PublicOrigin` | Exact public HTTPS origin without trailing slash |
| `Identity:Auth0:ClientId`, `ClientSecret` | Approved confidential application credentials |
| `Identity:Auth0:ConnectionId`, `ConnectionName` | Approved Auth0 database connection |
| `Identity:Auth0:ClaimsNamespace` | Controlled HTTPS namespace for verified assurance evidence |
| `Identity:Auth0:AssurancePolicyVersion` | Version matching both installed reviewed OTP Actions |
| `Identity:Auth0:AllowedReturnPaths` | At most 64 unique exact local destinations, including `/identity/account`; no query, fragment or wildcard. Release 1 retains only `/`, `/identity/account`, `/shop`, `/catalog/browse`, `/ordering/requests`, `/ordering/requests/incoming` and `/administration/catalog`. A restricted override must include the destinations its users need. Deferred workspaces remain unavailable. |
| `Release:Profile` | `release1` by default. `full` is reserved for retained backlog regression/development and must not be enabled on the pilot. |
| `Identity:Keys:Directory` | Existing absolute persistent key directory shared by participating API instances |
| `Identity:Keys:ApplicationName` | Stable discriminator unique to this application/environment |
| `Identity:Keys:CertificatePath`, `CertificatePassword` | Absolute encrypted PKCS#12 file and separately managed password; current valid private RSA certificate, at least 2048 bits |
| `Identity:Keys:PreviousCertificates:{index}:CertificatePath`, `CertificatePassword` | Retained private decryption certificates during rotation |
| `Identity:TrustedProxies:{index}` | Exact directly connected proxy IP; no wildcard network |
| `Integrity:SealsRequired` | `true` for the `full` profile: the API refuses to start unless the provenance seal key ring exists in `Identity:Keys:Directory` (see below) |
| `Mongo:Uri`, `Mongo:Database` | Authorized dedicated replica-set database; explicit migration before serving business traffic |

Environment variable nesting uses double underscores, for example `Identity__Keys__CertificatePath`. Paths, mount ACLs, secrets, certificate rotation and key restore must be reviewed in the actual hosting environment. PFX passwords alone do not establish host isolation. Startup loads and retains certificates, constructs one persistent protected provider for sessions/OIDC/anti-forgery, checks key XML encryption and forces a protect/unprotect readiness probe. It never substitutes ephemeral keys. Existing stored key XML must already be encrypted.

### Provenance seal keys (full profile)

Stock, dispatch, invoice and order history is sealed as it is written, so a request verifies a seal instead of re-proving the whole history. Without the seal key ring every such read falls back to the full proof, which is correct but slow (an unsealed stock release took 216 s in the probe; sealed, invoice issuing takes about 2.4 s). The ring is a set of HMAC keys stored only in `Identity:Keys:Directory`, encrypted by the same certificate-protected provider as the session keys; key material never enters MongoDB or any output.

1. Provision once per environment, before the first full-profile start, with the API's own configuration: `UnitToko.Api.exe provision-seal-keys` (same environment variables or `--Identity:Keys:...` arguments as the service). It creates the first key, prints only its id, and leaves an existing ring unchanged.
2. Set `Integrity:SealsRequired=true` (the full deployment template does). The deployment check refuses a full configuration without it or without `seal-current.txt` in the key directory, and the API refuses to start without the ring.
3. Back up the key directory with the session keys. Losing the ring makes existing seals unverifiable; reads then refuse sealed history instead of trusting it.
4. History written before the ring existed stays unsealed and keeps the full-proof fallback until it is backfilled.

The local pilot provisions its own ring in its protected key directory on start and runs with `SealsRequired=true`.

## Browser requests

- `GET /api/v1/identity/session` returns current verified account context and time limits, or anonymous status; no raw session/provider tokens.
- `GET /api/v1/identity/csrf` returns a request token kept only in browser memory and sets the matching secure host-only HttpOnly anti-forgery cookie. Fetch anew before an unsafe operation; identity changes invalidate prior subject-bound evidence.
- Native URL-encoded `POST /auth/login` starts a maintained code/PKCE challenge. Optional invitation ID/code are posted in the body and converted to a hashed server-side binding; they are not query parameters.
- Native `POST /auth/step-up` and `/auth/logout` carry the reviewed `expectedActorId`; it checks stale-page consistency and cannot authenticate anyone. Step-up eligibility does not grant business authority or refresh existing evidence.
- Every unsafe browser `/api/v1` or `/auth` request requires exact `Origin` and a valid `X-CSRF-Token` header or `__RequestVerificationToken` form field. The browser client obtains the token before dispatch, without retrying a business write. A failed preflight is a known unsent request; an earlier uncertain request remains uncertain.
- Callback and signed-out paths accept only GET; the maintained handler validates state/nonce/correlation/signature/protocol before local completion. Arbitrary POST callbacks are rejected. Local logout revokes the stored session family before clearing the cookie and does not claim a global Auth0 logout.

Local per-IP fixed-window controls allow 20 login/step-up attempts per five minutes and 240 other API/auth requests per minute per host, without a queue. These are initial operational settings, not measured capacity or complete abuse protection; provider/account throttling, shared edge limits and realistic load require deployment acceptance. The private backend/proxy topology must prevent IP-header spoofing.

## Provider machine callbacks

The two mapped POST receivers use separate rotating bearer credentials, the same trusted HTTPS origin, bounded raw payloads and transactional revocation. Browser cookies cannot authenticate or refresh a session on those endpoints. Their metadata-based Origin/anti-forgery exemption does not apply to other paths or methods. Both transports remain disabled by default. See [provider security-event operations](provider-security-events.md) for exact configuration, accepted native schemas, v5 migration, permanent admission holds and external coverage gates.

## Remaining activation gates

Actual Auth0 tier, region, ownership, Action ordering/event semantics, verified email/enrollment/recovery behavior and authenticated deduplicated external security-change coverage are unproven. Invitation and recovery delivery intentions remain blocked. No tenant, email, live account, paid resource or production data is provisioned by these files. Release 1 requires the sign-in, provisioning, session-revocation, catalog and request acceptance checks relevant to its exact scope; the full 97-item backlog is not its prerequisite. A local synthetic provider is a separate test tool and never production Auth0 acceptance.

Implementation references: `backend/src/UnitToko.Api/Identity/IdentityRuntime.cs`, `IdentityBrowserSecurity.cs`, `IdentityKeyMaterial.cs` and `IdentityEndpoints.cs`. Official framework behavior: [anti-forgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0) and [trusted forwarding](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

## Frontend security headers and the identity origin

The frontend sends a Content-Security-Policy on every route (`frontend/src/lib/security-headers.ts`). Sign-in, step-up and sign-out are form posts that the API redirects to the identity provider, and Chrome applies `form-action` to that redirect. Set `UNIT_TOKO_IDENTITY_ORIGIN` to the provider's HTTPS origin (for example `https://<tenant>.<region>.auth0.com`) in the frontend build environment. Until it is set, `form-action` allows `'self' https:`.

Next.js computes `headers()` at **build time**. Changing `UNIT_TOKO_IDENTITY_ORIGIN` therefore needs a new frontend build. An enterprise or social connection that redirects through another origin must be added to this value before it is enabled.
