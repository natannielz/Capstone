import { test } from "node:test";
import assert from "node:assert/strict";
import { runCommand } from "../lib/domain/engine";
import { seedState } from "../lib/domain/seed";
import { DomainError, type Collection } from "../lib/domain/model";
import type { Actor, Role } from "../lib/domain/accounts";
import { attachmentContext, canReadAttachment, scopeState } from "../lib/domain/selectors";

// Destination: website/tests/rbac.test.ts. These relative imports target that location.
// Domain tests cover projection and command authorization. They do not substitute for
// HTTP/session tests: repository.execute must reload the current actor from storage.
function fixture() {
  let state = seedState();
  const date = state.orders[0].createdDate!;
  const actor = (id: string): Actor => {
    const user = state.users.find(u => u.id === id);
    assert.ok(user, `Missing test actor ${id}`);
    return user;
  };
  return {
    get s() { return state; }, date, actor,
    act(who: string, type: string, data: Record<string, unknown>) {
      const output = runCommand(state, actor(who), {
        id: crypto.randomUUID(), type, date, data,
      });
      state = output.state;
      return output.result.id;
    },
    deny(who: string, type: string, data: Record<string, unknown>) {
      const before = structuredClone(state);
      assert.throws(() => runCommand(state, actor(who), {
        id: crypto.randomUUID(), type, date, data,
      }), (error: unknown) => error instanceof DomainError && error.status === 403,
      `${who} must receive 403 for ${type}, not a missing-record or validation error`);
      assert.deepEqual(state, before,
        "Denied commands must preserve all records, audit entries, periods and revision");
    },
  };
}
type Fixture = ReturnType<typeof fixture>;
const customerFinance: Collection[] = [
  "invoices", "invoiceLines", "payments", "allocations", "credits", "refunds",
  "journals", "journalLines", "periods",
];
const ids = (items: { id: string }[]) => items.map(x => x.id).sort();
function fileData(scope: string, targetId: string, name: string) {
  return { attachmentId: crypto.randomUUID(), scope, targetId, name,
    mime: "application/pdf", size: 128 };
}
function changeRole(f: Fixture, id: string, role: Role) {
  f.act("admin", "admin.user", { id, role, active: true });
  assert.equal(f.actor(id).role, role, "Exercise a real administrative role change");
}

// Seed alone has no refunds, credits, expenses or attachments. Populate both sides
// of every boundary so an accidental all-record projection cannot pass vacuously.
function financialFixture() {
  const f = fixture();
  const opsShipment = f.s.shipments.find(sh =>
    f.s.orders.find(o => o.id === sh.orderId)?.divisionId === "div-ops")!;
  const tiShipment = f.s.shipments.find(sh =>
    f.s.orders.find(o => o.id === sh.orderId)?.divisionId === "div-ti")!;
  f.act("kurir", "shipment.dispatch", { id: tiShipment.id });
  f.act("pic-b", "shipment.receive", { id: tiShipment.id, lines:
    f.s.shipmentLines.filter(l => l.shipmentId === tiShipment.id)
      .map(l => ({ id: l.id, accepted: l.qty })) });
  f.act("staf", "sale.finalize", { id: tiShipment.id });
  const tiInvoice = f.act("penagihan", "invoice.issue", { divisionId: "div-ti",
    dueDate: f.date, lines: f.s.shipmentLines.filter(l => l.shipmentId === tiShipment.id)
      .map(l => ({ shipmentLineId: l.id })) });
  const opsInvoice = f.s.invoices.find(i => i.divisionId === "div-ops")!.id;
  const opsCredit = f.act("penagihan", "credit.request", { invoiceId: opsInvoice,
    amount: 1000, reason: "OPS credit privacy fixture" });
  const tiCredit = f.act("penagihan", "credit.request", { invoiceId: tiInvoice,
    amount: 1000, reason: "TI credit privacy fixture" });
  const opsPayment = f.act("penagihan", "payment.record", { divisionId: "div-ops",
    amount: 20000, reference: "PRIVATE-BANK-OPS", payer: "Private payer OPS",
    note: "Private bank reconciliation note OPS" });
  const tiPayment = f.act("penagihan", "payment.record", { divisionId: "div-ti",
    amount: 20000, reference: "PRIVATE-BANK-TI", payer: "Private payer TI",
    note: "Private bank reconciliation note TI" });
  for (const [paymentId, invoiceId] of [[opsPayment, opsInvoice], [tiPayment, tiInvoice]]) {
    f.act("penagihan", "payment.verify", { id: paymentId });
    f.act("penagihan", "payment.allocate", { paymentId,
      lines: [{ invoiceId, amount: 10000 }] });
  }
  const opsRefund = f.act("penagihan", "refund.request", { paymentId: opsPayment,
    amount: 1000, reason: "OPS confirmation" });
  const tiRefund = f.act("penagihan", "refund.request", { paymentId: tiPayment,
    amount: 1000, reason: "TI confirmation" });
  const ownExpense = f.act("staf", "expense.create", { shipmentId: opsShipment.id,
    amount: 5000, category: "Parkir", description: "Staf expense" });
  const otherExpense = f.act("kurir", "expense.create", { shipmentId: opsShipment.id,
    amount: 7500, category: "Parkir", description: "Courier expense" });
  const ownExpenseFile = f.act("staf", "attachment.add",
    fileData("expense", ownExpense, "staf-expense.pdf"));
  const otherExpenseFile = f.act("kurir", "attachment.add",
    fileData("expense", otherExpense, "courier-expense.pdf"));
  const opsPaymentFile = f.act("pic-a", "attachment.add",
    fileData("payment", opsPayment, "PRIVATE-BANK-OPS.pdf"));
  const tiPaymentFile = f.act("pic-b", "attachment.add",
    fileData("payment", tiPayment, "PRIVATE-BANK-TI.pdf"));
  const opsReceipt = f.act("pic-a", "attachment.add",
    fileData("receipt", opsShipment.id, "OPS-receipt.pdf"));
  const tiReceipt = f.act("pic-b", "attachment.add",
    fileData("receipt", tiShipment.id, "TI-receipt.pdf"));
  return { ...f, get s() { return f.s; }, refs: { opsShipment: opsShipment.id,
    tiShipment: tiShipment.id, opsInvoice, tiInvoice, opsCredit, tiCredit,
    opsPayment, tiPayment, opsRefund, tiRefund, ownExpense, otherExpense,
    ownExpenseFile, otherExpenseFile, opsPaymentFile, tiPaymentFile, opsReceipt, tiReceipt } };
}

test("RBAC staf sees operational work but no customer finance or bank-file metadata", () => {
  const f = financialFixture(), before = structuredClone(f.s);
  for (const key of customerFinance) assert.ok(f.s[key].length > 0, `Fixture ${key} is populated`);
  const view = scopeState(f.s, f.actor("staf"));
  for (const key of customerFinance) assert.deepEqual(view[key], [], `Staf must not receive ${key}`);
  assert.ok(view.orders.length > 0 && view.batches.length > 0, "Operational access remains useful");
  assert.deepEqual(ids(view.expenses), [f.refs.ownExpense]);
  assert.ok(view.attachments.some(a => a.id === f.refs.ownExpenseFile));
  assert.ok(view.attachments.some(a => a.id === f.refs.opsReceipt));
  assert.ok(!view.attachments.some(a => a.id === f.refs.otherExpenseFile));
  assert.ok(!JSON.stringify(view).includes("PRIVATE-BANK"), "File names and references are private too");
  assert.ok(!view.audits.some(a => /^(payment|invoice|credit|refund|period)\./.test(a.action)));
  assert.ok(view.users.every(u => u.id === "staf" || u.role === "kurir"));
  assert.ok(view.users.filter(u => u.id !== "staf").every(u => !u.email && !u.phone));
  assert.deepEqual(f.s, before, "Scoping must not mutate stored data");
});

test("RBAC administrator account management does not confer transactional or financial read access", () => {
  const f = financialFixture();
  f.act("admin", "admin.user", { id: "pic-b", role: "pic", divisionId: "div-ti", active: true });
  const view = scopeState(f.s, f.actor("admin"));
  assert.ok(view.users.length > 1 && view.divisions.length > 1 && view.suppliers.length > 0);
  for (const key of ["products", "batches", "orders", "orderLines", "reservations", "substitutions",
    "shipments", "shipmentLines", "complaints", "purchases", "purchaseLines", "supplierPayments",
    "stockReturns", "stocktakes", "expenses", "movements", "attachments", ...customerFinance] as Collection[]) {
    assert.deepEqual(view[key], [], `Administrator must not receive ${key}`);
  }
  assert.ok(view.audits.length > 0);
  assert.ok(view.audits.every(a => /^(admin|profile)\./.test(a.action)));
  assert.ok(!JSON.stringify(view).includes("PRIVATE-BANK"));
});

test("RBAC kepala and laporan retain totals while bank references, payer identity and evidence are redacted", () => {
  const f = financialFixture();
  for (const who of ["kepala", "laporan"]) {
    const view = scopeState(f.s, f.actor(who));
    assert.ok(view.invoices.length > 0 && view.payments.some(p => p.amount === 20000));
    for (const p of view.payments) {
      assert.equal(p.reference, ""); assert.equal(p.note, ""); assert.equal(p.evidence, "");
      assert.equal(p.createdBy, ""); assert.equal(p.verifiedBy, null);
    }
    assert.ok(!JSON.stringify(view).includes("PRIVATE-BANK"));
  }
  assert.ok(scopeState(f.s, f.actor("penagihan")).payments.some(p => p.reference === "PRIVATE-BANK-OPS"));
});

test("RBAC PIC projection isolates the entire transaction graph, not just top-level orders", () => {
  const f = financialFixture();
  for (const [who, own, foreign] of [["pic-a", "ops", "ti"], ["pic-b", "ti", "ops"]] as const) {
    const view = scopeState(f.s, f.actor(who));
    const division = f.actor(who).divisionId;
    assert.ok(view.orders.length > 0 && view.invoices.length > 0 && view.payments.length > 0);
    assert.ok(view.orders.every(o => o.divisionId === division));
    assert.ok(view.orderLines.every(l => view.orders.some(o => o.id === l.orderId)));
    assert.ok(view.shipments.every(sh => view.orders.some(o => o.id === sh.orderId)));
    assert.ok(view.shipmentLines.every(l => view.shipments.some(sh => sh.id === l.shipmentId)));
    assert.ok(view.invoiceLines.every(l => view.invoices.some(i => i.id === l.invoiceId)));
    assert.ok(view.allocations.every(a => view.payments.some(p => p.id === a.paymentId)
      && view.invoices.some(i => i.id === a.invoiceId)));
    assert.ok(view.invoices.some(i => i.id === f.refs[`${own}Invoice`]));
    assert.ok(!view.invoices.some(i => i.id === f.refs[`${foreign}Invoice`]));
    assert.ok(view.credits.some(c => c.id === f.refs[`${own}Credit`]));
    assert.ok(!view.credits.some(c => c.id === f.refs[`${foreign}Credit`]));
    assert.ok(view.refunds.some(r => r.id === f.refs[`${own}Refund`]));
    assert.ok(!view.refunds.some(r => r.id === f.refs[`${foreign}Refund`]));
    assert.ok(view.attachments.some(a => a.id === f.refs[`${own}PaymentFile`]));
    assert.ok(!view.attachments.some(a => a.id === f.refs[`${foreign}Receipt`]));
    assert.ok(view.payments.every(p => p.divisionId === division), "Unknown bank deposits must not appear");
    assert.deepEqual(ids(view.users), [who]);
    assert.deepEqual(view.reservations, []);
    assert.ok(view.batches.every(b => b.cost === 0));
    assert.ok(view.shipmentLines.every(l => l.cost === 0));
    for (const key of ["suppliers", "purchases", "purchaseLines", "supplierPayments",
      "journals", "journalLines", "periods", "audits"] as Collection[]) assert.deepEqual(view[key], []);
  }
});

test("RBAC PIC cannot target another division through commands or attachment upload", () => {
  const f = financialFixture();
  const foreignOrder = f.s.orders.find(o => o.divisionId === "div-ti" && o.status === "submitted")!;
  const foreignLine = f.s.shipmentLines.find(l => l.shipmentId === f.refs.tiShipment)!;
  f.act("kepala", "order.review", { id: foreignOrder.id, approve: true });
  const pendingLine = f.s.orderLines.find(l => l.orderId === foreignOrder.id)!;
  f.act("staf", "substitution.propose", { orderLineId: pendingLine.id,
    productId: f.s.products.find(p => p.id !== pendingLine.productId && p.active)!.id, reason: "Replacement" });
  const substitution = f.s.substitutions.find(s => s.orderLineId === pendingLine.id)!;
  f.deny("pic-a", "order.cancel", { id: foreignOrder.id, reason: "Foreign cancellation" });
  f.deny("pic-a", "shipment.receive", { id: f.refs.tiShipment,
    lines: [{ id: foreignLine.id, accepted: foreignLine.qty }] });
  f.deny("pic-a", "complaint.create", { shipmentLineId: foreignLine.id, qty: 1, reason: "Foreign complaint" });
  f.deny("pic-a", "substitution.decide", { id: substitution.id, approve: true });
  f.deny("pic-a", "attachment.add", fileData("payment", f.refs.tiPayment, "foreign.pdf"));
  f.deny("pic-a", "attachment.add", fileData("receipt", f.refs.tiShipment, "foreign.pdf"));
  // The rightful PIC can still act on the pending substitution.
  f.act("pic-b", "substitution.decide", { id: substitution.id, approve: false });
  assert.equal(f.s.substitutions.find(s => s.id === substitution.id)?.status, "rejected");
});

test("RBAC PIC-supplied division IDs cannot redirect new orders or payments", () => {
  const f = fixture();
  const order = f.act("pic-a", "order.create", { divisionId: "div-ti", neededAt: f.date,
    lines: [{ productId: f.s.products[0].id, qty: 1 }] });
  const payment = f.act("pic-a", "payment.record", { divisionId: "div-ti", amount: 1000, payer: "PIC" });
  assert.equal(f.s.orders.find(o => o.id === order)?.divisionId, "div-ops");
  assert.equal(f.s.payments.find(p => p.id === payment)?.divisionId, "div-ops");
});

test("RBAC attachment reads are deny-by-default for role, ownership, assignment and inactive users", () => {
  const f = financialFixture();
  const paymentReaders = new Set(["pic-a", "penagihan", "pimpinan", "akuntansi"]);
  const expenseReaders = new Set(["kurir", "kepala", "penagihan", "pimpinan", "akuntansi"]);
  for (const actor of f.s.users) {
    assert.equal(canReadAttachment(f.s, actor, f.refs.opsPaymentFile), paymentReaders.has(actor.id), actor.id);
    assert.equal(canReadAttachment(f.s, actor, f.refs.otherExpenseFile), expenseReaders.has(actor.id), actor.id);
    assert.equal(canReadAttachment(f.s, actor, "nonexistent-file"), false);
    assert.equal(canReadAttachment(f.s, { ...actor, active: false }, f.refs.opsReceipt), false);
  }
  const unassigned: Actor = { ...f.actor("kurir"), id: "unassigned-courier" };
  assert.equal(canReadAttachment(f.s, unassigned, f.refs.opsReceipt), false);
  assert.equal(canReadAttachment(f.s, f.actor("kurir"), f.refs.opsReceipt), true);
  const original = f.s.attachments[0];
  f.s.attachments.push({ ...original, id: "unsupported-scope", scope: "internal-secret" });
  for (const actor of f.s.users) assert.equal(canReadAttachment(f.s, actor, "unsupported-scope"), false);
});

test("RBAC staf cannot write evidence to another employee's expense", () => {
  const f = financialFixture();
  assert.equal(canReadAttachment(f.s, f.actor("staf"), f.refs.otherExpenseFile), false);
  f.deny("staf", "attachment.add", fileData("expense", f.refs.otherExpense, "injected-evidence.pdf"));
  assert.throws(() => attachmentContext(f.s, f.actor("staf"), "expense", f.refs.otherExpense),
    (error: unknown) => error instanceof DomainError && error.status === 403);
  assert.deepEqual(attachmentContext(f.s, f.actor("staf"), "expense", f.refs.ownExpense),
    { divisionId: null, shipmentId: f.refs.opsShipment });
});

test("RBAC courier sees only assigned lines and quantities, with prices and cost removed", () => {
  const f = fixture();
  f.s.users.push({ ...f.actor("kurir"), id: "kurir-other", name: "Other Courier" });
  const orderId = f.act("pic-a", "order.create", { neededAt: f.date,
    lines: [{ productId: "teh", qty: 6 }, { productId: "gula", qty: 3 }, { productId: "kopi", qty: 1 }] });
  f.act("kepala", "order.review", { id: orderId, approve: true });
  const [tea, sugar, coffee] = f.s.orderLines.filter(l => l.orderId === orderId);
  f.act("staf", "stock.reserve", { orderLineId: tea.id, qty: 6 });
  f.act("staf", "stock.reserve", { orderLineId: sugar.id, qty: 3 });
  const ownShipment = f.act("staf", "shipment.create", { orderId, courierId: "kurir",
    lines: [{ orderLineId: tea.id, qty: 2 }] });
  const otherShipment = f.act("staf", "shipment.create", { orderId, courierId: "kurir-other",
    lines: [{ orderLineId: tea.id, qty: 4 }, { orderLineId: sugar.id, qty: 3 }] });
  const view = scopeState(f.s, f.actor("kurir"));
  assert.ok(view.shipments.some(sh => sh.id === ownShipment));
  assert.ok(!view.shipments.some(sh => sh.id === otherShipment));
  assert.ok(view.shipments.every(sh => sh.courierId === "kurir"));
  assert.deepEqual(ids(view.orderLines.filter(l => l.orderId === orderId)), [tea.id]);
  assert.equal(view.orderLines.find(l => l.id === tea.id)?.qty, 2, "Hide the 4 units assigned elsewhere");
  assert.ok(!view.orderLines.some(l => l.id === sugar.id || l.id === coffee.id));
  assert.ok(view.orderLines.every(l => l.price === 0 && l.reservedQty === 0));
  assert.ok(view.products.every(p => p.price === 0 && p.minimum === 0));
  assert.ok(view.shipmentLines.every(l => l.price === 0 && l.cost === 0));
  assert.deepEqual(view.batches, []); assert.deepEqual(view.reservations, []);
  assert.deepEqual(view.substitutions, []);
  for (const key of customerFinance) assert.deepEqual(view[key], []);
  f.deny("kurir", "shipment.dispatch", { id: otherShipment });
  f.act("kurir-other", "shipment.dispatch", { id: otherShipment });
  f.deny("kurir", "shipment.proof", { id: otherShipment, receiver: "Other", evidence: "Foreign proof" });
  f.deny("kurir", "shipment.fail", { id: otherShipment, reason: "Foreign failure" });
  f.deny("kurir", "expense.create", { shipmentId: otherShipment, amount: 1000,
    category: "Parkir", description: "Foreign expense" });
  f.deny("kurir", "attachment.add", fileData("receipt", otherShipment, "foreign-receipt.pdf"));
  f.act("kurir", "shipment.dispatch", { id: ownShipment });
  assert.equal(f.s.shipments.find(sh => sh.id === ownShipment)?.status, "dispatched");
});

test("RBAC profile update cannot change identity, authority, login email or inject extra properties", () => {
  const f = fixture();
  const injections: Record<string, unknown>[] = [
    { role: "admin" }, { divisionId: "div-ti" }, { active: false }, { id: "pic-b" },
    { email: "other@example.test" }, { avatar: "https://example.test/a.png" },
    { password: "overwrite" }, { createdBy: "admin" },
    JSON.parse('{"__proto__":{"role":"admin"}}') as Record<string, unknown>,
  ];
  for (const extra of injections) f.deny("pic-a", "profile.update", { name: "Attempted edit", ...extra });
  const before = structuredClone(f.actor("pic-a")), other = structuredClone(f.actor("pic-b"));
  f.act("pic-a", "profile.update", { name: "Nadia Updated", phone: "081234567890", position: "PIC Pantry" });
  assert.equal(f.actor("pic-a").name, "Nadia Updated");
  for (const key of ["id", "role", "divisionId", "active", "email", "avatar"] as const)
    assert.equal(f.actor("pic-a")[key], before[key]);
  assert.deepEqual(f.actor("pic-b"), other);
});

test("RBAC inactive users cannot read state, edit profiles or read attachments", () => {
  const f = financialFixture();
  f.act("admin", "admin.user", { id: "pic-a", role: "pic", divisionId: "div-ops", active: false });
  assert.throws(() => scopeState(f.s, f.actor("pic-a")),
    (error: unknown) => error instanceof DomainError && error.status === 403);
  f.deny("pic-a", "profile.update", { name: "Inactive change" });
  f.deny("pic-a", "order.create", { neededAt: f.date, lines: [{ productId: "teh", qty: 1 }] });
  assert.equal(canReadAttachment(f.s, f.actor("pic-a"), f.refs.opsPaymentFile), false);
});

test("RBAC credit requester cannot self-approve after being promoted to pimpinan", () => {
  const f = financialFixture();
  changeRole(f, "penagihan", "pimpinan");
  f.deny("penagihan", "credit.approve", { id: f.refs.opsCredit });
  assert.equal(f.s.credits.find(c => c.id === f.refs.opsCredit)?.status, "requested");
  f.act("pimpinan", "credit.approve", { id: f.refs.opsCredit });
  assert.equal(f.s.credits.find(c => c.id === f.refs.opsCredit)?.approvedBy, "pimpinan");
});

test("RBAC expense requester cannot self-approve after a role change", () => {
  const f = financialFixture();
  changeRole(f, "staf", "pimpinan");
  f.deny("staf", "expense.approve", { id: f.refs.ownExpense });
  assert.equal(f.s.expenses.find(e => e.id === f.refs.ownExpense)?.status, "requested");
  f.act("pimpinan", "expense.approve", { id: f.refs.ownExpense });
  assert.equal(f.s.expenses.find(e => e.id === f.refs.ownExpense)?.approvedBy, "pimpinan");
});

test("RBAC monthly report submitter cannot self-approve after a role change", () => {
  const f = fixture(), month = f.date.slice(0, 7);
  f.act("laporan", "period.submit", { month });
  changeRole(f, "laporan", "pimpinan");
  f.deny("laporan", "period.approve", { id: month });
  assert.equal(f.s.periods.find(p => p.id === month)?.status, "review");
  f.act("pimpinan", "period.approve", { id: month });
  f.act("akuntansi", "period.close", { id: month });
  assert.equal(f.s.periods.find(p => p.id === month)?.closedBy, "akuntansi");
});

test("RBAC refund requester cannot self-approve after a role change", () => {
  const f = financialFixture();
  changeRole(f, "penagihan", "pimpinan");
  f.deny("penagihan", "refund.approve", { id: f.refs.opsRefund });
  f.act("pimpinan", "refund.approve", { id: f.refs.opsRefund });
  assert.equal(f.s.refunds.find(r => r.id === f.refs.opsRefund)?.approvedBy, "pimpinan");
});

test("RBAC rejected role commands preserve the complete original state", () => {
  const f = financialFixture();
  const order = f.s.orders.find(o => o.status === "submitted")!;
  const line = f.s.orderLines.find(l => l.orderId === order.id)!;
  const record = f.s.payments.find(p => p.status === "recorded")!;
  const attempts: [string, string, Record<string, unknown>][] = [
    ["pic-a", "payment.verify", { id: record.id }],
    ["staf", "payment.allocate", { paymentId: f.refs.opsPayment,
      lines: [{ invoiceId: f.refs.opsInvoice, amount: 1 }] }],
    ["kurir", "sale.finalize", { id: f.refs.opsShipment }],
    ["penagihan", "order.review", { id: order.id, approve: true }],
    ["laporan", "product.update", { id: "teh", price: 100, minimum: 1, returnMonths: 1 }],
    ["admin", "stock.reserve", { orderLineId: line.id, qty: 1 }],
    ["admin", "payment.verify", { id: record.id }],
    ["pimpinan", "invoice.issue", { divisionId: "div-ops", dueDate: f.date,
      lines: [{ shipmentLineId: f.s.shipmentLines[0].id }] }],
    ["akuntansi", "credit.approve", { id: f.refs.opsCredit }],
    ["kepala", "payment.verify", { id: record.id }],
    ["pic-a", "admin.user", { id: "pic-a", role: "admin", active: true }],
  ];
  for (const [who, type, data] of attempts) f.deny(who, type, data);
});
