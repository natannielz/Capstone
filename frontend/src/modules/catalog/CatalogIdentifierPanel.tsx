'use client';
import {useCallback, useEffect, useRef, useState} from 'react';
import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {ownerKey, type CatalogSku} from './catalog-workflow';
import {IdentifierJournal, createCatalogIdentifierGateway, identifierCommand, identifierJournalPrefix, identifierReference,
  type CatalogIdentifierView, type IdentifierCommand, type IdentifierReference, type IdentifierResult} from './catalog-identifier-client';
import common from '../administration/access.module.css';
import styles from './catalog-editor.module.css';
import { formatDateTime } from '@/lib/format';

export function useCatalogIdentifierRecovery(actorId: string) {
  const [journal] = useState(() => new IdentifierJournal(() => window.localStorage));
  const [snapshot, setSnapshot] = useState<{actorId: string | null; entries: IdentifierReference[]; blocked: boolean}>({actorId: null, entries: [], blocked: false});
  const refresh = useCallback(() => {
    try {setSnapshot({actorId, entries: journal.list(actorId), blocked: false});}
    catch {setSnapshot({actorId, entries: [], blocked: true});}
  }, [actorId, journal]);
  useEffect(() => {
    let active = true; void Promise.resolve().then(() => {if (active) refresh();});
    const changed = (event: StorageEvent) => {if (event.key === null || event.key.startsWith(identifierJournalPrefix(actorId))) refresh();};
    window.addEventListener('storage', changed); return () => {active = false; window.removeEventListener('storage', changed);};
  }, [actorId, refresh]);
  const ready = snapshot.actorId === actorId;
  return {journal, entries: ready ? snapshot.entries : [], blocked: ready && snapshot.blocked, locked: !ready || snapshot.blocked || snapshot.entries.length > 0, refresh};
}
export type CatalogIdentifierRecovery = ReturnType<typeof useCatalogIdentifierRecovery>;
function message(error: unknown) {
  if (error instanceof RequestNotSentError) return 'Permintaan belum dikirim. Periksa isian dan penyimpanan browser, lalu kirim lagi.';
  if (error instanceof AccessFailure && error.status === 403) return 'Izin pada cakupan ini perlu diperiksa kembali. Referensi hasil yang belum pasti tetap disimpan.';
  if (error instanceof AccessFailure && error.status === 409) return 'Versi atau isi sumber berubah. Muat data terbaru; hasil permintaan asli yang belum pasti tetap perlu diperiksa.';
  return 'Hasil belum dapat dipastikan. Gunakan referensi asli pada bagian pemulihan kode; jangan membuat permintaan pengganti.';
}
export function CatalogIdentifierRecoveryPanel({actorId, recovery, disabled, isCurrent, onBusyChange, onRecovered, onFailure}: {
  actorId: string; recovery: CatalogIdentifierRecovery; disabled: boolean; isCurrent: () => boolean; onBusyChange: (value: boolean) => void;
  onRecovered: (result: IdentifierResult) => void; onFailure: (failure: unknown) => void;
}) {
  const [gateway] = useState(() => createCatalogIdentifierGateway()), [busy, setBusy] = useState(false), [notice, setNotice] = useState('');
  const pending = useRef(false);
  async function recover(original: IdentifierReference) {
    if (disabled || pending.current || !isCurrent()) return; pending.current = true; setBusy(true); onBusyChange(true); setNotice('');
    try {
      const outcome = await gateway.recover(original, {actorId, isCurrent}); if (!isCurrent()) return;
      if (outcome.status === 'confirmed') {
        recovery.journal.confirmed(original); recovery.refresh();
        setNotice('Hasil permintaan asli terkonfirmasi. Referensi lain tetap disimpan.'); onRecovered(outcome.result);
      } else setNotice('Hasil belum terkonfirmasi. Referensi asli tetap disimpan; periksa kembali sebelum membuat perubahan baru.');
    } catch (failure) {if (isCurrent()) {setNotice(message(failure)); onFailure(failure);}}
    finally {pending.current = false; setBusy(false); onBusyChange(false);}
  }
  if (!recovery.entries.length && !recovery.blocked && !notice) return null;
  return <section className={common.accountSection} aria-label="Pemulihan kode barang"><h2>Periksa hasil pencatatan kode</h2>
    <p>Referensi asli tetap tersedia setelah browser dibuka kembali. Penyimpanan ini hanya berisi referensi, tanpa kode barang, alasan, atau isi formulir.</p>
    {notice && <p role="status">{notice}</p>}
    {recovery.blocked && <div role="alert"><p>Referensi browser belum dapat dibaca. Perubahan ditahan sampai penyimpanan dapat diperiksa.</p>
      <button type="button" className={common.secondary} disabled={disabled || busy} onClick={recovery.refresh}>Periksa referensi kode</button></div>}
    <ul className={common.recoveryList}>{recovery.entries.map(ref => <li key={ref.operation + ':' + ref.key} data-identifier-recovery-key={ref.key}>
      <div><strong>{ref.operation === 'catalog.identifier.assign' ? 'Pencatatan kode' : ref.operation === 'catalog.identifier.retire' ? 'Penghentian kode' : 'Koreksi kode utama'} · SKU {ref.skuId}</strong>
        <p>{formatDateTime(ref.createdAt)}</p><details><summary>Referensi bantuan</summary><code>{ref.key}</code></details></div>
      <button type="button" className={common.secondary} disabled={disabled || busy || recovery.blocked} onClick={() => void recover(ref)}>Periksa hasil kode</button>
    </li>)}</ul>
  </section>;
}
export function CatalogIdentifierPanel({actorId, sku, recovery, locked, isCurrent, onClose, onChanged, onFailure}: {
  actorId: string; sku: CatalogSku; recovery: CatalogIdentifierRecovery; locked: boolean; isCurrent: () => boolean;
  onClose: () => void; onChanged: (result: IdentifierResult) => void; onFailure: (failure: unknown) => void;
}) {
  const [gateway] = useState(() => createCatalogIdentifierGateway()), [view, setView] = useState<CatalogIdentifierView | null>(null);
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [notice, setNotice] = useState('');
  const [action, setAction] = useState<'assign' | 'retire' | 'correct'>('assign'), [kind, setKind] = useState<'skuCode' | 'gtin'>('skuCode');
  const [raw, setRaw] = useState(''), [selected, setSelected] = useState(''), [provenance, setProvenance] = useState(''), [reason, setReason] = useState('');
  const [preview, setPreview] = useState<IdentifierCommand | null>(null), alive = useRef(false), pending = useRef(false), generation = useRef(0);
  const failure = useRef(onFailure); useEffect(() => {failure.current = onFailure;}, [onFailure]);
  const owner = ownerKey(sku.owner);
  const current = useCallback(() => alive.current && isCurrent(), [isCurrent]);
  const cancelRead = useCallback(() => {generation.current++;}, []);
  const load = useCallback(async () => {
    const ticket = ++generation.current; setLoading(true); setView(null); setPreview(null);
    try {const result = await gateway.read(sku.id, {actorId, isCurrent: () => current() && generation.current === ticket}, owner);
      if (current() && generation.current === ticket) {setView(result); setNotice('');}}
    catch (error) {if (current() && generation.current === ticket) {setNotice(message(error)); failure.current(error);}}
    finally {if (current() && generation.current === ticket) setLoading(false);}
  }, [actorId, sku.id, owner, gateway, current]);
  useEffect(() => {alive.current = true; void Promise.resolve().then(load); return () => {alive.current = false; cancelRead();};}, [load, cancelRead]);
  const prior = useRef<IdentifierReference[]>([]);
  useEffect(() => {
    const changed = prior.current.some(old => !recovery.entries.some(ref => ref.operation === old.operation && ref.key === old.key)); prior.current = recovery.entries;
    let active = true;
    void Promise.resolve().then(() => {
      if (!active) return;
      if (changed) {setView(null); setPreview(null); setRaw(''); setReason(''); setProvenance(''); setNotice('Permintaan diselesaikan. Muat data terbaru sebelum meninjau kode lain.');}
      else if (recovery.entries.length) setPreview(null);
    });
    return () => {active = false;};
  }, [recovery.entries]);
  function choose(next: 'assign' | 'retire' | 'correct', id = '') {setAction(next); setSelected(id); setRaw(''); setProvenance(''); setReason(''); setPreview(null); setNotice('');}
  function review() {
    if (!view || locked || recovery.locked || busy) return;
    try {setPreview(identifierCommand(view, action, {kind, rawValue: raw, identifierId: selected, provenanceReference: provenance.trim(), reason: reason.trim()})); setNotice('');}
    catch {setNotice('Periksa kode, kemasan, referensi asal, dan alasan. GTIN harus sama persis dengan sumber yang diperiksa.');}
  }
  async function save() {
    if (!preview || !view || locked || recovery.locked || pending.current || !current()) return;
    const command = preview, original = identifierReference(actorId, command, crypto.randomUUID(), new Date().toISOString());
    pending.current = true; setBusy(true); setNotice('');
    try {
      try {recovery.journal.remember(original); recovery.refresh();} catch {throw new RequestNotSentError(503, 'catalog_identifier_storage_unavailable');}
      const result = await gateway.submit(command, original, {actorId, isCurrent: current}); if (!current()) return;
      recovery.journal.confirmed(original); recovery.refresh(); setPreview(null); setRaw(''); setReason(''); setProvenance(''); onChanged(result);
    } catch (error) {
      // Only an explicit pre-dispatch failure proves this first command was not sent.
      if (error instanceof RequestNotSentError) {try {recovery.journal.confirmed(original); recovery.refresh();} catch {/* Preserve unreadable references. */}}
      if (current()) {setPreview(null); setView(null); setNotice(message(error)); failure.current(error);}
    } finally {pending.current = false; if (alive.current) setBusy(false);}
  }
  const disabled = locked || recovery.locked || busy || loading || !view?.catalog.active;
  const target = view?.identifiers.find(row => row.id === selected);
  return <section className={common.accountSection} aria-label="Kode barang dan GTIN"><div className={styles.sectionHeading}><div><h2>Kode barang dan GTIN</h2>
    <p>{view?.catalog.code ?? sku.code} · {sku.definition.packageLabel} · {sku.definition.baseUnitsPerPackage} {sku.definition.baseUnitCode} per kemasan</p></div>
    <button type="button" className={common.secondary} disabled={busy} onClick={onClose}>Kembali ke varian</button></div>
    <p className={common.help}>Catat kode dari sumber yang telah diperiksa. Pemeriksaan digit GTIN hanya memeriksa format; pencatatan ini tidak membuktikan hak penggunaan atau keaslian barang.</p>
    {notice && <p role="status" className={styles.notice}>{notice}</p>}
    <button type="button" className={common.secondary} disabled={busy || locked || recovery.locked || loading} onClick={() => void load()}>Muat kode terbaru</button>
    {loading && <p role="status">Memeriksa kode dan kemasan terbaru…</p>}
    {view && <><p>Kode utama saat ini: <strong>{view.identifiers.find(row => row.isPrimary)?.canonicalValue}</strong></p>
      <p className={common.help}>SKU tetap untuk riwayat: {view.catalog.code}. Koreksi kode utama tidak mengganti identitas barang atau kode pada transaksi yang telah dicatat.</p>
      <ul className={styles.variantList}>{view.identifiers.map(row => <li key={row.id}><div className={styles.sectionHeading}><strong>{row.rawValue}</strong>
      <span>{row.isPrimary ? 'Kode utama' : row.kind === 'gtin' ? 'GTIN' : row.kind === 'skuCode' ? 'Alias SKU' : 'Kode sumber'} · {row.retiredAt ? 'Dihentikan' : 'Aktif'}</span></div>
      <p>Nilai pembanding: {row.canonicalValue}</p><p>Referensi asal: {row.provenanceReference}</p>
      <p className={common.help}>Dicatat {formatDateTime(row.effectiveAt)}{row.retiredAt ? ' · dihentikan ' + formatDateTime(row.retiredAt) : ''}</p>
      <div className={common.actions}>{row.isPrimary ? <button type="button" className={common.secondary} disabled={disabled || preview !== null} onClick={() => choose('correct', row.id)}>Koreksi kode utama</button> :
        !row.retiredAt && <button type="button" className={common.danger} disabled={disabled || preview !== null} onClick={() => choose('retire', row.id)}>Hentikan kode {row.rawValue}</button>}</div></li>)}</ul>
      {!preview && <form className={common.form} onSubmit={event => {event.preventDefault(); review();}}><h3>{action === 'assign' ? 'Catat kode tambahan' : action === 'retire' ? 'Hentikan kode tambahan' : 'Koreksi kode utama'}</h3>
        {action !== 'assign' && <><p>{target?.rawValue}</p><p className={styles.notice}>{action === 'correct' ? 'Kode utama sebelumnya tetap disimpan sebagai alias. Identitas dan isi kemasan tetap sama.' : 'Riwayat dan reservasi identitas kode tetap tersimpan; penghentian tidak menghapusnya.'}</p>
          <button type="button" className={common.secondary} disabled={disabled} onClick={() => choose('assign')}>Batal tindakan</button></>}
        {action === 'assign' && <label>Jenis kode<select value={kind} disabled={disabled} onChange={event => {setKind(event.target.value as 'skuCode' | 'gtin'); setRaw('');}}><option value="skuCode">Alias SKU internal</option><option value="gtin">GTIN pada kemasan</option></select></label>}
        {action !== 'retire' && <label>{action === 'correct' ? 'Kode utama baru' : kind === 'gtin' ? 'GTIN dari kemasan' : 'Alias SKU'}<input value={raw} maxLength={128} autoComplete="off" disabled={disabled} onChange={event => setRaw(event.target.value)} required/></label>}
        <label>Referensi asal kode<input value={provenance} maxLength={500} autoComplete="off" disabled={disabled} onChange={event => setProvenance(event.target.value)} required/></label>
        <label>Alasan pencatatan kode<textarea value={reason} maxLength={1000} rows={3} disabled={disabled} onChange={event => setReason(event.target.value)} required/></label>
        <button className={common.primary} disabled={disabled}>Tinjau pencatatan kode</button></form>}
      {preview && <section className={common.review} aria-label="Konfirmasi pencatatan kode"><h3>Periksa kode dan kemasan</h3><p>{view.catalog.code} · {view.catalog.definition.packageLabel} · {view.catalog.definition.baseUnitsPerPackage} {view.catalog.definition.baseUnitCode}</p>
        <p>Tindakan: {action === 'assign' ? 'Tambahkan kode' : action === 'retire' ? 'Hentikan kode' : 'Koreksi kode utama'}</p>
        <p>Kode: {preview.operation === 'catalog.identifier.assign' ? preview.rawValue : preview.operation === 'catalog.identifier.correct-sku' ? preview.newCode : target?.rawValue}</p>
        {'canonicalValue' in preview && <p>Nilai pembanding: {preview.canonicalValue}</p>}{preview.operation === 'catalog.identifier.assign' && <p>Ruang kode: {preview.namespace}</p>}
        <p>Referensi asal: {preview.provenanceReference}</p><p>Alasan: {preview.reason}</p><div className={common.actions}>
          <button type="button" className={common.primary} disabled={disabled} onClick={() => void save()}>Simpan kode</button>
          <button type="button" className={common.secondary} disabled={busy} onClick={() => setPreview(null)}>Kembali ke isian</button></div></section>}
    </>}
  </section>;
}
