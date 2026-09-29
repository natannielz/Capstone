import Link from "next/link";
import { canManageAccounts, canReviewPeople, canUseStockCounts, canUseStockSources, canUseStockSourceSuspensions, canUseStockSourcePolicies } from "../identity/identity-workflow";
import styles from "./access.module.css";

export function AdministrationNavigation({ current, actions, locked = false }: {
  current: "access" | "organizations" | "history" | "accounts" | "person-reviews" | "catalog" | "prices" | "counts" | "sources" | "source-revisions" | "source-aliases" | "source-suspensions" | "source-policies" | "order-reviews" | "orders" | "preparations" | "dispatches" | "shipments"; actions: readonly string[]; locked?: boolean;
}) {
  const links = [
    { id: "shipments", href: "/fulfillment/shipments", label: "Kiriman", available: actions.includes("shipment.read") },
    { id: "preparations", href: "/fulfillment/preparations", label: "Persiapan kiriman", available: actions.includes("shipment.prepare") },
    { id: "dispatches", href: "/fulfillment/dispatches", label: "Keberangkatan kiriman", available: actions.includes("shipment.dispatch") && actions.includes("shipment.assign") },
    { id: "orders", href: "/ordering/orders", label: "Pesanan saya", available: actions.includes("order.read") },
    { id: "order-reviews", href: "/ordering/reviews", label: "Peninjauan pesanan", available: actions.some(action => ["order.spend.approve", "order.accept", "order.cancel"].includes(action)) },
    { id: "prices", href: "/catalog/prices", label: "Harga kemasan", available: actions.some(action => ["pricing.manage", "pricing.approve"].includes(action)) },
    { id: "source-policies", href: "/inventory/sources/policies", label: "Kebijakan operasional stok", available: canUseStockSourcePolicies(actions) },
    { id: "source-suspensions", href: "/inventory/sources/suspensions", label: "Penghentian sumber", available: canUseStockSourceSuspensions(actions) },
    { id: "source-aliases", href: "/inventory/sources/aliases", label: "Pemetaan kanal", available: canUseStockSources(actions) },
    { id: "source-revisions", href: "/inventory/sources/revisions", label: "Perubahan sumber", available: canUseStockSources(actions) },
    { id: "sources", href: "/inventory/sources", label: "Sumber stok", available: canUseStockSources(actions) },
    { id: "counts", href: "/inventory/counts", label: "Hitung barang", available: canUseStockCounts(actions) },
    { id: "catalog", href: "/administration/catalog", label: "Katalog toko", available: actions.includes("catalog.manage") },
    { id: "account", href: "/identity/account", label: "Akun saya", available: true },
    { id: "accounts", href: "/administration/accounts", label: "Akun dan undangan", available: canManageAccounts(actions) },
    { id: "person-reviews", href: "/administration/person-reviews", label: "Keterkaitan akun", available: canReviewPeople(actions) },
    { id: "access", href: "/administration/access", label: "Akses dan keanggotaan", available: actions.some(action => action.startsWith("access.") || action.startsWith("membership.")) },
    { id: "organizations", href: "/administration/organizations", label: "Organisasi dan unit", available: actions.some(action => ["technical.configure", "organization.approve", "access.audit.read"].includes(action)) },
    { id: "history", href: "/administration/history", label: "Riwayat perubahan", available: actions.some(action => ["access.audit.read", "access.approve", "membership.approve", "access.revoke", "technical.configure", "organization.approve"].includes(action)) },
  ];
  return <nav className={styles.administrationNav} aria-label="Pengelolaan">
    {links.filter(link => link.available || link.id === current).map(link => <Link key={link.id} href={link.href}
      aria-current={current === link.id ? "page" : undefined} aria-disabled={locked}
      onClick={event => { if (locked) event.preventDefault(); }}>{link.label}</Link>)}
  </nav>;
}
