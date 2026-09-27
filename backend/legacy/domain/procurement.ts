import type {Purchase, PurchaseLine, State} from "./model";
import {sum} from "./selectors";

export function purchaseLineRemaining(line:PurchaseLine) {
  return line.qty-line.received-(line.cancelled??0);
}

export function purchaseSummary(s:State,purchase:Purchase) {
  const lines=s.purchaseLines.filter(line=>line.purchaseId===purchase.id);
  const payments=s.supplierPayments.filter(payment=>payment.purchaseId===purchase.id);
  const ordered=sum(lines.map(line=>line.qty*line.cost));
  const retained=sum(lines.map(line=>(line.qty-(line.cancelled??0))*line.cost));
  const received=sum(lines.map(line=>line.received*line.cost));
  const paid=sum(payments.map(payment=>payment.amount));
  return {lines,ordered,retained,received,paid,
    remainingQty:["draft","ordered"].includes(purchase.status)?sum(lines.map(purchaseLineRemaining)):0,
    advance:Math.max(0,paid-received),unpaid:Math.max(0,received-paid),
    paymentRemaining:Math.max(0,retained-paid),
    unusedFunding:purchase.fundingStatus==="disbursed"?Math.max(0,purchase.fundingAmount-paid):0};
}
