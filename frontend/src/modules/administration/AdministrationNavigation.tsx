import Link from "next/link";
import { canManageAccounts, canReviewPeople, canUseStockCounts, canUseStockSources } from "../identity/identity-workflow";
import styles from "./access.module.css";

export function AdministrationNavigation({ current, actions, locked = false }: {
  current: "access" | "organizations" | "history" | "accounts" | "person-reviews" | "catalog" | "counts" | "sources" | "source-revisions" | "source-aliases"; actions: readonly string[]; locked?: boolean;
}) {
  const links = [
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
