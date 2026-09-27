export const ROLES = ["pic", "kepala", "staf", "kurir", "laporan", "penagihan", "pimpinan", "akuntansi", "admin", "customer"] as const;
export type Role = typeof ROLES[number];
export const isRole = (value: unknown): value is Role => typeof value === "string" && (ROLES as readonly string[]).includes(value);
export type Actor = { id: string; name: string; role: Role; divisionId: string | null; active: boolean; email?:string; avatar?:string; phone?:string; position?:string; address?:string };
// Public labels are display metadata. The authoritative demo account fixture stays in backend/legacy.
export const ROLE_LABELS: Record<Role,string> = {
  pic:"PIC Divisi",kepala:"Kepala Toko",staf:"Staf Toko",kurir:"Kurir",
  laporan:"Pengolah Laporan",penagihan:"Bagian Penagihan",pimpinan:"Pimpinan Unit",
  akuntansi:"Akuntansi",admin:"Administrator",customer:"Pelanggan"
};
