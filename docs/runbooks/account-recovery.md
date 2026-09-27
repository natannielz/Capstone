# Account recovery — implementation and activation gates

Status: T03 configured local API and administration screen are implemented. Identity is unavailable by default; provider delivery and owner completion remain blocked. No live tenant was activated.

## Implemented local behavior

An authenticated owner with a fresh OTP factor, or an operator with the explicit recovery permission and required fresh factor, may request password or factor recovery through the local backend service. Authorized operators can review the scoped account and initiate the request from /administration/accounts through the protected API. The request contains an account reference, expected security revision, supported recovery type, idempotency key and audit reason. It never accepts a permanent password or replacement factor secret.

One Mongo transaction checks authority and the expected revision, increments the account security revision, revokes existing local sessions, and writes a blocked encrypted provider intention plus minimal audit evidence. A failed intention write rolls back the entire transaction. Session-backed business commands use the same identity/session fences, so an older pending command cannot commit after revocation. Suspended/revoked accounts are not reactivated.

A blocked intention means only that local recovery initiation was recorded. It does **not** mean Auth0 changed a password or factor, sent a message, or completed owner recovery. Provider delivery and the owner's completion step remain unimplemented activation gates.

## Required before enabling production recovery

1. Accept the configured authentication runtime, protected shared keys, exact origin, same-origin/anti-forgery controls and abuse limits in the actual deployment. Local operator screens exist; complete and verify the recovery-only owner journey.
2. Configure the approved tenant, database connection, mail sender, secret manager, paid entitlements if required, and exact callback/logout destinations. Obtain authorization before provisioning or sending any real invitation/recovery message.
3. Implement a durable provider dispatcher that rechecks the recorded authority, target binding, current account security revision and intention status before an external effect. Handle uncertain provider outcomes with reconciliation; retries must not create multiple valid tickets or reset a newer account state.
4. Use provider-issued, expiring, single-use owner recovery. The owner chooses the new secret in the provider flow. Administrators do not receive or set the owner's replacement password.
5. Prevent remembered SSO, an old factor or a provider event replay from restoring stale authority. Define the account's allowed sign-in state while recovery is pending and prove the provider completion transition before activation.
6. Authenticate and deduplicate provider revocation events; verify coverage for dashboard/API password, factor and account changes. Supplemental logs are not proof of immediate security-event delivery. The planned back-channel entitlement and external factor-change coverage remain unresolved.
7. Revoke old local sessions after relevant provider changes and prove that an in-flight sensitive command aborts. Test owner completion, failed/expired links, repeated events, lost replies and suspended accounts through the actual frontend/API origin.
8. Establish a named, audited exceptional-recovery procedure with independent approval. Do not seed an emergency user or bypass business approval independence.

## Operational handling

Until delivery is enabled, keep intentions blocked and preserve their encrypted payloads. Do not manually send a token, reset a production factor or mark an intention delivered based on a local fixture result. Local synthetic tests use disposable accounts and a loopback-only replica set.

After activation, support should use the audited account directory and current security revision, explain that old sessions will close, record the reason, and track actual provider acknowledgement and owner completion separately. A timeout or uncertain response is not proof of failure; resolve the original operation before issuing a replacement request. The local API and administration screen already reconcile actor-owned operation receipts. An uncertain request locks new mutations; a confirmed retry or receipt clears the completed draft. Changed/expired accounts clear the prior displayed details.

Provider-specific constraints and source links are recorded in [Auth0 integration research](../research/2026-09-28-auth0-integration.md). General key handling, migration, backup/restore and deployment acceptance remain required.

Provider completion and admission-hold constraints: [Auth0 lifecycle research](../research/2026-09-28-auth0-lifecycle.md). Do not activate factor removal until legitimate owner verification is bound to the exact recovery intent; fresh enrollment alone is insufficient.
