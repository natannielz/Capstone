# Provider security-event local acceptance — 28 September 2026

Scope: ACC01.3 native Auth0 event/log receivers, transactional revocation and admission restrictions. This document does not accept actual tenant delivery, owner recovery, provider reconciliation or production activation.

## Delivered behavior

- Separate disabled-by-default native CloudEvent and JSONARRAY log receivers with distinct rotating bearer secrets, exact HTTPS origin, and no browser-cookie authentication. Exemptions use mapped endpoint metadata; other paths/methods retain Origin and anti-forgery checks.
- Credential verification before sensitive parsing, a 64 KiB raw-body bound, depth/batch limits, exact tenant/stream/target fields, duplicate-property rejection and strict security scalars. Unused metadata fits within the body/depth budget without suppressing an otherwise valid change.
- Native event/log IDs, safe semantic fingerprints and permanent transactional deduplication. Applied, duplicate, ignored and unknown-subject outcomes have the same empty HTTP acknowledgement. Conflicting facts under a reused source ID fail without changing earlier evidence.
- Shared subject/account/session fences, security-epoch increments and session revocation on recognized changes. Block/delete/factor-loss restrictions also cover a previously unknown subject racing its first approved invitation callback. Password/factor changes require primary and factor evidence strictly newer than a monotonic received-time cutoff.
- V5 strict schemas and indexes for three private collections, preserving prior migration history, account/session data and stronger existing validators. Incompatible rows/indexes, expiring retention and capped collections fail preflight. No production migration, account seed or historical hold inference occurred.

## Review findings resolved

Independent reviewers identified and the implementation corrected:

1. Initial account admission had no shared identity row to conflict with an unknown-subject block. A durable subject fence now serializes both paths; the race test starts a real transaction snapshot before pausing.
2. An older pending sign-in proof could inherit the incremented account security revision. Both proof timestamps now must exceed the subject cutoff, including direct session creation; replay and clock regression cannot lower it.
3. Capped/expiring collection settings could invalidate permanent replay protection or transactional writes. Migration preflight now rejects them.
4. Excessive limits on irrelevant profile metadata could reject a valid security event. Eight failing regressions established the issue; recursive duplicate checks and security-relevant/body/depth/batch limits remain after the correction.

HTTP source review found the machine/browser isolation and non-disclosing failures sound. Reviewers made no live-provider claim.

## Verification

| Check | Result |
| --- | --- |
| Full backend regression | 355 cases executed, zero skipped: 354 passed and one stale copied-contract comparison failed; 10m21s |
| Contract artifact refresh | Rebuilt after export; all 14 foundation cases passed, including the sole failed comparison. Source and copied OpenAPI SHA-256 hashes match. No application source change was needed |
| New provider cases | 43 verifier + 9 real-Mongo service/race + 6 HTTP boundary cases passed together (58/58); final mixed-timestamp/clock-regression assertions also passed in the focused service run and full run |
| Migration | 20 new v5 cases and 29 earlier migration regressions passed; all are included in the full regression |
| Existing identity regression | 25/25 focused onboarding/session/sign-in/full-host cases passed before full integration |
| Contracts and frontend | Server export/check and generated-client drift check pass; TypeScript and import boundaries pass; 10/10 affected API/access/CSRF client cases pass under Node 24.19.0 |
| Independent source review | Stale-proof, subject-race, capped-retention and ignored-metadata findings corrected and rechecked; no remaining blocking source finding in this slice |

The full run began before the regenerated OpenAPI file was copied to the test output. The one failing test read that old build artifact. Rebuilding refreshed it, and the targeted 14-case foundation run passed. Thus all 355 distinct backend cases have passing evidence across the full run and targeted recheck; this is not described as one 355/355 green full-suite invocation. The final application source stayed unchanged throughout that correction. No new browser/production-frontend-build acceptance is claimed for this backend-only slice.

Commands: `.tools/dotnet10/dotnet.exe test backend/UnitToko.slnx --no-restore`; then the same SDK with `test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --no-restore --filter FullyQualifiedName~FoundationTests`. The final full and targeted TRX files are `tmp/production/provider-event-results/acc01-provider-full.trx` and `acc01-provider-foundation-refresh.trx`. Focused development results are recorded in `tmp/production/provider-event-service-report.md`; earlier verifier/endpoint RED artifacts remain in the same results directory. All database tests use disposable databases on the guarded loopback replica set, never production records. No hosted CI or actual Auth0 request was performed.

## Remaining gates

- Actual tenant native fixtures for every enabled Dashboard/API mutation, metadata size/depth acceptance, delivery/retention/retry, secret rotation, clock/proxy handling, operational lag/gap response and source replay reconciliation.
- Complete owner verification, recovery-only admission, provider dispatcher, uncertain-outcome reconciliation and an approved release transition. A block/deletion/factor-removal hold has no release endpoint yet.
- Complete connected browser → TLS gateway → maintained OIDC handler → API → disposable Mongo flow. HTTP tests and two store instances are not a multi-host deployment or full browser acceptance claim.
- All remaining ACC02–ACC04 and accounting, catalog/stock, procurement, delivery, invoicing, payment/OCR, after-sales, operations and pilot requirements in the active report plan.

Operational configuration and exact response semantics: [provider-event runbook](../runbooks/provider-security-events.md). Provider capabilities and limitations: [official-source research](../research/2026-09-28-auth0-lifecycle.md). Production remains unavailable by default; local tests never activate the provider or close the overall goal.
