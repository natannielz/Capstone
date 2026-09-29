'use client';

import { useEffect, useRef, useState, type FormEvent } from 'react';
import Link from 'next/link';
import { AccessFailure } from '../administration/workflow';
import { createBuyerCatalogGateway, type BuyerVariant } from '../catalog/catalog-buyer-client';
import { CatalogReadGate } from '../catalog/catalog-editor-state';
import { prepareDraftCommand, type DraftCommand, type OrderDraft } from './order-draft-client';
import { createOrderQuoteGateway, orderAmount, orderQuantity, type OrderQuote } from './order-quote-client';
import { draftMessage } from './DraftSave';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';

export function OrderDraftEditor({ actorId, draft, initialQuote, initialIssue, locked, isCurrent, onSubmit, onFailure }: {
  actorId: string; draft: OrderDraft; initialQuote: OrderQuote | null; initialIssue: unknown; locked: boolean;
  isCurrent: () => boolean; onSubmit: (command: DraftCommand) => Promise<void>; onFailure: (error: unknown) => void;
}) {
  const [quotes] = useState(() => createOrderQuoteGateway()), [catalog] = useState(() => createBuyerCatalogGateway()), [gate] = useState(() => new CatalogReadGate());
  const [selections, setSelections] = useState(draft.input.selections), [quantities, setQuantities] = useState(draft.input.selections.map(item => String(item.packages)));
  const [labels, setLabels] = useState(() => new Map((initialQuote?.lines ?? []).map(line => [line.definition.skuId, { name: line.familyName, code: line.skuCode, definition: line.definition }])));
  const [reference, setReference] = useState(draft.input.purchaseReference ?? ''), [delivery, setDelivery] = useState(draft.input.delivery !== null);
  const [address, setAddress] = useState(draft.input.delivery ?? { recipientName: '', phone: '', address: '', city: '', postalCode: '', instructions: '' });
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(initialIssue), [search, setSearch] = useState(''), [results, setResults] = useState<BuyerVariant[]>([]);
  const [searchPage, setSearchPage] = useState<{ query: string; offset: number; total: string } | null>(null), [selected, setSelected] = useState<BuyerVariant | null>(null), [packageQuantity, setPackageQuantity] = useState('');
  const [review, setReview] = useState<{ command: DraftCommand; quote: OrderQuote } | null>(null);
  const alive = useRef(false), pending = useRef(false), reviewRef = useRef<HTMLDivElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; gate.cancel(); }; }, [gate]);
  useEffect(() => { if (review) reviewRef.current?.focus(); }, [review]);
  const disabled = locked || busy || review !== null, current = () => alive.current && isCurrent();
  const failed = (failure: unknown) => { setError(failure); if (failure instanceof AccessFailure && ([401, 403].includes(failure.status) || failure.code === 'actor_changed')) onFailure(failure); };
  async function find(query = search.trim(), offset = 0) {
    if (disabled || pending.current || !current()) return;
    pending.current = true; setBusy(true); setError(null); setSelected(null); setResults([]);
    const request = gate.begin(actorId, 'search:' + query + ':' + offset);
    try {
      const page = await catalog.query(actorId, query, offset, request.signal);
      if (request.current() && current()) { setResults(page.items); setSearchPage({ query, offset, total: page.total }); }
    } catch (failure) { if (request.current() && current()) { failed(failure); setSearchPage(null); } }
    finally { pending.current = false; if (request.current()) setBusy(false); }
  }
  async function choose(item: BuyerVariant) {
    if (disabled || pending.current || !current()) return;
    pending.current = true; setBusy(true); setError(null); setSelected(null); setPackageQuantity('');
    const request = gate.begin(actorId, item.publicId);
    try {
      const variant = await catalog.variant(actorId, item.publicId, request.signal);
      if (request.current() && current()) {
        if (!variant) throw new AccessFailure(409, 'order_selection_changed');
        setSelected(variant);
      }
    } catch (failure) { if (request.current() && current()) failed(failure); }
    finally { pending.current = false; if (request.current()) setBusy(false); }
  }
  function includePackage() {
    if (!selected?.currentPrice || disabled || !current()) return;
    try {
      const packages = orderQuantity(packageQuantity, selected.definition.minimumOrderPackages, selected.definition.orderIncrementPackages);
      const choice = { skuId: selected.skuId, definitionId: selected.definition.id, definitionVersion: selected.definition.version, priceId: selected.currentPrice.id, priceVersion: selected.currentPrice.version, packages };
      const index = selections.findIndex(item => item.skuId === choice.skuId);
      if (index < 0 && selections.length >= 100) throw new AccessFailure(400, 'invalid_order_draft');
      setSelections(index < 0 ? [...selections, choice] : selections.map((item, position) => position === index ? choice : item));
      setQuantities(index < 0 ? [...quantities, String(packages)] : quantities.map((item, position) => position === index ? String(packages) : item));
      setLabels(new Map(labels).set(choice.skuId, { name: selected.familyName, code: selected.code, definition: selected.definition }));
      setSelected(null); setResults([]); setSearchPage(null); setSearch(''); setError(null);
    } catch (failure) { failed(failure); }
  }
  async function prepare(event: FormEvent) {
    event.preventDefault(); if (disabled || pending.current || !current()) return;
    setError(null); pending.current = true; setBusy(true); const request = gate.begin(actorId, 'review');
    try {
      const input = { context: draft.input.context, selections: selections.map((item, index) => {
        const definition = labels.get(item.skuId)?.definition;
        return { ...item, packages: orderQuantity(quantities[index], definition?.minimumOrderPackages ?? 1, definition?.orderIncrementPackages ?? 1) };
      }), purchaseReference: reference.trim() || null, delivery: delivery ? { recipientName: address.recipientName.trim(), phone: address.phone.trim(),
        address: address.address.trim(), city: address.city.trim(), postalCode: address.postalCode?.trim() || null, instructions: address.instructions?.trim() || null } : null };
      const command = prepareDraftCommand(actorId, draft.id, draft.revision, input, 'Perbarui kemasan dan rencana pengiriman draf');
      const quote = await quotes.preview(actorId, input.context, input.selections, request.signal);
      if (request.current() && current()) setReview({ command, quote });
    } catch (failure) { if (request.current() && current()) failed(failure); }
    finally { pending.current = false; if (request.current()) setBusy(false); }
  }
  return <section className={styles.editor} aria-label="Isi draf" aria-busy={busy || locked}>
    <h2>Draf {draft.id}</h2><p className={common.help}>Versi {draft.revision}. Perubahan baru tersimpan setelah Anda meninjau dan mengonfirmasi.</p>
    {!locked && <p><Link href={'/ordering/checkout?draft=' + encodeURIComponent(draft.id)}>Lanjut ke pengajuan draf tersimpan</Link></p>}
    {error !== null && <p role="alert" className={common.alert}>{initialQuote === null && error === initialIssue ? 'Sebagian kemasan atau harga tidak lagi tersedia. Pilih kembali kemasan dari katalog; pilihan lama tidak diganti otomatis.' : draftMessage(error, true)}</p>}
    <form className={styles.form} onSubmit={event => void prepare(event)}>
      <ul className={styles.lines}>{selections.map((item, index) => {
        const label = labels.get(item.skuId);
        return <li className={styles.line} key={item.skuId}><h3>{label ? label.name + ' — ' + label.definition.packageLabel : 'Kemasan yang perlu diperiksa'}</h3>
          {label ? <p>{label.code} · {label.definition.baseUnitsPerPackage} {label.definition.baseUnitCode} per kemasan</p> : <p className={common.help}>Buka katalog untuk memilih kemasan terbaru, lalu hapus pilihan lama jika sudah diganti.</p>}
          <label>Jumlah kemasan {index + 1}<input required inputMode="numeric" maxLength={7} value={quantities[index]} disabled={disabled} onChange={event => setQuantities(values => values.map((value, position) => position === index ? event.target.value : value))} /></label>
          {label && <p className={common.help}>Minimum {label.definition.minimumOrderPackages}; kelipatan {label.definition.orderIncrementPackages}.</p>}
          <button type="button" className={common.secondary} disabled={disabled} onClick={() => { setSelections(values => values.filter((_, position) => position !== index)); setQuantities(values => values.filter((_, position) => position !== index)); }}>Hapus kemasan {index + 1}</button>
        </li>;
      })}</ul>
      {selections.length === 0 && <p>Pilih setidaknya satu kemasan sebelum menyimpan draf.</p>}
      <label>Referensi pembelian (opsional)<input value={reference} disabled={disabled} maxLength={160} onChange={event => setReference(event.target.value)} /></label>
      <label className={common.checkbox}><input type="checkbox" checked={delivery} disabled={disabled} onChange={event => setDelivery(event.target.checked)} />Isi rencana pengiriman</label>
      {delivery && <fieldset className={common.fields} disabled={disabled}><legend>Rencana pengiriman</legend><p className={common.help}>Alamat ini diajukan oleh Anda. Tujuan dan penerima tetap perlu diverifikasi sebelum pesanan diproses.</p>
        <label>Nama penerima<input required value={address.recipientName} maxLength={160} onChange={event => setAddress({ ...address, recipientName: event.target.value })} /></label>
        <label>Telepon penerima<input required type="tel" value={address.phone} maxLength={32} onChange={event => setAddress({ ...address, phone: event.target.value })} /></label>
        <label><span id="draft-address-label">Alamat pengiriman</span><textarea aria-labelledby="draft-address-label" required rows={3} value={address.address} maxLength={1000} onChange={event => setAddress({ ...address, address: event.target.value })} /></label>
        <div className={common.twoFields}><label>Kota<input required value={address.city} maxLength={120} onChange={event => setAddress({ ...address, city: event.target.value })} /></label>
          <label>Kode pos (opsional)<input inputMode="numeric" pattern="[0-9]{5}" value={address.postalCode ?? ''} maxLength={5} onChange={event => setAddress({ ...address, postalCode: event.target.value })} /></label></div>
        <label><span id="draft-instructions-label">Catatan pengiriman (opsional)</span><textarea aria-labelledby="draft-instructions-label" rows={2} value={address.instructions ?? ''} maxLength={1000} onChange={event => setAddress({ ...address, instructions: event.target.value })} /></label>
      </fieldset>}
      <button className={common.primary} disabled={disabled || selections.length === 0}>{busy ? 'Memeriksa…' : 'Tinjau perubahan draf'}</button>
    </form>
    {review && <div className={styles.review} ref={reviewRef} role="region" aria-label="Konfirmasi draf" tabIndex={-1}><h3>Periksa sebelum menyimpan</h3>
      <ul>{review.quote.lines.map(line => <li key={line.definition.skuId}>{line.familyName} — {line.packages} {line.definition.packageLabel} · {line.baseUnits} {line.definition.baseUnitCode} · {orderAmount(review.quote.currency, line.catalogSubtotal)}</li>)}</ul>
      <p><strong>Subtotal barang {orderAmount(review.quote.currency, review.quote.catalogSubtotal)}</strong></p><p>Referensi: {review.command.input.purchaseReference ?? 'Belum diisi'}</p>
      {review.command.input.delivery && <p className={styles.address}>{review.command.input.delivery.recipientName} · {review.command.input.delivery.phone}{'\n'}{review.command.input.delivery.address}{'\n'}{review.command.input.delivery.city} {review.command.input.delivery.postalCode}</p>}
      <p className={common.help}>Draf belum menjadi pesanan. Harga dan ketersediaan diperiksa lagi saat pengajuan.</p><div className={common.actions}>
        <button className={common.primary} disabled={locked || busy} onClick={() => void onSubmit(review.command)}>Konfirmasi simpan draf</button>
        <button className={common.secondary} disabled={locked || busy} onClick={() => setReview(null)}>Kembali mengedit</button></div></div>}
    <section className={styles.section} aria-label="Pilihan kemasan baru"><h3>Tambah atau pilih kembali kemasan</h3><p className={common.help}>Setiap pilihan harus diperiksa untuk cabang draf ini. Memilih ulang kode yang sama mengganti kemasan, harga, dan jumlahnya setelah konfirmasi Anda.</p>
      <form className={common.lookup} onSubmit={event => { event.preventDefault(); void find(); }}><label>Cari kemasan untuk draf<input value={search} maxLength={128} disabled={disabled} onChange={event => setSearch(event.target.value)} /></label>
        <button className={common.secondary} disabled={disabled}>Cari kemasan</button></form>
      <ul className={styles.choices}>{results.map(item => <li key={item.skuId}><button className={common.secondary} disabled={disabled} onClick={() => void choose(item)}>
        <strong>{item.familyName}</strong><span>{item.code} · {item.definition.packageLabel} · {item.definition.baseUnitsPerPackage} {item.definition.baseUnitCode}</span></button></li>)}</ul>
      {searchPage && <div className={common.actions}><button className={common.secondary} disabled={disabled || searchPage.offset === 0} onClick={() => void find(searchPage.query, Math.max(0, searchPage.offset - 25))}>Kemasan sebelumnya</button>
        <button className={common.secondary} disabled={disabled || searchPage.offset >= 10000 || BigInt(searchPage.offset + results.length) >= BigInt(searchPage.total)} onClick={() => void find(searchPage.query, searchPage.offset + 25)}>Kemasan berikutnya</button></div>}
      {searchPage && results.length === 0 && <p>Tidak ada kemasan yang sesuai dengan pencarian ini.</p>}
      {selected && <div className={styles.selection} role="region" aria-label="Periksa pilihan kemasan"><h3>{selected.familyName} — {selected.definition.packageLabel}</h3><p>{selected.code} · {selected.definition.baseUnitsPerPackage} {selected.definition.baseUnitCode} per kemasan</p>
        <p>{selected.currentPrice ? orderAmount(selected.currentPrice.currency, selected.currentPrice.unitPrice) + ' per kemasan' : 'Harga belum tersedia'}</p>
        <p>Minimum {selected.definition.minimumOrderPackages}; kelipatan {selected.definition.orderIncrementPackages}.</p>
        <label>Jumlah kemasan pilihan<input inputMode="numeric" value={packageQuantity} maxLength={7} disabled={disabled} onChange={event => setPackageQuantity(event.target.value)} /></label>
        <div className={common.actions}><button className={common.primary} disabled={disabled || !selected.currentPrice || !packageQuantity} onClick={includePackage}>
          {selections.some(item => item.skuId === selected.skuId) ? 'Ganti pilihan kode ini' : 'Tambahkan kemasan ini'}</button>
          <button className={common.secondary} disabled={disabled} onClick={() => setSelected(null)}>Batalkan pilihan</button></div></div>}
    </section>
  </section>;
}
