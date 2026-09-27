# T03 Auth0 integration research

Research date: 28 September 2026. Status: implementation recommendation; provider acceptance remains open. Sources are official Auth0 documentation/support articles and Microsoft documentation checked on this date. No tenant, account, application, invitation, email, subscription or external integration was created or changed during this research.

The user confirmed that there is no existing approved identity provider and selected preparation of the planned Auth0 integration. First launch serves BNI branches and approved business accounts. The November dates remain references; readiness controls launch.

## Recommended boundary

Use Auth0 Universal Login through a confidential ASP.NET Core web client. Keep Unit Toko authorization, approved invitations, memberships, grants and audit in MongoDB. Auth0 supplies authenticated identity and verifiable factor evidence; it does not decide branch, warehouse or financial access. Start with an approved Auth0 database connection and authenticator-app OTP. Do not assume that BNI SSO exists or enable social/federated connections before their identity and assurance behavior is accepted.

The recommendation is conditional on the gates below. In particular, the documented Enterprise restriction on back-channel logout and the unresolved coverage of externally initiated factor changes can affect whether Auth0 satisfies the current plan within the available budget. No paid tier is selected here.

## Code flow and server-held sessions

Microsoft recommends a confidential web client using authorization code with PKCE, and describes a backend-for-frontend boundary for browser applications. Its example uses cookie authentication plus `AddOpenIdConnect`, code response type and unmapped claim names. .NET 9 and later can use PAR automatically when discovery advertises support; verify the actual tenant configuration instead of assuming PAR availability. [Microsoft OIDC web authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)

Implementation contract proposed for Unit Toko:

1. Serve the frontend and API through one public origin, with the backend owning login, callback, session, step-up and logout routes. Pin exact callback/logout destinations and a fixed HTTPS issuer. Permit only local allowlisted return paths.
2. Let the maintained OIDC handler perform code exchange and signature, issuer, audience, lifetime, nonce and correlation validation. Keep PKCE verifier, state and client credentials on the backend. Keep framework claim mapping disabled so claim names remain explicit. Pin signing algorithms and support discovery/JWKS key rotation.
3. Before issuing a business session, apply verified-email, approved invitation/membership, active identity and required-factor gates. No fallback to legacy demo cookies, arbitrary headers, submitted roles or email-derived identity.
4. Use an opaque, rotated session reference in a `Secure`, `HttpOnly`, host-only cookie. Keep all provider tokens and identity tickets in durable backend storage. Do not put tokens in frontend storage, JSON responses, URLs or logs. Avoid `offline_access` initially unless a specific server operation needs refresh tokens.
5. Protect state-changing browser routes, including logout and step-up initiation, with the existing session plus anti-CSRF validation. Retain the OIDC handler's necessary correlation/nonce cookie behavior rather than globally forcing a SameSite setting that breaks callback processing.

`CookieAuthenticationOptions.SessionStore` accepts an `ITicketStore`; when configured, only a session identifier is sent to the client. A plain `SaveTokens=true` sample is insufficient for our storage requirement without a server-side ticket store. Use a Mongo-backed implementation or an equivalent explicit opaque-session service, with encrypted ticket/token payloads and a protected, shared key ring. [Microsoft SessionStore API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.cookies.cookieauthenticationoptions.sessionstore?view=aspnetcore-10.0)

Cookie validation must observe backend revocation. Microsoft documents `ValidatePrincipal` for reacting to backend identity changes, and shared Data Protection keys/application identity for multiple instances. Its .NET 10 API behavior returns 401/403 instead of redirecting known API endpoints to login. [Microsoft cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0)

Local session design: explicit `createdAt`, `lastUsedAt`, absolute/idle expiry, revoked status, issuer, subject, provider `sid` when supported, immutable local user ID, security version and validated assurance evidence. Enforce the proposed 30-minute idle and 8-hour absolute bounds on retrieval; database TTL cleanup is not an expiration check. Cookie renewal must not reset absolute lifetime or factor freshness. Keep tokens separately encrypted and limit retention to their actual purpose.

## Identity, verified email and approved invitations

Use the validated `(iss, sub)` pair as the unique provider binding to a BSON Long internal user ID. Treat `email` as an attribute and `email_verified` as a required boolean claim for onboarding, never as the identity key or a business entitlement. Auth0 notes that email verification is imperfect; links can be activated by scanners, and some external-provider flows can overwrite verification state. Email OTP verification has additional tenant configuration prerequisites. [Auth0 email verification](https://auth0.com/docs/manage-users/user-accounts/verify-emails)

Auth0 Organization invitations require the acceptance route to forward both `invitation` and `organization` to authorization. The invited email must match the account used; successful acceptance marks the email verified. Invitation availability depends on the plan and login implementation. These provider mechanics do not replace Unit Toko approval. [Auth0 Organization invitations](https://auth0.com/docs/manage-users/organizations/configure-organizations/invite-members)

If Organizations are adopted, persist the expected provider organization ID in server-side login state and validate returned `org_id`; Auth0 recommends organization IDs for token validation. A query parameter, organization display name or email domain is not sufficient. Model branches/divisions locally; do not automatically create an Auth0 Organization for every branch. The exact provider-to-business organization mapping remains an acceptance decision. [Auth0 organization-aware development](https://auth0.com/docs/manage-users/organizations/custom-development)

Proposed local invitation record and acceptance transaction:

- Store a hashed one-time secret/reference, intended normalized email, approved business organization/scope, requested grant proposal references, expiry, status, approver and audit reason. If Auth0 creates an invitation, also bind its ID/ticket reference and expected organization/connection. Never log the raw link.
- At callback, require a validated provider identity, exact intended-email match under one documented normalization policy, verified email, required MFA and the same invitation/state binding. Reject expired, revoked, replayed, wrong-organization and wrong-invitee attempts.
- Atomically consume the invitation, create/link only the approved provider binding and activate only approvals that remain valid. Recheck current scope and approver/grant constraints; login cannot invent approval or bypass maker/checker rules. Concurrent redemption must have one winner.
- Never automatically merge existing users by email. A second provider subject requires a separately authorized linking process proving control of both identities.

The Organization invitation API defaults to sending email; any future review-only generation must explicitly disable that behavior. Its documented TTL defaults to seven days and caps at thirty days; choose a shorter explicit project expiry where appropriate. A least-privilege M2M client needs `create:organization_invitations` for this operation, independently of the interactive web client. [Auth0 invitation API](https://auth0.com/docs/api/management/v2/organizations/post-invitations)

**Open signup is a real configuration gate.** Disabling signup on a database connection can also prevent invited users from creating accounts. Official support describes both a guarded organization-signup approach and a pre-provisioned-user/password-ticket alternative; they must be tested against the chosen login configuration. Do not copy the illustrative invitation-list scan as a complete authorization check. [Auth0 restricted signup support](https://support.auth0.com/center/s/article/Allow-to-sign-up-only-users-who-were-invited-to-an-organziation), [Auth0 invitation flow limitations](https://support.auth0.com/center/s/article/Is-it-possible-to-implement-an-invitation-flow-using-Auth0-organizations-while-disabling-sign-up-on-a-connection)

For a strict closed pilot, prefer proving an admin-approved, pre-provisioned database-account flow with public signup disabled before considering any organization signup exception. Auth0 documents owner-set passwords via password-change invitation tickets, with newly created users initially unverified. Keep the local invitation and verified-email gates separate; the provider's presentation metadata is not acceptance evidence. This is a candidate fallback, not a claim that its entire lifecycle has passed. [Auth0 application invitation workflow](https://auth0.com/docs/customize/email/send-email-invitations-for-application-signup)

## MFA evidence and freshness

| Evidence | What can be accepted | What it cannot establish |
| --- | --- | --- |
| Validated `iss`, `sub`, `aud`, lifetime and nonce | Identity and intended OIDC transaction | Approved membership or financial authority |
| `email_verified=true` | Provider's email-verification assertion | Immutable identity or entitlement |
| `amr` array containing `mfa` | MFA occurred in the issuing login context | Factor type or time within the last five minutes |
| `auth_time` | Provider authentication time, checked against requested reauthentication | Actual second-factor completion time |
| Requested `acr_values` | Input requesting a stronger flow | Proof that the requested flow succeeded |
| Signed custom factor claims from a controlled Action | Allowed completed factor and its recorded completion time | Freshness unless backend checks timestamps and binds the step-up request |
| New cookie issue time, ID-token `iat`, refreshed token | New artifact issuance | New authentication or fresh MFA |

Auth0's web step-up documentation uses `amr` containing `mfa`, and explicitly notes that silent authentication or refresh-issued ID tokens can omit `amr`. Do not interpret a newly issued token as refreshed MFA. The documented `acr_values` URI is a request signal consumed by an Action, not a server-side authorization result. [Auth0 web step-up](https://auth0.com/docs/secure/multi-factor-authentication/step-up-authentication/configure-step-up-authentication-for-web-apps)

`prompt=login` alone cannot prove reauthentication. Auth0 documents checking `auth_time` with `max_age`, and explains that an upstream identity provider may still reuse its own SSO session. Therefore `max_age=0` plus a recent `auth_time` is not sufficient evidence of a fresh second factor. [Auth0 reauthentication](https://auth0.com/docs/authenticate/login/max-age-reauthentication)

Auth0 describes `event.authentication.methods` as methods completed during the user's session. That history must not be treated as automatically belonging to the current transaction. `event.user.last_password_reset` is database-connection-specific and is not a universal password/factor revocation feed. [Auth0 post-login event reference](https://auth0.com/docs/actions/reference/post-login/post-login-event-object)

Use the newer `api.authentication.challengeWith({type: 'otp'})`/enrollment methods rather than deriving evidence in the same Action that requests MFA. The API waits for a challenge before subsequent Actions run; already completed challenges in the current transaction can be ignored, and invalid/unavailable factors can cause failure. `recordMethod` records a custom authentication method after a redirect; it must not be used to manufacture an OTP assertion. [Auth0 post-login API reference](https://auth0.com/docs/actions/reference/post-login/post-login-api-object)

Auth0 documents that `methods` is refreshed at the start of the next Action, with `name: 'mfa'` and a factor `type`. The tenant must use Universal Login and enable customization of MFA factors using Actions. Set a tenant MFA policy that still requires MFA if the custom Action fails to run. The flow permits only four combined enroll/challenge commands. [Auth0 customized factor selection](https://auth0.com/docs/secure/multi-factor-authentication/customize-mfa/customize-mfa-selection-universal-login)

An official support article shows completed method objects containing `name`, `timestamp` and, for MFA, `type`. It explains why legacy `api.multifactor.enable` can leave first-login MFA absent from the Action event: that challenge happens after the Action pipeline. [Auth0 first-login MFA evidence](https://support.auth0.com/center/s/article/using-actions-mfa-authentication-method-is-missing-on-first-login)

Proposed two-stage Action contract and backend enforcement:

1. An Action restricted to the reviewed client/connection requests OTP enrollment or challenge. Use an explicit allowed-factor list. Missing factors, unsupported flows and failed challenges deny access; do not bypass MFA on refresh or noninteractive paths to make them work.
2. A later Action inspects the completed `mfa` method and actual timestamp, then emits namespaced claims such as `mfa_at`, `mfa_method` and `assurance_policy_version`. Do not substitute current time, token issue time, user-editable metadata, `enrolledFactors` or a request flag. Enrollment counts only if the tenant produces acceptable completed-factor evidence.
3. Backend parsing accepts the controlled namespace, expected policy version, allowed factor and plausible timestamp only after normal ID-token validation. Begin with OTP as `MultiFactor`; do not label OTP phishing-resistant. Recovery-code, email or SMS fallback must not silently satisfy privileged OTP policy. Actual recovery claim behavior is a tenant test gate.
4. For step-up, create a short-lived, one-use server record bound to current local session, user, issuer, subject, intended operation and start time. Request interactive reauthentication and the controlled factor challenge. Require returned identity to match and actual factor time to be at or after request start, allowing only a small documented clock skew; reject old history and silent-SSO reuse. Then require factor age at the sensitive operation to be at most five minutes. Rotate the session without extending absolute lifetime.
5. A missing claim, disabled Action, stale policy version, cancelled challenge or malformed/future timestamp denies business capability. Do not trust frontend statements that step-up succeeded. Fresh MFA changes assurance only; it never grants permissions or silently executes a pending business command.

Use a namespace under an organization-controlled domain; Auth0 restricts reserved claims and recommends collision-resistant custom namespaces. The placeholder names above do not designate a purchased domain or deployed Action. [Auth0 custom claims](https://auth0.com/docs/secure/tokens/json-web-tokens/create-custom-claims)

Current code uses `ActorContext.AuthenticatedAt` for freshness. T03 should explicitly define it as the validated factor-completion time for an MFA actor, or split provider authentication time and factor time into separate fields. Persist both in session evidence for clarity. Never feed raw `auth_time` or cookie creation time into the current five-minute MFA policy.

## Logout, recovery and revocation

Application logout must immediately revoke the local session, then perform the configured provider logout. Password/factor recovery initiated through Unit Toko should revoke affected local sessions before requesting provider changes. Recovery lets the owner choose the new secret; an administrator never chooses, sees or receives a permanent password.

Auth0 MFA reset removes enrollment and requires enrollment again on the next login. Recovery-code use produces a replacement recovery code. These behaviors do not by themselves prove that existing Unit Toko sessions were revoked. [Auth0 MFA reset and recovery](https://auth0.com/docs/secure/multi-factor-authentication/reset-user-mfa)

### Back-channel logout

Auth0 documents OIDC back-channel logout as Enterprise-only. It supplies provider `sid` for association with local sessions, and delivery is asynchronous. Its token example contains `iss`, `sub`, `aud`, `iat`, `exp`, `jti`, `sid` and the back-channel `events` member. [Auth0 back-channel logout](https://auth0.com/docs/authenticate/login/logout/back-channel-logout)

The callback must receive POSTed `logout_token`, validate signature with rotating tenant keys, trusted issuer/client audience, lifetime, the expected `events` object, and `sid`/`sub`; a `nonce` must be absent. Auth0 documents a two-minute token lifetime, five-second callback timeout, and no retry for a 400 validation response. The discovery flags must be checked in the real tenant. [Auth0 back-channel configuration](https://auth0.com/docs/authenticate/login/logout/back-channel-logout/configure-back-channel-logout)

Configured initiators can cover RP/IdP logout, password change, session expiry/revocation, account deletion and email-identifier change. Additional initiators are not all enabled by default; they react to session termination rather than causing it. **Factor reset/enrollment change is not listed.** The older configuration page's broad troubleshooting statement that only standard logout triggers delivery should not override this dedicated initiator configuration; test the configured events individually. [Auth0 logout initiators](https://auth0.com/docs/authenticate/login/logout/back-channel-logout/oidc-back-channel-logout-initiators)

Proposed receiver rules: match issuer and known session/subject bindings; never revoke by email or unvalidated body fields. Deduplicate `jti` durably and make valid duplicate delivery idempotent. Atomically record the event and revoke the targeted session(s) before acknowledging successful logout. Keep processing below the provider timeout, return a retryable failure for transient persistence errors, monitor failures, and explicitly test logout/callback races and late delivery. A revocation event must never reactivate an account.

### Other authenticated events

Auth0's database-connection `post-change-password` Action is an asynchronous, non-blocking trigger intended to notify other systems. Its sample notification is not a production authentication protocol. For a project-owned notification, use an authenticated, replay-protected envelope with immutable subject, occurrence time and unique ID; test every password-change path, including administrator/API changes. Failure cannot roll back the provider password change. [Auth0 post-change-password trigger](https://auth0.com/docs/customize/actions/explore-triggers/post-change-password)

Log categories include `gd_unenroll`, `gd_update_device_account`, `gd_enrollment_complete`, password change `scp`, and Management API `sapi`. These are candidates for audit/reconciliation, not proven coverage of every factor mutation. [Auth0 log filters](https://auth0.com/docs/customize/log-streams/event-filters) Auth0 explicitly advises against Log Streams for critical-path or real-time decisions. **Do not use log delivery as the sole immediate revocation guarantee.** [Auth0 Log Streams limitations](https://auth0.com/docs/customize/log-streams)

A custom Log Stream can send a configured Authorization header over a webhook. Configure a dedicated high-entropy credential, validate it in constant time over HTTPS, rotate it, bound payload size and deduplicate log IDs. This is bearer authentication; the documented feature does not establish a per-body HMAC signature. [Auth0 custom log webhooks](https://auth0.com/docs/customize/log-streams/custom-log-streams)

Auth0 Event Streams separately document bearer-authenticated webhook delivery. Their current catalog covers users, organizations, groups and connections; it does not list a dedicated factor-change event. The `user.updated` example includes multifactor profile data, but that is not a promise that every enrollment/reset emits this event. Verify exact schemas, release/plan availability, source tenant and stream identity with the real tenant. [Auth0 event webhooks](https://auth0.com/docs/customize/events/create-an-event-stream), [Auth0 event catalog](https://auth0.com/docs/events), [Auth0 user.updated schema](https://auth0.com/docs/events/user/user.updated)

Event deliveries may duplicate or arrive out of order. Current documentation describes four attempts including the initial delivery, no retry on 4XX, and automatic disabling of failing streams. Persist a durable inbox before 2XX, monitor delivery health and implement reconciliation/redelivery; do not equate a configured webhook with guaranteed delivery. [Auth0 event delivery behavior](https://auth0.com/docs/customize/events/events-best-practices)

**Unresolved security gate:** externally initiated Dashboard/API factor changes need a verified authenticated event or another independently proven mechanism within the accepted revocation bound. Neither ordinary logout initiators nor the generic event catalog proves this. Short session expiry and fresh MFA help bound exposure but do not fulfill immediate revocation by themselves. Keep this acceptance item open; do not silently weaken the requirement to fit a cheaper plan.

A concurrency detail for implementation: revoking a session at HTTP-request entry is insufficient if a sensitive transaction is already in flight. Update the identity's shared authorization fence plus a version that the transactional actor guard actually checks. Either deliberately advance the existing identity `grantVersion` on security revocation, or introduce an explicit security version/session guard through `ActorContext` and the store. Incrementing only the fence can cause a retry that still accepts the same stale actor. The retry must reread current revocation/version state. Local sessions, identity/fence changes and immutable audit should commit together; provider API follow-up can use the outbox.

## Region, entitlement and operational gates

Auth0's current public-cloud region table lists Australia, Canada, Europe, Japan, United Kingdom and United States. It does not list Indonesia or Singapore. The chosen region controls hosting locality; sub-locality cannot be selected manually. Tenant names are immutable and cannot be reused after deletion. Separate development, staging and production tenants are recommended by the vendor. Obtain the owner's region/data-handling decision before creating them. [Auth0 tenant regions](https://auth0.com/docs/get-started/auth0-overview/create-tenants)

| Gate | Required evidence before provider acceptance |
| --- | --- |
| Tenant and regional approval | Owner-approved region, production domain/issuer strategy, environment isolation and responsible tenant administrators. No tenant credentials assumed. |
| Commercial entitlement | Confirm OTP, required Action commands/customization, Organizations if used, invitations, back-channel logout, event delivery, log retention, quotas, custom domain and email delivery under the actual plan. Back-channel logout is documented Enterprise-only; do not purchase automatically. |
| Adaptive MFA | Not required by this design. Auth0 documents Enterprise plus an add-on; risk-based MFA must not replace mandatory privileged MFA. [Adaptive MFA prerequisites](https://auth0.com/docs/secure/multi-factor-authentication/adaptive-mfa) |
| Closed onboarding | Prove direct signup is blocked, approved invitation redemption works, default organization membership cannot be acquired by guessing an ID, and no business session exists before email/MFA/approval gates. |
| Claims | Record redacted actual ID-token and Action event shapes for first enrollment, normal login, existing SSO, remembered browser, explicit step-up, recovery and refresh. Establish exact signed claim types and timestamp semantics. |
| Revocation | Demonstrate local logout and provider logout; password reset/change; factor add/remove/reset; account disable/delete; grant removal; retries, delayed events and concurrent sensitive commands across at least two API instances. |
| Delivery and recovery | Verify authenticated event coverage and maximum accepted delay; alarm/redelivery/reconciliation procedures; recovery ownership, replacement-code behavior and secure operator access. |
| Real origins | Test HTTPS reverse proxy, callback cookies, CSRF, callback/logout allowlists, forwarded headers, return URL rejection and browser storage against the planned frontend/API origin arrangement. |
| Secrets and operations | Approved secret manager, encrypted shared ticket store and key-ring persistence/rotation, least-privilege M2M scopes, sender/domain configuration, rate limits and token-free logs. |

## Implementation sequence and acceptance evidence

Local work can proceed without claiming provider success:

1. Define provider/session/invitation/event contracts and migrations, keeping the production actor resolver unavailable until configuration and gates are satisfied.
2. Implement the opaque-session store, lifecycle/version fencing and callback mapping behind those contracts. Add tests for time bounds, concurrent revocation, malformed claims, state replay and invitation redemption races.
3. Implement same-origin login/logout/step-up routes, CSRF and invitation/recovery UI with a provider adapter. Keep fake provider fixtures isolated to test hosting; they cannot enable production login.
4. Prepare versioned Auth0 Action/configuration drafts, expected claims and a tenant acceptance script. Review permitted scopes and destinations before applying any external changes.
5. When tenant access and operational choices are supplied, configure a development tenant and run the matrix below. Record actual evidence separately from local fixture tests; only then consider provider acceptance and subsequent production setup.

| Scenario | Required result |
| --- | --- |
| Valid credentials, missing/false email verification | No transaction-capable session |
| Verified provider user without approved membership | No organization access |
| Wrong invitee/org/connection, expired/revoked/replayed invitation | No binding or grant created |
| Missing or malformed custom MFA proof, disabled claim Action | No business session or privileged continuation |
| First enrollment versus an existing enrolled user | Actual permitted factor evidence obtained in both cases |
| New cookie/token with old factor time; silent/remembered SSO | No fresh-MFA authorization |
| Explicit step-up cancelled, subject swapped, state replayed | No assurance upgrade |
| Fresh OTP but permission revoked during request | No stale business commit |
| Password or factor change from app, Dashboard and API | Old local sessions unusable within the accepted bound; pending sensitive writes fenced |
| Duplicate/out-of-order/forged event; unknown tenant; expired logout JWT | No unauthorized state change or reactivation; valid duplicates safe |
| Event endpoint unavailable, stream disabled, key rotated | Observable failure, recovery path and fail-closed behavior for affected security guarantees |
| Logout in one browser/device | Correct intended session scope; global revocation only when explicitly requested or required |
| Recovery code or weaker fallback used | Recovery policy applied; no accidental privileged OTP assurance |

Documentation research cannot verify tenant entitlements, actual token/Action payloads, callback reachability, notification delivery, administrative event coverage, upstream federation behavior or production operating cost. Those gates remain open. This report supports implementation preparation; it is not a provider acceptance or launch approval.
