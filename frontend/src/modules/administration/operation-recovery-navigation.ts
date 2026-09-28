export function recoveryPage(operation: string) {
  if (operation === "inventory.stock-count.record") return { href: "/inventory/counts", label: "halaman hitung barang" };
  return operation.startsWith("catalog.") ? { href: "/administration/catalog", label: "halaman katalog" }
    : { href: "/administration/person-reviews", label: "halaman peninjauan orang" };
}
export function operationLabel(operation: string) {
  if (operation === "inventory.stock-count.record") return "Pengamatan hitungan barang";
  if (operation.startsWith("catalog.")) return "Perubahan katalog";
  if (operation.startsWith("person.review.")) return "Peninjauan keterkaitan akun";
  if (operation.startsWith("organization.")) return "Perubahan struktur organisasi";
  if (operation.includes("membership")) return "Perubahan keanggotaan";
  if (operation.includes("scope")) return "Perubahan status unit";
  if (operation.includes("identity")) return "Perubahan status akun";
  return "Perubahan akses";
}
