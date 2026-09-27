# Auth0 integration preparation

Status: local adapter and test fixtures; tenant configuration has not been applied or accepted. `Identity:Auth0:Enabled` defaults to false. The application has no fallback issuer, credential login, provider token in browser storage, or legacy authentication bridge. Root application registration and operational acceptance are separate gates.

## Boundary

The confidential ASP.NET Core client uses authorization code with PKCE. The maintained handler validates signing keys, exact issuer/client audience, lifetime, state, nonce and correlation before the explicit completion bridge runs. The callback is the fixed HTTPS origin plus `/auth/callback`, with code-only query response mode. Provider tokens are never returned in browser JSON; the ID token is handed to the encrypted server session store for its narrow logout purpose. No refresh scope or access-token retention is enabled. [Microsoft OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)

`IOidcSignInCompletion` is called from `TicketReceived`, after protocol validation. It must re-read and consume the durable OIDC transaction in the same Mongo transaction as invitation admission and session create/rotation. The root implementation owns the opaque cookie after successful commit. A step-up transaction binds the original user, issuer/subject, session hash/version, security/grant versions, intended permission, allowed destination, and independent challenge nonce. It cannot execute the intended business operation automatically.

OIDC state stores a protected random transaction reference. A separate challenge nonce is a transient authentication parameter and becomes a signed claim only after the configured Actions complete. Mongo stores SHA-256 hashes of both references. Login expires in ten minutes; step-up in five. Expiry checks are independent of TTL cleanup. Identity is the exact issuer/subject pair; normalized email is an onboarding attribute and does not link accounts.

## Versioned Actions

The drafts in `v1/` run in this order:

1. `01-request-otp.cjs`: requires the reviewed client, database connection, exact callback, verified email and code flow. It records transaction metadata and the previous MFA-method fingerprints, then requests OTP challenge or enrollment.
2. `02-assert-otp.cjs`: requires the same transaction binding, a newly completed OTP method with an actual timestamp, and valid policy metadata. It emits the controlled namespaced assurance object in the signed ID token. It does not use the clock, token issue time, enrollment flag, user metadata, or a request flag as factor-completion evidence.

Auth0 documents that challenge requests delay subsequent Actions until completion; the later Action can inspect updated method evidence. Transaction metadata carries the first-stage binding. The strict baseline comparison intentionally denies remembered/previous methods and an enrollment that produces no new completed-method evidence. The real tenant must prove the observed ordering and timestamp shape before acceptance; if its behavior differs, keep the integration disabled and review the contract rather than weakening the check. [Auth0 Action API](https://auth0.com/docs/actions/reference/post-login/post-login-api-object), [factor selection](https://auth0.com/docs/secure/multi-factor-authentication/customize-mfa/customize-mfa-selection-universal-login)

The backend requires native boolean `email_verified`, a numeric `auth_time`, the exact custom object shape, allowed `otp` method, approved connection/policy version, and matching challenge hash. Both authentication time and actual factor time must belong to the current transaction within the documented 30-second server/provider clock tolerance. Fresh-factor age remains separately limited to five minutes. Missing or duplicate claims, strings pretending to be booleans/numbers, weaker recovery factors, changed subject, old methods, and future timestamps deny admission. The custom namespace must use an organization-controlled HTTPS domain outside Auth0's reserved domains. [Auth0 custom claims](https://auth0.com/docs/secure/tokens/json-web-tokens/create-custom-claims)

## Configuration and secrets

`v1/tenant-config.draft.json` is a review artifact, not an importable deployed tenant. Its null issuer/client/connection/region fields require owner decisions. Keep the confidential secret and Action settings in the approved secret manager; never fill them into committed files. The required Action settings are listed in the draft. No invitation email, tenant, application, provider account, Action binding or paid feature was created.

Use a reviewed persistent encrypted Data Protection key ring across API instances. Pin the actual HTTPS origin and issuer; allow only reviewed proxy forwarding and same-origin requests. Login and step-up initiation are POST requests protected by the application's CSRF/origin policy. The fixed GET OIDC callback uses handler state/nonce/correlation checks. The logout destination is fixed to the application root.

## Acceptance remains open

- Owner-approved region, domain/issuer, tenant administrators, secret manager and commercial entitlements.
- Closed signup/invitation behavior, verified email, first enrollment, normal login, remembered browser, primary reauthentication, factor timestamp shape, stage ordering, cancelled challenges, weaker recovery paths and JWKS rotation in the actual tenant.
- Real HTTPS reverse-proxy origin, callback cookies, CSRF, browser storage and multi-instance key-ring/session behavior.
- External password/factor/account-change revocation across Dashboard, API and app paths, including delayed/duplicate/missing events and in-flight business transactions.
- OIDC back-channel logout is documented as Enterprise-only. It does not establish complete coverage for externally initiated factor changes; that security and commercial gate remains unresolved. [Auth0 back-channel logout](https://auth0.com/docs/authenticate/login/logout/back-channel-logout)

Local tests use an isolated TestServer and in-process RSA provider backchannel in the test assembly only. They test this implementation's checks; they do not demonstrate actual Auth0 payloads, entitlement, delivery, callback reachability, or provider acceptance. The Action regression command is `node --test backend/integrations/auth0/v1/actions.test.cjs`.
