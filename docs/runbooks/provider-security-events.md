# Auth0 security-event receivers

Status: local implementation; production activation and actual tenant delivery acceptance remain open. Both transports default to disabled. This receiver does not send mail, change provider accounts, complete owner recovery or release admission holds.

## Transport contract

| Receiver | Required native payload | Recognized changes |
| --- | --- | --- |
| `POST /api/v1/identity/provider-events/auth0-events` | One CloudEvent, `specversion: 1.0`, exact source/tenant/stream, native event ID/time and `data.object.user_id` | `user.updated` with Boolean `blocked: true`; `user.deleted` |
| `POST /api/v1/identity/provider-events/auth0-logs` | Custom Log Stream configured as **JSONARRAY**: `[{log_id, data: {type, date, user_id, ...}}]` | `scp`, `gd_unenroll`, `gd_enrollment_complete`, `gd_update_device_account` |

Use HTTPS through the same approved one-origin proxy as the API. Each stream has its own `Authorization: Bearer <secret>` credential. These are native configured bearer callbacks, not invented signed webhooks. Only the two mapped POST endpoints bypass browser Origin/anti-forgery checks. They never authenticate or refresh an attached browser cookie. Other paths and methods keep browser protections; a matching URL prefix or bearer header cannot grant an exemption.

Events accept `application/json` or `application/cloudevents+json`; logs accept `application/json`. Limits are 65,536 bytes including chunked bodies, JSON depth 16, 100 log entries, bounded security-relevant fields, and exact scalar types. Ignored profile metadata may use the remaining body budget; its long strings or arrays cannot suppress an otherwise valid security event. Duplicate properties at any depth are rejected. Authentication precedes sensitive JSON parsing. Old unseen events are accepted; event timestamps over 30 seconds ahead of the server clock are rejected. Synchronize clocks and alert on validation failures.

The affected account is bound by configured issuer plus the full native `user_id`. Email and `details.auth.subject` are never substitutes; the latter may identify the administrator who performed an action. Unknown subjects cannot create identities, profiles, memberships or approvals. Well-formed unrelated events are acknowledged without changing authority. Unblock/enrollment notifications never release an existing hold.

## Configuration and rotation

Supply `Identity:Auth0SecurityEvents` through reviewed environment configuration and secret storage. Environment variables use double underscores in place of colons.

| Setting | Meaning |
| --- | --- |
| `EventsEnabled`, `LogsEnabled` | Independent opt-in booleans, default false |
| `Issuer` | Exact HTTPS issuer, matching the enabled `Identity:Auth0:Issuer` |
| `EventSource`, `TenantId`, `EventStreamId` | Exact accepted native Event Stream metadata; required when EventsEnabled |
| `EventsBearerToken`, `LogsBearerToken` | Separate cryptographically random current credentials for enabled transports |
| `PreviousEventsBearerToken`, `PreviousLogsBearerToken` | Optional distinct previous credentials during deliberate rotation |

Generate at least 32 random bytes per secret and encode them using a supported bearer format. The startup checks enforce length/format/diversity, not actual entropy. Never commit, paste into diagnostics or reuse credentials across streams/rotation slots. Options are snapshotted by the verifier: rotation is a coordinated deployment, not an unreviewed live mutation. Deploy current+previous, update the provider, verify delivery on all instances, then remove previous in a later coordinated deployment. Real provider configuration requires separate authorization and acceptance.

Apply migration v5 explicitly before enabling either receiver. It adds `identitySecurityEvents`, `identityAdmissionHolds` and `identityProviderSubjects`; it does not backfill accounts or infer holds from old blocked recovery intentions. Preflight rejects incompatible rows, index/validator settings, expiring retention and capped collections before changing validators. Prior stronger validators and v1-v4 history remain preserved.

## Committed effects and retry behavior

Every recognized event has its own Mongo transaction. The transaction deduplicates the native source ID under its issuer, fences the provider subject, advances a known account's security revision, revokes its local sessions, and records a minimal private event row. Same-ID/different-semantic-facts delivery is a conflict. A batch is acknowledged only after all recognized entries commit; after a partial failure the provider can replay the entire batch safely.

Block, deletion and factor removal create a permanent admission restriction and, for a known account, a provenance-bearing user hold. A shared per-subject transactional fence covers the race in which a first invitation callback would otherwise create an account after an unknown-subject block. Password/factor changes also establish a monotonic local-receipt freshness cutoff: both verified primary authentication and factor completion must be later than that cutoff. A delayed event can therefore require another sign-in. Old events never decrease the cutoff or security revision.

The same restrictions are checked during initial/existing admission and session creation/read/rotation. Business commands retain their existing account/session transaction fences. This closes races after the event transaction wins; asynchronous delivery cannot stop a business action already committed before the event arrived.

Replay records, subject restrictions and holds have no TTL. Unknown-subject outcomes stay unknown on replay even if an identity is subsequently linked. Unknown block/delete/factor-removal events retain their subject restriction; unknown password/factor events retain the freshness cutoff. No raw provider payload, subject, email or credential is stored in these three collections. Hashes are private operational identifiers, not public account identifiers.

| Response | Operational meaning |
| --- | --- |
| 204 | Applied, duplicate, unknown subject or unrelated event; no account details disclosed |
| 400 / 403 / 409 / 413 / 415 | Invalid payload, credential/origin rejection, conflicting event ID, oversized body or wrong media type; investigate and recover delivery explicitly |
| 429 | Local request limit reached; inspect load/edge policy and delivery retry behavior |
| 503 | Disabled identity/receiver or uncertain commit; do not declare coverage healthy |
| Other 5xx | Storage/runtime failure; reconcile source delivery before considering the event applied |

Native Event Stream 4xx responses may not be retried. Monitor failure records, disabled streams, receipt lag, schema rejects, clock skew and replay gaps; validate the actual retry/retention behavior in the selected tenant. The local receiver is not an implemented provider replay poller, monitoring service or guaranteed immediate revocation channel.

## Admission holds and owner recovery

No administrative release endpoint is provided in this slice. Do not delete rows, lower revisions, clear subject restrictions or infer recovery completion from a provider unblock, a password event or a newly enrolled OTP. The pending recovery dispatcher and owner-verification journey must authorize an exact reviewed transition before such release is implemented. This operational restriction is one reason production activation remains gated.

Local blocked recovery intentions remain blocked; they are not retroactively converted into provider holds. See [account recovery](account-recovery.md) and [provider lifecycle research](../research/2026-09-28-auth0-lifecycle.md) for the distinction between provider change evidence and legitimate-owner proof.

## External acceptance still required

Accept sanitized real payloads for every enabled Dashboard/API mutation path, exact target fields, native source/tenant/stream identifiers, tier/region entitlement, distinct secret rotation, one-origin TLS/proxy behavior, clock handling, provider retry/retention, outage replay/reconciliation, and operational response to delayed or missing changes. Log Streams remain supplemental because their timeliness and completeness do not establish immediate security-change coverage. OIDC back-channel logout, provider reconciliation, actual email/factor recovery and a complete owner journey remain separate work. Keep release gates open until these are demonstrated.

Official source findings and coverage limitations: [Auth0 lifecycle research](../research/2026-09-28-auth0-lifecycle.md). Local test results: [provider-event acceptance](../quality/provider-event-acceptance-2026-09-28.md).
