import { identityDestinations } from './identity-workflow';
import { isPilotRelease } from '../../lib/release-scope';

const areas = [
  { id: 'orders', label: 'Kebutuhan cabang', description: 'Pengajuan kebutuhan dan hasil peninjauan toko.', prefixes: ['/ordering/'] },
  { id: 'fulfillment', label: 'Pengiriman dan layanan', description: 'Persiapan, pengiriman, penerimaan dan keluhan barang.', prefixes: ['/fulfillment/'] },
  { id: 'procurement', label: 'Pengadaan dan pemasok', description: 'Kebutuhan toko, pemasok, pembelian dan penerimaan pengadaan.', prefixes: ['/procurement/'] },
  { id: 'finance', label: 'Keuangan dan pembukuan', description: 'Aturan komersial, tagihan, bukti pembayaran, mutasi bank dan buku akuntansi.', prefixes: ['/billing/', '/accounting/', '/reporting/', '/ordering/commercial-policies'] },
  { id: 'catalog', label: isPilotRelease ? 'Katalog barang' : 'Katalog dan persediaan', description: isPilotRelease ? 'Pilihan kemasan dan pengelolaan katalog toko.' : 'Kemasan, harga, sumber stok dan hitungan fisik barang.', prefixes: ['/shop', '/catalog/', '/inventory/', '/operations/', '/administration/catalog'] },
  { id: 'organization', label: 'Organisasi dan akses', description: 'Akun, penugasan, struktur organisasi, riwayat perubahan dan kesehatan sistem.', prefixes: ['/administration/', '/operations/health'] },
] as const;
/** The most specific prefix wins: /ordering/commercial-policies is finance, /operations/health is organisation. */
function areaOf(href: string): string | undefined {
  let best: { id: string; length: number } | undefined;
  for (const area of areas) for (const prefix of area.prefixes)
    if (href.startsWith(prefix) && (!best || prefix.length > best.length)) best = { id: area.id, length: prefix.length };
  return best?.id;
}
const searchable = (value: string) => value.normalize('NFKC').toLocaleLowerCase('id-ID').trim();

/** Presentation only: every destination is still selected by the live permission discovery. */
export function workspaceDirectory(actions: readonly string[], query = '') {
  const destinations = identityDestinations(actions), terms = searchable(query).split(/\s+/u).filter(Boolean);
  return areas.map(area => ({ id: area.id, label: area.label, description: area.description,
    items: destinations.filter(item => areaOf(item.href) === area.id)
      .filter(item => terms.every(term => searchable([area.label, item.label, item.description].join(' ')).includes(term))),
  })).filter(area => area.items.length > 0);
}
