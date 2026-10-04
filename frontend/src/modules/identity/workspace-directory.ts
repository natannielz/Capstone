import { identityDestinations } from './identity-workflow';
import { isPilotRelease } from '../../lib/release-scope';

const areas = [
  { id: 'orders', label: 'Kebutuhan cabang', description: 'Pengajuan kebutuhan dan hasil peninjauan toko.', prefixes: ['/ordering/'] },
  { id: 'fulfillment', label: 'Pengiriman dan layanan', description: 'Persiapan, pengiriman, penerimaan dan keluhan barang.', prefixes: ['/fulfillment/'] },
  { id: 'procurement', label: 'Pengadaan dan pemasok', description: 'Kebutuhan toko, pemasok, pembelian dan penerimaan pengadaan.', prefixes: ['/procurement/'] },
  { id: 'finance', label: 'Keuangan dan pembukuan', description: 'Tagihan, bukti pembayaran, mutasi bank dan buku akuntansi.', prefixes: ['/billing/', '/accounting/'] },
  { id: 'catalog', label: isPilotRelease ? 'Katalog barang' : 'Katalog dan persediaan', description: isPilotRelease ? 'Pilihan kemasan dan pengelolaan katalog toko.' : 'Kemasan, harga, sumber stok dan hitungan fisik barang.', prefixes: ['/shop', '/catalog/', '/inventory/', '/administration/catalog'] },
  { id: 'organization', label: 'Organisasi dan akses', description: 'Akun, penugasan, struktur organisasi dan riwayat perubahan.', prefixes: ['/administration/'] },
] as const;
const searchable = (value: string) => value.normalize('NFKC').toLocaleLowerCase('id-ID').trim();

/** Presentation only: every destination is still selected by the live permission discovery. */
export function workspaceDirectory(actions: readonly string[], query = '') {
  const destinations = identityDestinations(actions), terms = searchable(query).split(/\s+/u).filter(Boolean);
  return areas.map(area => ({ id: area.id, label: area.label, description: area.description,
    items: destinations.filter(item => areas.find(candidate => candidate.prefixes.some(prefix => item.href.startsWith(prefix)))?.id === area.id)
      .filter(item => terms.every(term => searchable([area.label, item.label, item.description].join(' ')).includes(term))),
  })).filter(area => area.items.length > 0);
}
