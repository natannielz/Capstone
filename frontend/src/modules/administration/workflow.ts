import { RequestNotSentError } from "../../api/client";
import type { components } from "../../api/generated/schema";

export type GrantRequest = components["schemas"]["GrantRequestDto"];
export type GrantPreview = components["schemas"]["AccessPreviewDto"];
export type Scope = components["schemas"]["AccessScopeDto"];

export class AccessFailure extends Error {
  constructor(readonly status: number, readonly code: string, readonly traceId?: string) {
    super(code);
    this.name = "AccessFailure";
  }
}

export function stableContent(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stableContent).join(",")}]`;
  if (value !== null && typeof value === "object") {
    const record = value as Record<string, unknown>;
    return `{${Object.keys(record).filter(key => record[key] !== undefined).sort()
      .map(key => `${JSON.stringify(key)}:${stableContent(record[key])}`).join(",")}}`;
  }
  return JSON.stringify(value) ?? "null";
}

/** A late preview can never validate a draft that changed while the request was running. */
export class PreviewSession<T = GrantRequest> {
  private content = "";
  private generation = 0;
  private accepted: GrantPreview | null = null;

  replace(request: T | null) {
    const next = stableContent(request);
    if (next !== this.content) {
      this.content = next;
      this.generation++;
      this.accepted = null;
    }
  }
  begin(request: T) {
    this.replace(request);
    this.accepted = null;
    return { generation: ++this.generation, content: this.content };
  }
  accept(ticket: { generation: number; content: string }, preview: GrantPreview): boolean {
    if (ticket.generation !== this.generation || ticket.content !== this.content) return false;
    this.accepted = preview;
    return true;
  }
  invalidate() { this.generation++; this.accepted = null; }
  get value() { return this.accepted; }
}

type Attempt = { content: string; key: string; unresolved: boolean; state: "pending" | "uncertain" | "failed" | "complete" };

/** One key belongs to one immutable logical request, including target and expected revision. */
export class MutationLedger {
  private attempts = new Map<string, Attempt>();
  constructor(private readonly newKey: () => string = () => crypto.randomUUID()) {}

  begin(operation: string, payload: unknown): string {
    const content = stableContent(payload);
    const previous = this.attempts.get(operation);
    if (previous?.state === "pending") throw new AccessFailure(0, "request_in_progress");
    if (previous?.state === "uncertain" && previous.content !== content)
      throw new AccessFailure(0, "resolve_previous_attempt");
    const attempt = previous && previous.state !== "complete" && previous.content === content
      ? { ...previous, state: "pending" as const }
      : { content, key: this.newKey(), unresolved: false, state: "pending" as const };
    this.attempts.set(operation, attempt);
    return attempt.key;
  }
  complete(operation: string) { const attempt = this.attempts.get(operation); if (attempt) { attempt.state = "complete"; attempt.unresolved = false; } }
  fail(operation: string, error: unknown) {
    const attempt = this.attempts.get(operation);
    if (attempt) {
      attempt.unresolved ||= !(error instanceof RequestNotSentError) && (!(error instanceof AccessFailure) || error.status === 0 || error.status >= 500);
      attempt.state = attempt.unresolved ? "uncertain" : "failed";
    }
  }
  isUncertain(operation: string) { return this.attempts.get(operation)?.state === "uncertain"; }
  resetAfterReconciliation() { this.attempts.clear(); }
}

export async function submitLogicalRequest<T>(ledger: MutationLedger, operation: string, payload: unknown,
  send: (key: string) => Promise<T>): Promise<T> {
  const key = ledger.begin(operation, payload);
  try {
    const result = await send(key);
    ledger.complete(operation);
    return result;
  } catch (error) {
    ledger.fail(operation, error);
    throw error;
  }
}

export function accessMessage(error: unknown): string {
  if (error instanceof RequestNotSentError) return "Permintaan belum dikirim. Perlindungan sesi belum dapat disiapkan. Periksa koneksi, lalu coba kembali.";
  if (!(error instanceof AccessFailure)) return "Koneksi terputus. Hasil belum dapat dipastikan. Coba lagi dengan permintaan yang sama.";
  if (error.code === "recovery_storage_unavailable")
    return "Penyimpanan referensi pemulihan di perangkat tidak tersedia. Perubahan baru belum dikirim. Aktifkan penyimpanan situs; jangan hapus referensi permintaan yang belum terkonfirmasi.";
  if (error.code === "actor_changed")
    return "Akun yang sedang masuk telah berubah. Masuk kembali dengan akun pemilik permintaan untuk memeriksa hasilnya.";
  if (error.code === "recovery_cleanup_failed")
    return "Hasil server sudah terkonfirmasi, tetapi referensi pada perangkat belum dapat dibersihkan. Periksa hasil sekali lagi setelah penyimpanan situs tersedia; jangan kirim perubahan yang sama sebagai permintaan baru.";
  if (error.code === "commit_outcome_unknown" || error.code === "resolve_previous_attempt")
    return "Hasil perubahan belum dapat dipastikan. Coba lagi tanpa mengubah isian; permintaan yang sama akan diperiksa kembali.";
  if (error.status === 401) return "Sesi terverifikasi belum tersedia atau telah berakhir. Masuk kembali dengan akun organisasi Anda.";
  if (error.code === "step_up_required") return "Verifikasi dua langkah terbaru diperlukan sebelum melanjutkan. Muat ulang setelah verifikasi akun.";
  if (error.status === 403) return "Akses untuk tindakan ini tidak tersedia. Muat ulang untuk memeriksa izin terbaru.";
  if (error.status === 409) return "Data atau izin telah berubah. Data terbaru dimuat; periksa dan pratinjau kembali sebelum mengajukan.";
  if (error.status >= 500) return "Layanan belum tersedia. Coba lagi dengan permintaan yang sama.";
  if (error.code === "request_in_progress") return "Permintaan masih diproses.";
  return "Permintaan belum dapat diterima. Periksa akun, cakupan, masa berlaku, dan alasan pengajuan.";
}
