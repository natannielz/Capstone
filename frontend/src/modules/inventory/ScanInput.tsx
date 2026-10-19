'use client';

import {useEffect, useRef, useState, useSyncExternalStore} from 'react';
import {AccessFailure} from '../administration/workflow';
import {createScanGateway, type ScanCandidate, type ScanCode, type ScanContext, type ScanResolution} from './scan-client';
import {CameraScanner} from './CameraScanner';
import {cameraScanSupported, codeKindFromScan} from './camera-scan';
import common from '../administration/access.module.css';

const messages: Record<string, string> = {invalid_code: 'Kode tidak dapat dibaca. Masukkan teks asli tanpa spasi tambahan.', invalid_gtin: 'Panjang atau angka pemeriksa GTIN tidak sesuai.',
  invalid_sku_code: 'Format kode SKU tidak sesuai.', gtin_mapping_unavailable: 'GTIN belum memiliki pemetaan yang disetujui. Pilih barang secara manual dan minta peninjauan pemetaan.',
  code_not_in_context: 'Kode tidak ditemukan pada pilihan yang diizinkan untuk pekerjaan ini.', source_mapping_not_in_document: 'Kode sumber ini belum memiliki pemetaan pada konteks pekerjaan ini.',
  multiple_document_targets: 'Kode menunjuk lebih dari satu pilihan. Pilih sumber atau baris dokumen secara manual sebelum mencatat jumlah.'};
const subscribeNothing = () => () => {};
export function ScanInput({actorId, context, disabled, current, onReset, onManual, onConfirm}: {actorId: string; context: ScanContext; disabled: boolean;
  current: () => boolean; onReset: () => void; onManual: () => void; onConfirm: (candidate: ScanCandidate) => Promise<void> | void}) {
  const [gateway] = useState(createScanGateway), [codeKind, setCodeKind] = useState<ScanCode['codeKind'] | ''>(''), [rawCode, setRawCode] = useState('');
  const [sourceNamespace, setSourceNamespace] = useState<ScanCode['sourceNamespace']>(null), [sourceAccount, setSourceAccount] = useState('');
  const [resolution, setResolution] = useState<ScanResolution | null>(null), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  const alive = useRef(false), active = useRef<AbortController | null>(null), sending = useRef(false);
  // Browser capability, read without a hydration mismatch (false while rendering on the server).
  const cameraAvailable = useSyncExternalStore(subscribeNothing, () => cameraScanSupported(window as never), () => false);
  const [cameraOpen, setCameraOpen] = useState(false);
  useEffect(() => {alive.current = true; return () => {alive.current = false; active.current?.abort();};}, []);
  function clear() {active.current?.abort(); setResolution(null); setError(''); onReset();}
  async function resolve(scanned?: {codeKind: 'gtin' | 'sku'; rawCode: string}) {
    const kind = scanned?.codeKind ?? codeKind, raw = scanned?.rawCode ?? rawCode;
    if (disabled || sending.current || !kind || !current() || !navigator.onLine) return;
    clear(); sending.current = true; setBusy(true); const controller = new AbortController(); active.current = controller;
    try {const value = await gateway.resolve(actorId, context, {codeKind: kind, rawCode: raw, sourceNamespace: kind === 'supplier' ? sourceNamespace : null,
      sourceAccount: kind === 'supplier' ? sourceAccount : null}, () => alive.current && active.current === controller && current() && navigator.onLine, controller.signal);
      if (alive.current && active.current === controller && current()) setResolution(value);
    } catch (failure) {if (alive.current && active.current === controller) setError(failure instanceof AccessFailure && [401, 403, 409].includes(failure.status)
      ? 'Sesi, penugasan atau dokumen berubah. Muat ulang pekerjaan sebelum memeriksa kode.' : 'Kode belum dapat diperiksa. Tidak ada jumlah yang ditambahkan.');}
    finally {sending.current = false; if (alive.current) setBusy(false);}
  }
  async function confirm() {
    if (disabled || sending.current || !current() || !navigator.onLine || resolution?.kind !== 'match') return;
    sending.current = true; setBusy(true); setError('');
    try {await onConfirm(resolution.candidates[0]); if (alive.current) setResolution(null);}
    catch {if (alive.current) {setResolution(null); onReset(); setError('Kemasan atau dokumen berubah. Periksa kembali pilihan terbaru.');}}
    finally {sending.current = false; if (alive.current) setBusy(false);}
  }
  return <section className={common.review} aria-label="Periksa kode barang" aria-busy={busy}>
    <h3>Periksa kode barang</h3><p>Pemindaian hanya memeriksa identitas. Jumlah, lot dan kedaluwarsa tetap diisi serta ditinjau secara terpisah.</p>
    <label>Jenis kode<select aria-label="Jenis kode" value={codeKind} disabled={disabled || busy} onChange={event => {clear(); setCodeKind(event.target.value as ScanCode['codeKind'] | '');}}>
      <option value="">Pilih jenis kode</option><option value="sku">SKU internal</option><option value="gtin">GTIN kemasan</option>{context.purpose === 'receiving' && <option value="supplier">Kode pemasok / sumber</option>}</select></label>
    {codeKind === 'supplier' && <><label>Sumber kode<select aria-label="Sumber kode" value={sourceNamespace ?? ''} disabled={disabled || busy} onChange={event => {clear(); setSourceNamespace(event.target.value as ScanCode['sourceNamespace']);}}>
      <option value="">Pilih sumber</option><option value="supplier">Pemasok</option><option value="omi">OMI</option><option value="s_mart">Smart</option></select></label>
      <label>Akun sumber<input aria-label="Akun sumber" autoComplete="off" maxLength={128} value={sourceAccount} disabled={disabled || busy} onChange={event => {clear(); setSourceAccount(event.target.value);}} /></label></>}
    <label>Kode hasil pindai<input aria-label="Kode hasil pindai" autoComplete="off" maxLength={256} value={rawCode} disabled={disabled || busy || !codeKind}
      onChange={event => {clear(); setRawCode(event.target.value);}} onKeyDown={event => {if (event.key === 'Enter') {event.preventDefault(); if (!event.repeat) void resolve();}}} /></label>
    <button type="button" className={common.secondary} disabled={disabled || busy || !codeKind || !rawCode || codeKind === 'supplier' && (!sourceNamespace || !sourceAccount)} onClick={() => void resolve()}>Periksa kode</button>
    {cameraAvailable && !cameraOpen && <button type="button" className={common.secondary} disabled={disabled || busy} onClick={() => {clear(); setCameraOpen(true);}}>Pindai dengan kamera</button>}
    {cameraOpen && <CameraScanner onClose={() => setCameraOpen(false)} onDetected={(raw, format) => {setCameraOpen(false); const kind = codeKindFromScan(raw, format);
      if (!kind) {setError('Kode dari kamera tidak dapat dipastikan. Coba lagi atau ketik kode secara manual.'); return;}
      setCodeKind(kind); setRawCode(raw.trim()); void resolve({codeKind: kind, rawCode: raw.trim()});}} />}
    <button type="button" className={common.secondary} disabled={disabled || busy} onClick={() => {active.current?.abort(); setResolution(null); setRawCode(''); setError(''); onManual();}}>Gunakan pilihan manual</button>
    {error && <p role="alert">{error}</p>}{resolution && (resolution.kind === 'match' ? <div role="status"><p>Kode cocok dengan satu pilihan. Periksa isi kemasan sebelum memilih.</p>
      <p><strong>{resolution.candidates[0].skuCode} · {resolution.candidates[0].packageLabel}</strong> · {resolution.candidates[0].baseUnitsPerPackage} {resolution.candidates[0].baseUnitCode} per kemasan</p>
      {resolution.candidates[0].currentPrimaryCode && <p>Kode utama saat ini: {resolution.candidates[0].currentPrimaryCode} · SKU tetap: {resolution.candidates[0].skuCode}</p>}
      <button type="button" className={common.primary} disabled={disabled || busy} onClick={() => void confirm()}>Konfirmasi kemasan hasil pindai</button></div> :
      <p role="alert">{messages[resolution.reason] ?? 'Kode belum menghasilkan satu pilihan yang dapat dikonfirmasi.'} Tidak ada jumlah yang ditambahkan; gunakan pilihan manual di pekerjaan ini.</p>)}
  </section>;
}
