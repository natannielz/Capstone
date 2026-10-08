import Link from "next/link";
import { isPilotPathAllowed, isPilotRelease } from '../../lib/release-scope';
import { canManageAccounts, canReviewPeople, canUseStockCounts, canUseStockSources, canUseStockSourceSuspensions, canUseStockSourcePolicies, canUseStockSourceReconciliations, canUseOrganizationAddresses } from "../identity/identity-workflow";
import styles from "./access.module.css";

export function AdministrationNavigation({ current, actions, locked = false }: {
  current: "pack-conversion-executions" | "stock-releases" | "source-activations" | "pack-conversion-rules" | "source-reconciliations" | "organization-addresses" | "cases" | "access" | "organizations" | "history" | "accounts" | "person-reviews" | "catalog" | "prices" | "counts" | "sources" | "source-revisions" | "source-aliases" | "source-suspensions" | "source-policies" | "order-reviews" | "orders" | "preparations" | "dispatches" | "shipments" | "invoices" | "payments" | "payment-reviews" | "receiving-accounts" | "bank-statements" | "bank-receipts"; actions: readonly string[]; locked?: boolean;
}) {
  const links = [
    { id: "branch-requests", href: "/ordering/requests", label: "Permintaan saya", available: actions.some(action => ["order.request.read", "order.request.submit"].includes(action)) },
    { id: "branch-requests-incoming", href: "/ordering/requests/incoming", label: "Permintaan masuk", available: actions.includes("order.request.decide") },
    { id: "source-reconciliations", href: "/inventory/source-reconciliations", label: "Rekonsiliasi jumlah fisik", available: canUseStockSourceReconciliations(actions) },
    { id: "cases", href: "/fulfillment/cases", label: "Keluhan barang", available: actions.some(action => ["case.read", "case.request"].includes(action)) },
    { id: "bank-receipts", href: "/billing/bank-receipts", label: "Penerimaan bank", available: actions.includes("bank.receipt.read") },
    { id: "bank-statements", href: "/billing/bank-statements", label: "Mutasi bank", available: actions.includes("bank.statement.read") },
    { id: "receiving-accounts", href: "/billing/receiving-accounts", label: "Rekening penerimaan", available: actions.includes("receiving.account.read") },
    { id: "payment-reviews", href: "/billing/payment-reviews", label: "Pemeriksaan pembayaran", available: actions.includes("payment.review.read") && actions.includes("invoice.read") },
    { id: "invoices", href: "/billing/invoices", label: "Tagihan", available: actions.some(action => ["invoice.read", "invoice.issue"].includes(action)) },
    { id: "payments", href: "/billing/payments", label: "Bukti pembayaran", available: actions.includes("payment.evidence") && actions.includes("invoice.read") },
    { id: "shipments", href: "/fulfillment/shipments", label: "Kiriman", available: actions.some(action => ["shipment.read", "delivery.attempt"].includes(action)) },
    { id: "preparations", href: "/fulfillment/preparations", label: "Persiapan kiriman", available: actions.includes("shipment.prepare") },
    { id: "dispatches", href: "/fulfillment/dispatches", label: "Keberangkatan kiriman", available: actions.includes("shipment.dispatch") && actions.includes("shipment.assign") },
    { id: "orders", href: "/ordering/orders", label: "Pesanan saya", available: actions.includes("order.read") },
    { id: "order-reviews", href: "/ordering/reviews", label: "Peninjauan pesanan", available: actions.some(action => ["order.spend.approve", "order.accept", "order.cancel"].includes(action)) },
    { id: "prices", href: "/catalog/prices", label: "Harga kemasan", available: actions.some(action => ["pricing.manage", "pricing.approve"].includes(action)) },
    { id: "source-policies", href: "/inventory/sources/policies", label: "Kebijakan operasional stok", available: canUseStockSourcePolicies(actions) },
    { id: "source-activations", href: "/inventory/sources/activations", label: "Aktivasi sumber stok", available: canUseStockSources(actions) },
    { id: "source-suspensions", href: "/inventory/sources/suspensions", label: "Penghentian sumber", available: canUseStockSourceSuspensions(actions) },
    { id: "source-aliases", href: "/inventory/sources/aliases", label: "Pemetaan kanal", available: canUseStockSources(actions) },
    { id: "source-revisions", href: "/inventory/sources/revisions", label: "Perubahan sumber", available: canUseStockSources(actions) },
    { id: "sources", href: "/inventory/sources", label: "Sumber stok", available: canUseStockSources(actions) },
    { id: "counts", href: "/inventory/counts", label: "Hitung barang", available: canUseStockCounts(actions) },
    { id: "replenishment", href: "/inventory/replenishment", label: "Kebutuhan pembelian", available: actions.includes("stock.read") },
    { id: "stock-releases", href: "/inventory/stock-releases", label: "Pelepasan stok ditahan", available: actions.some(action => ["stock.release.propose", "stock.release.approve"].includes(action)) },
    { id: "pack-conversion-rules", href: "/inventory/pack-conversions", label: "Aturan buka kemasan", available: actions.includes("stock.convert") },
    { id: "pack-conversion-executions", href: "/inventory/pack-conversion-executions", label: "Pelaksanaan buka kemasan", available: actions.includes("stock.convert") },
    { id: "catalog", href: "/administration/catalog", label: "Katalog toko", available: actions.includes("catalog.manage") },
    { id: "account", href: "/identity/account", label: "Akun saya", available: true },
    { id: "accounts", href: "/administration/accounts", label: "Akun dan undangan", available: canManageAccounts(actions) },
    { id: "person-reviews", href: "/administration/person-reviews", label: "Keterkaitan akun", available: canReviewPeople(actions) },
    { id: "access", href: "/administration/access", label: "Akses dan keanggotaan", available: actions.some(action => action.startsWith("access.") || action.startsWith("membership.")) },
    { id: "organizations", href: "/administration/organizations", label: "Organisasi dan unit", available: actions.some(action => ["technical.configure", "organization.approve", "access.audit.read"].includes(action)) },
    { id: "organization-addresses", href: "/administration/organizations/addresses", label: "Alamat organisasi", available: canUseOrganizationAddresses(actions) },
    { id: "history", href: "/administration/history", label: "Riwayat perubahan", available: actions.some(action => ["access.audit.read", "access.approve", "membership.approve", "access.revoke", "technical.configure", "organization.approve"].includes(action)) },
  ];
  return <nav className={styles.administrationNav} aria-label="Pengelolaan">
    {links.filter(link => (link.available || link.id === current) && (!isPilotRelease || isPilotPathAllowed(link.href))).map(link => <Link key={link.id} href={link.href}
      aria-current={current === link.id ? "page" : undefined} aria-disabled={locked}
      onClick={event => { if (locked) event.preventDefault(); }}>{link.label}</Link>)}
  </nav>;
}
