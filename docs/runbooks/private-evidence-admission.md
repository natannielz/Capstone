# Private evidence admission operations

This is operating guidance for the CatalogImage admission service. The `full` profile connects admission to the [catalog-photo workspace](catalog-photos.md); the retained `release1` pilot does not activate those routes. Local verification and outstanding host/deployment acceptance are recorded in the [progress ledger](../quality/production-progress.md). No production policy or service configuration is provisioned by these instructions.

## Interpreting upload outcomes

A reservation records the actual target, original uploader identity, fixed storage attempt and retained maximum capacity before bytes are accepted. A completed upload is **quarantined**. It is not a clean image, a usable product picture, proof of a payment, or verified funds. The validation request is durable. The validation worker and its scanner, decoder and policy must be configured and accepted independently; missing or unavailable dependencies cannot produce a clean verdict.

Raw upload is allowed only for a fresh attempt. Keep the original reservation and request identity through uncertain outcomes. An interrupted or unknown operation must never be retried with a replacement body under the same upload, assigned a new generation, or treated as safely stopped because a lease expired.

For a frozen publish-pending upload, bodyless recovery may reconcile the exact committed content tuple under current original-uploader authority. It cannot reconstruct successful input EOF from leftover files. Unsealed or otherwise unresolved attempts remain unresolved and retain their capacity. A missing receipt is not proof that an operation failed.

Completed retries return the existing exact result after current authorization. Other permitted Catalog staff can obtain safe status but cannot become the original uploader. Host operators do not gain website or financial permissions through storage access.

## Intervention and capacity

Retain partial, ready and original copies through interruption or unknown commit results. Do not manually overwrite originals, remove attempt files, release reserved capacity, change claim fields, forge a receipt, or mark a file clean to clear an error. Cleanup and replacement need their own reviewed retained-evidence contracts and are not implemented here.

Missing, disabled, changed or unobserved storage policy prevents unfinished mutation. Resolve the configured host/policy problem through an authorized operational procedure; a boolean setting alone does not establish safe storage. Only isolated tests currently provision synthetic policies. Filesystem requirements and platform limits are in the [storage runbook](private-evidence-storage.md).

The explicit validation consumer must scan and fully decode the exact immutable original under enforced resource limits, persist provenance and publish a terminal result before any eligibility decision. Scanner absence, outage, timeout or unreadable content must remain explicit outcomes. Local synthetic scanner fixtures do not validate the actual hosted scanner. OCR and human payment review are further independent steps.

Migration v13 runs explicitly with the normal migration command, never on an API request or application startup. Invalid historical shapes, relationships, indexes or lifecycle claims fail before target mutation. Preserve the failing database for diagnosis and correct source data only through an approved migration process; do not bypass validators or fabricate migration history.

See [implementation acceptance](../quality/private-evidence-admission-acceptance-2026-09-28.md) for actual tested scope and [admission contract](../requirements/private-evidence-admission.md) for internal state/signature details. These instructions do not authorize paid provisioning, real-data import or production activation.
