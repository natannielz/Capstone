import Link from "next/link";
import styles from "./access.module.css";

export function AdministrationNavigation({ current, actions, locked = false }: {
  current: "access" | "organizations" | "history"; actions: readonly string[]; locked?: boolean;
}) {
  const links = [
    { id: "account", href: "/identity/account", label: "Akun saya", available: true },
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
