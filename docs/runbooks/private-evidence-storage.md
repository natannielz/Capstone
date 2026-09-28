# Private evidence storage — local adapter and operating boundary

Status, 28 September 2026: the internal Windows file adapter has local protocol and permissions verification. It is not registered as production storage and has no public upload or download endpoint. Unsupported hosts and the default adapter return unavailable. Full PAY01 remains open.

## Current behavior

The caller must supply an internal storage attempt only after it has committed reservation, uploader, target, attempt identity and capacity. That durable caller lifecycle is still being implemented. Tokens and generations are server-generated canonical 64-character lowercase hexadecimal values; browser paths, original filenames, URLs and supplied checksums are not storage addresses or integrity proof.

Staging creates a new .part candidate, counts actual input bytes and computes SHA-256 through complete EOF. It flushes and closes that file, reopens and verifies it, then creates and verifies a separate .ready candidate. Neither a suffix nor file existence establishes completion. The source stream remains caller-owned. Empty, oversized, interrupted or mismatched input cannot become sealed evidence; partial bytes remain private and retained.

Publication accepts the frozen expected content tuple from a known successful database commit. It verifies and holds the ready file, creates the original generation with CreateNew, copies within the exact bound, flushes, closes and verifies the result. It does not rely on File.Move, append, overwrite or an exception cleanup delete. A concurrent publisher may receive an unresolved/busy outcome; it must reconcile the same recorded generation.

A lost publication response can be resolved only by inspecting the exact expected generation and matching complete length and hash. A still-unfinalized matching candidate is re-flushed internally before ExistingIdentical is returned, since matching bytes alone do not prove the earlier flush completed. Partial or conflicting candidates remain ineligible and untouched. Never reopen an accepted original through this pending-publication recovery path.

OpenVerified holds the same exclusively opened original that it fully hashes. It exposes a read-only, nonseekable stream bounded to the verified length; no writable handle or raw path escapes. Disposal closes without deleting. This is integrity evidence only: the Documents service must finalize current actor/target/association authorization after slow verification, enforce its own bounded read lease, and emit no bytes after unknown authorization outcomes.

## Observed Windows capability

The adapter accepts an existing absolute root on a local fixed NTFS drive. It checks every ancestor and leaf for links/reparse points and unsafe ownership or access rules, requires protected root inheritance and observed service rights, and checks each held file's ACL. It does not silently repair existing paths or permissions.

The current service identity is trusted. Explicit operator inputs support LocalSystem, built-in Administrators and a specific complete service SID. Broad principals such as Everyone, Authenticated Users, all application packages, all services and domain-wide user groups reject. Arbitrary custom account/group inputs are unsupported until their identity/membership policy is reviewed. This is a host capability constraint, unrelated to website roles or finance authority.

Untrusted content access or namespace mutation rejects. Directory traversal is distinguished from execution of a private file: an untrusted execute-only file permission also rejects. Deny/conditional/object ACE arrangements the verifier does not understand fail closed. A local passing check still assumes trusted service/admin processes do not alter the namespace or accepted generations.

Do not place evidence under web/static assets, source control, shared/synchronized directories or an unverified network/mount location. The production root, filesystem, service identity, permissions, backup/restore behavior and required durability must be separately accepted before runtime activation. No configured path or boolean alone grants that acceptance.

## Caller responsibilities still being implemented

1. Commit the authorized reservation, independent lineage/version-row/attempt IDs, uploader binding and capacity before any external I/O. Account for part, ready and original copies, including every failed or unresolved attempt.
2. Require a known successful commit of the complete-EOF expected tuple before publication. A lost staging result cannot be reconstructed as successful input EOF from a partial file.
3. Reauthorize upload/completion/recovery on the real Catalog family/SKU target, bound to the original uploader. Freeze the immutable original reference and quarantine it in the same transaction as the required audit, job and receipt.
4. Keep storage I/O outside Mongo transaction retries. An unknown database completion preserves bytes and reconciles the original operation; it must not allocate another logical upload or delete the generation.
5. Decode and scan the exact original under enforced resource limits before eligibility. No local hash or clean-looking header substitutes for a decoder or scanner verdict.
6. Pin exact immutable associations, fence reads after slow integrity work, and implement claimed tombstone cleanup with retention/holds/lease checks. This adapter intentionally exposes no deletion API; absence or an expired lease alone never authorizes cleanup.

## Limits and evidence

Finite budgets enforce accepted bytes, bounded buffers and a fixed intended operation deadline. Arbitrary Stream cancellation is cooperative and OS flush is synchronous; a hard kill deadline requires additional isolation. No active I/O is detached and treated as safely stopped. File flushing and process restart recovery do not establish atomic Mongo/filesystem commit or power-loss durability of directory metadata.

Local Windows tests exercise real protected files, concurrent publication, partial/corrupt recovery, byte/deadline/source failures, ACL rejection, ancestor/leaf junctions and held-handle write/delete denial. File symlink creation was unavailable under this host's privileges and is missing coverage; junction coverage does not replace it. Linux and other host activation remain unavailable. Platform-specific tests report explicit skips instead of silently passing an unexercised path.

See [private evidence acceptance](../quality/private-evidence-acceptance-2026-09-28.md), [service contracts](../requirements/private-evidence-contracts.md), and the [complete PAY01 plan](../superpowers/plans/2026-09-28-report-payments-ocr.md). This runbook does not authorize paid provisioning, real-data migration, policy/grant creation or production activation.
