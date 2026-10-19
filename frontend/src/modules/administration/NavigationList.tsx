import Link from "next/link";
import { useId } from "react";
import { navigationClick, type NavigationGroup } from "../identity/workspace-navigation";

/**
 * One rendering of the grouped workspace navigation. The sidebar and the mobile sheet each render one, so heading ids are
 * namespaced per instance and each landmark gets its own label (both can be in the DOM while the sheet is open).
 */
export function NavigationList({ groups, locked, label, onNavigate }: { groups: NavigationGroup[]; locked: boolean; label: string; onNavigate?: () => void }) {
  const prefix = useId();
  return <nav className="workspace-nav" aria-label={label}>
    {groups.map(group => <section key={group.id} aria-labelledby={`${prefix}-${group.id}`}>
      <h2 id={`${prefix}-${group.id}`}>{group.label}</h2>
      <ul>{group.items.map(item => <li key={item.href}>
        <Link href={item.href} aria-current={item.current ? "page" : undefined} aria-disabled={locked || !item.available || undefined}
          title={item.available ? item.description : "Akses ke halaman ini sedang tidak tersedia."}
          onClick={event => { navigationClick(locked || !item.available)(event); if (!locked && item.available) onNavigate?.(); }}>
          {item.label}{!item.available && <span className="workspace-nav-note"> · tidak tersedia</span>}
        </Link>
      </li>)}</ul>
    </section>)}
  </nav>;
}
