import type {State} from "./model";
import {today} from "./selectors";

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
    if (!shipment || !order || !product) return [];
    return [{id: line.id, divisionId: order.divisionId, shipmentNumber: shipment.number, orderNumber: order.number,
      product: product.name, sku: product.sku, unit: product.unit, qty: line.accepted, price: line.price,
      value: line.accepted * line.price, date: line.finalizedDate || shipment.receivedDate || shipment.date}];
  }).sort((a,b) => a.date.localeCompare(b.date) || a.shipmentNumber.localeCompare(b.shipmentNumber) || a.sku.localeCompare(b.sku));
}

export function invoiceSelection(state: State, divisionId: string, ids: string[]) {
  const available = billableRows(state).filter(row => row.divisionId === divisionId);
  const selected = available.filter(row => ids.includes(row.id));
  const error = !divisionId ? "Pilih divisi pemesan."
    : !ids.length ? "Pilih minimal satu baris penerimaan untuk invoice."
    : ids.length > MAX_INVOICE_LINES ? `Satu invoice maksimal ${MAX_INVOICE_LINES} baris. Kurangi pilihan dan terbitkan sisanya pada invoice berikutnya.`
    : new Set(ids).size !== ids.length || selected.length !== ids.length ? "Sebagian pilihan sudah ditagih atau tidak tersedia untuk divisi ini. Periksa pilihan sebelum melanjutkan."
    : "";
  return {selected, error, total: selected.reduce((total,row)=>total+row.value,0), remaining: available.length-selected.length,
    minimumDate: selected.reduce((date,row)=>row.date>date?row.date:date, earliestOpenInvoiceDate(state))};
}
