import { parsePaymentAllocationResult, type PaymentAllocationResult } from "../billing/payment-allocation-client";
import { parsePaymentAllocationCorrectionResult, type PaymentAllocationCorrectionResult } from "../billing/payment-allocation-correction-client";
import { parsePaymentVerificationResult, type PaymentVerificationResult } from "../billing/payment-verification-client";
import { parseBankReceiptResult, type BankReceiptResult } from "../billing/bank-receipt-client";
import { parseStatementResult, type StatementResult } from "../billing/bank-statement-client";
import { validateReceivingReceipt, type ReceivingResult } from "../billing/receiving-account-client";
import { parsePaymentCorrectionResult, paymentCorrectionOwner, type PaymentCorrectionResult } from "../billing/payment-correction-client";
import { parseManualReviewResult, type PaymentManualReviewResult } from "../billing/payment-manual-review-client";
import { parseReviewActionResult, type PaymentReviewActionResult } from "../billing/payment-review-action-client";
import { parseInvoice, type Invoice } from "../billing/invoice-client";
import { parseCaseResult, parseCaseDecisionResult, type CaseResult, type CaseDecisionResult } from "../fulfillment/case-client";
import { parseReturnResult, type ReturnResult } from "../fulfillment/return-client";
import { parseInspectionResult, type InspectionResult } from "../fulfillment/inspection-client";
import { parseResolutionResult, type ResolutionResult } from "../fulfillment/resolution-client";
import { parseReturnEntitlementResult, type ReturnEntitlementResult } from "../fulfillment/return-entitlement-client";
import { parseReturnFinancialApprovalResult, type ReturnFinancialApprovalResult } from "../fulfillment/return-financial-approval-client";
import { parseRefundPayoutDraftResult, parseRefundPayoutResult, refundPayoutOwner, type RefundPayoutDraftResult, type RefundPayoutResult } from "../fulfillment/refund-payout-client";
import { parseRefundSettlementResult, type RefundSettlementResult } from "../fulfillment/refund-settlement-client";
import { parseOrganizationAddressResult, type OrganizationAddressResult } from "../organizations/organization-address-client";
import { parseSpendingPolicyResult, type SpendingPolicyResult } from "../ordering/order-spending-policy-client";
import { parseAccountingBookResult, type AccountingBookResult } from "../accounting/accounting-book-client";
import { parseProcurementResult, type ProcurementResult } from "../procurement/procurement-request-client";
import { parseSupplierResult, type SupplierResult } from "../procurement/supplier-client";
import { parsePurchaseOrderResult, type PurchaseOrderResult } from "../procurement/purchase-order-client";
import { parseFundingResult, type FundingResult } from "../procurement/procurement-funding-client";
import { parsePaymentResult, parsePaymentReservation, parsePaymentSubmissionResult, paymentSubmissionOwner, parsePaymentReplacementResult, type PaymentReplacementResult, type PaymentSubmissionResult, type PaymentDraftResult, type PaymentReservation } from "../billing/payment-client";
import { parseDeliveryAttempt, type DeliveryAttempt } from "../fulfillment/delivery-attempt-client";
import { parseReceiptProof, type ReceiptProof } from "../fulfillment/receipt-proof-client";
import { parseReceipt, parseReceiptChallenge, type Receipt, type ReceiptChallenge } from "../fulfillment/receipt-client";
import { validReference } from "../organizations/access-scope";
import { AccessFailure, MutationLedger } from "./workflow";
import { priceAction, priceOutcome, type PriceResult } from "../catalog/catalog-price-workflow";
import { priceRecoverySku } from "../catalog/catalog-price-recovery";
import { parseDraftResult, type DraftResult } from "../ordering/order-draft-client";
import { parseOrderSubmitResult, type OrderSubmitResult } from "../ordering/order-submit-client";
import { orderDecisionSelection, parseOrderDecisionResult, type OrderDecisionResult } from "../ordering/order-review-client";
import { parsePreparationResult, type PreparationResult } from "../fulfillment/preparation-client";
import { parseDispatchResult, type DispatchResult } from "../fulfillment/dispatch-client";
import { parseShipmentDocument, type ShipmentDocument } from "../fulfillment/shipment-document-client";

export type OperationReference = Readonly<{ actorId: string; operation: string; key: string; createdAt: string }>;
type JournalStorage = Pick<Storage, "length" | "key" | "getItem" | "setItem" | "removeItem">;
const prefix = "unit-toko:pending:v1:";
const keyPattern = /^[A-Za-z0-9._:-]{1,128}$/;
const operationPattern = /^(?:procurement\.funding\.(?:request|approve|reject)|procurement\.purchase\.order\.(?:propose|approve|reject)|procurement\.supplier\.(?:propose|observe|approve|reject)|procurement\.request\.(?:submit|approve|reject)|accounting\.book\.(?:propose|approve|reject)|billing\.refund\.payout\.(?:draft|record|verify)|evidence\.refund-payout-image\.reserve|billing\.return\.financial\.approve|billing\.return\.entitlement\.request|billing\.bank-receipt\.confirm|billing\.bank-statement\.stage|billing\.receiving-account\.(?:propose|observe|approve|reject|withdraw)|billing\.payment\.(?:draft\.save|replacement\.create|submit|verify|allocate|allocation\.correct|review\.(?:correct|manual|manage))|evidence\.payment-image\.reserve|billing\.invoice\.issue|(access|organization)\.[a-z.]{1,100}|identity\.(?:invitation\.(?:propose|approve|rejected|withdrawn|revoked)|recovery\.request)|person\.review\.(?:propose|approve|reject|withdraw)|fulfillment\.(?:case\.(?:request|decide|return_receive|return_inspect|resolution_request)|preparation\.save|shipment\.dispatch|surat-jalan\.issue|delivery-attempt\.record|receipt-proof\.issue|receipt\.(?:challenge\.issue|confirm))|ordering\.(?:spending\.policy\.(?:propose|approve|reject)|draft\.save|order\.(?:submit|cancel|spending\.(?:approve|reject)|store\.(?:accept|reject)))|catalog\.(?:price\.(?:propose|approve|reject|withdraw)|category\.save|family\.(?:create|edit)|sku\.(?:create|revise|replace|retire|publish))|inventory\.stock-count\.record|stock\.source\.(?:suspend|(?:(?:revision|alias|policy)\.)?(?:propose|approve|reject|withdraw)))$(?![\s\S])/;
const storageKey = (entry: OperationReference) => `${prefix}${entry.actorId}:${entry.operation}:${entry.key}`;

/** Only recovery references are stored. Requests, reasons, grants and credentials never enter this journal. */
export class OperationJournal {
  constructor(private readonly storage: () => JournalStorage, private readonly now: () => Date = () => new Date()) {}

  list(actorId: string): OperationReference[] {
    return this.withStorage(storage => {
      if (!validReference(actorId)) throw new Error("Invalid actor reference");
      const keys = Array.from({ length: storage.length }, (_, index) => storage.key(index))
        .filter((key): key is string => key !== null && key.startsWith(`${prefix}${actorId}:`));
      return keys.map(key => {
        const value: unknown = JSON.parse(storage.getItem(key) ?? "null");
        if (!validEntry(value) || value.actorId !== actorId || storageKey(value) !== key) throw new Error("Invalid recovery reference");
        return value;
      }).sort((left, right) => left.createdAt.localeCompare(right.createdAt));
    });
  }

  remember(actorId: string, operation: string, key: string): OperationReference {
    return this.withStorage(storage => {
      const existing = this.list(actorId);
      const previous = existing.find(item => item.operation === operation && item.key === key);
      if (previous) return previous;
      if (existing.length >= 32) throw new Error("Unresolved request limit reached");
      const entry = { actorId, operation, key, createdAt: this.now().toISOString() };
      if (!validEntry(entry)) throw new Error("Invalid recovery reference");
      const serialized = JSON.stringify(entry);
      storage.setItem(storageKey(entry), serialized);
      if (storage.getItem(storageKey(entry)) !== serialized) throw new Error("Recovery reference was not retained");
      return entry;
    });
  }

  confirmed(entry: OperationReference) {
    this.withStorage(storage => {
      if (!validEntry(entry)) throw new Error("Invalid recovery reference");
      storage.removeItem(storageKey(entry));
      if (storage.getItem(storageKey(entry)) !== null) throw new Error("Recovery reference could not be cleared");
    });
  }

  private withStorage<T>(action: (storage: JournalStorage) => T): T {
    try { return action(this.storage()); }
    catch { throw new AccessFailure(400, "recovery_storage_unavailable"); }
  }
}

function validEntry(value: unknown): value is OperationReference {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const entry = value as Record<string, unknown>;
  return Object.keys(entry).sort().join(",") === "actorId,createdAt,key,operation" &&
    typeof entry.actorId === "string" && validReference(entry.actorId) &&
    typeof entry.operation === "string" && operationPattern.test(entry.operation) &&
    typeof entry.key === "string" && keyPattern.test(entry.key) &&
    typeof entry.createdAt === "string" && entry.createdAt.length <= 30 && Number.isFinite(Date.parse(entry.createdAt));
}

export async function submitTrackedRequest<T>(ledger: MutationLedger, journal: OperationJournal,
  actorId: string, logicalOperation: string, operation: string, payload: unknown, send: (key: string) => Promise<T>): Promise<T> {
  const key = ledger.begin(logicalOperation, payload);
  let entry: OperationReference | undefined;
  try {
    // Storage failure stops the command before it reaches the server.
    entry = journal.remember(actorId, operation, key);
    const result = await send(key);
    ledger.complete(logicalOperation);
    // A cleanup failure must never turn a confirmed command into a retry.
    try { journal.confirmed(entry); } catch { /* The remaining reference can be reconciled on reload. */ }
    return result;
  } catch (error) {
    ledger.fail(logicalOperation, error);
    if (entry && !ledger.isUncertain(logicalOperation)) {
      try { journal.confirmed(entry); } catch { /* Retain the reference rather than masking the original failure. */ }
    }
    throw error;
  }
}


export type CommittedRecoveryReceipt = Readonly<{ state: "committed"; result?: Readonly<{ id: string; version: string }> | null }>;
export type PersonRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{ id: string; version: string }>; recordedAt: string }> | Readonly<{ state: "closed"; result: null; recordedAt: string }>;
export type CatalogRecoveryReceipt = Extract<PersonRecoveryReceipt, { state: "committed" }>;
export type StockCountRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{ id: string; version: "1" }>; recordedAt: string }>;
export type StockSourceRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{
  proposalId: string; proposalVersion: "1"; status: "proposed" | "approved" | "rejected" | "withdrawn"; decisionId: string | null; physicalPoolId: string | null;
}> }>;
export type StockSourceRevisionRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{
  proposalId: string; proposalVersion: "1"; status: "proposed" | "approved" | "rejected" | "withdrawn"; physicalPoolId: string;
  basePoolVersion: string | null; appliedPoolVersion: string | null; decisionId: string | null;
}> }>;
export type StockSourceAliasRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{
  proposalId: string; proposalVersion: "1"; status: "proposed" | "approved" | "rejected" | "withdrawn"; physicalPoolId: string;
  poolVersion: string; baseAliasVersion: string | null; appliedAliasVersion: string | null; decisionId: string | null;
}> }>;
export type StockSourceSuspensionRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{
  suspensionId: string; physicalPoolId: string; poolVersion: string; suspensionVersion: string; status: "suspended";
}> }>;
export type StockSourcePolicyRecoveryReceipt = Readonly<{ state: "committed"; result: Readonly<{
  proposalId: string; proposalVersion: "1"; status: "proposed" | "approved" | "rejected" | "withdrawn"; physicalPoolId: string;
  basePolicyVersion: string | null; decisionId: string | null; policyId: string | null; policyVersion: string | null;
}> }>;
export type RecoveryOutcome = CommittedRecoveryReceipt | Readonly<{ state: "closed" | "unconfirmed" }>;
type LookupReceipt = Readonly<{ state: string; result?: Readonly<{ id: string; version: string }> | null }>;
export type LegacyRecoveryAdapter = Readonly<{
  lookup: (entry: OperationReference) => Promise<LookupReceipt | null>;
  close: (entry: OperationReference, reason: string) => Promise<LookupReceipt | null>;
}>;

/** Supply this only from the owning client screen. A missing receipt is never a failed or closed request. */
export type OperationRecoveryAdapters = Readonly<{
  "procurement.funding"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: FundingResult }> | null> }>;
  "procurement.purchase.order"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PurchaseOrderResult }> | null> }>;
  "procurement.supplier"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: SupplierResult }> | null> }>;
  "procurement.request"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ProcurementResult }> | null> }>;
  "accounting.book"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: AccountingBookResult }> | null> }>;
  "ordering.spending.policy"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: SpendingPolicyResult }> | null> }>;
  "organization.address"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: OrganizationAddressResult }> | null> }>;
  "billing.refund.payout.verify"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: RefundSettlementResult }> | null> }>;
  "billing.refund.payout"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: RefundPayoutDraftResult | RefundPayoutResult }> | null> }>;
  "evidence.refund-payout-image"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentReservation }> | null> }>;
  "billing.return.financial.approve"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ReturnFinancialApprovalResult }> | null> }>;
  "billing.return.entitlement.request"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ReturnEntitlementResult }> | null> }>;
  "fulfillment.case"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: CaseResult | CaseDecisionResult | ReturnResult | InspectionResult | ResolutionResult }> | null> }>;
  "billing.bank-receipt"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: BankReceiptResult }> | null> }>;
  "billing.bank-statement"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: StatementResult }> | null> }>;
  "billing.receiving-account"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ReceivingResult }> | null> }>;
  "billing.payment.allocate"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentAllocationResult }> | null> }>;
  "billing.payment.allocation.correct"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentAllocationCorrectionResult }> | null> }>;
  "billing.payment.verify"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentVerificationResult }> | null> }>;
  "billing.payment.review.correct"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentCorrectionResult }> | null> }>;
  "billing.payment.review.manual"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentManualReviewResult }> | null> }>;
  "billing.payment.review.manage"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentReviewActionResult }> | null> }>;
  "billing.payment.submit"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentSubmissionResult }> | null> }>;
  "billing.payment.replacement.create"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentReplacementResult }> | null> }>;
  "billing.payment"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentDraftResult }> | null> }>;
  "evidence.payment-image"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentReservation }> | null> }>;
  "billing.invoice"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: Invoice }> | null> }>;
  "fulfillment.delivery-attempt"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: DeliveryAttempt }> | null> }>;
  "fulfillment.receipt-proof"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ReceiptProof }> | null> }>;
  "fulfillment.receipt"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: Receipt | ReceiptChallenge }> | null> }>;
  "fulfillment.surat-jalan"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ShipmentDocument }> | null> }>;
  "fulfillment.shipment"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: DispatchResult }> | null> }>;
  "fulfillment.preparation"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PreparationResult }> | null> }>;
  "ordering.decision"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: OrderDecisionResult }> | null> }>;
  "ordering.order"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: OrderSubmitResult }> | null> }>;
  "ordering.draft"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: DraftResult }> | null> }>;
  "catalog.price"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PriceResult }> | null> }>;
  "stock.source.policy"?: Readonly<{ lookup: (entry: OperationReference) => Promise<StockSourcePolicyRecoveryReceipt | null> }>;
  "stock.source.suspend"?: Readonly<{ lookup: (entry: OperationReference) => Promise<StockSourceSuspensionRecoveryReceipt | null> }>;
  "stock.source.alias"?: Readonly<{ lookup: (entry: OperationReference) => Promise<StockSourceAliasRecoveryReceipt | null> }>;
  "stock.source.revision"?: Readonly<{ lookup: (entry: OperationReference) => Promise<StockSourceRevisionRecoveryReceipt | null> }>;
  "stock.source"?: Readonly<{ lookup: (entry: OperationReference) => Promise<StockSourceRecoveryReceipt | null> }>;
  catalog?: Readonly<{ lookup: (entry: OperationReference) => Promise<CatalogRecoveryReceipt | null> }>;
  "inventory.stock-count"?: Readonly<{ lookup: (entry: OperationReference) => Promise<StockCountRecoveryReceipt | null> }>;
  "person.review"?: Readonly<{
    lookup: (entry: OperationReference) => Promise<PersonRecoveryReceipt | null>;
    close?: Readonly<{ allowed: (operation: string) => boolean; resolve: (entry: OperationReference, reason: string) => Promise<PersonRecoveryReceipt | null> }>;
  }>;
}>;

function recoveryNamespace(operation: string): "procurement.funding" | "procurement.purchase.order" | "procurement.supplier" | "procurement.request" | "accounting.book" | "ordering.spending.policy" | "organization.address" | "billing.refund.payout.verify" | "billing.refund.payout" | "evidence.refund-payout-image" | "billing.return.financial.approve" | "billing.return.entitlement.request" | "fulfillment.case" | "billing.bank-receipt" | "billing.bank-statement" | "billing.receiving-account" | "access" | "organization" | "identity" | "person.review" | "catalog" | "catalog.price" | "inventory.stock-count" | "stock.source" | "stock.source.revision" | "stock.source.alias" | "stock.source.suspend" | "stock.source.policy" | "ordering.draft" | "ordering.order" | "ordering.decision" | "fulfillment.preparation" | "fulfillment.shipment" | "fulfillment.surat-jalan" | "fulfillment.receipt" | "fulfillment.receipt-proof" | "fulfillment.delivery-attempt" | "billing.invoice" | "billing.payment.allocate" | "billing.payment.allocation.correct" | "billing.payment.verify" | "billing.payment.review.correct" | "billing.payment.submit" | "billing.payment" | "evidence.payment-image" | "billing.payment.review.manual" | "billing.payment.review.manage" | "billing.payment.replacement.create" | null {
  if (!operationPattern.test(operation)) return null;
  if (/^procurement\.funding\.(request|approve|reject)$(?![\s\S])/.test(operation)) return "procurement.funding";
  if (/^procurement\.purchase\.order\.(propose|approve|reject)$(?![\s\S])/.test(operation)) return "procurement.purchase.order";
  if (/^procurement\.supplier\.(propose|observe|approve|reject)$(?![\s\S])/.test(operation)) return "procurement.supplier";
  if (/^procurement\.request\.(submit|approve|reject)$(?![\s\S])/.test(operation)) return "procurement.request";
  if (/^accounting\.book\.(propose|approve|reject)$(?![\s\S])/.test(operation)) return "accounting.book";
  if (/^ordering\.spending\.policy\.(propose|approve|reject)$(?![\s\S])/.test(operation)) return "ordering.spending.policy";
  if (/^organization\.address\.(propose|approve|reject)$(?![\s\S])/.test(operation)) return "organization.address";
  if (operation === "billing.refund.payout.verify") return "billing.refund.payout.verify";
  if (operation === "billing.refund.payout.draft" || operation === "billing.refund.payout.record") return "billing.refund.payout";
  if (operation === "evidence.refund-payout-image.reserve") return "evidence.refund-payout-image";
  if (operation === "billing.return.financial.approve") return "billing.return.financial.approve";
  if (operation === "billing.return.entitlement.request") return "billing.return.entitlement.request";
  if (operation === "fulfillment.case.request" || operation === "fulfillment.case.decide" || operation === "fulfillment.case.return_receive" || operation === "fulfillment.case.return_inspect" || operation === "fulfillment.case.resolution_request") return "fulfillment.case";
  if (operation === "billing.bank-receipt.confirm") return "billing.bank-receipt";
  if (operation === "billing.bank-statement.stage") return "billing.bank-statement";
  if (operation.startsWith("billing.receiving-account.")) return "billing.receiving-account";
  if (operation === "billing.payment.allocate") return "billing.payment.allocate";
  if (operation === "billing.payment.allocation.correct") return "billing.payment.allocation.correct";
  if (operation === "billing.payment.verify") return "billing.payment.verify";
  if (operation === "billing.payment.review.correct") return "billing.payment.review.correct";
  if (operation === "billing.payment.review.manual") return "billing.payment.review.manual";
  if (operation === "billing.payment.review.manage") return "billing.payment.review.manage";
  if (operation === "billing.payment.submit") return "billing.payment.submit";
  if (operation === "billing.payment.replacement.create") return "billing.payment.replacement.create";
  if (operation === "billing.payment.draft.save") return "billing.payment";
  if (operation === "evidence.payment-image.reserve") return "evidence.payment-image";
  if (operation === "billing.invoice.issue") return "billing.invoice";
  if (operation === "fulfillment.delivery-attempt.record") return "fulfillment.delivery-attempt";
  if (operation === "fulfillment.receipt-proof.issue") return "fulfillment.receipt-proof";
  if (operation.startsWith("fulfillment.receipt.")) return "fulfillment.receipt";
  if (operation === "fulfillment.surat-jalan.issue") return "fulfillment.surat-jalan";
  if (operation === "fulfillment.shipment.dispatch") return "fulfillment.shipment";
  if (operation === "fulfillment.preparation.save") return "fulfillment.preparation";
  if (operation === "ordering.draft.save") return "ordering.draft";
  if (operation === "ordering.order.submit") return "ordering.order";
  if (operation.startsWith("ordering.order.")) return "ordering.decision";
  if (operation.startsWith("catalog.price.")) return "catalog.price";
  if (operation.startsWith("stock.source.policy.")) return "stock.source.policy";
  if (operation === "stock.source.suspend") return "stock.source.suspend";
  if (operation.startsWith("stock.source.alias.")) return "stock.source.alias";
  if (operation.startsWith("stock.source.revision.")) return "stock.source.revision";
  if (operation.startsWith("stock.source.")) return "stock.source";
  if (operation === "inventory.stock-count.record") return "inventory.stock-count";
  if (operation.startsWith("catalog.")) return "catalog";
  if (operation.startsWith("person.review.")) return "person.review";
  if (operation.startsWith("organization.")) return "organization";
  if (operation.startsWith("identity.")) return "identity";
  if (operation.startsWith("access.")) return "access";
  return null;
}

export function operationRecoveryCapabilities(operation: string, adapters: OperationRecoveryAdapters = {}):
  Readonly<{ canCheck: boolean; canClose: boolean; unavailable: "adapter" | "operation" | null }> {
  const namespace = recoveryNamespace(operation);
  if (!namespace) return { canCheck: false, canClose: false, unavailable: "operation" };
  if (namespace === "procurement.funding" || namespace === "procurement.purchase.order" || namespace === "procurement.supplier" || namespace === "procurement.request" || namespace === "accounting.book" || namespace === "ordering.spending.policy" || namespace === "organization.address" || namespace === "billing.refund.payout.verify" || namespace === "billing.refund.payout" || namespace === "evidence.refund-payout-image" || namespace === "billing.return.financial.approve" || namespace === "billing.return.entitlement.request" || namespace === "fulfillment.case") return typeof adapters[namespace]?.lookup === "function"
    ? { canCheck: true, canClose: false, unavailable: null } : { canCheck: false, canClose: false, unavailable: "adapter" };
  if (namespace === "billing.payment.replacement.create" || namespace === "billing.payment.review.manage" || namespace === "billing.payment.review.manual" || namespace === "billing.bank-receipt" || namespace === "billing.bank-statement" || namespace === "billing.receiving-account" || namespace === "billing.payment.allocation.correct" || namespace === "billing.payment.allocate" || namespace === "billing.payment.verify" || namespace === "billing.payment.review.correct" || namespace === "billing.payment.submit" || namespace === "billing.payment" || namespace === "evidence.payment-image" || namespace === "billing.invoice" || namespace === "fulfillment.delivery-attempt" || namespace === "fulfillment.receipt-proof" || namespace === "fulfillment.receipt" || namespace === "fulfillment.surat-jalan" || namespace === "fulfillment.shipment" || namespace === "fulfillment.preparation" || namespace === "ordering.decision" || namespace === "ordering.order" || namespace === "ordering.draft" || namespace === "catalog.price" || namespace === "catalog" || namespace === "inventory.stock-count" || namespace === "stock.source" || namespace === "stock.source.revision" || namespace === "stock.source.alias" || namespace === "stock.source.suspend" || namespace === "stock.source.policy") return typeof adapters[namespace]?.lookup === "function"
    ? { canCheck: true, canClose: false, unavailable: null } : { canCheck: false, canClose: false, unavailable: "adapter" };
  if (namespace === "person.review") return typeof adapters[namespace]?.lookup === "function"
    ? { canCheck: true, canClose: typeof adapters[namespace]?.close?.resolve === "function" && adapters[namespace]?.close?.allowed(operation) === true, unavailable: null }
    : { canCheck: false, canClose: false, unavailable: "adapter" };
  return { canCheck: true, canClose: true, unavailable: null };
}

/** Reconcile one immutable reference; never rebuild or retry its original command from persisted metadata. */
export async function reconcileOperationRecovery(entry: OperationReference, journal: OperationJournal,
  legacy: LegacyRecoveryAdapter, adapters: OperationRecoveryAdapters = {}, closeReason?: string, isCurrent: () => boolean = () => true): Promise<RecoveryOutcome> {
  const namespace = recoveryNamespace(entry.operation);
  if (!namespace) throw new AccessFailure(400, "recovery_operation_unsupported");
  if (!validEntry(entry)) throw new AccessFailure(400, "recovery_reference_invalid");
  const reference = Object.freeze({ ...entry });
  const requireCurrent = () => { if (!isCurrent()) throw new AccessFailure(409, "recovery_request_stale"); };
  requireCurrent();
  let receipt: LookupReceipt | null;
  if (namespace === "procurement.funding" || namespace === "procurement.purchase.order" || namespace === "procurement.supplier" || namespace === "procurement.request" || namespace === "accounting.book" || namespace === "ordering.spending.policy" || namespace === "organization.address" || namespace === "billing.refund.payout.verify" || namespace === "billing.refund.payout" || namespace === "evidence.refund-payout-image" || namespace === "billing.return.financial.approve" || namespace === "billing.return.entitlement.request" || namespace === "fulfillment.case" || namespace === "billing.payment.replacement.create" || namespace === "billing.payment.review.manage" || namespace === "billing.payment.review.manual" || namespace === "billing.bank-receipt" || namespace === "billing.bank-statement" || namespace === "billing.receiving-account" || namespace === "billing.payment.allocation.correct" || namespace === "billing.payment.allocate" || namespace === "billing.payment.verify" || namespace === "billing.payment.review.correct" || namespace === "billing.payment.submit" || namespace === "billing.payment" || namespace === "evidence.payment-image") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state") throw new AccessFailure(502, "recovery_receipt_invalid");
      if (namespace === "procurement.funding") { const result = parseFundingResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.requestId, version: result.requestVersion } }; }
      else if (namespace === "procurement.purchase.order") { const result = parsePurchaseOrderResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } }; }
      else if (namespace === "procurement.supplier") { const result = parseSupplierResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } }; }
      else if (namespace === "procurement.request") { const result = parseProcurementResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.requestId, version: result.requestVersion } }; }
      else if (namespace === "accounting.book") { const result = parseAccountingBookResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } }; }
      else if (namespace === "ordering.spending.policy") { const result = parseSpendingPolicyResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.policyVersion ?? "1" } }; }
      else if (namespace === "organization.address") { const result = parseOrganizationAddressResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.addressVersion ?? "1" } }; }
      else if (namespace === "fulfillment.case") { const result = reference.operation === 'fulfillment.case.resolution_request' ? parseResolutionResult(recovered.result, reference.key) : reference.operation === 'fulfillment.case.return_inspect' ? parseInspectionResult(recovered.result, reference.key) : reference.operation === 'fulfillment.case.return_receive' ? parseReturnResult(recovered.result, reference.key) : reference.operation === 'fulfillment.case.decide' ? parseCaseDecisionResult(recovered.result, reference.key) : parseCaseResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: reference.operation === 'fulfillment.case.return_receive' || reference.operation === 'fulfillment.case.return_inspect' || reference.operation === 'fulfillment.case.resolution_request' ? (result as ReturnResult | InspectionResult | ResolutionResult).returnId : result.caseId, version: result.revision } }; }
      else if (namespace === "billing.refund.payout.verify") { const result = parseRefundSettlementResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.settlementId, version: "1" } }; }
      else if (namespace === "billing.refund.payout") { const result = reference.operation === "billing.refund.payout.draft" ? parseRefundPayoutDraftResult(recovered.result, reference.key) : parseRefundPayoutResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: "payoutId" in result ? result.payoutId : result.draftId, version: "1" } }; }
      else if (namespace === "evidence.refund-payout-image") { refundPayoutOwner(reference.key, "reserve"); const result = parsePaymentReservation(recovered.result); receipt = { state: "committed", result: { id: result.reservationId, version: result.revision } }; }
      else if (namespace === "billing.return.financial.approve") { const result = parseReturnFinancialApprovalResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.financialApprovalId, version: result.invoiceVersion } }; }
      else if (namespace === "billing.return.entitlement.request") { const result = parseReturnEntitlementResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.returnId, version: result.version } }; }
      else if (namespace === "billing.bank-receipt") { const result = parseBankReceiptResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.receiptId, version: "1" } }; }
      else if (namespace === "billing.bank-statement") { const result = parseStatementResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.importId, version: "1" } }; }
      else if (namespace === "billing.receiving-account") { const result = validateReceivingReceipt(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.accountVersion ?? "1" } }; }
      else if (namespace === "billing.payment.allocate") { const result = parsePaymentAllocationResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.allocationId, version: result.receiptVersion } }; }
      else if (namespace === "billing.payment.allocation.correct") { const result = parsePaymentAllocationCorrectionResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.correctionId, version: result.receiptVersion } }; }
      else if (namespace === "billing.payment.verify") { const result = parsePaymentVerificationResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.verificationId, version: "1" } }; }
      else if (namespace === "billing.payment.review.manual") { const result = parseManualReviewResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.manualReviewId, version: result.reviewRevision } }; }
      else if (namespace === "billing.payment.review.manage") { const result = parseReviewActionResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.actionId, version: result.reviewRevision } }; }
      else if (namespace === "billing.payment.review.correct") { const result = parsePaymentCorrectionResult(recovered.result); if (result.declarationId !== paymentCorrectionOwner(reference.key)) throw new AccessFailure(502, "recovery_receipt_invalid"); receipt = { state: "committed", result: { id: result.declarationId, version: result.reviewRevision } }; }
      else if (namespace === "billing.payment.submit") { const result = parsePaymentSubmissionResult(recovered.result); if (result.declarationId !== paymentSubmissionOwner(reference.key)) throw new AccessFailure(502, "recovery_receipt_invalid"); receipt = { state: "committed", result: { id: result.declarationId, version: result.reviewRevision } }; }
      else if (namespace === "billing.payment.replacement.create") { const result = parsePaymentReplacementResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.declarationId, version: result.revision } }; }
      else if (namespace === "billing.payment") { const result = parsePaymentResult(recovered.result); receipt = { state: "committed", result: { id: result.declarationId, version: result.revision } }; }
      else { const result = parsePaymentReservation(recovered.result); receipt = { state: "committed", result: { id: result.reservationId, version: result.revision } }; }
    }
  } else if (namespace === "billing.invoice") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseInvoice(recovered.result); receipt = { state: "committed", result: { id: result.id, version: "1" } };
    }
  } else if (namespace === "fulfillment.delivery-attempt") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseDeliveryAttempt(recovered.result);
      receipt = { state: "committed", result: { id: result.id, version: result.sequence } };
    }
  } else if (namespace === "fulfillment.receipt-proof") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseReceiptProof(recovered.result);
      receipt = { state: "committed", result: { id: result.id, version: result.version } };
    }
  } else if (namespace === "fulfillment.receipt") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = entry.operation === "fulfillment.receipt.challenge.issue" ? parseReceiptChallenge(recovered.result) : parseReceipt(recovered.result);
      if ('token' in result ? result.token !== null : result.recipientId !== entry.actorId) throw new AccessFailure(502, "recovery_receipt_invalid");
      receipt = { state: "committed", result: { id: result.id, version: "1" } };
    }
  } else if (namespace === "fulfillment.surat-jalan") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state" || !recovered.result ||
        Object.keys(recovered.result).sort().join(",") !== "byteLength,contentHash,id,issuedAt,shipmentId,shipmentRevision,version") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseShipmentDocument(recovered.result);
      receipt = { state: "committed", result: { id: result.id, version: result.version } };
    }
  } else if (namespace === "fulfillment.shipment") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state" || !recovered.result ||
        Object.keys(recovered.result).sort().join(",") !== "number,numberChannel,numberEntity,numberYear,orderId,recordedAt,revision,shipmentId,suborderId") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseDispatchResult(recovered.result);
      receipt = { state: "committed", result: { id: result.shipmentId, version: result.revision } };
    }
  } else if (namespace === "fulfillment.preparation") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state" || !recovered.result ||
        Object.keys(recovered.result).sort().join(",") !== "orderId,preparationId,recordedAt,revision,suborderId,versionId") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parsePreparationResult(recovered.result);
      receipt = { state: "committed", result: { id: result.preparationId, version: result.revision } };
    }
  } else if (namespace === "ordering.decision") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state" ||
        !recovered.result || Object.keys(recovered.result).sort().join(",") !== "action,decisionId,orderId,recordedAt,revision,stage,state,suborderId") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseOrderDecisionResult(recovered.result);
      const selected = orderDecisionSelection(entry.operation);
      if (result.stage !== selected.stage || result.action !== selected.action) throw new AccessFailure(502, "recovery_receipt_invalid");
      receipt = { state: "committed", result: { id: result.orderId, version: result.revision } };
    }
  } else if (namespace === "ordering.order") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state" ||
        !recovered.result || Object.keys(recovered.result).sort().join(",") !== "orderId,recordedAt,revision,spendingState,suborderIds") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseOrderSubmitResult(recovered.result);
      receipt = { state: "committed", result: { id: result.orderId, version: result.revision } };
    }
  } else if (namespace === "ordering.draft") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state" ||
        !recovered.result || Object.keys(recovered.result).sort().join(",") !== "draftId,recordedAt,revision,versionId") throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = parseDraftResult(recovered.result);
      receipt = { state: "committed", result: { id: result.draftId, version: result.revision } };
    }
  } else if (namespace === "catalog.price") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state")
        throw new AccessFailure(502, "recovery_receipt_invalid");
      const result = priceOutcome(recovered.result, priceAction(reference.operation), priceRecoverySku(reference.key));
      receipt = { state: "committed", result: { id: result.proposalId, version: result.revision } };
    }
  } else if (namespace === "stock.source.policy") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters["stock.source.policy"];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const source = await adapter.lookup(reference); requireCurrent();
    if (source === null) receipt = null;
    else {
      const invalid = () => { throw new AccessFailure(502, "recovery_receipt_invalid"); };
      if (!source || source.state !== "committed" || !source.result ||
        Object.keys(source).sort().join(",") !== "result,state") return invalid();
      const result = source.result, expected = reference.operation === "stock.source.policy.propose" ? "proposed"
        : reference.operation === "stock.source.policy.approve" ? "approved" : reference.operation === "stock.source.policy.reject" ? "rejected" : "withdrawn";
      const id = (value: unknown): value is string => typeof value === "string" && value.trim() === value && validReference(value);
      const basis = (value: unknown) => value === "0" || id(value) && value !== "9223372036854775807";
      if (Object.keys(result).sort().join(",") !== "basePolicyVersion,decisionId,physicalPoolId,policyId,policyVersion,proposalId,proposalVersion,status" ||
        !id(result.proposalId) || result.proposalVersion !== "1" || result.status !== expected || !id(result.physicalPoolId) ||
        (expected === "proposed" ? result.decisionId !== null || !basis(result.basePolicyVersion) : !id(result.decisionId) || result.basePolicyVersion !== null) ||
        (expected === "approved" ? !id(result.policyId) || !id(result.policyVersion) : result.policyId !== null || result.policyVersion !== null)) return invalid();
      receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } };
    }
  } else if (namespace === "stock.source.suspend") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters["stock.source.suspend"];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const source = await adapter.lookup(reference); requireCurrent();
    if (source === null) receipt = null;
    else {
      const invalid = () => { throw new AccessFailure(502, "recovery_receipt_invalid"); };
      if (!source || source.state !== "committed" || !source.result ||
        Object.keys(source).sort().join(",") !== "result,state") return invalid();
      const result = source.result;
      const id = (value: unknown): value is string => typeof value === "string" && value.trim() === value && validReference(value);
      if (Object.keys(result).sort().join(",") !== "physicalPoolId,poolVersion,status,suspensionId,suspensionVersion" ||
        result.status !== "suspended" || !id(result.suspensionId) || !id(result.physicalPoolId) || !id(result.poolVersion) || !id(result.suspensionVersion)) return invalid();
      receipt = { state: "committed", result: { id: result.suspensionId, version: result.suspensionVersion } };
    }
  } else if (namespace === "stock.source.alias") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters["stock.source.alias"];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const source = await adapter.lookup(reference); requireCurrent();
    if (source === null) receipt = null;
    else {
      const invalid = () => { throw new AccessFailure(502, "recovery_receipt_invalid"); };
      if (!source || source.state !== "committed" || !source.result ||
        Object.keys(source).sort().join(",") !== "result,state") return invalid();
      const result = source.result, expected = reference.operation === "stock.source.alias.propose" ? "proposed"
        : reference.operation === "stock.source.alias.approve" ? "approved" : reference.operation === "stock.source.alias.reject" ? "rejected" : "withdrawn";
      const id = (value: unknown): value is string => typeof value === "string" && value.trim() === value && validReference(value);
      const basis = (value: unknown) => value === "0" || id(value) && value !== "9223372036854775807";
      if (Object.keys(result).sort().join(",") !== "appliedAliasVersion,baseAliasVersion,decisionId,physicalPoolId,poolVersion,proposalId,proposalVersion,status" ||
        !id(result.proposalId) || result.proposalVersion !== "1" || result.status !== expected || !id(result.physicalPoolId) || !id(result.poolVersion) ||
        (expected === "proposed" ? result.decisionId !== null || !basis(result.baseAliasVersion) : !id(result.decisionId) || result.baseAliasVersion !== null) ||
        (expected === "approved" ? !id(result.appliedAliasVersion) : result.appliedAliasVersion !== null)) return invalid();
      receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } };
    }
  } else if (namespace === "stock.source.revision") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters["stock.source.revision"];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const source = await adapter.lookup(reference);
    requireCurrent();
    if (source === null) receipt = null;
    else {
      const invalid = () => { throw new AccessFailure(502, "recovery_receipt_invalid"); };
      if (!source || source.state !== "committed" || !source.result) return invalid();
      const result = source.result, expected = reference.operation === "stock.source.revision.propose" ? "proposed"
        : reference.operation === "stock.source.revision.approve" ? "approved" : reference.operation === "stock.source.revision.reject" ? "rejected" : "withdrawn";
      const id = (value: unknown): value is string => typeof value === "string" && value.trim() === value && validReference(value);
      if (!id(result.proposalId) || result.proposalVersion !== "1" || result.status !== expected || !id(result.physicalPoolId) ||
        (expected === "proposed" ? result.decisionId !== null || !id(result.basePoolVersion) || result.basePoolVersion === "9223372036854775807"
          : !id(result.decisionId) || result.basePoolVersion !== null) ||
        (expected === "approved" ? !id(result.appliedPoolVersion) || BigInt(result.appliedPoolVersion) < BigInt(2) : result.appliedPoolVersion !== null)) return invalid();
      receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } };
    }
  } else if (namespace === "stock.source") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters["stock.source"];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const source = await adapter.lookup(reference);
    requireCurrent();
    if (source === null) receipt = null;
    else {
      const invalid = () => { throw new AccessFailure(502, "recovery_receipt_invalid"); };
      if (!source || source.state !== "committed" || !source.result) return invalid();
      const result = source.result, expected = reference.operation === "stock.source.propose" ? "proposed"
        : reference.operation === "stock.source.approve" ? "approved" : reference.operation === "stock.source.reject" ? "rejected" : "withdrawn";
      const id = (value: unknown) => typeof value === "string" && value.trim() === value && validReference(value);
      if (!id(result.proposalId) || result.proposalVersion !== "1" || result.status !== expected ||
        (expected === "proposed" ? result.decisionId !== null || result.physicalPoolId !== null
          : !id(result.decisionId) || (expected === "approved" ? !id(result.physicalPoolId) : result.physicalPoolId !== null))) return invalid();
      receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } };
    }
  } else if (namespace === "catalog" || namespace === "inventory.stock-count") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const committedReceipt = await adapter.lookup(reference);
    requireCurrent();
    if (committedReceipt !== null && (!committedReceipt || committedReceipt.state !== "committed" ||
      typeof committedReceipt.recordedAt !== "string" || committedReceipt.recordedAt.length > 40 || !/^\d{4}-\d\d-\d\dT/.test(committedReceipt.recordedAt) || !Number.isFinite(Date.parse(committedReceipt.recordedAt)) ||
      !committedReceipt.result || typeof committedReceipt.result.id !== "string" || typeof committedReceipt.result.version !== "string" ||
      !validReference(committedReceipt.result.id) || !validReference(committedReceipt.result.version) ||
      namespace === "inventory.stock-count" && committedReceipt.result.version !== "1"))
      throw new AccessFailure(502, "recovery_receipt_invalid");
    receipt = committedReceipt;
  } else if (namespace === "person.review") {
    const adapter = adapters[namespace];
    if (closeReason !== undefined && typeof adapter?.close?.resolve !== "function") throw new AccessFailure(400, "recovery_close_unsupported");
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    let personReceipt: PersonRecoveryReceipt | null;
    if (closeReason === undefined) personReceipt = await adapter.lookup(reference);
    else {
      if (adapter.close?.allowed(reference.operation) !== true) throw new AccessFailure(403, "recovery_close_unavailable");
      if (!closeReason.trim() || closeReason.trim().length > 1000) throw new AccessFailure(400, "recovery_reason_invalid");
      personReceipt = await adapter.close.resolve(reference, closeReason.trim());
    }
    requireCurrent();
    if (personReceipt !== null && (!personReceipt || !["committed", "closed"].includes(personReceipt.state) ||
      typeof personReceipt.recordedAt !== "string" || personReceipt.recordedAt.length > 40 || !/^\d{4}-\d\d-\d\dT/.test(personReceipt.recordedAt) || !Number.isFinite(Date.parse(personReceipt.recordedAt)) ||
      (personReceipt.state === "closed" ? personReceipt.result !== null : !personReceipt.result || typeof personReceipt.result.id !== "string" || typeof personReceipt.result.version !== "string" || !validReference(personReceipt.result.id) || !validReference(personReceipt.result.version))))
      throw new AccessFailure(502, "recovery_receipt_invalid");
    receipt = personReceipt;
  } else {
    receipt = closeReason === undefined ? await legacy.lookup(reference) : await legacy.close(reference, closeReason.trim());
  }
  requireCurrent();
  if (receipt?.state !== "committed" && receipt?.state !== "closed") return { state: "unconfirmed" };
  try { journal.confirmed(reference); }
  catch { throw new AccessFailure(400, "recovery_cleanup_failed"); }
  return receipt.state === "closed" ? { state: "closed" } : { state: "committed", ...(receipt.result ? { result: { id: receipt.result.id, version: receipt.result.version } } : {}) };
}
