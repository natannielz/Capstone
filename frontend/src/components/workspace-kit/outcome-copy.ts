/** Fixed copy for workspace states ("Tenaga Operasional" voice: Anda, plain, verb-first, no jargon). */
export const OUTCOME_KINDS = ["loading", "empty-first", "empty-filtered", "denied", "offline", "conflict", "unknown", "error"] as const;
export type OutcomeKind = (typeof OUTCOME_KINDS)[number];
export type OutcomeCopy = { title: string; body: string; action?: string };

const COPY: Record<OutcomeKind, OutcomeCopy> = {
  "loading": { title: "Memuat data", body: "Data terbaru sedang diambil. Isian Anda tetap tersimpan." },
  "empty-first": { title: "Belum ada data", body: "Data akan tampil di sini setelah ada pengajuan yang dapat Anda lihat." },
  "empty-filtered": { title: "Tidak ada yang cocok dengan filter", body: "Ubah atau hapus filter untuk melihat data lain.", action: "Hapus filter" },
  "denied": { title: "Akses tidak tersedia", body: "Akun Anda belum memiliki penugasan untuk bagian ini. Hubungi pengelola organisasi jika Anda memerlukannya." },
  "offline": { title: "Anda sedang luring", body: "Data yang sudah tampil masih dapat dibaca. Tindakan akan tersedia lagi setelah koneksi pulih." },
  "conflict": { title: "Data sudah berubah", body: "Data diubah setelah Anda membukanya. Muat ulang untuk melihat versi terbaru; isian Anda tidak dikirim.", action: "Muat ulang" },
  "unknown": { title: "Hasil belum pasti", body: "Koneksi terputus sebelum jawaban diterima. Periksa hasil dengan referensi yang sama; tindakan tidak dikirim ulang.", action: "Periksa hasil" },
  "error": { title: "Terjadi kendala sistem", body: "Data belum dapat dimuat. Coba lagi beberapa saat lagi; isian Anda tetap tersimpan.", action: "Coba lagi" },
};

export function outcomeCopy(kind: OutcomeKind): OutcomeCopy {
  return COPY[kind];
}
