import type { components } from "../../api/generated/schema";
import { RequestNotSentError } from "../../api/client";

export type IdentitySession = components["schemas"]["IdentitySessionDto"];

export class IdentityFailure extends Error {
  constructor(readonly status: number, readonly code: string, readonly traceId?: string) {
    super(code); this.name = "IdentityFailure";
  }
}

const returnPaths = ["/identity/account", "/administration/access", "/administration/organizations", "/administration/organizations/addresses", "/administration/history", "/administration/person-reviews", "/administration/catalog", "/catalog/prices", "/catalog/browse", "/shop", "/ordering/drafts", "/ordering/checkout", "/ordering/reviews", "/ordering/orders", "/ordering/spending-policies", "/fulfillment/preparations", "/fulfillment/dispatches", "/fulfillment/shipments", "/fulfillment/receive", "/fulfillment/cases", "/billing/invoices", "/billing/payments", "/billing/payment-reviews", "/billing/receiving-accounts", "/billing/bank-statements", "/billing/bank-receipts", "/accounting/books", "/procurement/requests", "/inventory/counts", "/inventory/sources", "/inventory/sources/revisions", "/inventory/sources/aliases", "/inventory/sources/suspensions", "/inventory/sources/policies"];
export const identityReturnPath = (value?: string | null) => value && returnPaths.includes(value) ? value : "/identity/account";
const validId = (value: string) => /^[1-9][0-9]{0,18}$/.test(value) && (value.length < 19 || value <= "9223372036854775807");
const validTime = (value: unknown): value is string => typeof value === "string" && value.length <= 40 && /^\d{4}-\d\d-\d\dT/.test(value) && Number.isFinite(Date.parse(value));
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);

/** This is a display contract, never a source of authorization. Invalid responses clear the account view. */
export function parseIdentitySession(value: unknown): IdentitySession {
  const invalid = () => { throw new IdentityFailure(502, "invalid_session_response"); };
  if (!record(value) || typeof value.authenticated !== "boolean" || !validTime(value.serverTime) || !Array.isArray(value.stepUpOperations) || value.stepUpOperations.length > 200 ||
    !value.stepUpOperations.every(item => typeof item === "string" && /^[a-z]+(?:\.[a-z]+){1,4}$/.test(item))) return invalid();
  const timeFields = ["factorCompletedAt", "freshFactorUntil", "idleExpiresAt", "absoluteExpiresAt"] as const;
  if (!value.authenticated) {
    if (value.account !== null || timeFields.some(field => value[field] !== null) || value.stepUpOperations.length !== 0) return invalid();
  } else {
    if (!record(value.account) || typeof value.account.userId !== "string" || !validId(value.account.userId) ||
      !(value.account.displayName === null || typeof value.account.displayName === "string" && value.account.displayName.length <= 200) ||
      typeof value.account.verifiedEmail !== "string" || value.account.verifiedEmail.length < 3 || value.account.verifiedEmail.length > 320 ||
      timeFields.some(field => !validTime(value[field]))) return invalid();
    if (Date.parse(value.idleExpiresAt as string) > Date.parse(value.absoluteExpiresAt as string) ||
      Date.parse(value.factorCompletedAt as string) > Date.parse(value.freshFactorUntil as string)) return invalid();
  }
  // Pick only display fields; unexpected provider or session secrets are never retained in UI state.
  return { authenticated: value.authenticated, account: value.account === null ? null : {
    userId: (value.account as Record<string, string>).userId,
    displayName: (value.account as Record<string, string | null>).displayName,
    verifiedEmail: (value.account as Record<string, string>).verifiedEmail,
  }, factorCompletedAt: value.factorCompletedAt as string | null, freshFactorUntil: value.freshFactorUntil as string | null,
    idleExpiresAt: value.idleExpiresAt as string | null, absoluteExpiresAt: value.absoluteExpiresAt as string | null,
    serverTime: value.serverTime, stepUpOperations: [...value.stepUpOperations] as string[] };
}

export function loginFields(returnPath: string, invitationId: string, invitationSecret: string, invitationRequired = false): Record<string, string> {
  if (!returnPaths.includes(returnPath)) throw new IdentityFailure(400, "invalid_return_path");
  if (!invitationRequired && !invitationId && !invitationSecret) return { returnPath };
  if (!validId(invitationId) || !/^[a-f0-9]{64}$/.test(invitationSecret)) throw new IdentityFailure(400, "invalid_invitation");
  return { returnPath, invitationId, invitationSecret };
}

export function stepUpFields(session: IdentitySession, operation: string, returnPath: string): Record<string, string> {
  if (!session.authenticated || !session.account) throw new IdentityFailure(401, "unauthenticated");
  if (!session.stepUpOperations.includes(operation)) throw new IdentityFailure(403, "step_up_not_allowed");
  if (!returnPaths.includes(returnPath)) throw new IdentityFailure(400, "invalid_return_path");
  return { expectedActorId: session.account.userId, operation, returnPath };
}

export function sessionTiming(session: IdentitySession, elapsedMs: number) {
  const now = Date.parse(session.serverTime) + Math.max(0, elapsedMs);
  const expired = !session.authenticated || session.idleExpiresAt === null || session.absoluteExpiresAt === null ||
    now >= Math.min(Date.parse(session.idleExpiresAt), Date.parse(session.absoluteExpiresAt));
  return { expired, fresh: !expired && session.freshFactorUntil !== null && now < Date.parse(session.freshFactorUntil) };
}

export function canManageAccounts(actions: readonly string[]) { return actions.some(action => ["invite.manage", "recovery.initiate", "membership.approve", "access.approve", "access.audit.read"].includes(action)); }

export function canReviewPeople(actions: readonly string[]) { return actions.some(action => ["person.review.read", "person.review.propose", "person.review.approve"].includes(action)); }

export function canUseOrganizationAddresses(actions: readonly string[]) { return actions.some(action => ["organization.address.read", "technical.configure", "organization.approve"].includes(action)); }

export function canUseStockSources(actions: readonly string[]) { return actions.some(action => ["stock.source.read", "stock.source.propose", "stock.source.approve"].includes(action)); }

export function canUseStockSourcePolicies(actions: readonly string[]) { return actions.some(action => ["stock.source.read", "stock.source.policy.propose", "stock.source.policy.approve"].includes(action)); }

export function canUseStockSourceSuspensions(actions: readonly string[]) { return actions.includes("stock.source.read") || actions.includes("stock.source.suspend"); }

export function canUseStockCounts(actions: readonly string[]) { return actions.includes("stock.count") || actions.includes("stock.read"); }

export function identityDestinations(actions: readonly string[]) {
  return [
    { href: "/fulfillment/cases", label: "Keluhan barang", description: "Laporkan masalah pada barang yang diterima dan ikuti pengajuannya.", visible: actions.some(action => ["case.read", "case.request"].includes(action)) },
    { href: "/fulfillment/preparations", label: "Persiapan kiriman", description: "Pilih kemasan dan jumlah yang akan disiapkan dari pesanan toko yang diterima.", visible: actions.includes("shipment.prepare") },
    { href: "/fulfillment/dispatches", label: "Keberangkatan kiriman", description: "Periksa lot fisik dan kurir sebelum barang diberangkatkan.", visible: actions.includes("shipment.dispatch") && actions.includes("shipment.assign") },
    { href: "/fulfillment/shipments", label: "Kiriman", description: "Lihat barang yang diberangkatkan dan tujuan dalam penugasan Anda.", visible: actions.some(action => ["shipment.read", "delivery.attempt"].includes(action)) },
    { href: "/fulfillment/receive", label: "Penerimaan kiriman", description: "Pindai QR kurir atau periksa hasil konfirmasi penerimaan.", visible: actions.includes("receipt.accept") },
    { href: "/billing/invoices", label: "Tagihan", description: "Tinjau penerimaan, terbitkan invoice dan periksa dokumen tagihan.", visible: actions.some(action => ["invoice.read", "invoice.issue"].includes(action)) },
    { href: "/billing/bank-receipts", label: "Penerimaan bank", description: "Periksa kredit independen dan telusuri pencatatan kas.", visible: actions.includes("bank.receipt.read") },
    { href: "/billing/bank-statements", label: "Mutasi bank", description: "Periksa dan simpan sumber mutasi untuk rekonsiliasi independen.", visible: actions.includes("bank.statement.read") },
    { href: "/billing/receiving-accounts", label: "Rekening penerimaan", description: "Ajukan dan periksa rekening melalui kanal bank yang disetujui.", visible: actions.includes("receiving.account.read") },
    { href: "/procurement/requests", label: "Permintaan pengadaan", description: "Ajukan dan periksa kebutuhan barang berdasarkan lokasi, jalur dan kemasan katalog.", visible: actions.some(action => ["procurement.read", "procurement.prepare", "procurement.approve"].includes(action)) },
    { href: "/accounting/books", label: "Buku akuntansi", description: "Tinjau pemetaan akun dan periode pertama buku organisasi, toko dan kanal.", visible: actions.some(action => ["accounting.book.read", "accounting.book.propose", "accounting.book.approve"].includes(action)) },
    { href: "/billing/payment-reviews", label: "Pemeriksaan pembayaran", description: "Periksa pengajuan cabang, bukti asli dan saran pembacaan dalam cakupan akses.", visible: actions.includes("payment.review.read") && actions.includes("invoice.read") },
    { href: "/billing/payments", label: "Bukti pembayaran", description: "Catat transfer dan simpan bukti pembayaran untuk invoice cabang.", visible: actions.includes("payment.evidence") && actions.includes("invoice.read") },
    { href: "/ordering/drafts", label: "Draf pesanan", description: "Lanjutkan kebutuhan barang dan rencana pengiriman cabang Anda.", visible: actions.includes("order.submit") },
    { href: "/ordering/orders", label: "Pesanan saya", description: "Lihat kemasan, tujuan, biaya dan perkembangan pesanan yang telah diajukan.", visible: actions.includes("order.read") },
    { href: "/ordering/reviews", label: "Peninjauan pesanan", description: "Tinjau persetujuan belanja cabang atau penerimaan toko sesuai penugasan.", visible: actions.some(action => ["order.spend.approve", "order.accept", "order.cancel"].includes(action)) },
    { href: "/ordering/spending-policies", label: "Persetujuan belanja", description: "Ajukan dan periksa aturan persetujuan untuk cabang atau divisi.", visible: actions.some(action => ["order.spending.policy.read", "order.spending.policy.propose", "order.spending.policy.approve"].includes(action)) },
    { href: "/shop", label: "Katalog kemasan", description: "Lihat isi dan harga kemasan dalam katalog akun Anda.", visible: actions.includes("catalog.read") },
    { href: "/catalog/prices", label: "Harga kemasan", description: "Ajukan harga per kemasan dan tinjau secara independen.", visible: actions.some(action => ["pricing.manage", "pricing.approve"].includes(action)) },
    { href: "/inventory/sources/policies", label: "Kebijakan operasional stok", description: "Tetapkan pencatat stok dan prosedur pengoperasian melalui peninjauan.", visible: canUseStockSourcePolicies(actions) },
    { href: "/inventory/sources/suspensions", label: "Penghentian sumber", description: "Periksa kanal terdampak dan catat penghentian sesuai penugasan.", visible: canUseStockSourceSuspensions(actions) },
    { href: "/inventory/sources/aliases", label: "Pemetaan kanal", description: "Tinjau penggunaan satu alokasi oleh kanal lain.", visible: canUseStockSources(actions) },
    { href: "/inventory/sources/revisions", label: "Perubahan sumber", description: "Bandingkan dan tinjau perubahan alokasi yang sudah terdaftar.", visible: canUseStockSources(actions) },
    { href: "/inventory/sources", label: "Sumber stok", description: "Ajukan dan tinjau pengaturan sumber stok sesuai penugasan.", visible: canUseStockSources(actions) },
    { href: "/inventory/counts", label: "Hitung barang", description: "Catat hitungan fisik atau lihat pengamatan sesuai penugasan.", visible: canUseStockCounts(actions) },
    { href: "/administration/catalog", label: "Katalog toko", description: "Kelola keluarga produk dan kemasan dalam lingkup toko Anda.", visible: actions.includes("catalog.manage") },
    { href: "/administration/person-reviews", label: "Keterkaitan akun", description: "Tinjau pengajuan penangguhan keterkaitan akun sesuai penugasan.", visible: canReviewPeople(actions) },
    { href: "/administration/accounts", label: "Akun dan undangan", description: "Tinjau undangan dan akun sesuai lingkup tugas.", visible: canManageAccounts(actions) },
    { href: "/administration/access", label: "Akses dan keanggotaan", description: "Ajukan atau tinjau izin sesuai cakupan tugas.", visible: actions.some(action => action.startsWith("access.") || action.startsWith("membership.")) },
    { href: "/administration/organizations/addresses", label: "Alamat organisasi", description: "Ajukan dan periksa alamat penagihan, cabang dan gudang sesuai penugasan.", visible: canUseOrganizationAddresses(actions) },
    { href: "/administration/organizations", label: "Organisasi dan unit", description: "Kelola struktur organisasi melalui persetujuan.", visible: actions.some(action => ["technical.configure", "organization.approve", "access.audit.read"].includes(action)) },
    { href: "/administration/history", label: "Riwayat perubahan", description: "Periksa jejak pengajuan dan keputusan akses.", visible: actions.some(action => ["access.audit.read", "access.approve", "membership.approve", "access.revoke", "technical.configure", "organization.approve"].includes(action)) },
  ].filter(item => item.visible);
}

export function identityMessage(error: unknown): string {
  if (error instanceof RequestNotSentError) return "Permintaan belum dikirim. Perlindungan sesi belum dapat disiapkan. Periksa koneksi, lalu coba lagi.";
  if (!(error instanceof IdentityFailure)) return "Koneksi belum tersedia. Periksa koneksi, lalu coba lagi. Tidak ada perubahan akun yang dikirim.";
  if (error.code === "invalid_invitation") return "Masukkan nomor dan kode undangan lengkap dari pengelola. Gunakan alamat email yang menerima undangan.";
  if (error.code === "actor_changed") return "Akun di peramban telah berubah. Muat ulang sebelum melanjutkan.";
  if (error.status === 401) return "Sesi Anda telah berakhir. Masuk kembali untuk melanjutkan.";
  if (error.status === 403) return "Akun atau tindakan ini belum memiliki akses yang disetujui. Hubungi pengelola organisasi Anda.";
  if (error.status === 429) return "Terlalu banyak percobaan. Tunggu sebentar sebelum mencoba kembali.";
  if (error.status >= 500) return "Layanan akun belum tersedia. Coba lagi nanti atau hubungi pengelola organisasi Anda.";
  return "Permintaan belum dapat dilanjutkan. Periksa isian dan status akun Anda.";
}
