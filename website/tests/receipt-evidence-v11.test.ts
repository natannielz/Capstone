import {test} from "node:test";
import assert from "node:assert/strict";
import {DEMO_ACCOUNTS, type Actor} from "../lib/domain/accounts";
import {journal, runCommand} from "../lib/domain/engine";
import {DomainError, type Attachment, type Command} from "../lib/domain/model";
import {emptyState} from "../lib/domain/seed";
import {canReadAttachment, invoiceTotal, today} from "../lib/domain/selectors";
import {saveAttachment, type AttachmentUpload} from "../lib/server/attachment-upload";

function fixture() {
  const f = {state: emptyState(), objects: new Map<string, ArrayBuffer>(), puts: 0, failStorage: false};
  const date = today();
  f.state.users = structuredClone(DEMO_ACCOUNTS);
  f.state.users.push(
    {id: "customer-b", name: "Pelanggan B", role: "customer", divisionId: null, active: true},
    {id: "courier-b", name: "Kurir lain", role: "kurir", divisionId: null, active: true},
  );
  f.state.divisions = [
    {id: "div-ops", name: "Operasional", code: "OPS", address: "Alamat OPS"},
    {id: "div-ti", name: "Teknologi", code: "TI", address: "Alamat TI"},
  ];
  f.state.products = [{id: "p", sku: "P", name: "Barang penerimaan", category: "OMI", unit: "pcs", price: 10000, minimum: 0, returnMonths: 1, active: true}];
  f.state.batches = [{id: "b", productId: "p", code: "B", qty: 100, held: 0, cost: 6000, expiry: "2099-01-01", location: "Rak uji"}];
  journal(f.state, date, "opening", "Saldo awal", [["inventory", 600000, 0], ["capital", 0, 600000]]);
  const actor = (id: string) => f.state.users.find(user => user.id === id)!;
  const execute = (user: Actor, command: Command) => {
    const outcome = runCommand(f.state, actor(user.id), command);
    f.state = outcome.state;
    return outcome.result;
  };
  const act = (who: string, type: string, data: Record<string, unknown>) => execute(actor(who), {id: crypto.randomUUID(), type, date, data}).id;
  function dispatch(who = "pic-a") {
    const orderId = act(who, "order.create", {neededAt: date, recipientName: "Penerima uji", recipientPhone: "0800000000", address: "Alamat uji", lines: [{productId: "p", qty: 3, unitPrice: 10000}]});
    act("kepala", "order.review", {id: orderId, approve: true});
    const line = f.state.orderLines.find(line => line.orderId === orderId)!;
    act("staf", "stock.reserve", {orderLineId: line.id, qty: 3});
    const shipmentId = act("staf", "shipment.create", {orderId, courierId: "kurir", lines: [{orderLineId: line.id, qty: 3}]});
    act("kurir", "shipment.dispatch", {id: shipmentId});
    const shipmentLineId = f.state.shipmentLines.find(line => line.shipmentId === shipmentId)!.id;
    return {orderId, shipmentId, shipmentLineId, receipt: {id: shipmentId, receiver: "Penerima uji", lines: [{id: shipmentLineId, accepted: 3}]}};
  }
  const dependencies = {
    files: {
      async get(id: string) {const bytes = f.objects.get(id); return bytes ? {body: new Uint8Array(bytes)} : null;},
      async put(id: string, bytes: ArrayBuffer) {f.puts++; if (f.failStorage) throw new Error("Storage unavailable"); f.objects.set(id, bytes.slice(0));},
    },
    async loadState() {return structuredClone(f.state);},
    async execute(user: Actor, command: Command) {return execute(user, command);},
  };
  // Real protected upload path and stored bytes; never insert metadata to grant receipt permission.
  const upload = (shipmentId: string, who = "kurir", patch: Partial<AttachmentUpload> = {}) => saveAttachment(actor(who), {
    scope: "receipt", targetId: shipmentId, name: "surat-jalan-bertanda-tangan-simulasi.pdf",
    content: new TextEncoder().encode("%PDF-1.7\nSurat Jalan simulasi: diterima sesuai rincian. Tanda tangan penerima uji.\n%%EOF").buffer,
    ...patch,
  }, dependencies);
  function denied(action: () => unknown, pattern: RegExp) {
    const before = structuredClone(f.state);
    assert.throws(action, pattern);
    assert.deepEqual(f.state, before, "Denial must preserve every transaction, journal, audit and period revision");
  }
  const issue = (ids: ReturnType<typeof dispatch>) => {
    const order = f.state.orders.find(order => order.id === ids.orderId)!;
    return act("penagihan", "invoice.issue", {divisionId: order.divisionId, customerId: order.customerId, dueDate: date, lines: [{shipmentLineId: ids.shipmentLineId}]});
  };
  return {f, actor, act, dispatch, upload, denied, issue};
}

test("v11 staff cannot receive, finalize or invoice with a note instead of receipt evidence", () => {
  const {f, act, dispatch, denied, issue} = fixture(), ids = dispatch();
  for (const evidence of [undefined, "", "ok", "Berkas bukti: forged.pdf"]) {
    denied(() => act("staf", "shipment.receive", {...ids.receipt, evidence}), /Unggah foto atau PDF Surat Jalan bertanda tangan/);
  }
  denied(() => act("staf", "sale.finalize", {id: ids.shipmentId}), /Bukti dan konfirmasi/);
  denied(() => issue(ids), /belum siap ditagih/);
  assert.equal(f.state.attachments.length, 0);
  assert.equal(f.state.shipments[0].status, "dispatched");
  assert.equal(f.state.shipmentLines[0].accepted, 0);
});

test("v11 courier or staff free-text delivery proof cannot bypass protected receipt upload", () => {
  for (const who of ["kurir", "staf"]) {
    const {act, dispatch, denied} = fixture(), ids = dispatch();
    act(who, "shipment.proof", {id: ids.shipmentId, receiver: "Penerima", evidence: "Sudah ditandatangani"});
    denied(() => act("staf", "shipment.receive", ids.receipt), /Catatan saja belum cukup/);
  }
});

test("v11 receipt proof must belong to the exact shipment and buyer, with receipt scope", async () => {
  const {f, dispatch, upload, act, denied} = fixture(), own = dispatch(), other = dispatch();
  const result = await upload(other.shipmentId);
  denied(() => act("staf", "shipment.receive", own.receipt), /Unggah foto atau PDF/);
  const attachment = f.state.attachments.find(item => item.id === result.id)!;
  const original = structuredClone(attachment);
  const mismatches: Partial<Attachment>[] = [
    {targetId: own.shipmentId},
    {shipmentId: own.shipmentId},
    {targetId: own.shipmentId, shipmentId: own.shipmentId, scope: "expense"},
    {targetId: own.shipmentId, shipmentId: own.shipmentId, divisionId: "div-ti"},
    {targetId: own.shipmentId, shipmentId: own.shipmentId, customerId: "customer-b"},
  ];
  // Corrupt/mismatched legacy metadata must fail closed even when its display name looks valid.
  for (const patch of mismatches) {
    Object.assign(attachment, original, {customerId: undefined}, patch);
    denied(() => act("staf", "shipment.receive", own.receipt), /Unggah foto atau PDF/);
  }
});

test("v11 unauthorized buyers, unassigned courier and other staff roles cannot upload proof or receive", async () => {
  for (const buyer of ["pic-a", "customer-demo"]) {
    const {f, dispatch, upload, act, denied} = fixture(), ids = dispatch(buyer);
    const writers = buyer === "pic-a" ? ["pic-b", "customer-demo", "customer-b"] : ["pic-a", "pic-b", "customer-b"];
    for (const who of [...writers, "courier-b", "admin", "kepala", "penagihan", "laporan", "pimpinan", "akuntansi"]) {
      await assert.rejects(upload(ids.shipmentId, who), error => error instanceof DomainError && error.status === 403, who);
      denied(() => act(who, "shipment.receive", {...ids.receipt, evidence: "ok"}), /akses|tugas|divisi lain|pembeli lain/);
    }
    assert.equal(f.puts, 0); assert.equal(f.objects.size, 0); assert.equal(f.state.attachments.length, 0);
  }
});

test("v11 actual assigned-courier receipt upload enables staff confirmation and bills only accepted goods", async () => {
  const {f, actor, act, dispatch, upload, denied, issue} = fixture(), ids = dispatch();
  const proof = await upload(ids.shipmentId);
  assert.equal(f.objects.size, 1); assert.ok(f.objects.get(proof.id)!.byteLength > 0);
  assert.equal(canReadAttachment(f.state, actor("pic-a"), proof.id), true);
  assert.equal(canReadAttachment(f.state, actor("staf"), proof.id), true);
  for (const who of ["pic-b", "customer-demo", "courier-b", "admin"]) assert.equal(canReadAttachment(f.state, actor(who), proof.id), false, who);
  act("staf", "shipment.receive", {...ids.receipt, lines: [{id: ids.shipmentLineId, accepted: 2, reason: "Satu barang ditolak sesuai Surat Jalan"}]});
  denied(() => act("staf", "sale.finalize", {id: ids.shipmentId}), /Selesaikan komplain/);
  act("staf", "shipment.return", {shipmentLineId: ids.shipmentLineId, qty: 1, good: true, reason: "Dikembalikan sesuai Surat Jalan"});
  act("staf", "complaint.resolve", {id: f.state.complaints[0].id, resolution: "Satu barang kembali dan tidak ditagihkan"});
  act("staf", "sale.finalize", {id: ids.shipmentId});
  const invoiceId = issue(ids);
  assert.equal(invoiceTotal(f.state, invoiceId), 20000);
  assert.equal(f.state.shipmentLines[0].accepted, 2);
  assert.equal(f.state.shipmentLines[0].returned, 1);
  assert.equal(f.state.batches[0].qty, 98);
  denied(() => act("staf", "shipment.receive", ids.receipt), /belum dapat diterima/);
  denied(() => issue(ids), /sudah ditagih/);
});

test("v11 staff may upload buyer proof for a customer shipment; another buyer remains denied", async () => {
  const {f, actor, act, dispatch, upload, denied, issue} = fixture(), ids = dispatch("customer-demo");
  const proof = await upload(ids.shipmentId, "staf");
  assert.equal(canReadAttachment(f.state, actor("customer-demo"), proof.id), true);
  assert.equal(canReadAttachment(f.state, actor("customer-b"), proof.id), false);
  denied(() => act("customer-b", "shipment.receive", ids.receipt), /pembeli lain/);
  act("staf", "shipment.receive", ids.receipt);
  act("staf", "sale.finalize", {id: ids.shipmentId});
  const invoiceId = issue(ids);
  assert.equal(invoiceTotal(f.state, invoiceId), 30000);
});

test("v11 direct authenticated PIC or customer confirmation remains valid without a file", () => {
  for (const buyer of ["pic-a", "customer-demo"]) {
    const {f, act, dispatch, issue} = fixture(), ids = dispatch(buyer);
    act(buyer, "shipment.receive", ids.receipt);
    assert.equal(f.state.attachments.length, 0);
    assert.match(f.state.shipments[0].evidence, /Dikonfirmasi akun/);
    act("staf", "sale.finalize", {id: ids.shipmentId});
    const invoiceId = issue(ids);
    assert.equal(invoiceTotal(f.state, invoiceId), 30000);
  }
});

test("v11 failed storage upload publishes no proof and cannot authorize staff receipt", async () => {
  const {f, act, dispatch, upload, denied} = fixture(), ids = dispatch();
  f.failStorage = true;
  const before = structuredClone(f.state);
  await assert.rejects(upload(ids.shipmentId), /Storage unavailable/);
  assert.deepEqual(f.state, before); assert.equal(f.state.attachments.length, 0);
  denied(() => act("staf", "shipment.receive", {...ids.receipt, evidence: "Unggahan sedang diproses"}), /Unggah foto atau PDF/);
});
