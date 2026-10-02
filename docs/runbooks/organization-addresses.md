# Organization postal addresses

Open **Alamat organisasi** at `/administration/organizations/addresses`. Choose the address purpose, then the approved organization code and name shown for your assignment. An absent unit is not permission to enter its ID manually; ask the organization administrator to check its active hierarchy, approved profile and your assignment.

## Duties and purposes

| Duty | Existing permission | What it permits |
| --- | --- | --- |
| Technical maker | `technical.configure` | Review organization sources and submit an explicit postal proposal. Verified MFA and a current verified-person proof are required; this operation has no additional five-minute factor-age requirement. |
| Independent approver | `organization.approve` | Review and approve or reject an immutable proposal. The checker must be a verified person independent from the maker, with a second factor completed within five minutes. Different usernames alone do not prove independence. |
| Scoped reader | `organization.address.read` | Read postal details and proposal history within the actual assignment. Existing relationship requirements still apply, including the branch PIC marker where required. It grants no proposal, approval or financial powers. |

Administrative maker/checker read access follows their existing governance permissions. Do not add an invalid System grant for `organization.address.read` merely to make the screen visible. A navigation entry is task discovery; each protected request still checks the current session, people, hierarchy and permissions.

| Purpose | Eligible target | Meaning |
| --- | --- | --- |
| `legal_billing` — Penagihan legal | Buying or fulfillment organization | Postal address of the named organization for legal billing. |
| `delivery` — Pengiriman cabang / divisi | Branch or division | Postal destination for that branch or division. |
| `dispatch` — Keberangkatan gudang | Warehouse | Postal origin of that warehouse. |

Postal facts are limited to line 1, optional line 2, city, region, postal code and an explicitly entered two-letter country code. No address or country is inferred. Approval does not verify legal registration, tax identity, recipient identity, telephone, email, OTP channel, bank account, spending authority or stock ownership. The form deliberately contains no recipient/contact fields.

## Propose and decide

1. The maker selects the named unit and purpose, enters the complete address and a reason, then chooses **Periksa rincian pengajuan**. Review every postal field and the shown unit/profile/address versions. Confirm the review checkbox before sending.
2. A submitted proposal leaves the existing approved address unchanged. The proposal, original source and revision 1 remain frozen. To correct its postal facts, submit a new proposal rather than edit the saved proposal.
3. The independent checker opens the proposal, enters a decision reason and reviews approval or rejection. The final review freezes the exact proposal and binding. Confirm the checkbox, then record the decision. The server rechecks independence and current sources at completion.
4. Approval produces a new immutable address version. Reload current data to see the approved address. Rejection closes only that proposal and leaves the approved address unchanged.

New proposals require active hierarchy and approved profile provenance. Approval additionally requires the proposal's source and base address version to remain current. A pending proposal whose source/base version changed can be rejected after current authority and person checks; it cannot be approved.

## Current address versus retained history

**Alamat aktif yang disetujui** means the latest approved address still matches the current organization source. **Alamat terakhir disetujui** with the change warning means the approved postal facts remain historical, but the organization profile or hierarchy changed afterward. The historical address/version remains visible; the target selector does not present it as a usable current address.

For a stale address, the maker must use refreshed unit/profile/address versions and explicitly submit the intended postal facts again. An independent checker must approve the new proposal. Reapproval does not erase earlier versions. A current approved address is only a postal source: downstream order, shipment and invoice admission must also enforce their own permissions and integrity controls. This slice provides the scoped address source adapter; it does not by itself claim those downstream consumers are connected.

## Session, connection and uncertain results

Approval/rejection requires the existing five-minute fresh-factor assurance. Use **Perbarui verifikasi** for `organization.approve` when needed. Task discovery retains the approval entry when the factor ages, while protected reads/decisions continue to enforce assurance. Sessions keep the existing 30-minute idle timeout and eight-hour absolute lifetime; step-up is not an exemption from those limits. Proposals retain the existing `technical.configure` assurance rather than inventing a five-minute rule for that operation.

Offline state disables submissions and decisions. If a reply is lost, do **not** resend or create a replacement request. **Pemulihan permintaan → Periksa hasil** looks up the original operation/key using the original actor's current authorization. Stored recovery metadata contains only actor ID, operation, key and creation time; it contains no address, reason, binding, credentials or proof.

Return to the original account if the browser account changed. A different actor/key cannot recover the original receipt. A missing result is **not confirmed**, not evidence that the command failed; the reference remains and new changes stay locked. Address recovery has no automatic close/resend path. A confirmed result clears the reference; refresh and inspect current state before starting another change.

## Validation boundary

The address workspace, API, migration/source checks and actual browser rehearsal pass their focused local validation; exact evidence and earlier failures are recorded in the [production progress ledger](../quality/production-progress.md). This is local implementation evidence. Browser fixtures use synthetic identities, reviewed participants and postal addresses through real local Next/Kestrel/Mongo paths. They are not approved production operator/address inputs or live provider/deployment acceptance.

Code anchors: [workspace](../../frontend/src/modules/organizations/OrganizationAddressWorkspace.tsx), [strict client and recovery](../../frontend/src/modules/organizations/organization-address-client.ts), [API](../../backend/src/UnitToko.Api/Organizations/OrganizationAddressEndpoints.cs), [address owner](../../backend/src/UnitToko.Platform/Organizations/OrganizationAddressService.cs), [read projections](../../backend/src/UnitToko.Platform/Organizations/OrganizationAddressService.Reads.cs), [scoped source adapter](../../backend/src/UnitToko.Platform/Organizations/OrganizationAddressService.Source.cs), [session policy](../../backend/src/UnitToko.Modules/Identity/Contracts/SessionPolicy.cs), [local browser rehearsal](../../backend/tests/UnitToko.Tests/Organizations/OrganizationAddressBrowserTests.cs).
