# Stock operating-policy service verification — 29 September 2026

Status: locally verified service checkpoint; CAT02/OPS02 and the complete original production plan plus all 97 report requirements remain incomplete.

## Delivered boundary

The new service records an immutable policy definition for one physical stock pool and its configured channels. A proposal freezes the real source settings, shared safety buffer, every mapping, suspension version and prior accepted policy version. The new input identifies the stock writer, exact external tenant/feed interval where applicable, and reconciliation/outage procedure definitions. There is no duplicate editable buffer, invented source observation, accepted external capability or completed handoff.

Explicit stock.source.policy.propose and stock.source.policy.approve permissions have no automatic role-template grants. Approval and rejection require fresh MFA and both independent accounts and independently verified people. Withdrawal belongs to the proposer. Current authority covers the owner and all linked channels, including disabled historical mappings. General history remains separately authorized. Indonesian permission labels distinguish policy review from source setup.

Acceptance requires the source to remain stopped and every reviewed version/fingerprint to match. It records one sequential accepted version without changing settings, physical stock, costs or activation. The source pool contention fence serializes competing review and source changes. Whole-command receipts, decisions, accepted policy and audit records commit atomically; failures at final authorization roll everything back.

Original-actor recovery uses only the saved command receipt and rechecks current action/person authority. A missing receipt cannot create a replacement proposal or approval. Historical definitions remain readable through their original provenance after later legitimate source changes or current person-binding loss, but stale bindings cannot be approved or used as current operating authority.

Three draft collections and six supporting lookup indexes are strictly checked at runtime and enforced in isolated real-Mongo tests. Registered migrations remain at 18; applied migrations 1–18 are unchanged. The service is not exposed through new routes or a staff screen at this checkpoint.

## Evidence and corrections

Artifacts are retained in tmp/production/stock-source-operating-policy-results.

- Initial domain tests reproduced invalid writer/mode/tenant/feed/version inputs before implementation, then 22 passed.
- First service execution passed 23 of 25 cases. The two failures traced to the private independent-review participant record present on approval/rejection receipts. Validation now requires exactly the original participant record; it does not simply permit arbitrary extra receipt fields.
- The next workflow execution found three real database cross-field gaps: web policies accepted an external writer, external tenant or feed interval. All three were reproduced and fixed. One separate fixture incorrectly used an unsupported inactive scope status; it was corrected to the existing suspended status without changing application behavior.
- Four coherent-copy tests reproduced obsolete configuration being accepted at a falsely claimed later review time. The history validator now checks actual settings, mapping and suspension chronology at both proposal and approval. Equal millisecond timestamps do not invent a commit order.
- The 63-case workflow/history run passed after those corrections.
- The 76-case service run passed with zero failures/skips in 2 minutes 16 seconds. It includes two forced competing-reviewer races, a source-change race, four final-authority rollback cases, receipt removal between lookup and replay, independent people, every linked scope, stale reviews, exact participant/audit/receipt provenance, web/external policies and nonempty exact large stock/lot preservation. service-complete.trx SHA-256: 38ef75e59c327b233194aec85f4e268f544de871932791dd08a4ef217c16044d.
- Root review corrected the missing-recovery test to inspect the actual idCounters collection and assert a nonempty baseline. Two final-read revocation tests were added. All three final assertions passed. The affected preexisting source setup/revision/alias/suspension and authorization/person regression passed all 251 cases with zero failures/skips. Combined evidence covers 329 distinct backend cases across 330 executions; the allocator assertion is deliberately rerun. Final results and each TRX hash are saved in final-results.json.
- Frontend verification passed 657 normal, six future-date and seven golden executions: 670 total, zero failures/skips. Type checking, import boundaries, lint and the production build passed. Lint retains nine preexisting warnings. Generated path-only Next declarations were restored after inspection.
- Generated frontend API types match the committed contract. The compiled server OpenAPI contract check also passed unchanged. No new API/browser workflow is claimed for this service checkpoint.

Author review covers the complete owned diff and the original plan boundary. Independent review remains pending unavailable agent capacity; no independent acceptance is claimed.

## Remaining gates

Forward migration 19 must preflight the complete historical graph, both-direction audit/receipt references, indexes, allocators and original participants before any target writes, with renewable leases, majority writes and interrupted-upgrade recovery. Action-specific checker previews, bounded history, strict API/client contracts, dedicated receipt journal, staff workflow, permission/task/MFA discovery and actual browser acceptance follow.

A procedure definition, approved policy or inactive flag does not prove accepted control, completed reconciliation, complete drain, handoff, external hold, stock availability or reactivation. These producers and their owning canonical stock/cost transactions remain required, together with remaining business modules, real Auth0/Mongo/provider configuration, actual business decisions, load/restore/operations evidence and an accepted pilot.

GitHub publication is authorized and must preserve the full existing CI run before a new push. Vercel owning-account access remains unavailable; a prepared archive is not a deployment. No production backend activation, real-data cutover, financial transfer or successful pilot is asserted.