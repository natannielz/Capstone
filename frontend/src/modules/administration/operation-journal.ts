import { pdfReservationKeyOwner } from '../documents/pdf-evidence-client';
import { isSupplementOperation, supplementKeyBinding } from '../billing/payment-pdf-supplement-recovery';
import { parseSupplementRecoveryResult, type PaymentSupplement, type PaymentSupplementReservation } from '../billing/payment-pdf-supplement-client';
import { parseSupplierPayableResult, type SupplierPayableResult } from '../procurement/procurement-supplier-payable-client';
import { requestResult, type BranchRequestResult } from '../ordering/branch-request-state';
import { parseSourceReconciliationResult, type SourceReconciliationResult } from '../inventory/stock-source-reconciliation-client';
import { parseAdvanceReturnResult, type AdvanceReturnResult } from '../procurement/procurement-advance-return-client';
import { parseQualityAcceptanceResult, type QualityAcceptanceResult } from '../procurement/procurement-quality-acceptance-client';
import { parseStockValuationResult, type StockValuationResult } from '../procurement/procurement-stock-valuation-client';
import { parseStockReceiptResult, type ProcurementStockReceiptResult } from '../procurement/procurement-stock-receipt-client';
import { parseSupplierClaimResult, type SupplierClaimResult } from '../procurement/supplier-claim-client';
import { parseSupplierDocumentResult, supplierDocumentReservationOwner, type SupplierDocumentResult } from "../procurement/supplier-document-client";
import { parseProcurementInspectionResult, parseInspectionReservation, inspectionReservationOwner, type ProcurementInspectionResult, type InspectionReservation } from "../procurement/procurement-inspection-client";
import { parseProcurementReceivingResult, type ProcurementReceivingResult } from "../procurement/procurement-receiving-client";
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
import { returnLotOperation, returnLotOwner, parseReturnLotResult, type ReturnLotResult } from "../fulfillment/return-lot-client";
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
import { parseSupplierBankResult, type SupplierBankResult } from "../procurement/supplier-bank-client";
import { parseDisbursementResult, disbursementReservationOwner, type DisbursementResult } from "../procurement/procurement-disbursement-client";
import { parseProcurementSettlementResult, type ProcurementSettlementResult } from "../procurement/procurement-settlement-client";
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
import { catalogPhotoOperation, catalogPhotoKeySku, parseCatalogPhotoResult, type CatalogPhotoResult } from "../catalog/catalog-photo-workflow";
import { catalogPhotoReserveOperation, catalogPhotoReservationOwner, parseCatalogPhotoReservation, type CatalogPhotoReservation } from "../catalog/catalog-photo-evidence-client";
import { catalogPhotoRevalidationOperation, catalogPhotoRevalidationKeySku, parseCatalogPhotoRevalidationResult, type CatalogPhotoRevalidationResult } from "../catalog/catalog-photo-revalidation-api";
import { accountContactOperation, accountContactKeyActor, parseAccountContactResult, type AccountContactResult } from "../identity/account-contact-client";
import { packProposeOperation, packDecideOperation, packOperation, parsePackResult, type PackResult } from '../inventory/pack-conversion-workflow';

export type OperationReference = Readonly<{ actorId: string; operation: string; key: string; createdAt: string }>;
type JournalStorage = Pick<Storage, "length" | "key" | "getItem" | "setItem" | "removeItem">;
const prefix = "unit-toko:pending:v1:";
const keyPattern = /^[A-Za-z0-9._:-]{1,128}$/;
const operationPattern = /^(?:procurement\.qualityacceptance\.(?:propose|decide)|procurement\.advancereturn\.(?:propose|decide)|procurement\.payable\.(?:propose|decide)|stock\.source\.reconciliation\.(?:propose|decide)|procurement\.stockvaluation\.(?:propose|decide)|procurement\.stockreceipt\.receive|procurement\.supplierclaim\.(?:request|approve|reject)|procurement\.supplierdocument\.(?:draft|submit|match)|evidence\.supplier-document-image\.reserve|procurement\.inspection\.(?:draft|submit)|evidence\.procurement-unboxing-video\.reserve|procurement\.receiving\.(?:notice|intake)|procurement\.settlement\.verify|procurement\.disbursement\.(?:draft|record)|evidence\.procurement-disbursement-image\.reserve|procurement\.supplierbank\.(?:propose|observe|approve|reject)|procurement\.funding\.(?:request|approve|reject)|procurement\.purchase\.order\.(?:propose|approve|reject)|procurement\.supplier\.(?:propose|observe|approve|reject)|procurement\.request\.(?:submit|approve|reject)|accounting\.book\.(?:propose|approve|reject)|billing\.refund\.payout\.(?:draft|record|verify)|evidence\.refund-payout-image\.reserve|billing\.return\.financial\.approve|billing\.return\.entitlement\.request|billing\.bank-receipt\.confirm|billing\.bank-statement\.stage|billing\.receiving-account\.(?:propose|observe|approve|reject|withdraw)|billing\.payment\.(?:draft\.save|replacement\.create|submit|verify|allocate|allocation\.correct|review\.(?:correct|manual|manage))|evidence\.payment-image\.reserve|billing\.invoice\.issue|(access|organization)\.[a-z.]{1,100}|identity\.(?:invitation\.(?:propose|approve|rejected|withdrawn|revoked)|recovery\.request)|person\.review\.(?:propose|approve|reject|withdraw)|fulfillment\.(?:case\.(?:request|decide|return_receive|return_inspect|resolution_request)|preparation\.save|shipment\.dispatch|surat-jalan\.issue|delivery-attempt\.record|receipt-proof\.issue|receipt\.(?:challenge\.issue|confirm))|ordering\.(?:spending\.policy\.(?:propose|approve|reject)|draft\.save|order\.(?:submit|cancel|spending\.(?:approve|reject)|store\.(?:accept|reject)))|catalog\.(?:price\.(?:propose|approve|reject|withdraw)|category\.save|family\.(?:create|edit)|sku\.(?:create|revise|replace|retire|publish))|inventory\.stock-count\.record|stock\.source\.(?:suspend|(?:(?:revision|alias|policy)\.)?(?:propose|approve|reject|withdraw)))$(?![\s\S])/;
const storageKey = (entry: OperationReference) => `${prefix}${entry.actorId}:${entry.operation}:${entry.key}`;
const recognizedOperation = (operation: string) => isSupplementOperation(operation) || ['evidence.payment-pdf.reserve', 'evidence.supplier-document-pdf.reserve'].includes(operation) || operationPattern.test(operation) || /^ordering\.branchrequest\.(?:submit|decide)$(?![\s\S])/u.test(operation) ||
  operation === catalogPhotoOperation || operation === catalogPhotoReserveOperation || operation === catalogPhotoRevalidationOperation || operation === accountContactOperation || operation === returnLotOperation ||
  operation === packProposeOperation || operation === packDecideOperation;
function validPhotoKey(operation: string, key: string, actorId: string): boolean {
  try {
    if (isSupplementOperation(operation)) supplementKeyBinding(operation, key, actorId);
    if (operation === 'evidence.payment-pdf.reserve' || operation === 'evidence.supplier-document-pdf.reserve') pdfReservationKeyOwner(key, actorId, operation === 'evidence.payment-pdf.reserve' ? 'Payment' : 'SupplierInvoice');
    if (operation === catalogPhotoOperation) catalogPhotoKeySku(key);
    else if (operation === catalogPhotoReserveOperation) catalogPhotoReservationOwner(key);
    else if (operation === catalogPhotoRevalidationOperation) catalogPhotoRevalidationKeySku(key);
    else if (operation === returnLotOperation) returnLotOwner(key);
    else if (operation === accountContactOperation && accountContactKeyActor(key) !== actorId) return false;
    return true;
  } catch { return false; }
}

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
    typeof entry.operation === "string" && recognizedOperation(entry.operation) &&
    typeof entry.key === "string" && keyPattern.test(entry.key) && validPhotoKey(entry.operation, entry.key, entry.actorId) &&
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
  'inventory.pack-conversion.rule'?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: 'committed'; result: PackResult }> | null> }>;
  'billing.payment.supplement'?: Readonly<{lookup: (entry: OperationReference) => Promise<Readonly<{state: 'committed'; result: PaymentSupplement | PaymentSupplementReservation}> | null>}>;
  'evidence.payment-pdf'?: Readonly<{lookup: (entry: OperationReference) => Promise<Readonly<{state: 'committed'; result: PaymentReservation}> | null>}>;
  'evidence.supplier-document-pdf'?: Readonly<{lookup: (entry: OperationReference) => Promise<Readonly<{state: 'committed'; result: PaymentReservation}> | null>}>;
  "account.contact"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: AccountContactResult }> | null> }>;
  "catalog.photo"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: CatalogPhotoResult }> | null> }>;
  "evidence.catalog-image"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: CatalogPhotoReservation }> | null> }>;
  "evidence.image-revalidation"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: CatalogPhotoRevalidationResult }> | null> }>;
  'ordering.branchrequest'?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: 'committed'; result: BranchRequestResult }> | null> }>;
  "procurement.qualityacceptance"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: QualityAcceptanceResult }> | null> }>;
  "procurement.advancereturn"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: AdvanceReturnResult }> | null> }>;
  "procurement.payable"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: SupplierPayableResult }> | null> }>;
  "stock.source.reconciliation"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: SourceReconciliationResult }> | null> }>;
  "procurement.stockvaluation"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: StockValuationResult }> | null> }>;
  "procurement.stockreceipt"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ProcurementStockReceiptResult }> | null> }>;
  "procurement.supplierclaim"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: SupplierClaimResult }> | null> }>;
  "procurement.supplierdocument"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: SupplierDocumentResult }> | null> }>;
  "evidence.supplier-document-image"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentReservation }> | null> }>;
  "procurement.inspection"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ProcurementInspectionResult }> | null> }>;
  "evidence.procurement-unboxing-video"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: InspectionReservation }> | null> }>;
  "procurement.receiving"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ProcurementReceivingResult }> | null> }>;
  "procurement.settlement"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: ProcurementSettlementResult }> | null> }>;
  "procurement.supplierbank"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: SupplierBankResult }> | null> }>;
  "procurement.disbursement"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: DisbursementResult }> | null> }>;
  "evidence.procurement-disbursement-image"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: PaymentReservation }> | null> }>;
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
  "fulfillment.case"?: Readonly<{ lookup: (entry: OperationReference) => Promise<Readonly<{ state: "committed"; result: CaseResult | CaseDecisionResult | ReturnResult | InspectionResult | ResolutionResult | ReturnLotResult }> | null> }>;
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

function recoveryNamespace(operation: string): "catalog.photo" | "evidence.catalog-image" | "evidence.image-revalidation" | "ordering.branchrequest" | "procurement.qualityacceptance" | "procurement.advancereturn" | "procurement.payable" | "stock.source.reconciliation" | "procurement.stockvaluation" | "procurement.stockreceipt" | "procurement.supplierclaim" | "procurement.supplierdocument" | "evidence.supplier-document-image" | "procurement.inspection" | "evidence.procurement-unboxing-video" | "procurement.receiving" | "procurement.settlement" | "procurement.disbursement" | "evidence.procurement-disbursement-image" | "procurement.supplierbank" | "procurement.funding" | "procurement.purchase.order" | "procurement.supplier" | "procurement.request" | "accounting.book" | "ordering.spending.policy" | "organization.address" | "billing.refund.payout.verify" | "billing.refund.payout" | "evidence.refund-payout-image" | "billing.return.financial.approve" | "billing.return.entitlement.request" | "fulfillment.case" | "billing.bank-receipt" | "billing.bank-statement" | "billing.receiving-account" | "access" | "organization" | "identity" | "person.review" | "catalog" | "catalog.price" | "inventory.stock-count" | "stock.source" | "stock.source.revision" | "stock.source.alias" | "stock.source.suspend" | "stock.source.policy" | "ordering.draft" | "ordering.order" | "ordering.decision" | "fulfillment.preparation" | "fulfillment.shipment" | "fulfillment.surat-jalan" | "fulfillment.receipt" | "fulfillment.receipt-proof" | "fulfillment.delivery-attempt" | "billing.invoice" | "billing.payment.allocate" | "billing.payment.allocation.correct" | "billing.payment.verify" | "billing.payment.review.correct" | "billing.payment.submit" | "billing.payment" | "evidence.payment-image" | "billing.payment.review.manual" | "billing.payment.review.manage" | "billing.payment.replacement.create" | null {
  if (!recognizedOperation(operation)) return null;
  if (operation === catalogPhotoOperation) return "catalog.photo";
  if (operation === catalogPhotoReserveOperation) return "evidence.catalog-image";
  if (operation === catalogPhotoRevalidationOperation) return "evidence.image-revalidation";
  if (/^procurement\.qualityacceptance\.(propose|decide)$(?![\s\S])/.test(operation)) return "procurement.qualityacceptance";
  if (/^procurement\.advancereturn\.(propose|decide)$(?![\s\S])/.test(operation)) return "procurement.advancereturn";
  if (/^procurement\.payable\.(propose|decide)$(?![\s\S])/.test(operation)) return "procurement.payable";
  if (/^stock\.source\.reconciliation\.(propose|decide)$(?![\s\S])/.test(operation)) return "stock.source.reconciliation";
  if (/^procurement\.stockvaluation\.(propose|decide)$(?![\s\S])/.test(operation)) return "procurement.stockvaluation";
  if (operation === 'ordering.branchrequest.submit' || operation === 'ordering.branchrequest.decide') return 'ordering.branchrequest';
  if (operation === "procurement.stockreceipt.receive") return "procurement.stockreceipt";
  if (/^procurement\.supplierclaim\.(request|approve|reject)$(?![\s\S])/.test(operation)) return "procurement.supplierclaim";
  if (/^procurement\.supplierdocument\.(draft|submit|match)$(?![\s\S])/.test(operation)) return "procurement.supplierdocument";
  if (operation === "evidence.supplier-document-image.reserve") return "evidence.supplier-document-image";
  if (/^procurement\.disbursement\.(draft|record)$(?![\s\S])/.test(operation)) return "procurement.disbursement";
  if (/^procurement\.receiving\.(notice|intake)$(?![\s\S])/.test(operation)) return "procurement.receiving";
  if (operation === "procurement.settlement.verify") return "procurement.settlement";
  if (/^procurement\.inspection\.(draft|submit)$(?![\s\S])/.test(operation)) return "procurement.inspection";
  if (operation === "evidence.procurement-unboxing-video.reserve") return "evidence.procurement-unboxing-video";
  if (operation === "evidence.procurement-disbursement-image.reserve") return "evidence.procurement-disbursement-image";
  if (/^procurement\.supplierbank\.(propose|observe|approve|reject)$(?![\s\S])/.test(operation)) return "procurement.supplierbank";
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
  if (operation === "fulfillment.case.request" || operation === "fulfillment.case.decide" || operation === "fulfillment.case.return_receive" || operation === "fulfillment.case.return_inspect" || operation === "fulfillment.case.resolution_request" || operation === returnLotOperation) return "fulfillment.case";
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
  if (operation === packProposeOperation || operation === packDecideOperation) return typeof adapters['inventory.pack-conversion.rule']?.lookup === 'function'
    ? { canCheck: true, canClose: false, unavailable: null } : { canCheck: false, canClose: false, unavailable: 'adapter' };
  if (isSupplementOperation(operation)) return typeof adapters['billing.payment.supplement']?.lookup === 'function'
    ? {canCheck: true, canClose: false, unavailable: null} : {canCheck: false, canClose: false, unavailable: 'adapter'};
  if (operation === 'evidence.payment-pdf.reserve' || operation === 'evidence.supplier-document-pdf.reserve') {
    const adapter = adapters[operation === 'evidence.payment-pdf.reserve' ? 'evidence.payment-pdf' : 'evidence.supplier-document-pdf'];
    return typeof adapter?.lookup === 'function' ? {canCheck: true, canClose: false, unavailable: null} : {canCheck: false, canClose: false, unavailable: 'adapter'};
  }
  if (operation === accountContactOperation) return typeof adapters['account.contact']?.lookup === 'function'
    ? {canCheck: true, canClose: false, unavailable: null} : {canCheck: false, canClose: false, unavailable: 'adapter'};
  const namespace = recoveryNamespace(operation);
  if (!namespace) return { canCheck: false, canClose: false, unavailable: "operation" };
  if (namespace === 'catalog.photo' || namespace === 'evidence.catalog-image' || namespace === 'evidence.image-revalidation' || namespace === 'ordering.branchrequest') return typeof adapters[namespace]?.lookup === 'function'
    ? { canCheck: true, canClose: false, unavailable: null } : { canCheck: false, canClose: false, unavailable: 'adapter' };
  if (namespace === "procurement.qualityacceptance" || namespace === "procurement.advancereturn" || namespace === "procurement.payable" || namespace === "stock.source.reconciliation" || namespace === "procurement.stockvaluation" || namespace === "procurement.stockreceipt" || namespace === "procurement.supplierclaim" || namespace === "procurement.supplierdocument" || namespace === "evidence.supplier-document-image" || namespace === "procurement.inspection" || namespace === "evidence.procurement-unboxing-video" || namespace === "procurement.receiving" || namespace === "procurement.settlement" || namespace === "procurement.disbursement" || namespace === "evidence.procurement-disbursement-image" || namespace === "procurement.supplierbank" || namespace === "procurement.funding" || namespace === "procurement.purchase.order" || namespace === "procurement.supplier" || namespace === "procurement.request" || namespace === "accounting.book" || namespace === "ordering.spending.policy" || namespace === "organization.address" || namespace === "billing.refund.payout.verify" || namespace === "billing.refund.payout" || namespace === "evidence.refund-payout-image" || namespace === "billing.return.financial.approve" || namespace === "billing.return.entitlement.request" || namespace === "fulfillment.case") return typeof adapters[namespace]?.lookup === "function"
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
  if (entry.operation === packProposeOperation || entry.operation === packDecideOperation) {
    if (!validEntry(entry)) throw new AccessFailure(400, 'recovery_reference_invalid');
    if (closeReason !== undefined) throw new AccessFailure(400, 'recovery_close_unsupported');
    const requireCurrent = () => { if (!isCurrent()) throw new AccessFailure(409, 'recovery_request_stale'); }; requireCurrent();
    const reference = Object.freeze({ ...entry }), adapter = adapters['inventory.pack-conversion.rule'];
    if (typeof adapter?.lookup !== 'function') throw new AccessFailure(503, 'recovery_adapter_unavailable');
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) return { state: 'unconfirmed' };
    if (!recovered || recovered.state !== 'committed' || Object.keys(recovered).sort().join(',') !== 'result,state') throw new AccessFailure(502, 'recovery_receipt_invalid');
    const result = parsePackResult(recovered.result, packOperation(reference.operation)); requireCurrent();
    try { journal.confirmed(reference); } catch { throw new AccessFailure(400, 'recovery_cleanup_failed'); }
    return { state: 'committed', result: { id: result.proposalId, version: result.revision } };
  }
  if (isSupplementOperation(entry.operation)) {
    if (!validEntry(entry)) throw new AccessFailure(400, 'recovery_reference_invalid');
    if (closeReason !== undefined) throw new AccessFailure(400, 'recovery_close_unsupported');
    const requireCurrent = () => {if (!isCurrent()) throw new AccessFailure(409, 'recovery_request_stale');}; requireCurrent();
    const operation = entry.operation, reference = Object.freeze({...entry}); supplementKeyBinding(operation, reference.key, reference.actorId);
    const adapter = adapters['billing.payment.supplement'];
    if (typeof adapter?.lookup !== 'function') throw new AccessFailure(503, 'recovery_adapter_unavailable');
    const recovered = await adapter.lookup(reference); requireCurrent(); if (recovered === null) return {state: 'unconfirmed'};
    if (!recovered || recovered.state !== 'committed' || Object.keys(recovered).sort().join(',') !== 'result,state') throw new AccessFailure(502, 'recovery_receipt_invalid');
    const result = parseSupplementRecoveryResult(recovered.result, operation, reference.key, reference.actorId); requireCurrent();
    try {journal.confirmed(reference);} catch {throw new AccessFailure(400, 'recovery_cleanup_failed');}
    return {state: 'committed', result};
  }
  if (entry.operation === 'evidence.payment-pdf.reserve' || entry.operation === 'evidence.supplier-document-pdf.reserve') {
    if (!validEntry(entry)) throw new AccessFailure(400, 'recovery_reference_invalid');
    if (closeReason !== undefined) throw new AccessFailure(400, 'recovery_close_unsupported');
    const requireCurrent = () => {if (!isCurrent()) throw new AccessFailure(409, 'recovery_request_stale');}; requireCurrent();
    const reference = Object.freeze({...entry}); pdfReservationKeyOwner(reference.key, reference.actorId, reference.operation === 'evidence.payment-pdf.reserve' ? 'Payment' : 'SupplierInvoice');
    const adapter = adapters[reference.operation === 'evidence.payment-pdf.reserve' ? 'evidence.payment-pdf' : 'evidence.supplier-document-pdf'];
    if (typeof adapter?.lookup !== 'function') throw new AccessFailure(503, 'recovery_adapter_unavailable');
    const recovered = await adapter.lookup(reference); requireCurrent(); if (recovered === null) return {state: 'unconfirmed'};
    if (!recovered || recovered.state !== 'committed' || Object.keys(recovered).sort().join(',') !== 'result,state') throw new AccessFailure(502, 'recovery_receipt_invalid');
    const result = parsePaymentReservation(recovered.result); requireCurrent();
    try {journal.confirmed(reference);} catch {throw new AccessFailure(400, 'recovery_cleanup_failed');}
    return {state: 'committed', result: {id: result.reservationId, version: result.revision}};
  }
  if (entry.operation === accountContactOperation) {
    if (!validEntry(entry)) throw new AccessFailure(400, 'recovery_reference_invalid');
    if (closeReason !== undefined) throw new AccessFailure(400, 'recovery_close_unsupported');
    const requireCurrent = () => {if (!isCurrent()) throw new AccessFailure(409, 'recovery_request_stale');};
    requireCurrent();
    const adapter = adapters['account.contact'];
    if (typeof adapter?.lookup !== 'function') throw new AccessFailure(503, 'recovery_adapter_unavailable');
    const reference = Object.freeze({...entry}), recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) return {state: 'unconfirmed'};
    if (!recovered || recovered.state !== 'committed' || Object.keys(recovered).sort().join(',') !== 'result,state')
      throw new AccessFailure(502, 'recovery_receipt_invalid');
    const result = parseAccountContactResult(recovered.result, reference.actorId); requireCurrent();
    try {journal.confirmed(reference);} catch {throw new AccessFailure(400, 'recovery_cleanup_failed');}
    return {state: 'committed', result: {id: result.historyId, version: result.revision}};
  }
  const namespace = recoveryNamespace(entry.operation);
  if (!namespace) throw new AccessFailure(400, "recovery_operation_unsupported");
  if (!validEntry(entry)) throw new AccessFailure(400, "recovery_reference_invalid");
  const reference = Object.freeze({ ...entry });
  const requireCurrent = () => { if (!isCurrent()) throw new AccessFailure(409, "recovery_request_stale"); };
  requireCurrent();
  if (namespace === "catalog.photo" || namespace === "evidence.catalog-image" || namespace === "evidence.image-revalidation") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) return { state: "unconfirmed" };
    if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state")
      throw new AccessFailure(502, "recovery_receipt_invalid");
    const result = namespace === "catalog.photo"
      ? (() => { const photo = parseCatalogPhotoResult(recovered.result, catalogPhotoKeySku(reference.key));
        return { id: photo.associationId, version: photo.photoRevision }; })()
      : namespace === "evidence.catalog-image"
        ? (() => { catalogPhotoReservationOwner(reference.key); const reservation = parseCatalogPhotoReservation(recovered.result);
          return { id: reservation.reservationId, version: reservation.revision }; })()
        : (() => { const revalidation = parseCatalogPhotoRevalidationResult(recovered.result, catalogPhotoRevalidationKeySku(reference.key));
          return { id: revalidation.requestId, version: "1" }; })();
    requireCurrent();
    try { journal.confirmed(reference); }
    catch { throw new AccessFailure(400, "recovery_cleanup_failed"); }
    return { state: "committed", result };
  }
  let receipt: LookupReceipt | null;
  if (namespace === "procurement.qualityacceptance" || namespace === "procurement.advancereturn" || namespace === "procurement.payable" || namespace === "stock.source.reconciliation" || namespace === "procurement.stockvaluation" || namespace === "procurement.stockreceipt" || namespace === "procurement.supplierclaim" || namespace === "procurement.supplierdocument" || namespace === "evidence.supplier-document-image" || namespace === "procurement.inspection" || namespace === "evidence.procurement-unboxing-video" || namespace === "procurement.receiving" || namespace === "procurement.settlement" || namespace === "procurement.disbursement" || namespace === "evidence.procurement-disbursement-image" || namespace === "procurement.supplierbank" || namespace === "procurement.funding" || namespace === "procurement.purchase.order" || namespace === "procurement.supplier" || namespace === "procurement.request" || namespace === "accounting.book" || namespace === "ordering.spending.policy" || namespace === "organization.address" || namespace === "billing.refund.payout.verify" || namespace === "billing.refund.payout" || namespace === "evidence.refund-payout-image" || namespace === "billing.return.financial.approve" || namespace === "billing.return.entitlement.request" || namespace === "fulfillment.case" || namespace === "billing.payment.replacement.create" || namespace === "billing.payment.review.manage" || namespace === "billing.payment.review.manual" || namespace === "billing.bank-receipt" || namespace === "billing.bank-statement" || namespace === "billing.receiving-account" || namespace === "billing.payment.allocation.correct" || namespace === "billing.payment.allocate" || namespace === "billing.payment.verify" || namespace === "billing.payment.review.correct" || namespace === "billing.payment.submit" || namespace === "billing.payment" || namespace === "evidence.payment-image") {
    if (closeReason !== undefined) throw new AccessFailure(400, "recovery_close_unsupported");
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== "function") throw new AccessFailure(503, "recovery_adapter_unavailable");
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== "committed" || Object.keys(recovered).sort().join(",") !== "result,state") throw new AccessFailure(502, "recovery_receipt_invalid");
      if (namespace === "procurement.qualityacceptance") { const result = parseQualityAcceptanceResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.version } }; }
      else if (namespace === "procurement.advancereturn") { const result = parseAdvanceReturnResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.version } }; }
      else if (namespace === "procurement.payable") { const result = parseSupplierPayableResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.version } }; }
      else if (namespace === "stock.source.reconciliation") { const result = parseSourceReconciliationResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.revision } }; }
      else if (namespace === "procurement.stockvaluation") { const result = parseStockValuationResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.version } }; }
      else if (namespace === "procurement.stockreceipt") { const result = parseStockReceiptResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.receiptId, version: result.revision } }; }
      else if (namespace === "procurement.supplierclaim") { const result = parseSupplierClaimResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.claimId, version: result.claimVersion } }; }
      else if (namespace === "procurement.supplierdocument") { const result = parseSupplierDocumentResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.matchId ?? result.documentId ?? result.draftId, version: result.version } }; }
      else if (namespace === "evidence.supplier-document-image") { supplierDocumentReservationOwner(reference.key); const result = parsePaymentReservation(recovered.result); receipt = { state: "committed", result: { id: result.reservationId, version: result.revision } }; }
      else if (namespace === "procurement.inspection") { const result = parseProcurementInspectionResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.inspectionId ?? result.draftId, version: result.version } }; }
      else if (namespace === "evidence.procurement-unboxing-video") { inspectionReservationOwner(reference.key); const result = parseInspectionReservation(recovered.result); receipt = { state: "committed", result: { id: result.reservationId, version: result.revision } }; }
      else if (namespace === "procurement.receiving") { const result = parseProcurementReceivingResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.receiptId ?? result.noticeId, version: result.version === "0" ? "1" : result.version } }; }
      else if (namespace === "procurement.settlement") { const result = parseProcurementSettlementResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: result.id, version: "1" } }; }
      else if (namespace === "procurement.disbursement") { const result = parseDisbursementResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.draftId, version: result.draftVersion } }; }
      else if (namespace === "evidence.procurement-disbursement-image") { disbursementReservationOwner(reference.key); const result = parsePaymentReservation(recovered.result); receipt = { state: "committed", result: { id: result.reservationId, version: result.revision } }; }
      else if (namespace === "procurement.supplierbank") { const result = parseSupplierBankResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } }; }
      else if (namespace === "procurement.funding") { const result = parseFundingResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.requestId, version: result.requestVersion } }; }
      else if (namespace === "procurement.purchase.order") { const result = parsePurchaseOrderResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } }; }
      else if (namespace === "procurement.supplier") { const result = parseSupplierResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } }; }
      else if (namespace === "procurement.request") { const result = parseProcurementResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.requestId, version: result.requestVersion } }; }
      else if (namespace === "accounting.book") { const result = parseAccountingBookResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.proposalVersion } }; }
      else if (namespace === "ordering.spending.policy") { const result = parseSpendingPolicyResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.policyVersion ?? "1" } }; }
      else if (namespace === "organization.address") { const result = parseOrganizationAddressResult(recovered.result, reference.key, reference.operation); receipt = { state: "committed", result: { id: result.proposalId, version: result.addressVersion ?? "1" } }; }
      else if (namespace === "fulfillment.case") { const result = reference.operation === returnLotOperation ? parseReturnLotResult(recovered.result, reference.key) : reference.operation === 'fulfillment.case.resolution_request' ? parseResolutionResult(recovered.result, reference.key) : reference.operation === 'fulfillment.case.return_inspect' ? parseInspectionResult(recovered.result, reference.key) : reference.operation === 'fulfillment.case.return_receive' ? parseReturnResult(recovered.result, reference.key) : reference.operation === 'fulfillment.case.decide' ? parseCaseDecisionResult(recovered.result, reference.key) : parseCaseResult(recovered.result, reference.key); receipt = { state: "committed", result: { id: reference.operation === 'fulfillment.case.return_receive' || reference.operation === 'fulfillment.case.return_inspect' || reference.operation === 'fulfillment.case.resolution_request' || reference.operation === returnLotOperation ? (result as ReturnResult | InspectionResult | ResolutionResult | ReturnLotResult).returnId : result.caseId, version: result.revision } }; }
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
  } else if (namespace === 'ordering.branchrequest') {
    if (closeReason !== undefined) throw new AccessFailure(400, 'recovery_close_unsupported');
    const adapter = adapters[namespace];
    if (typeof adapter?.lookup !== 'function') throw new AccessFailure(503, 'recovery_adapter_unavailable');
    const recovered = await adapter.lookup(reference); requireCurrent();
    if (recovered === null) receipt = null;
    else {
      if (!recovered || recovered.state !== 'committed' || Object.keys(recovered).sort().join(',') !== 'result,state') throw new AccessFailure(502, 'recovery_receipt_invalid');
      const result = requestResult(recovered.result);
      if ((reference.operation === 'ordering.branchrequest.submit') !== (result.state === 'pending')) throw new AccessFailure(502, 'recovery_receipt_invalid');
      receipt = { state: 'committed', result: { id: result.requestId, version: result.version } };
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
