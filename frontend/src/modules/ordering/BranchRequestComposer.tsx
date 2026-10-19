'use client';

import { useEffect, useRef, useState } from 'react';
import { createBuyerCatalogGateway, type BuyerVariant } from '../catalog/catalog-buyer-client';
import { createBranchRequestGateway, requestContextLabel, type BranchRequestContextOption, type BranchRequestPreview } from './branch-request-client';
import { branchRequestInput, requestContextKey, requestMessage, requestPackages } from './branch-request-state';
import { BranchRequestFacts } from './BranchRequestFacts';
import common from '../administration/access.module.css';
import styles from './branch-request.module.css';
import { formatNumber } from '@/lib/format';

type Choice = { item: BuyerVariant; packages: number };
export function BranchRequestComposer({ actorId, locked, current, onSubmit, onFailure }: { actorId: string; locked: boolean; current: () => boolean;
  onSubmit: (preview: BranchRequestPreview) => Promise<void>; onFailure: (error: unknown) => void }) {
  const [gateway] = useState(() => createBranchRequestGateway()), [catalog] = useState(() => createBuyerCatalogGateway());
  const [contexts, setContexts] = useState<BranchRequestContextOption[]>([]), [contextKey, setContextKey] = useState(''), [choices, setChoices] = useState<Choice[]>([]), [note, setNote] = useState('');
  const [items, setItems] = useState<BuyerVariant[]>([]), [total, setTotal] = useState('0'), [search, setSearch] = useState(''), [query, setQuery] = useState(''), [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<BuyerVariant | null>(null), [quantity, setQuantity] = useState(''), [preview, setPreview] = useState<BranchRequestPreview | null>(null);
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), inFlight = useRef(false), searchSequence = useRef(0), review = useRef<HTMLElement>(null);
  const context = contexts.find(item => requestContextKey(item.context) === contextKey), disabled = locked || loading || busy;
  useEffect(() => {
    alive.current = true; const controller = new AbortController();
    void (async () => {
      try {
        const options = await gateway.contexts(actorId, controller.signal); if (controller.signal.aborted) return; setContexts(options);
        const publicId = new URLSearchParams(window.location.search).get('package');
        if (publicId !== null && /^[0-9a-f-]{36}$/u.test(publicId)) {
          const item = await catalog.variant(actorId, publicId, controller.signal);
          if (!controller.signal.aborted && item) { setSearch(item.code); setQuery(item.code); setNotice('Pilih cabang untuk memeriksa kemasan yang Anda buka dari katalog.'); }
        }
      } catch (failure) { if (!controller.signal.aborted) { setError(failure); onFailure(failure); } }
      finally { if (!controller.signal.aborted) setLoading(false); }
    })();
    return () => { alive.current = false; controller.abort(); searchSequence.current++; };
  }, [actorId, gateway, catalog, onFailure]);
  useEffect(() => { if (preview) review.current?.focus(); }, [preview]);
  async function find(nextContextKey = contextKey, nextQuery = search.trim(), nextOffset = 0) {
    const option = contexts.find(item => requestContextKey(item.context) === nextContextKey);
    if (!option || !current() || locked || inFlight.current) return;
    const sequence = ++searchSequence.current; inFlight.current = true; setBusy(true); setError(null); setItems([]); setTotal('0'); setSelected(null); setQuantity('');
    try { const page = await gateway.packages(actorId, option.context, nextQuery, nextOffset); if (alive.current && current() && sequence === searchSequence.current) { setItems(page.items); setTotal(page.total); setQuery(nextQuery); setOffset(nextOffset); } }
    catch (failure) { if (alive.current && current() && sequence === searchSequence.current) { setError(failure); onFailure(failure); } }
    finally { inFlight.current = false; if (alive.current && sequence === searchSequence.current) setBusy(false); }
  }
  function add() {
    if (!selected || disabled || !current()) return;
    try {
      const packages = requestPackages(quantity, selected.definition.minimumOrderPackages, selected.definition.orderIncrementPackages);
      if (choices.length >= 100 && !choices.some(choice => choice.item.skuId === selected.skuId)) { setNotice('Satu permintaan memuat paling banyak 100 jenis kemasan.'); return; }
      setChoices(previous => [...previous.filter(choice => choice.item.skuId !== selected.skuId), { item: selected, packages }]);
      setPreview(null); setSelected(null); setQuantity(''); setError(null); setNotice('Kemasan ditambahkan ke permintaan. Belum dikirim ke toko.');
    } catch (failure) { setError(failure); }
  }
  async function prepare() {
    if (!context || disabled || inFlight.current || !current()) return;
    inFlight.current = true; setBusy(true); setError(null); setPreview(null); setNotice('');
    try {
      const input = branchRequestInput({ context: context.context, note: note.trim() || null, lines: choices.map(({ item, packages }) => ({ skuId: item.skuId, definitionId: item.definition.id, definitionVersion: item.definition.version, packages })) });
      const result = await gateway.preview(actorId, input); if (alive.current && current()) setPreview(result);
    } catch (failure) { if (alive.current && current()) { setError(failure); onFailure(failure); } }
    finally { inFlight.current = false; if (alive.current) setBusy(false); }
  }
  async function confirm() {
    if (!preview || disabled || !current()) return;
    await onSubmit(preview);
    if (alive.current) setPreview(null);
  }
  return <>
    {error !== null && <p className={common.alert} role="alert">{requestMessage(error)}</p>}
    {notice && <p role="status" className={common.help}>{notice}</p>}
    <div className={styles.workspace}><section className={styles.section} aria-label="Pilih kemasan permintaan" aria-busy={loading || busy}><h2>1. Pilih cabang dan kemasan</h2>
      <label>Cabang pemesan<select value={contextKey} disabled={disabled || choices.length > 0 || preview !== null} onChange={event => { setContextKey(event.target.value); setPreview(null); void find(event.target.value, query); }}><option value="">Pilih cabang</option>{contexts.map(option => <option key={requestContextKey(option.context)} value={requestContextKey(option.context)}>{requestContextLabel(option)}</option>)}</select></label>
      {loading ? <p role="status">Memuat cabang yang tersedia…</p> : contexts.length === 0 && <p>Belum ada cabang pemesan dalam penugasan Anda.</p>}
      {choices.length > 0 && <p className={common.help}>Kosongkan pilihan kemasan sebelum mengganti cabang.</p>}
      {context && <><form className={styles.search} onSubmit={event => { event.preventDefault(); void find(); }}><label>Cari nama atau kode kemasan<input value={search} maxLength={128} disabled={disabled} onChange={event => setSearch(event.target.value)} /></label><button className={common.secondary} disabled={disabled}>Cari kemasan</button></form>
        {busy && <p role="status">Memeriksa pilihan kemasan…</p>}
        {!busy && items.length === 0 && <p className={styles.empty}>Tidak ada kemasan yang sesuai untuk cabang ini.</p>}
        <ul className={styles.list}>{items.map(item => <li className={styles.item} key={item.skuId}><strong>{item.familyName}</strong><small>{item.code}</small><p>{item.definition.packageLabel} · isi {formatNumber(item.definition.baseUnitsPerPackage)} {item.definition.baseUnitCode}</p><button className={common.secondary} disabled={disabled} onClick={() => { setSelected(item); setQuantity(''); setError(null); }}>Pilih {item.code}</button></li>)}</ul>
        <div className={common.actions}><button className={common.secondary} disabled={disabled || offset === 0} onClick={() => void find(contextKey, query, Math.max(0, offset - 25))}>Kemasan sebelumnya</button><button className={common.secondary} disabled={disabled || items.length === 0 || BigInt(offset + items.length) >= BigInt(total) || offset >= 10000} onClick={() => void find(contextKey, query, offset + 25)}>Kemasan berikutnya</button></div>
      </>}
      {selected && <section className={styles.scope} aria-label="Periksa kemasan pilihan"><h3>{selected.familyName} — {selected.definition.packageLabel}</h3><p>Isi {formatNumber(selected.definition.baseUnitsPerPackage)} {selected.definition.baseUnitCode} per kemasan.</p><p>Minimum {selected.definition.minimumOrderPackages}; kelipatan {selected.definition.orderIncrementPackages} kemasan.</p><div className={styles.quantity}><label>Jumlah kemasan<input inputMode="numeric" value={quantity} maxLength={7} disabled={disabled} onChange={event => setQuantity(event.target.value)} /></label><button className={common.primary} disabled={disabled || !quantity} onClick={add}>{choices.some(choice => choice.item.skuId === selected.skuId) ? 'Ganti jumlah kemasan' : 'Tambahkan kemasan'}</button></div><button className={common.secondary} disabled={disabled} onClick={() => setSelected(null)}>Tutup pilihan</button></section>}
    </section><section className={`${styles.section} ${styles.sticky}`} aria-label="Isi permintaan"><h2>2. Periksa kebutuhan Anda</h2><p className={common.help}>Pilihan sementara berada di halaman ini. Permintaan tersimpan setelah berhasil diajukan.</p>
      {context && <p><strong>{requestContextLabel(context)}</strong></p>}
      {choices.length === 0 ? <p className={styles.empty}>Pilih kemasan untuk memulai permintaan.</p> : <ul className={styles.list}>{choices.map(choice => <li className={styles.item} key={choice.item.skuId}><strong>{choice.item.familyName}</strong><p>{formatNumber(choice.packages)} {choice.item.definition.packageLabel} · isi {formatNumber(choice.item.definition.baseUnitsPerPackage)} {choice.item.definition.baseUnitCode} per kemasan</p><button className={common.secondary} disabled={disabled} onClick={() => { setSelected(choice.item); setQuantity(String(choice.packages)); setPreview(null); }}>Ubah jumlah {choice.item.code}</button><button className={common.secondary} disabled={disabled} onClick={() => { setChoices(previous => previous.filter(item => item.item.skuId !== choice.item.skuId)); setPreview(null); }}>Hapus {choice.item.code}</button></li>)}</ul>}
      <label>Catatan kebutuhan (opsional)<input value={note} maxLength={1000} disabled={disabled} onChange={event => { setNote(event.target.value); setPreview(null); }} /></label>
      <p className={common.help}>Satu permintaan ditujukan ke satu toko. Tujuannya diperiksa dari pilihan kemasan saat Anda meninjau.</p>
      <button className={common.primary} disabled={disabled || choices.length === 0 || preview !== null} onClick={() => void prepare()}>Tinjau permintaan</button>
    </section></div>
    {preview && <section ref={review} tabIndex={-1} className={styles.review} aria-label="Konfirmasi permintaan"><h2>3. Konfirmasi sebelum mengajukan</h2><BranchRequestFacts value={preview} /><p>Petugas toko akan meninjau kebutuhan ini. Harga, ketersediaan, dan pengaturan pemenuhan dikonfirmasi saat tindak lanjut.</p><div className={common.actions}><button className={common.primary} disabled={disabled} onClick={() => void confirm()}>Konfirmasi ajukan permintaan</button><button className={common.secondary} disabled={disabled} onClick={() => setPreview(null)}>Kembali mengedit</button></div></section>}
  </>;
}
