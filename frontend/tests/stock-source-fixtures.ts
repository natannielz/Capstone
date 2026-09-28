import type { IdentitySession } from "../src/modules/identity/identity-workflow";
import { parseSourceDetail, type SourceDraft } from "../src/modules/inventory/stock-source-workflow";
import { actorId, location } from "./stock-count-fixtures";
export const sourceDraft: SourceDraft = { physicalKey: "pool-utama", label: "Alokasi gudang", mode: "webLedger",
  control: "exclusiveWebAllocation", maximumAgeSeconds: "60", safetyBufferBaseUnits: "0", externalNamespace: "", reservationProtocolReference: "",
  discrepancyOwnerReference: "PIC gudang", handoffReference: "Serah terima", reason: "Pengajuan persediaan" };
export const sourceDetail = parseSourceDetail({ ...sourceDraft, maximumAgeMilliseconds: "60000", location,
  externalNamespace: null, reservationProtocolReference: null, id: "9007199254740993", proposalVersion: "1", status: "proposed",
  proposedAt: "2026-09-28T00:00:00Z", decision: null, physicalPoolId: null });
export const sourceResult = { proposalId: sourceDetail.id, proposalVersion: "1" as const, status: "proposed" as const, decisionId: null, physicalPoolId: null };
export const sourceSession: IdentitySession = { authenticated: true, account: { userId: actorId, displayName: "Petugas", verifiedEmail: "synthetic@example.test" },
  serverTime: "2026-09-28T01:00:00Z", factorCompletedAt: "2026-09-28T01:00:00Z", freshFactorUntil: "2026-09-28T01:05:00Z",
  idleExpiresAt: "2026-09-28T01:10:00Z", absoluteExpiresAt: "2026-09-28T02:00:00Z", stepUpOperations: ["stock.source.approve"] };
export class SourceMemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}
