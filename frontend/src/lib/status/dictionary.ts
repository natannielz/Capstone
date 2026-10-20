/**
 * Display tone for status labels. Colour comes from the tone (see --tone-* tokens), never from CSS that
 * matches label text, so rewording a label cannot silently drop its meaning. Unknown labels stay neutral.
 */
export type StatusTone = "neutral" | "info" | "warning" | "success" | "danger";

const groups: Record<Exclude<StatusTone, "neutral">, readonly string[]> = {
  warning: ["Menunggu tinjauan", "Menunggu verifikasi", "Menunggu persetujuan", "Siap dikirim"],
  info: ["Diproses", "Dalam perjalanan", "Dalam pengiriman"],
  success: ["Terpenuhi", "Diterima", "Lunas", "Terverifikasi", "Disetujui", "Transaksi final", "Aktif", "Terbuka untuk umum"],
  danger: ["Lewat jatuh tempo", "Ditolak", "Gagal dikirim", "Belum teridentifikasi"],
};

export const STATUS_LABELS: ReadonlyMap<string, StatusTone> = new Map(
  (Object.entries(groups) as Array<[StatusTone, readonly string[]]>).flatMap(([tone, labels]) => labels.map(label => [label, tone] as const)),
);

export function statusTone(label: string): StatusTone {
  return STATUS_LABELS.get(label.trim()) ?? "neutral";
}
