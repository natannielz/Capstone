import type {Command} from "../domain/model";
import {ApiError} from "./requests";

export type CustomerActionField = {key: string; label: string; value?: string | number; type?: "text" | "number" | "textarea"; optional?: boolean; min?: number; max?: number; help?: string};
export type CustomerActionPresentation = {title: string; description: string; fields: CustomerActionField[]; label?: string};
export type CustomerActionDraft = {
  version: 1;
  actorId: string;
  target: string;
  path: string;
  command: Command;
  baseData: Record<string, unknown>;
  values: Record<string, string>;
  presentation: CustomerActionPresentation;
};
type DraftStorage = Pick<Storage, "getItem" | "setItem" | "removeItem" | "key" | "length">;
const prefix = "unit-toko:customer-action:v1:";
function browserStorage(): DraftStorage | undefined {
  try { return typeof window === "undefined" ? undefined : window.sessionStorage; }
  catch { return undefined; }
}
function key(actorId: string, target: string) { return `${prefix}${encodeURIComponent(actorId)}:${encodeURIComponent(target)}`; }
export function customerActionTarget(type: string, data: Record<string, unknown>) {
  const id = data.id || data.invoiceId || data.shipmentLineId;
  if (typeof id !== "string" || !id) throw new Error("Tujuan tindakan belum tersedia. Muat ulang pesanan.");
  return `${type}:${id}`;
}
function object(value: unknown): value is Record<string, unknown> { return Boolean(value && typeof value === "object" && !Array.isArray(value)); }
function parse(raw: string | null, actorId: string, target?: string): CustomerActionDraft | undefined {
  if (!raw) return;
  try {
    const draft = JSON.parse(raw) as CustomerActionDraft;
    if (draft.version !== 1 || draft.actorId !== actorId || (target && draft.target !== target)
      || typeof draft.path !== "string" || !/^\/account(?:\/|$)/.test(draft.path)
      || !object(draft.command) || typeof draft.command.id !== "string" || draft.command.id.length < 10
      || typeof draft.command.type !== "string" || !object(draft.command.data) || !object(draft.baseData)
      || !object(draft.values) || !Object.values(draft.values).every(value => typeof value === "string")
      || !object(draft.presentation) || typeof draft.presentation.title !== "string" || typeof draft.presentation.description !== "string"
      || !Array.isArray(draft.presentation.fields) || !draft.presentation.fields.every(field => object(field) && typeof field.key === "string" && typeof field.label === "string")
      || draft.target !== customerActionTarget(draft.command.type, draft.command.data)
      || draft.target !== customerActionTarget(draft.command.type, draft.baseData)) return;
    return draft;
  } catch { return; }
}
export function readCustomerActionDraft(actorId: string, target: string, storage = browserStorage()) {
  try { return parse(storage?.getItem(key(actorId, target)) || null, actorId, target); }
  catch { return; }
}
export function findPendingCustomerAction(actorId: string, path: string, storage = browserStorage()) {
  if (!storage) return;
  try {
    for (let index = 0; index < storage.length; index++) {
      const name = storage.key(index);
      if (!name?.startsWith(`${prefix}${encodeURIComponent(actorId)}:`)) continue;
      const draft = parse(storage.getItem(name), actorId);
      if (draft?.path === path) return draft;
    }
  } catch { return; }
}

/** Persist before sending. An unresolved target can never be replaced with a new command. */
export function saveCustomerActionDraft(draft: CustomerActionDraft, storage = browserStorage()): CustomerActionDraft {
  if (!storage) throw new Error("Penyimpanan sesi browser tidak tersedia. Aktifkan penyimpanan browser sebelum mengirim tindakan.");
  const existing = readCustomerActionDraft(draft.actorId, draft.target, storage);
  if (existing) return existing;
  try {
    const raw = JSON.stringify(draft);
    storage.setItem(key(draft.actorId, draft.target), raw);
    if (storage.getItem(key(draft.actorId, draft.target)) !== raw) throw new Error("Draft not persisted");
    return JSON.parse(raw) as CustomerActionDraft;
  } catch { throw new Error("Permintaan belum dikirim karena penyimpanan sesi gagal. Periksa ruang penyimpanan browser, lalu coba kembali."); }
}
export function clearCustomerActionDraft(actorId: string, target: string, commandId: string, storage = browserStorage()) {
  if (!storage) return;
  // Do not erase a different request that another mounted view is recovering.
  if (readCustomerActionDraft(actorId, target, storage)?.command.id !== commandId) return;
  try { storage.removeItem(key(actorId, target)); }
  catch { /* Retaining a completed ID is safe: the repository returns the cached result. */ }
}
export function ambiguousCustomerActionError(cause: unknown) {
  // Network failure, 5xx, unreadable 2xx and unexpected thrown errors can occur
  // after a successful commit. Ordinary explicit 4xx responses are definitive.
  return !(cause instanceof ApiError && cause.status >= 400 && cause.status < 500 && cause.status !== 401);
}

/** Serializable forms can recover their numeric/row mapping after a page reload. */
export function customerActionValues(type: string, baseData: Record<string, unknown>, values: Record<string, string>) {
  if (type === "shipment.receive") return {...baseData, receiver: values.receiver, lines: Object.keys(values).filter(name => name.startsWith("qty_")).map(name => {
    const id = name.slice(4); return {id, accepted: Number(values[name]), reason: values[`reason_${id}`] || ""};
  })};
  if (type === "payment.record") return {...baseData, amount: Number(values.amount), reference: values.reference, note: values.note};
  if (type === "complaint.create") return {...baseData, qty: Number(values.qty), reason: values.reason};
  return {...baseData, ...values};
}
