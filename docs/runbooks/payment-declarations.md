# Payment declarations and private original uploads

This is the first connected M3 slice. It records private buyer drafts and admits original bytes into quarantine. It does not submit or verify a payment, allocate cash, settle an invoice, approve a refund, or execute a transfer. The scanner, decoder, local OCR, exact evidence association, independent bank review, allocation, and after-sales workflows remain open. Local fixtures do not accept any live provider, host, business policy or production data.

## Buyer workflow

`/billing/payments` requires both current `payment.evidence` and `invoice.read` permissions. The API checks every referenced invoice's actual constituent scopes and retained history. A draft can reference up to 20 distinct invoices belonging to the same buying organization, seller organization/shop and channel. Amounts remain checked decimal strings and BSON Decimal128; Int64 IDs and revisions remain strings across the HTTP boundary.

Only the original recorder can read, edit or recover a draft. Finance and technical administrators do not acquire access to these private drafts from their job titles. Changes append immutable versions with the original recorder, invoice snapshots, prior version/hash, request receipt and audit. Concurrent edits use the expected revision; the losing edit must reload. Missing or damaged prior history blocks reuse rather than recreating it.

Saving without a photo is permitted only while the record remains a draft. Each image reservation binds the payment declaration, current admitted revision, uploader and seller's storage scope. Scope/actor/session checks run again at durable upload transitions and after slow I/O. An upload is not an eligible payment-evidence association. Future submission must pin the exact declaration/evidence versions and satisfy the independent-maker and bank-review rules before any financial effect.

The screen accepts JPG/PNG originals up to 10 MiB. This client check is convenience only: the server treats all bytes as untrusted, imposes its own bounded stream, and applies the approved private-store policy's possibly smaller limit. A completed file is labelled as awaiting security checks. Filename, client MIME, a digest or a successful HTTP response never supplies a clean verdict.

## Unknown results

Draft saves and reservation requests retain only actor, operation, original key and timestamp in the existing browser recovery journal. No image, transfer amount, payer information, reason or credential is written to that journal. Reservation keys include the declaration ID as a recovery reference; this is not authorization. Recovery must still pass the owning API's current checks.

Use **Periksa hasil** after a lost save/reservation response. An absent receipt remains unconfirmed and cannot be closed through the generic access-administration endpoint. Do not resend a changed command under that key or create another reservation merely because a response was lost.

Once the reservation exists, read its durable status. A `fresh` attempt may receive the chosen original. `writing` and `publish_pending` require recovery of that attempt; pending publication can only use its committed exact byte tuple. Upload recovery never reads a new browser file. `interrupted` is incomplete and retains its quota charge. `quarantined` retains the immutable original and its validation request, with scanner/decoder explicitly unavailable. The buyer screen performs a read after uploading, so a lost upload response can be reconciled without another byte upload.

## Explicit host configuration

The API defaults to unavailable private storage. Operators may supply `Evidence:PrivateWindows:Root`, `Evidence:PrivateWindows:StoreId` and `Evidence:PrivateWindows:TrustedOperatorSids` through the approved runtime configuration. Partial settings fail startup. The existing adapter validates the real local NTFS root, ancestors, ownership, ACLs and service rights. Unsupported hosts or unsafe permissions remain unavailable. Configuration does not seed quotas or mark the host production-accepted.

An approved, active `evidenceStorePolicies` record with finite content, capacity, timing and buffer limits is also required. No deployment/request handler creates it. Never put the root under public assets or version control, loosen an existing directory's permissions to make a check pass, or activate it on unaccepted production infrastructure. Follow [private storage](private-evidence-storage.md) and [admission operations](private-evidence-admission.md).

The new byte endpoint has matched endpoint metadata; only that route bypasses the 64 KiB JSON buffer. It still requires the authenticated session, exact origin, CSRF, expected actor and quoted revision. It accepts unencoded `application/octet-stream`; chunked bytes remain bounded. It exposes no private path, generation, claim token or download URL.

## Forward migration and release

1. Rehearse against a restored copy of the actual database. Capture backup, migration history and integrity evidence; verify restore before approving real cutover. Enforce the existing single-writer/maintenance gate while applying DDL.
2. Migration 30 adds declaration heads and immutable versions with strict schemas and unique declaration/revision indexing. It refuses unaccepted legacy `payments`, declaration rows or orphan draft effects. Existing stronger validators and valid allocator high-water marks are preserved. Historical payment conversion requires its own approved input and rehearsal.
3. Migration 31 extends the applied v13 admission rules for the exact payment target/purpose pair. It replaces only the known v13 conjunct, preserving surrounding stronger constraints, other module validators and accepted indexes. It validates the complete prior catalog-evidence graph and counters before changing any target collection. Unknown composition, orphan payment evidence, unsafe retention or damaged history blocks installation. It rewrites no original bytes or business rows and supports interrupted-DDL resumption.
4. Run the normal explicit migration entry point. Confirm the current gate, schemas, uniqueness and unchanged prior history, then exercise the connected draft/upload/recovery flow with an authorized pilot account and approved isolated pilot records.
5. An older binary does not accept newer migration history. Never delete migration receipts or weaken validators to downgrade. Use the approved backup/restore and one-writer rollback process.

No real migration, provider sign-in, storage activation, deployed worker or pilot has been accepted by these local checks. Vercel publication and the actual API/Mongo/Auth0/private-storage connection checks remain release gates; a GitHub push is source publication only.
