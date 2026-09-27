import type {Payment, State} from "./model";
import {invoiceBalance, today} from "./selectors";
import {buyerKey, buyerLabel, sameBuyer, type BuyerRef} from "./buyers";

export const MAX_INVOICE_LINES = 100;
export function earliestOpenInvoiceDate(state: State) {
  const lastClosed=state.periods.filter(period=>period.status==="closed").map(period=>period.id).sort().at(-1);
  if(!lastClosed)return "";
  const [year,month]=lastClosed.split("-").map(Number);
  return new Date(Date.UTC(year,month,1)).toISOString().slice(0,10);
}

export function suggestedInvoiceDates(state: State, referenceDate=today()) {
  const earliest=earliestOpenInvoiceDate(state);
  const date=earliest>referenceDate?earliest:referenceDate;
  const due=new Date(`${date}T00:00:00Z`);
  due.setUTCDate(due.getUTCDate()+30);
  return {date,dueDate:due.toISOString().slice(0,10)};
}

export function billableRows(state: State) {
  const invoiced = new Set(state.invoiceLines.map(line => line.shipmentLineId));
  return state.shipmentLines.filter(line => line.finalized && line.accepted > 0 && !invoiced.has(line.id)).flatMap(line => {
    const shipment = state.shipments.find(item => item.id === line.shipmentId);
    const order = shipment && state.orders.find(item => item.id === shipment.orderId);
    const orderLine = state.orderLines.find(item => item.id === line.orderLineId);
    const product = orderLine && state.products.find(item => item.id === orderLine.productId);
    if (!shipment || !order || !product || !buyerKey(order)) return [];
    return [{id: line.id, divisionId: order.divisionId, customerId: order.customerId ?? null, shipmentNumber: shipment.number, orderNumber: order.number,
      product: product.name, sku: product.sku, unit: product.unit, qty: line.accepted, price: line.price,
      value: line.accepted * line.price, date: line.finalizedDate || shipment.receivedDate || shipment.date}];
  }).sort((a,b) => a.date.localeCompare(b.date) || a.shipmentNumber.localeCompare(b.shipmentNumber) || a.sku.localeCompare(b.sku));
}

/** Only buyers with final, unbilled receipts appear. No customer account list is needed. */
export function invoiceBuyerOptions(state: State) {
  const options = new Map<string, {key:string; label:string; buyer:BuyerRef; count:number}>();
  for (const row of billableRows(state)) {
    const key = buyerKey(row)!;
    const existing = options.get(key);
    if (existing) existing.count += 1;
    else {
      const buyer = {divisionId:row.divisionId, customerId:row.customerId};
      options.set(key, {key, label:buyerLabel(state,buyer), buyer, count:1});
    }
  }
  return [...options.values()].sort((left,right)=>left.label.localeCompare(right.label,"id-ID"));
}

export function invoiceSelection(state: State, selectedBuyer: string | null | BuyerRef, ids: string[]) {
  // String division IDs remain supported for callers and saved v6 workflows.
  const buyer = typeof selectedBuyer === "string" ? {divisionId:selectedBuyer} : selectedBuyer || {divisionId:null};
  const available = billableRows(state).filter(row => sameBuyer(row,buyer));
  const selected = available.filter(row => ids.includes(row.id));
  const error = !buyerKey(buyer) ? "Pilih pembeli yang akan ditagih."
    : !ids.length ? "Pilih minimal satu baris penerimaan untuk invoice."
    : ids.length > MAX_INVOICE_LINES ? `Satu invoice maksimal ${MAX_INVOICE_LINES} baris. Kurangi pilihan dan terbitkan sisanya pada invoice berikutnya.`
    : new Set(ids).size !== ids.length || selected.length !== ids.length ? "Sebagian pilihan sudah ditagih atau tidak tersedia untuk pembeli ini. Periksa pilihan sebelum melanjutkan."
    : "";
  return {selected, error, total: selected.reduce((total,row)=>total+row.value,0), remaining: available.length-selected.length,
    minimumDate: selected.reduce((date,row)=>row.date>date?row.date:date, earliestOpenInvoiceDate(state))};
}

/** Unknown and ambiguous payers never match an invoice, including null division IDs. */
export function allocationInvoices(state:State, payment:Payment, selectedIds:string[] = []) {
  if (!buyerKey(payment)) return [];
  return state.invoices.filter(invoice=>sameBuyer(invoice,payment)
    && (invoiceBalance(state,invoice.id)>0 || selectedIds.includes(invoice.id)))
    .sort((left,right)=>left.dueDate.localeCompare(right.dueDate)||left.number.localeCompare(right.number));
}
