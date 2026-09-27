import type { components } from "../../api/generated/schema";

export type AccessScope = components["schemas"]["AccessScopeDto"];
export type ScopeKind = components["schemas"]["GrantScopeKind"];
export const scopeKindLabels: Record<ScopeKind, string> = {
  buyerOwned: "Pembelian milik sendiri atau penugasan",
  buyingDivision: "PIC divisi tertentu",
  buyingBranch: "PIC cabang tertentu",
  buyingOrganization: "Seluruh organisasi pembeli",
  fulfillmentShop: "Toko dan kanal tertentu",
  fulfillmentWarehouse: "Gudang dan kanal tertentu",
  courierAssignment: "Pengiriman yang ditugaskan",
  system: "Administrasi sistem",
};

export function validReference(value: string): boolean {
  return /^[1-9][0-9]{0,18}$/.test(value) && (value.length < 19 || value <= "9223372036854775807");
}

export function describeScope(scope: AccessScope): string {
  const parts = [scopeKindLabels[scope.kind]];
  const fields = [["organizationId", "organisasi"], ["branchId", "cabang"], ["divisionId", "divisi"],
    ["shopId", "toko"], ["warehouseId", "gudang"], ["channelId", "kanal"]] as const;
  for (const [field, label] of fields) {
    const value = scope[field];
    if (typeof value === "string") parts.push(`${label} ${value}`);
  }
  return parts.join(", ");
}

export function toIsoDate(value: string): string | null {
  if (!value) return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

export function localDateTime(now = new Date()): string {
  const shifted = new Date(now.getTime() - now.getTimezoneOffset() * 60_000);
  return shifted.toISOString().slice(0, 16);
}
