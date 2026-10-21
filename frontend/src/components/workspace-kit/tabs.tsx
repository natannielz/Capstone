import Link from "next/link";

type LinkTab = { label: string; href: string };
type ViewTab = { label: string; value: string };

/**
 * Text tabs on a 1 px baseline; the current one has the 2 px primary underline (ui-standard §8.1).
 * - `href` items switch between pages: a <nav> of links, the current one `aria-current="page"`.
 * - `value` items switch views of one page: they stay buttons (so role and accessible name do not change for rehearsals),
 *   with `aria-pressed` on the current one, and call `onSelect`.
 * Links to other areas ("Permintaan masuk") are not tabs; put them in the page header as a secondary action.
 */
export function Tabs({ label, items, current, onSelect }: { label: string; items: LinkTab[] | ViewTab[]; current: string; onSelect?: (value: string) => void }) {
  if (items.every((item): item is LinkTab => "href" in item))
    return <nav aria-label={label} className="ui-tabs" data-kit="">
      {items.map(item => <Link key={item.href} className="ui-tab" aria-current={item.href === current ? "page" : undefined} href={item.href}>{item.label}</Link>)}
    </nav>;
  return <div role="group" aria-label={label} className="ui-tabs" data-kit="">
    {(items as ViewTab[]).map(item => <button key={item.value} type="button" className="ui-tab" aria-pressed={item.value === current} onClick={() => onSelect?.(item.value)}>{item.label}</button>)}
  </div>;
}
