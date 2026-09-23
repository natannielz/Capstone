import type {Actor} from "../domain/accounts";

export type ProfileValues = {name: string; phone: string; position: string; address: string};
export const PROFILE_FIELDS = ["name", "phone", "position", "address"] as const;
const LIMITS: Record<keyof ProfileValues, number> = {name: 100, phone: 30, position: 100, address: 500};
const MAX_AGE = 8 * 60 * 60 * 1000;
// Only actor identifiers are retained here, never profile or password contents.
const discardedInMemory = new Set<string>();
type DraftStorage = Pick<Storage, "getItem" | "setItem" | "removeItem">;
export type CustomerProfileDraft = {version: 1; actorId: string; generation?: string; updatedAt: number; baseline: ProfileValues; values: ProfileValues};
export type ProfileDraftSnapshot = string | null | undefined;

export function profileValues(actor: Pick<Actor, "name" | "phone" | "position" | "address">): ProfileValues {
  return {name: actor.name, phone: actor.phone || "", position: actor.position || "", address: actor.address || ""};
}

export function sameProfileValues(a: ProfileValues, b: ProfileValues) {
  return PROFILE_FIELDS.every(field => a[field] === b[field]);
}

/** The original server baseline distinguishes a local edit from a stale clean field. */
export function reconcileProfileValues(baseline: ProfileValues, current: ProfileValues, incoming: ProfileValues, restoring = false) {
  const values = {...incoming};
  const changedOnServer = PROFILE_FIELDS.filter(field => incoming[field] !== baseline[field]);
  const conflicts: (keyof ProfileValues)[] = [];
  for (const field of PROFILE_FIELDS) {
    // PATCH normalizes surrounding whitespace. A saved value is not an unsaved
    // edit just because an old browser draft could not be physically removed.
    if (current[field] !== baseline[field] && !(restoring && current[field].trim() === incoming[field])) {
      values[field] = current[field];
      if (incoming[field] !== baseline[field] && incoming[field] !== current[field]) conflicts.push(field);
    }
  }
  return {values, changedOnServer, conflicts};
}

function browserStorage(): DraftStorage | undefined {
  try { return typeof window === "undefined" ? undefined : window.sessionStorage; }
  catch { return undefined; }
}

export function customerProfileDraftKey(actorId: string) {
  return `unit-toko-profile-v15:${encodeURIComponent(actorId)}`;
}

function copyValues(values: ProfileValues): ProfileValues {
  // A credential, token, avatar file, or unrelated field can never enter the payload.
  return {name: values.name, phone: values.phone, position: values.position, address: values.address};
}

function validValues(value: unknown): value is ProfileValues {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  return Object.keys(record).length === PROFILE_FIELDS.length && PROFILE_FIELDS.every(field => typeof record[field] === "string" && record[field].length <= LIMITS[field]);
}

export function loadCustomerProfileDraft(actorId: string, storage = browserStorage(), now = Date.now()): {draft?: CustomerProfileDraft; available: boolean} {
  if (!storage) return {available: false};
  try {
    const raw = storage.getItem(customerProfileDraftKey(actorId));
    if (!raw || discardedInMemory.has(actorId)) return {available: true};
    const draft = JSON.parse(raw) as CustomerProfileDraft;
    if (!draft || draft.version !== 1 || draft.actorId !== actorId || !Number.isFinite(draft.updatedAt)
      || (draft.generation !== undefined && (typeof draft.generation !== "string" || draft.generation.length > 100))
      || draft.updatedAt > now + 60_000 || now - draft.updatedAt > MAX_AGE
      || !validValues(draft.baseline) || !validValues(draft.values)) return {available: true};
    return {available: true, draft: {version: 1, actorId, ...(draft.generation ? {generation: draft.generation} : {}), updatedAt: draft.updatedAt, baseline: copyValues(draft.baseline), values: copyValues(draft.values)}};
  } catch { return {available: false}; }
}

export function captureCustomerProfileDraft(actorId: string, storage = browserStorage()): ProfileDraftSnapshot {
  try { return storage?.getItem(customerProfileDraftKey(actorId)); }
  catch { return undefined; }
}

/** An older mounted/pending editor may only clear the exact generation it owns. */
export function clearCustomerProfileDraftSnapshot(actorId: string, expected: ProfileDraftSnapshot, storage = browserStorage()): "cleared" | "superseded" | "unavailable" {
  if (!storage || expected === undefined) return "unavailable";
  try {
    if (storage.getItem(customerProfileDraftKey(actorId)) !== expected) return "superseded";
  } catch { return "unavailable"; }
  return clearCustomerProfileDraft(actorId, storage) ? "cleared" : "unavailable";
}

export function clearCustomerProfileDraft(actorId: string, storage = browserStorage()) {
  discardedInMemory.add(actorId);
  if (!storage) return false;
  const key = customerProfileDraftKey(actorId);
  try {
    if (storage.getItem(key) === null) return true;
    storage.removeItem(key);
    if (storage.getItem(key) === null) return true;
  } catch { /* Some environments reject removal while still allowing writes. */ }
  try {
    const tombstone = JSON.stringify({version: 1, actorId, discarded: true});
    storage.setItem(key, tombstone);
    return storage.getItem(key) === tombstone;
  } catch { return false; }
}

/** Local draft cleanup is optional and must never prevent server logout. */
export async function logoutWithProfileCleanup(actorId: string, revokeSession: () => Promise<void>, storage = browserStorage()) {
  await revokeSession();
  return {draftCleared: clearCustomerProfileDraft(actorId, storage)};
}

/** Called synchronously from each text edit, before a SPA Back/Forward can unmount it. */
export function saveCustomerProfileDraft(actorId: string, baseline: ProfileValues, values: ProfileValues, storage = browserStorage(), now = Date.now()) {
  if (!storage) return false;
  const safeBaseline = copyValues(baseline), safeValues = copyValues(values);
  if (!validValues(safeBaseline) || !validValues(safeValues)) return false;
  if (sameProfileValues(safeBaseline, safeValues)) return clearCustomerProfileDraft(actorId, storage);
  try {
    const draft: CustomerProfileDraft = {version: 1, actorId, generation: crypto.randomUUID(), updatedAt: now, baseline: safeBaseline, values: safeValues};
    const raw = JSON.stringify(draft);
    storage.setItem(customerProfileDraftKey(actorId), raw);
    const saved = storage.getItem(customerProfileDraftKey(actorId)) === raw;
    if (saved) discardedInMemory.delete(actorId);
    return saved;
  } catch { return false; }
}
