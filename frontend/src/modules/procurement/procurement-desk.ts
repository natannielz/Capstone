/** The location chosen before "Muat data terbaru", found again among the freshly read targets (null when no longer assigned). */
export function keptTarget<T>(targets: readonly T[], key: string | null, keyOf: (target: T) => string): T | null {
  return key === null ? null : targets.find(target => keyOf(target) === key) ?? null;
}

/** Reads the remembered location once and forgets it, so an aborted, offline or unauthorised read cannot restore a stale key later. */
export function takeKeptKey(keep: { current: string | null }): string | null {
  const key = keep.current; keep.current = null; return key;
}

/** The kept location with its list, read again; null (and no list read) when the location is no longer among the fresh targets. */
export async function restoreKeptLocation<T, P>(targets: readonly T[], key: string | null, keyOf: (target: T) => string, readList: (target: T) => Promise<P>): Promise<{ target: T; page: P } | null> {
  const target = keptTarget(targets, key, keyOf);
  return target ? { target, page: await readList(target) } : null;
}

/**
 * After a save that the server CONFIRMED (the save resolves), shows the notice and reads the list and the detail again so the new record appears without a reload.
 * A save that throws (rejected, not sent, or uncertain) reads nothing. The list is read first and independently: a failed detail read still leaves the list fresh,
 * and the first failure is rethrown once both reads were tried.
 */
export async function refreshAfterConfirmedSave<S>(save: () => Promise<S>, after: { valid: () => boolean; confirmed: (saved: S) => void; list: () => Promise<void>; detail: (saved: S) => Promise<void> }): Promise<void> {
  const saved = await save();
  if (!after.valid()) return;
  after.confirmed(saved);
  const failures: unknown[] = [];
  for (const read of [after.list, () => after.detail(saved)]) {
    try { await read(); } catch (failure) { failures.push(failure); }
  }
  if (failures.length > 0) throw failures[0];
}
