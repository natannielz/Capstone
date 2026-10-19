import type { NavigationGroup } from "@/modules/identity/workspace-navigation";

export type PaletteResult = { href: string; label: string; description: string; area: string };

const normalize = (value: string) => value.normalize("NFKD").replace(/\p{M}/gu, "").toLocaleLowerCase("id-ID").trim();

/** Available destinations matching every query term; label prefix first, then word start, label, description/area. */
export function paletteSearch(groups: NavigationGroup[], query: string): PaletteResult[] {
  const terms = normalize(query).split(/\s+/u).filter(Boolean);
  const results = groups.flatMap(group => group.items.filter(item => item.available).map(item => ({ href: item.href, label: item.label, description: item.description, area: group.label })));
  if (!terms.length) return results;
  const ranked = results.flatMap((result, index) => {
    const label = normalize(result.label), rest = normalize(`${result.description} ${result.area}`);
    if (!terms.every(term => label.includes(term) || rest.includes(term))) return [];
    const first = terms[0];
    const rank = label.startsWith(first) ? 0 : label.split(/\s+/u).some(word => word.startsWith(first)) ? 1 : label.includes(first) ? 2 : 3;
    return [{ result, rank, index }];
  });
  return ranked.sort((a, b) => a.rank - b.rank || a.index - b.index).map(entry => entry.result);
}

/** Ctrl+K / ⌘+K, except while the user is typing in a field. */
export function isPaletteShortcut(event: { key: string; ctrlKey: boolean; metaKey: boolean; targetTag: string; editable: boolean }): boolean {
  if (event.key.toLowerCase() !== "k" || !(event.ctrlKey || event.metaKey)) return false;
  return !event.editable && !["INPUT", "TEXTAREA", "SELECT"].includes(event.targetTag);
}
