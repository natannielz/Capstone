/** The location chosen before "Muat data terbaru", found again among the freshly read targets (null when no longer assigned). */
export function keptTarget<T>(targets: readonly T[], key: string | null, keyOf: (target: T) => string): T | null {
  return key === null ? null : targets.find(target => keyOf(target) === key) ?? null;
}
