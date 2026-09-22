import { test } from "node:test";
import assert from "node:assert/strict";
import { dashboardView } from "../lib/domain/dashboard-views";
import { DEMO_ACCOUNTS } from "../lib/domain/accounts";
import { canOpenPage } from "../lib/domain/navigation";
import { seedState, emptyState } from "../lib/domain/seed";
import { scopeState, invoiceBalance } from "../lib/domain/selectors";

function actor(id: string) {
  const account = DEMO_ACCOUNTS.find(item => item.id === id);
  assert.ok(account);
  return account;
}

test("dashboard destinations remain allowed for every internal account in empty and populated states", () => {
  for (const source of [emptyState(), seedState()]) {
    for (const account of DEMO_ACCOUNTS) {
      const s = scopeState(source, account);
      const before = structuredClone(s);
      const view = dashboardView({ s, actor: account });
      if (account.role === "customer") {
        assert.equal(view, null);
        continue;
      }
      assert.ok(view, account.id);
      assert.ok(canOpenPage(account.role, view.priority.page), account.id);
      assert.equal(view.queues.length, 3);
      for (const queue of view.queues) assert.ok(canOpenPage(account.role, queue.page), `${account.id}: ${queue.page}`);
      assert.deepEqual(s, before, "Reading the dashboard must not reorder or mutate state");
    }
  }
});

test("PIC priority only counts actionable orders from that division", () => {
  const source = seedState();
  const assigned = source.shipments.find(shipment => shipment.status === "ready");
  assert.ok(assigned);
  assigned.status = "dispatched";
  const a = actor("pic-a"), b = actor("pic-b");
  const aView = dashboardView({ s: scopeState(source, a), actor: a });
  const bView = dashboardView({ s: scopeState(source, b), actor: b });
  assert.equal(aView?.priority.page, "catalog");
  assert.equal(bView?.priority.page, "orders");
  assert.equal(bView?.priority.query?.queue, "needs-pic");
  assert.equal(bView?.priority.count, 1);
});

test("financial dashboard excludes paid invoices and orders unpaid invoices by earliest due date", () => {
  const source = seedState();
  const template = source.invoices[0];
  const line = source.invoiceLines[0];
  assert.ok(template && line);
  source.invoices = [
    { ...template, id: "later", number: "INV-003", dueDate: "2026-10-01" },
    { ...template, id: "early", number: "INV-002", dueDate: "2026-08-01" },
    { ...template, id: "same-date", number: "INV-001", dueDate: "2026-08-01" },
    { ...template, id: "paid", number: "INV-000", dueDate: "2026-07-01" },
  ];
  source.invoiceLines = source.invoices.map(invoice => ({ ...line, id: `line-${invoice.id}`, invoiceId: invoice.id, subtotal: 100, tax: 0 }));
  source.credits = [];
  source.allocations = [{ id: "paid-allocation", paymentId: source.payments[0].id, invoiceId: "paid", amount: 100, date: "2026-07-01" }];
  const account = actor("akuntansi");
  const s = scopeState(source, account);
  const view = dashboardView({ s, actor: account });
  assert.deepEqual(view?.openInvoices.map(invoice => invoice.id), ["same-date", "early", "later"]);
  assert.equal(invoiceBalance(s, "paid"), 0);
  assert.deepEqual(source.invoices.map(invoice => invoice.id), ["later", "early", "same-date", "paid"]);
});

test("courier priority opens an assigned task and empty state points to history", () => {
  const source = seedState(), account = actor("kurir");
  const view = dashboardView({ s: scopeState(source, account), actor: account });
  assert.equal(view?.priority.page, "deliveries");
  assert.ok(view?.priority.id);
  assert.ok(source.shipments.some(shipment => shipment.id === view.priority.id && shipment.courierId === account.id));
  const empty = dashboardView({ s: scopeState(emptyState(), account), actor: account });
  assert.equal(empty?.priority.waiting, false);
  assert.equal(empty?.priority.query?.status, "received");
  assert.equal(empty?.activeShipments.length, 0);
});

test("admin totals describe actual settings data rather than an unavailable activity log", () => {
  const source = seedState(), account = actor("admin");
  const s = scopeState(source, account);
  const view = dashboardView({ s, actor: account });
  assert.equal(view?.priority.page, "admin");
  assert.equal(view?.queues.find(queue => queue.label === "Pemasok terdaftar")?.value, s.suppliers.length);
  assert.equal(view?.queues.some(queue => /aktivitas/i.test(queue.label)), false);
});

test("finance priorities distinguish payment verification, report approval, and period closing", () => {
  const source = seedState();
  const collection = actor("penagihan");
  const collectionView = dashboardView({ s: scopeState(source, collection), actor: collection });
  assert.equal(collectionView?.priority.page, "payments");
  assert.equal(collectionView?.priority.count, source.payments.filter(payment => payment.status === "recorded").length);
  source.credits = []; source.refunds = []; source.expenses = []; source.purchases = [];
  source.periods = [{ id: "2026-09", revision: 1, status: "review", approvedRevision: null, approvedBy: null, submittedBy: "laporan", closedBy: null, closedAt: "", snapshot: null }];
  const manager = actor("pimpinan");
  const managerView = dashboardView({ s: scopeState(source, manager), actor: manager });
  assert.equal(managerView?.priority.page, "periods");
  assert.equal(managerView?.priority.count, 1);
  source.periods[0].status = "approved";
  const accountant = actor("akuntansi");
  const accountingView = dashboardView({ s: scopeState(source, accountant), actor: accountant });
  assert.equal(accountingView?.priority.page, "periods");
  assert.equal(accountingView?.priority.waiting, true);
});
