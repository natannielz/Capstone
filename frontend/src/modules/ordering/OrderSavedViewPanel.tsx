'use client';

import {useCallback, useEffect, useRef, useState} from 'react';
import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {createOrderViewGateway, defaultOrderHistoryFilter, orderHistorySharePath, orderViewContextKey, parseOrderHistoryShare,
  parseOrderViewResult, prepareOrderViewCommand, type OrderHistoryFilter, type OrderViewCommand, type SavedOrderView} from './order-view-client';
import {OrderViewJournal, orderViewJournalPrefix, type OrderViewReference} from './order-view-journal';
import styles from '../administration/access.module.css';

type Props = {actorId: string; active: OrderHistoryFilter | null; locked: boolean; isCurrent: () => boolean;
  onApply: (filter: OrderHistoryFilter) => void; onLock: (locked: boolean) => void; onFailure: (error: unknown) => void};
const label = (item: Awaited<ReturnType<ReturnType<typeof createOrderViewGateway>['contexts']>>[number]) =>
  [item.organizationName ?? 'Organisasi', item.branchName ?? 'Cabang', item.context.divisionId ? item.divisionName ?? 'Divisi' : null].filter(Boolean).join(' / ');
export function OrderSavedViewPanel({actorId, active, locked, isCurrent, onApply, onLock, onFailure}: Props) {
  const [gateway] = useState(() => createOrderViewGateway()), [journal] = useState(() => new OrderViewJournal(() => localStorage));
  const [options, setOptions] = useState<Awaited<ReturnType<typeof gateway.contexts>>>([]), [chosen, setChosen] = useState<OrderHistoryFilter>(defaultOrderHistoryFilter);
  const [page, setPage] = useState<Awaited<ReturnType<typeof gateway.list>>>({items: [], nextId: null}), [selected, setSelected] = useState<SavedOrderView | null>(null);
  const [name, setName] = useState(''), [review, setReview] = useState<{command: OrderViewCommand; shown: OrderHistoryFilter; name: string} | null>(null), [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false), [ready, setReady] = useState(false), [references, setReferences] = useState<OrderViewReference[]>([]), [storageError, setStorageError] = useState(false);
  const [notice, setNotice] = useState(''), [error, setError] = useState(''), [reload, setReload] = useState(0), [origin, setOrigin] = useState('');
  const alive = useRef(false), epoch = useRef(0), running = useRef(false), reviewRegion = useRef<HTMLElement>(null), apply = useRef(onApply), current = useRef(isCurrent), failed = useRef(onFailure);
  useEffect(() => {apply.current = onApply; current.current = isCurrent; failed.current = onFailure;});
  function clearReview() {epoch.current++; setReview(null); setConfirmed(false);}
  const refreshJournal = useCallback(() => {
    try {setReferences(journal.list(actorId)); setStorageError(false);} catch {setStorageError(true);}
  }, [journal, actorId]);
  useEffect(() => {
    alive.current = true; let mounted = true; const lifecycleEpoch = epoch;
    const changed = (event: StorageEvent) => {
      if (event.key === null || event.key.startsWith(orderViewJournalPrefix(actorId))) {
        epoch.current++; setReview(null); setConfirmed(false); setSelected(null); setName('');
        setNotice('Ada perubahan filter di tab lain. Periksa hasil yang belum pasti, lalu muat ulang sebelum mengubah filter tersimpan.');
        refreshJournal(); setReload(value => value + 1);
      }
    };
    void Promise.resolve().then(() => {if (mounted) {refreshJournal(); setOrigin(window.location.origin);}});
    window.addEventListener('storage', changed);
    return () => {mounted = false; alive.current = false; lifecycleEpoch.current++; window.removeEventListener('storage', changed);};
  }, [actorId, refreshJournal]);
  useEffect(() => {onLock(busy || storageError || references.length > 0 || review !== null || !ready);}, [busy, storageError, references.length, review, ready, onLock]);
  useEffect(() => {if (review) reviewRegion.current?.focus();}, [review]);
  const share = active && origin ? new URL(orderHistorySharePath(active), origin).href : '';
  useEffect(() => {
    let live = true; const controller = new AbortController(), generation = ++epoch.current;
    void Promise.resolve().then(async () => {
      if (!live) return; setBusy(true); setError('');
      try {
        const contexts = await gateway.contexts(actorId, controller.signal), saved = await gateway.list(actorId, null, controller.signal);
        if (!live || !current.current() || epoch.current !== generation) return;
        setOptions(contexts); setPage(saved);
        if (!ready) {
          const shared = parseOrderHistoryShare(window.location.search);
          if (shared.context !== null && !contexts.some(item => orderViewContextKey(item.context) === orderViewContextKey(shared.context))) throw new AccessFailure(403, 'order_view_context_unavailable');
          setChosen(shared); apply.current(shared); setReady(true);
        }
      } catch (failure) {if (live) {setError('Filter belum dapat dimuat atau tidak tersedia untuk akun ini. Periksa akses dan tautannya.'); if (!(failure instanceof AccessFailure && failure.code === 'order_view_context_unavailable')) failed.current(failure);}}
      finally {if (live) setBusy(false);}
    });
    return () => {live = false; controller.abort();};
  // ready is consumed only during initial URL admission; later refreshes preserve the explicitly applied filter.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gateway, actorId, reload]);
  const blocked = locked || busy || references.length > 0 || storageError;
  async function work(task: (valid: () => boolean) => Promise<void>) {
    if (running.current || !current.current()) return; running.current = true; setBusy(true); setError(''); const generation = ++epoch.current;
    const valid = () => alive.current && current.current() && epoch.current === generation;
    try {await task(valid);} catch (failure) {if (alive.current) {setError(failure instanceof RequestNotSentError ? 'Perubahan belum dikirim. Periksa isian dan muat kembali filter.' : 'Hasil belum dapat dipastikan. Periksa hasil permintaan asli sebelum membuat perubahan lain.'); failed.current(failure);}}
    finally {running.current = false; if (alive.current) {setBusy(false); refreshJournal();}}
  }
  function applyChosen(value: OrderHistoryFilter) {
    if (blocked || review || !isCurrent()) return;
    if (value.context !== null && !options.some(item => orderViewContextKey(item.context) === orderViewContextKey(value.context))) {setError('Konteks filter tidak tersedia untuk akun ini.'); return;}
    clearReview(); setChosen(value); setSelected(null); setName(''); apply.current(value); setReady(true);
    window.history.replaceState(null, '', orderHistorySharePath(value));
  }
  async function load(id: string) {
    if (blocked || review) return;
    await work(async valid => {const saved = await gateway.read(actorId, id); if (!valid()) return;
      setChosen(saved.filter); setSelected(saved); setName(saved.name); apply.current(saved.filter); setReady(true);
      window.history.replaceState(null, '', orderHistorySharePath(saved.filter));});
  }
  async function preview(action: 'create' | 'rename' | 'remove') {
    if (blocked || review || !active) return;
    await work(async valid => {
      const saved = action === 'create' ? null : selected === null ? null : await gateway.read(actorId, selected.id);
      if (!valid()) return; if (action !== 'create' && !saved) throw new RequestNotSentError(400, 'invalid_order_view');
      const command = prepareOrderViewCommand(actorId, action, saved, name, active);
      setReview({command, shown: saved?.filter ?? active, name: action === 'remove' ? saved!.name : name}); setConfirmed(false);
    });
  }
  async function commit() {
    if (blocked || !review || !confirmed) return; const shown = review;
    await work(async valid => {
      if (journal.list(actorId).length > 0) throw new RequestNotSentError(409, 'resolve_original_operation');
      const ref: OrderViewReference = {actorId, action: shown.command.body.action, viewId: shown.command.body.viewId, revision: shown.command.revision,
        key: 'order-view.' + crypto.randomUUID(), createdAt: new Date().toISOString()};
      journal.remember(ref); refreshJournal();
      try {
        const result = await gateway.change(shown.command, ref.key, valid); parseOrderViewResult(result, ref);
        journal.confirmed(ref); if (valid()) {setReview(null); setConfirmed(false); setSelected(null); setName(''); setNotice('Perubahan filter tersimpan.'); setReload(value => value + 1);}
      } catch (failure) {if (failure instanceof RequestNotSentError) journal.confirmed(ref); throw failure;}
    });
  }
  async function recover(ref: OrderViewReference) {
    if (busy || locked || storageError) return;
    await work(async valid => {const result = await gateway.recover(actorId, ref.key);
      if (result === null) {if (valid()) setNotice('Hasil permintaan asli belum ditemukan. Referensi tetap disimpan; periksa lagi tanpa membuat permintaan pengganti.'); return;}
      parseOrderViewResult(result, ref); journal.confirmed(ref);
      if (valid()) {setReview(null); setConfirmed(false); setSelected(null); setName(''); setNotice('Hasil asli perubahan filter sudah ditemukan.'); setReload(value => value + 1);}
    });
  }
  return <section className={styles.accountSection} aria-label="Filter pesanan" aria-busy={busy}>
    <h2>Filter dan urutan</h2><p>Tautan filter hanya membagikan pilihan tampilan. Setiap akun tetap melihat pesanan miliknya yang diizinkan.</p>
    {error && <p role="alert" className={styles.alert}>{error}</p>}{notice && <p role="status">{notice}</p>}
    {storageError && <p role="alert">Referensi pemulihan belum dapat disimpan atau dibaca. Periksa penyimpanan browser sebelum mengubah filter tersimpan.</p>}
    {references.length > 0 && <section aria-label="Pemulihan filter tersimpan"><h3>Periksa hasil perubahan filter</h3>
      {references.map(ref => <p key={ref.key}>Permintaan {ref.action === 'create' ? 'simpan' : ref.action === 'rename' ? 'ubah nama' : 'hapus'} · {ref.createdAt}
        <button className={styles.secondary} disabled={busy || locked || storageError} onClick={() => void recover(ref)}>Periksa hasil filter</button></p>)}</section>}
    <label>Cabang atau divisi pesanan<select value={orderViewContextKey(chosen.context)} disabled={blocked || !!review} onChange={event => {
      const option = options.find(item => orderViewContextKey(item.context) === event.target.value); setChosen(value => ({...value, context: option?.context ?? null}));
    }}><option value="">Semua konteks yang diizinkan</option>{options.map(item => <option key={orderViewContextKey(item.context)} value={orderViewContextKey(item.context)}>{label(item)}</option>)}</select></label>
    <label>Urutan pesanan<select value={chosen.sort} disabled={blocked || !!review} onChange={event => setChosen(value => ({...value, sort: event.target.value === 'oldest' ? 'oldest' : 'newest'}))}>
      <option value="newest">Terbaru dahulu</option><option value="oldest">Terlama dahulu</option></select></label>
    <div className={styles.actions}><button className={styles.primary} disabled={blocked || !!review} onClick={() => applyChosen(chosen)}>Terapkan filter</button>
      <button className={styles.secondary} disabled={busy || locked || !!review} onClick={() => {clearReview(); setReload(value => value + 1);}}>Muat ulang filter</button>
      {!ready && <button className={styles.secondary} disabled={blocked} onClick={() => applyChosen(defaultOrderHistoryFilter)}>Gunakan filter bawaan</button>}</div>
    {active && <><label>Tautan filter pesanan<input readOnly value={share} /></label><button className={styles.secondary} disabled={blocked || !!review} onClick={() => {
      void navigator.clipboard.writeText(share).then(() => setNotice('Tautan filter disalin.'), () => setNotice('Pilih dan salin tautan filter pada kolom di atas.'));
    }}>Salin tautan filter</button></>}
    <h3>Filter tersimpan saya</h3>{page.items.length === 0 && <p>Belum ada filter tersimpan yang tersedia pada halaman ini.</p>}
    <ul>{page.items.map(item => <li key={item.id}><button className={styles.secondary} disabled={blocked || !!review} onClick={() => void load(item.id)}>Muat filter {item.name}</button></li>)}</ul>
    <div className={styles.actions}><button className={styles.secondary} disabled={blocked || !!review} onClick={() => void work(async valid => {const found = await gateway.list(actorId); if (valid()) setPage(found);})}>Filter terbaru</button>
      {page.nextId && <button className={styles.secondary} disabled={blocked || !!review} onClick={() => void work(async valid => {const found = await gateway.list(actorId, page.nextId); if (valid()) setPage(found);})}>Filter berikutnya</button>}</div>
    <label>Nama filter pesanan<input value={name} maxLength={80} disabled={blocked || !!review} onChange={event => setName(event.target.value)} /></label>
    <div className={styles.actions}><button className={styles.primary} disabled={blocked || !!review || !active || name.trim().length === 0} onClick={() => void preview('create')}>Tinjau simpan filter baru</button>
      {selected && <><button className={styles.secondary} disabled={blocked || !!review || name.trim().length === 0} onClick={() => void preview('rename')}>Tinjau nama filter</button>
        <button className={styles.secondary} disabled={blocked || !!review} onClick={() => void preview('remove')}>Tinjau hapus filter</button></>}</div>
    {review && <section ref={reviewRegion} tabIndex={-1} aria-label="Konfirmasi perubahan filter"><h3>Periksa perubahan filter</h3>
      <p>{review.command.body.action === 'create' ? 'Simpan filter baru' : review.command.body.action === 'rename' ? 'Ubah nama filter' : 'Hapus filter'}: {review.name}</p>
      <p>{review.shown.context === null ? 'Semua konteks yang diizinkan' : options.filter(item => orderViewContextKey(item.context) === orderViewContextKey(review.shown.context)).map(label).join('')} · {review.shown.sort === 'newest' ? 'Terbaru dahulu' : 'Terlama dahulu'}</p>
      <label><input type="checkbox" checked={confirmed} disabled={blocked} onChange={event => setConfirmed(event.target.checked)} />Saya sudah memeriksa perubahan filter ini.</label>
      <div className={styles.actions}><button className={styles.primary} disabled={blocked || !confirmed} onClick={() => void commit()}>Konfirmasi perubahan filter</button>
        <button className={styles.secondary} disabled={blocked} onClick={clearReview}>Batalkan perubahan filter</button></div></section>}
  </section>;
}
