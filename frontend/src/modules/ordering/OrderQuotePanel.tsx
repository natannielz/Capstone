'use client';

import { useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { AccessFailure } from '../administration/workflow';
import type { BuyerVariant } from '../catalog/catalog-buyer-client';
import { createOrderQuoteGateway, orderAmount, orderContextKey, orderQuantity, type OrderContextOption, type OrderQuote } from './order-quote-client';
import shared from '../administration/access.module.css';
import styles from './order-quote.module.css';
import { useDraftSave } from './DraftSave';
import { prepareDraftCommand } from './order-draft-client';

function message(error: unknown) {
  if (error instanceof AccessFailure) {
    if (error.code === 'order_package_quantity_invalid') return 'Isi jumlah kemasan sesuai minimum dan kelipatan pemesanan.';
    if (error.code === 'actor_changed') return 'Akun berubah. Tutup rincian lalu periksa kembali sesi Anda.';
    if (error.status === 401) return 'Sesi berakhir. Masuk kembali sebelum menyiapkan pesanan.';
    if (error.status === 403) return 'Cabang atau kemasan ini belum tersedia untuk pemesanan melalui akun Anda.';
    if (error.code === 'order_selection_changed') return 'Harga atau kemasan telah berubah. Tutup rincian lalu periksa kemasan terbaru.';
  }
  return 'Subtotal belum dapat dihitung. Periksa koneksi lalu coba lagi.';
}
export function OrderQuotePanel({ actorId, item, isCurrent }: { actorId: string; item: BuyerVariant; isCurrent: () => boolean }) {
  const [gateway] = useState(() => createOrderQuoteGateway());
  const [options, setOptions] = useState<OrderContextOption[]>([]), [branch, setBranch] = useState(''), [quantity, setQuantity] = useState('');
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [quote, setQuote] = useState<OrderQuote | null>(null);
  const request = useRef<AbortController | null>(null), result = useRef<HTMLDivElement>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const saving = useDraftSave(actorId, isCurrent, value => { setSaved(value.draftId); setQuote(null); }, undefined, loading || busy);
  useEffect(() => {
    const controller = new AbortController(); request.current = controller;
    void gateway.contexts(actorId, controller.signal).then(value => { if (!controller.signal.aborted) setOptions(value); })
      .catch(failure => { if (!controller.signal.aborted) setError(failure); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => { controller.abort(); request.current?.abort(); };
  }, [actorId, gateway]);
  async function calculate() {
    if (loading || busy || saving.locked || !item.currentPrice || !isCurrent()) return;
    setQuote(null); setError(null);
    const chosen = options.find(option => orderContextKey(option.context) === branch);
    if (!chosen) { setError(new AccessFailure(400, 'invalid_order_context')); return; }
    const controller = new AbortController(); request.current?.abort(); request.current = controller;
    try {
      const packages = orderQuantity(quantity, item.definition.minimumOrderPackages, item.definition.orderIncrementPackages); setBusy(true);
      const value = await gateway.preview(actorId, chosen.context, [{ skuId: item.skuId, definitionId: item.definition.id,
        definitionVersion: item.definition.version, priceId: item.currentPrice.id, priceVersion: item.currentPrice.version, packages }], controller.signal);
      if (!controller.signal.aborted) { setQuote(value); requestAnimationFrame(() => result.current?.focus()); }
    } catch (failure) {
      if (!controller.signal.aborted) { setError(failure); if (failure instanceof AccessFailure && [401, 403, 409].includes(failure.status)) setOptions([]); }
    } finally { if (!controller.signal.aborted) setBusy(false); }
  }
  return <section className={styles.panel} aria-label="Perhitungan untuk cabang" aria-busy={loading || busy}>
    <h3>Siapkan jumlah untuk cabang</h3><p className={shared.help}>Pilih cabang dan jumlah kemasan untuk memeriksa subtotal terbaru.</p>
    {loading ? <p role="status">Memeriksa cabang yang tersedia…</p> : options.length > 0 ? <form onSubmit={event => { event.preventDefault(); void calculate(); }}>
      <label>Cabang pemesan<select value={branch} required disabled={busy || saving.locked} onChange={event => { setBranch(event.target.value); setQuote(null); setSaved(null); setError(null); }}>
        <option value="">Pilih cabang</option>{options.map(option => <option value={orderContextKey(option.context)} key={orderContextKey(option.context)}>
          {option.organizationName ?? 'Organisasi ' + option.context.organizationId} · {option.branchName ?? 'Cabang ' + option.context.branchId}{option.context.divisionId ? ' · ' + (option.divisionName ?? 'Divisi ' + option.context.divisionId) : ''}
        </option>)}</select></label>
      <label>Jumlah kemasan<input inputMode="numeric" value={quantity} maxLength={7} required disabled={busy || saving.locked} aria-describedby="quote-quantity-help"
        onChange={event => { setQuantity(event.target.value); setQuote(null); setSaved(null); setError(null); }} /></label>
      <p id="quote-quantity-help" className={shared.help}>Minimum {item.definition.minimumOrderPackages}; kelipatan {item.definition.orderIncrementPackages}. Satu {item.definition.packageLabel} berisi {item.definition.baseUnitsPerPackage} {item.definition.baseUnitCode}.</p>
      <button className={shared.primary} disabled={busy || saving.locked || saved !== null || branch === '' || quantity === ''}>{busy ? 'Menghitung…' : 'Periksa subtotal'}</button>
    </form> : error === null && <p>Belum ada cabang pemesan yang tersedia untuk akun ini.</p>}
    {error !== null && <p role="alert" className={shared.alert}>{message(error)}</p>}
    {saving.recoveryView}
    {saved && <p role="status" className={shared.success}>Draf tersimpan. <Link href="/ordering/drafts">Buka draf pesanan</Link> untuk melengkapi barang dan pengiriman.</p>}
    {quote && <div className={styles.result} ref={result} tabIndex={-1} role="status"><h4>Subtotal barang</h4><p className={styles.amount}>{orderAmount(quote.currency, quote.catalogSubtotal)}</p>
      <p>{quote.lines[0].packages} {quote.lines[0].definition.packageLabel} · {BigInt(quote.lines[0].baseUnits).toLocaleString('id-ID')} {quote.lines[0].definition.baseUnitCode}</p>
      <p className={shared.help}>Perhitungan awal. Pesanan belum dikirim dan stok belum dipesan. Pajak, ongkir, dan syarat pembayaran diperiksa sebelum pengajuan.</p>
      <button className={shared.primary} disabled={busy || saving.locked} onClick={() => {
        if (!item.currentPrice) return;
        void saving.submit(prepareDraftCommand(actorId, null, '0', { context: quote.context, selections: quote.lines.map(line => ({
          skuId: line.definition.skuId, definitionId: line.definition.id, definitionVersion: line.definition.version,
          priceId: line.price.id, priceVersion: line.price.version, packages: line.packages })), delivery: null, purchaseReference: null }, 'Simpan pilihan kemasan sebagai draf'));
      }}>{saving.busy ? 'Menyimpan…' : 'Simpan sebagai draf'}</button></div>}
  </section>;
}
