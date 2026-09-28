export function recoveryPage(operation: string) {
  if (/^stock\.source\.revision\.(propose|approve|reject|withdraw)$/.test(operation)) return { href: "/inventory/sources/revisions", label: "halaman perubahan sumber stok" };
  if (/^stock\.source\.(propose|approve|reject|withdraw)$/.test(operation)) return { href: "/inventory/sources", label: "halaman sumber stok" };
  if (operation === "inventory.stock-count.record") return { href: "/inventory/counts", label: "halaman hitung barang" };
  return operation.startsWith("catalog.") ? { href: "/administration/catalog", label: "halaman katalog" }
    : { href: "/administration/person-reviews", label: "halaman peninjauan orang" };
}
export function operationLabel(operation: string) {
  const sources: Readonly<Record<string, string>> = {
    "stock.source.revision.propose": "Pengajuan perubahan sumber stok", "stock.source.revision.approve": "Persetujuan perubahan sumber stok",
    "stock.source.revision.reject": "Penolakan perubahan sumber stok", "stock.source.revision.withdraw": "Penarikan perubahan sumber stok", "stock.source.propose": "Pengajuan sumber stok", "stock.source.approve": "Persetujuan sumber stok",
    "stock.source.reject": "Penolakan sumber stok", "stock.source.withdraw": "Penarikan pengajuan sumber stok" };
  if (sources[operation]) return sources[operation];
  if (operation === "inventory.stock-count.record") return "Pengamatan hitungan barang";
  if (operation.startsWith("catalog.")) return "Perubahan katalog";
  if (operation.startsWith("person.review.")) return "Peninjauan keterkaitan akun";
  if (operation.startsWith("organization.")) return "Perubahan struktur organisasi";
  if (operation.includes("membership")) return "Perubahan keanggotaan";
  if (operation.includes("scope")) return "Perubahan status unit";
  if (operation.includes("identity")) return "Perubahan status akun";
  return "Perubahan akses";
}
