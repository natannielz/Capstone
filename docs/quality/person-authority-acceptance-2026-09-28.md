# Person authority — local producer and approval consumer checkpoint

Status: locally accepted person-proof producer and shared business approval consumer. The production goal and all 97 report acceptance rows remain open.

## Implemented scope

- ACC03.1 has 43 pure cases for verified distinct people, unknown/disputed/malformed proof, all independently checked approval participants, and pairwise period-close independence. Original account and workflow-revision checks remain in force.
- Explicit deployment migration v8 adds five strict person-authority collections with complete preflight, independent account-review provenance, cross-links, compatible-index/stronger-validator preservation, majority writes, resumability and counter repair. No authority or people are seeded.
- Resolution uses current reviewed source/evidence/binding/equivalence data and complete groups, including historical accounts. Only a private proof retaining each exact record can enter the same transaction's fence. Unknown, disputed, malformed, expired or ambiguous authority fails closed.
- Shared business approvals resolve and fence actor and original historical participants before effects. A private typed receipt preserves the original permission and account/revision evidence; replay checks current person/scope/MFA/session authority without repeating the business write or rechecking its completed revision.
- Final checks cover expiry after business writes, receipt persistence and the last session read. Session, identity and person fences roll back with any denied new effect. A late account equivalence can deny later use without rewriting a completed financial fact.

[Person authority contracts](../architecture/person-authority.md) define exact scope and future writer obligations.

## Verification

The pinned .NET 10.0.401 SDK and real disposable loopback MongoDB replica-set databases were used. No production database or account was touched.

| Check | Evidence |
| --- | --- |
| Pure policy TDD | 28 expected failures against stubs, followed by 57/57 passing (43 new + 14 prior approval cases); person-domain-{red,green}.log/.trx |
| Shared command TDD | Unknown person initially permitted an approval; targeted regression failed before integration: person-store-red.log/.trx |
| Migration and original integration | 24/24 v8 migration cases, 11 initial command cases, four ordinary fences, three existing approvals and four session fences passed in person-registry-integration.trx |
| Current focused run | 164 cases: 163 passed and one older v7 DDL allowlist failed because the registered chain now includes v8. Its expectation was corrected; no application change was needed. person-focused.log/.trx |
| Corrected expectations | 3/3 passed: resumed v7–v8 chain and exact denial-code assertions for ordinary/sensitive permission swaps. person-review-recheck.log/.trx |
| New registry | 38/38 passed in the focused run, including complete graph, malformed types/fields/dates, unversioned changes, inactive historical identity, expiry/clock reversal and binding-insert/equivalence races |
| New approval boundary | 15/15 passed in the focused run, with the two strengthened permission assertions also passing in the recheck |
| Authenticated expiry | 9/9 passed: idle, absolute and fresh-factor deadlines after receipt persistence or the final session read, including replay; effects, receipt and fences roll back |
| API/client contracts | Compiled API export check and generated frontend client drift check passed; no public contract changed |
| Full backend | **715/715 passed, zero failures/skips, 24m46s**; person-full.log/.trx |

Artifacts are under ignored tmp/production/person-authority-results/. The first registry integration attempt failed all 37 registry cases before execution because fixture database names exceeded MongoDB's limit. The fixture names were shortened; the subsequent 38-case run reached and passed the actual behavior. The first migration run had one native _id-array rejection-code mismatch; changing that malformed fixture to a scalar zero tested the intended schema, and the corrected case plus all 24 final migration cases passed.

Independent source reviews covered pure policy, storage/resolution/fencing and command/receipt integration. Review corrections aligned migration and runtime denial of same-person reviewers, added authenticated post-persistence expiry tests, and tightened permission/receipt negative assertions. No frontend application code changed; no new browser, hosted CI, provider, load or pilot acceptance is claimed.

## Remaining gates

ACC03.2–.3 parent steps remain open. Governed source/binding/equivalence proposal and review commands, immutable review history/original person snapshots, exception handling, invitation ambiguity review, and affected-account/session invalidation remain to implement. Source approval and named independent adjudicators are pending D01. The specialized access, membership and invitation services have not yet been wired to this producer.

ACC03.4–.5 finance conflict policy and reviewed role-template migration remain open. The real business modules still need their owning approval commands, accounting rules, audit and end-to-end checks. The shared source fence's throughput needs representative load testing. These fixtures do not constitute operational staff verification, a production payment flow or approved finance policy.

Actual Auth0 configuration/recovery, remaining modules, operations/restore, accepted branch pilot and controlled rollout remain required by the [master plan](../superpowers/plans/2026-09-27-unit-toko-production.md). No live entitlement, provider activation, external message, paid resource, financial transfer or production cutover was performed.
