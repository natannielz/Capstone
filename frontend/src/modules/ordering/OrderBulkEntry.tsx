'use client';

import { useEffect, useRef, useState } from 'react';
import { AccessFailure } from '../administration/workflow';
import { createBuyerBulkGateway } from '../catalog/catalog-bulk-client';
import { buyerCurrentCode, type BuyerVariant } from '../catalog/catalog-buyer-client';
import type { DraftInput } from './order-draft-client';
import { orderDisplayAmount } from './order-quote-client';
import { BulkInputError, parseBulkSkuInput, prepareBulkSkuSelection, type BulkSkuRow } from './order-bulk';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';

type Review = { rows: readonly BulkSkuRow[]; variants: readonly BuyerVariant[]; prepared: ReturnType<typeof prepareBulkSkuSelection> };
export function OrderBulkEntry({ actorId, currentSelections, isCurrent, onApply, onCancel, onFailure }: {
  actorId: string; currentSelections: DraftInput['selections']; isCurrent: () => boolean;
  onApply: (result: Review['prepared'], variants: readonly BuyerVariant[]) => void;
  onCancel: () => void; onFailure: (error: unknown) => void;
}) {
  const [gateway] = useState(() => createBuyerBulkGateway());
  const [text, setText] = useState(''), [busy, setBusy] = useState(false), [error, setError] = useState<string | null>(null);
  const [review, setReview] = useState<Review | null>(null);
  const alive = useRef(false), pending = useRef(false), request = useRef<AbortController | null>(null), region = useRef<HTMLDivElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; request.current?.abort(); }; }, []);
  useEffect(() => { if (review) region.current?.focus(); }, [review]);
  async function inspect() {
    if (pending.current || !isCurrent()) return;
    setError(null); setReview(null);
    let rows: readonly BulkSkuRow[];
    try { rows = parseBulkSkuInput(text); }
    catch (failure) { setError(failure instanceof BulkInputError ? failure.message : 'Periksa format daftar SKU.'); return; }
    const controller = new AbortController(); request.current = controller; pending.current = true; setBusy(true);
    try {
      const matches = await gateway.exact(actorId, rows.map(row => row.code), controller.signal);
      if (!alive.current || controller.signal.aborted || !isCurrent()) return;
      const prepared = prepareBulkSkuSelection(rows, matches, currentSelections);
      const variants = matches.map(item => item.variant);
      setReview({ rows, variants, prepared });
    } catch (failure) {
      if (!alive.current || controller.signal.aborted || !isCurrent()) return;
      if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed')) onFailure(failure);
      setError(failure instanceof BulkInputError ? failure.message : failure instanceof AccessFailure && failure.status === 403
        ? 'Sebagian SKU tidak tersedia untuk akun ini. Periksa kode dan akses katalog; daftar belum diterapkan.'
        : 'Daftar belum dapat diperiksa. Periksa koneksi, lalu coba kembali.');
    } finally { pending.current = false; if (alive.current) setBusy(false); }
  }
  function apply() {
    if (!review || busy || !alive.current || !isCurrent()) return;
    onApply(review.prepared, review.variants);
  }
  return <section className={styles.review} aria-label="Masukkan banyak SKU" aria-busy={busy}>
    <h3>Masukkan banyak SKU</h3>
    <p className={common.help}>Tempel dua kolom: kode SKU internal dan jumlah kemasan, dipisahkan tab atau titik koma. Maksimum 100 SKU berbeda. Jumlah berarti kemasan SKU tersebut, bukan satuan di dalamnya.</p>
    <p className={common.help}>Gunakan kode utama atau alias internal yang masih aktif. Kode supplier dan barcode tidak diterima pada isian ini. Dua kode untuk barang yang sama harus diringkas menjadi satu baris sebelum diperiksa. Harga dan kemasan diperiksa sebelum daftar diterapkan.</p>
    <label>Daftar SKU dan jumlah kemasan<textarea rows={6} maxLength={16000} value={text} disabled={busy || review !== null}
      onChange={event => { setText(event.target.value); setError(null); }} /></label>
    {error && <p role="alert" className={common.alert}>{error}</p>}
    {!review && <button type="button" className={common.primary} disabled={busy} onClick={() => void inspect()}>{busy ? 'Memeriksa daftar…' : 'Periksa daftar SKU'}</button>}
    {review && <div ref={region} tabIndex={-1} role="region" aria-label="Konfirmasi daftar SKU">
      <h4>Periksa kemasan dan jumlah</h4>
      <ul className={styles.lines}>{review.rows.map((row, index) => {
        const item = review.variants[index];
        return <li className={styles.line} key={item.skuId}><strong>{buyerCurrentCode(item)} · {item.familyName}</strong>
          <p>Kode yang Anda masukkan: {row.code}. Kode utama saat ini: {buyerCurrentCode(item)}.</p>
          {item.code !== buyerCurrentCode(item) && <p>Referensi SKU tetap pada catatan transaksi: {item.code}.</p>}
          <p>{row.packages} {item.definition.packageLabel} · {item.definition.baseUnitsPerPackage} {item.definition.baseUnitCode} per kemasan</p>
          <p>Minimum {item.definition.minimumOrderPackages}; kelipatan {item.definition.orderIncrementPackages}. Harga per kemasan: {orderDisplayAmount(item.currentPrice!.currency, item.currentPrice!.unitPrice)}.</p>
          {review.prepared.replacements.includes(item.skuId) && <p>Jumlah SKU ini dalam draf akan diganti menjadi {row.packages} setelah Anda menerapkan daftar.</p>}
        </li>;
      })}</ul>
      <p>Daftar hanya mengubah isian draf. Tinjau dan konfirmasi penyimpanan setelah daftar diterapkan. Harga, stok dan akses tetap diperiksa saat pengajuan.</p>
      <div className={common.actions}><button type="button" className={common.primary} disabled={busy} onClick={apply}>Terapkan daftar ke draf</button>
        <button type="button" className={common.secondary} disabled={busy} onClick={() => setReview(null)}>Perbaiki daftar SKU</button></div>
    </div>}
    <button type="button" className={common.secondary} onClick={() => { request.current?.abort(); onCancel(); }}>Batalkan daftar SKU</button>
  </section>;
}
