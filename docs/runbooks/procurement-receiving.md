# Supplier arrival and physical container receipt

The `/procurement/receiving` workspace records an actual supplier arrival notice against an approved purchase order, followed by a separate observation of physical containers. OMI regular, DDO, S-Mart and Pasar Kering use the original approved order, including credit and on-receipt terms with no advance payment. The order's approved commercial history remains immutable when its original quote expires.

## Access and source documents

Scoped `procurement.read` permits order, notice and container history. `procurement.prepare` records the supplier notice; the separate `procurement.container.receive` permission records physical custody. Both actions require current five-minute MFA, verified person provenance, active location scope and current grants. One person may perform both if explicitly granted both permissions; this observation does not invent a new independent financial approval.

Select the approved order and record the actual supplier document reference, notice date, declared number of containers and explanatory note. DDO additionally requires the actual request and telephone acknowledgement references. Other routes do not accept those DDO fields. A supplier document may cover more than one purchase order; duplicate protection includes the supplier, location and order. Do not fabricate a supplier acknowledgement or delivery schedule to make an order eligible.

Include `/procurement/receiving` in the explicitly configured Auth0 return-path allowlist before enabling this workspace. Screen links and role templates alone grant no authority.

## Physical receipt and exceptions

Record the actual intake date/reference and observed matched, excess and held containers. Matched containers consume the remaining declared capacity. Excess containers are separately observed and must all be held. Held counts may also include matched containers; they cannot exceed the total physically observed. Opened, damaged or unverified deliveries require all observed containers to be held. Mixed condition requires a positive held count. Explain every excess, hold or non-intact condition.

Partial receipt is permitted. A new observation uses the current capacity revision; competing receipts cannot spend the same declared capacity twice. All received containers remain `pending_inspection`, including intact ones. A container is not a SKU package: this workflow creates no item quantity, sellable stock, cost lot, accounting journal, payable, supplier clearance or source activation. Preserve the original physical custody and discrepancy records for subsequent item inspection.

Review the source and proposed counts before confirming. Notice or intake, capacity, operation receipt, audit and outbox commit atomically. If a committed reply is lost, use **Periksa hasil** with the original account and operation key. Recovery reads the existing outcome without resending the command. Missing or incompatible historical records must fail closed; they must not restore consumed capacity or manufacture a replacement receipt.

## Migration and acceptance

Migration 68 creates empty arrival-notice, container-head and intake stores under the existing one-writer gate. It preserves actual purchase-order allocations and retained payment/refund/procurement settlement ownership, including version-1 and version-2 bank claims and pinned disbursement originals. Incompatible indexes, orphan effects or missing retained owners/proofs block preflight before changes. Compatible stronger operator validators remain enforced. No migration seeds live people, grants, policies, stock or financial facts.

Real deployment requires approved provider access and operational inputs, quiescent writers, verified backup/restore and operator acceptance. See the [progress ledger](../quality/production-progress.md) for the actual implementation and validation state. Local synthetic fixtures and a GitHub push are not production acceptance.

Private unboxing video, item inspection, supplier invoice/OCR matching, supplier claims, unused advances, clearance, stock/source reconciliation and accounting close remain part of the production goal. They must consume the real receipt history and their own approved policies before releasing stock or posting money.
