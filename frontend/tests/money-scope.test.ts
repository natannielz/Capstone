import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { formatDisplayMoney, formatMoney, formatMoneyText } from "../src/lib/format";
import { orderAmount, orderDisplayAmount } from "../src/modules/ordering/order-quote-client";
import { formatAccountingReportMoney } from "../src/modules/reporting/accounting-report-client";

// Orchestrator ruling (Task 3.5 fix round 1): staff finance and accounting screens keep fixed two decimals; buyer-facing
// storefront and ordering screens drop ",00" on whole rupiah amounts. Real fractions are kept on both.
test("exact (finance) path keeps two decimals; display (buyer) path drops ,00 only on whole rupiah", () => {
  for (const [value, exact, display] of [["1250000.00", "Rp1.250.000,00", "Rp1.250.000"], ["0.00", "Rp0,00", "Rp0"], ["12000.25", "Rp12.000,25", "Rp12.000,25"],
    ["12000.5", "Rp12.000,50", "Rp12.000,50"], ["-25000.00", "-Rp25.000,00", "-Rp25.000"]] as const) {
    assert.equal(formatMoney("IDR", value), exact, `formatMoney ${value}`);
    assert.equal(formatDisplayMoney("IDR", value), display, `formatDisplayMoney ${value}`);
  }
  // Order amounts arrive as validated two-decimal API strings.
  for (const [value, exact, display] of [["1250000.00", "Rp1.250.000,00", "Rp1.250.000"], ["0.00", "Rp0,00", "Rp0"], ["12000.25", "Rp12.000,25", "Rp12.000,25"]] as const) {
    assert.equal(orderAmount("IDR", value), exact, `orderAmount ${value}`);
    assert.equal(orderDisplayAmount("IDR", value), display, `orderDisplayAmount ${value}`);
  }
  assert.equal(formatMoneyText("7000.00"), "Rp7.000,00", "procurement review panels are finance: exact");
  assert.equal(formatAccountingReportMoney("1250000.00", "IDR"), "Rp1.250.000,00", "accounting reports: exact");
  assert.equal(formatDisplayMoney("USD", "1500.00"), "USD 1.500,00", "other currencies keep their decimals");
});

const finance = [
  "src/modules/billing/InvoiceWorkspace.tsx", "src/modules/billing/PaymentReviewWorkspace.tsx", "src/modules/billing/PaymentVerificationPanel.tsx",
  "src/modules/billing/PaymentAllocationPanel.tsx", "src/modules/billing/PaymentAllocationCorrectionPanel.tsx", "src/modules/billing/PaymentWorkspace.tsx",
  "src/modules/billing/BankReceiptWorkspace.tsx", "src/modules/billing/BankStatementWorkspace.tsx", "src/modules/invoice-releases/InvoiceReleaseWorkspace.tsx",
  "src/modules/fulfillment/RefundSettlementPanel.tsx", "src/modules/fulfillment/RefundPayoutPanel.tsx", "src/modules/fulfillment/ReturnFinancialApprovalPanel.tsx",
  "src/modules/fulfillment/ReturnEntitlementPanel.tsx", "src/modules/reporting/accounting-report-client.ts",
  "src/modules/procurement/ProcurementDisbursementWorkspace.tsx", "src/modules/procurement/ProcurementReceivingWorkspace.tsx", "src/modules/procurement/ProcurementSettlementWorkspace.tsx",
  "src/modules/procurement/procurement-supplier-payable-forms.tsx", "src/modules/procurement/procurement-advance-return-forms.tsx", "src/modules/procurement/procurement-stock-valuation-forms.tsx",
  "src/modules/procurement/ProcurementFundingWorkspace.tsx", "src/modules/procurement/PurchaseOrderWorkspace.tsx",
];
const buyer = [
  "src/modules/catalog/BuyerCatalog.tsx", "src/modules/ordering/AddToCartPanel.tsx", "src/modules/ordering/CartWorkspace.tsx",
  "src/modules/ordering/OrderHistoryWorkspace.tsx", "src/modules/ordering/OrderDraftEditor.tsx", "src/modules/ordering/OrderBulkEntry.tsx", "src/modules/ordering/order-checkout-details.tsx",
];
test("finance screens use the exact formatter; buyer storefront and ordering screens use the display formatter", () => {
  for (const file of finance) assert.doesNotMatch(readFileSync(file, "utf8"), /formatDisplayMoney|orderDisplayAmount/, `${file} must keep fixed decimals`);
  for (const file of buyer) {
    const source = readFileSync(file, "utf8");
    assert.match(source, /formatDisplayMoney|orderDisplayAmount/, `${file} shows buyer amounts`);
    assert.doesNotMatch(source, /\bformatMoney\(|\borderAmount\(/, `${file} must not show exact finance amounts`);
  }
});
