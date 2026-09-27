import type { components } from "../../api/generated/schema";
import { AccessFailure, accessMessage } from "../administration/workflow";
import { validReference } from "./access-scope";

type Schemas = components["schemas"];
export type Organization = Schemas["OrganizationDto"];
export type OrganizationProposal = Schemas["OrganizationProposalDto"];
export type OrganizationRequest = Schemas["OrganizationRequestDto"];
export type OrganizationKind = Schemas["ScopeNodeKind"];
export const organizationKinds: Record<OrganizationKind, string> = {
  buyingOrganization: "Organisasi pembeli", fulfillmentOrganization: "Organisasi pemenuhan",
  branch: "Cabang", division: "Divisi", shop: "Toko", warehouse: "Gudang", channel: "Kanal penjualan",
};
export const parentKinds: Record<OrganizationKind, OrganizationKind | null> = {
  buyingOrganization: null, fulfillmentOrganization: null, branch: "buyingOrganization", division: "branch",
  shop: "fulfillmentOrganization", warehouse: "shop", channel: "fulfillmentOrganization",
};
export const changeLabels = { create: "Tambah unit", updateProfile: "Ubah nama atau kode", reactivate: "Aktifkan kembali" } as const;
export function organizationLabel(item: Organization) { return item.name ? `${item.code} · ${item.name}` : `Referensi ${item.id} — nama belum ditetapkan`; }
export function statusLabel(status: string) {
  return ({ active: "Aktif", suspended: "Ditangguhkan", pending: "Menunggu keputusan", approved: "Disetujui", rejected: "Ditolak", withdrawn: "Ditarik", revoked: "Dicabut" } as Record<string, string>)[status] ?? status;
}
export function newOrganizationRequest(): OrganizationRequest {
  return { changeKind: "create", kind: "branch", parentId: null, code: "", name: "", scopeId: null, expectedScopeVersion: "0", expectedProfileVersion: "0" };
}
export function editOrganizationRequest(item: Organization, changeKind: "updateProfile" | "reactivate"): OrganizationRequest {
  return { changeKind, kind: item.kind, parentId: item.parentId, scopeId: item.id, code: item.code ?? "", name: item.name ?? "",
    expectedScopeVersion: item.scopeVersion, expectedProfileVersion: item.profileVersion ?? "0" };
}
export function validOrganizationDraft(request: OrganizationRequest, reason: string): boolean {
  return request.code.trim().length > 0 && request.code.trim().length <= 64 && request.name.trim().length > 0 && request.name.trim().length <= 160 &&
    reason.trim().length > 0 && reason.length <= 1000 && (parentKinds[request.kind] === null ? request.parentId === null : validReference(request.parentId ?? ""));
}
export function organizationMessage(error: unknown): string {
  if (error instanceof AccessFailure) {
    if (error.code === "organization_impact_changed") return "Akses yang terdampak telah berubah. Tarik pengajuan ini dan buat pengajuan baru agar dampaknya dapat diperiksa kembali.";
    if (error.code === "organization_code_exists") return "Kode sudah dipakai oleh unit sejenis pada induk ini. Gunakan kode yang berbeda.";
    if (error.code === "beneficiary_cannot_approve") return "Pengaktifan ini memengaruhi akses Anda. Pemeriksa lain yang tidak terdampak perlu memutuskan pengajuan.";
    if (error.code === "organization_parent_unavailable") return "Induk unit tidak lagi tersedia atau aktif. Periksa struktur terbaru sebelum membuat pengajuan.";
    if (error.status === 409 && error.code !== "actor_changed") return "Data telah berubah. Pilihan lama dibersihkan; periksa unit atau pengajuan terbaru sebelum melanjutkan.";
  }
  return accessMessage(error);
}
