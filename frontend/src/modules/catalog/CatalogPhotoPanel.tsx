'use client';

import {useCallback, useEffect, useRef, useState} from 'react';
import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {OperationJournal, OperationReference} from '../administration/operation-journal';
import type {CatalogSku} from './catalog-workflow';
import type {CatalogPhotoGateway} from './catalog-photo-client';
import {catalogPhotoOperation, createCatalogPhotoAssociationWorkflow, prepareCatalogPhotoAssociation,
  type CatalogPhotoCurrent, type CatalogPhotoResult, type CatalogPhotoTarget} from './catalog-photo-workflow';
import {catalogPhotoEvidenceLabel, catalogPhotoEvidenceReady, catalogPhotoReservationKey, catalogPhotoReservationOwner,
  catalogPhotoReserveOperation, validateCatalogPhotoFile, type CatalogPhotoEvidenceGateway,
  type CatalogPhotoEvidenceStatus, type CatalogPhotoReservation} from './catalog-photo-evidence-client';
import common from '../administration/access.module.css';
import styles from './catalog-photo.module.css';

export type CatalogPhotoPanelProps = {
  actorId: string; sku: CatalogSku; journal: OperationJournal; gateway: CatalogPhotoGateway; evidenceGateway: CatalogPhotoEvidenceGateway;
  locked: boolean; onClose: () => void; onChanged: (result: CatalogPhotoResult) => void; onFailure: (error: unknown) => void;
};
function message(error: unknown): string {
  if (error instanceof RequestNotSentError && error.code === 'catalog_photo_image_required') return 'Pilih satu foto PNG atau JPEG, lebih dari 0 byte dan paling besar 10 MiB.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.status === 401 || error.code === 'actor_changed') return 'Sesi berubah. Masuk dengan akun pemilik permintaan untuk memeriksa hasil.';
    if (error.status === 403) return 'Akses foto tidak tersedia untuk akun atau cakupan ini. Periksa akses terbaru.';
    if (error.code === 'invalid_catalog_photo_request') return 'Periksa foto yang dipilih dan isi alasan tanpa spasi di awal atau akhir.';
    if (error.code === 'recovery_storage_unavailable') return 'Referensi pemulihan belum dapat disimpan di perangkat. Foto belum dikirim.';
    if (error.code === 'catalog_photo_evidence_invalid' || error.code === 'catalog_photo_response_invalid') return 'Bukti foto dari server tidak sesuai. Data tidak digunakan; muat ulang dan periksa hasil permintaan sebelumnya.';
    if (error.status === 409 && error.code !== 'resolve_previous_attempt') return 'Varian, foto, atau sesi telah berubah. Muat ulang sumber sebelum melanjutkan.';
  }
  return 'Hasil belum dapat dipastikan. Gunakan pemeriksaan hasil di bawah; jangan membuat pengganti atau mengirim ulang secara otomatis.';
}
const evidenceIdentity = (status: CatalogPhotoEvidenceStatus) => status.reference ? `${status.reservationId}:${status.reference.id}:${status.reference.version}:${status.reference.sha256}` : '';

/** A target/session lock unmounts the private editor, aborting requests and revoking every image URL. */
export function CatalogPhotoPanel(props: CatalogPhotoPanelProps) {
  if (props.locked) return <section className={styles.panel} aria-busy="true"><h3>Foto utama varian</h3><p>Foto disembunyikan sementara sesi atau tindakan lain diperiksa.</p></section>;
  return <PhotoEditor key={`${props.actorId}:${props.sku.id}:${props.sku.version}:${props.sku.definition.id}:${props.sku.definition.version}`} {...props}/>;
}

function PhotoEditor({actorId, sku, journal, gateway, evidenceGateway, onClose, onChanged, onFailure}: CatalogPhotoPanelProps) {
  const [source] = useState<CatalogPhotoTarget>(() => ({skuId: sku.id, skuRevision: sku.version, definitionId: sku.definition.id, definitionVersion: sku.definition.version}));
  const [services] = useState(() => ({journal, gateway, evidenceGateway, onChanged, onFailure}));
  const [workflow] = useState(() => createCatalogPhotoAssociationWorkflow(gateway, journal));
  const [metadata, setMetadata] = useState<CatalogPhotoCurrent | null>(null), [entries, setEntries] = useState<OperationReference[]>([]);
  const [reservation, setReservation] = useState<CatalogPhotoReservation | null>(null), [status, setStatus] = useState<CatalogPhotoEvidenceStatus | null>(null);
  const [file, setFile] = useState<File | null>(null), [reason, setReason] = useState(''), [consent, setConsent] = useState(false);
  const [preview, setPreview] = useState<{url: string; kind: 'current' | 'upload'; identity: string; loaded: boolean} | null>(null);
  const [busy, setBusy] = useState(true), [offline, setOffline] = useState(false), [blocked, setBlocked] = useState(false);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), inFlight = useRef(false), controller = useRef<AbortController | null>(null), imageUrl = useRef<string | null>(null);
  const selectedReservationEntry = useRef<OperationReference | null>(null), fileInput = useRef<HTMLInputElement>(null), previewHeading = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && navigator.onLine, []);
  const closePreview = useCallback(() => {
    if (imageUrl.current) URL.revokeObjectURL(imageUrl.current);
    imageUrl.current = null; setPreview(null); setConsent(false);
  }, []);
  const refreshEntries = useCallback(() => {
    const loaded = services.journal.list(actorId).filter(entry => entry.operation === catalogPhotoOperation ||
      entry.operation === catalogPhotoReserveOperation && catalogPhotoReservationOwner(entry.key) === source.skuId);
    setEntries(loaded); return loaded;
  }, [actorId, services, source]);

  useEffect(() => {
    alive.current = true;
    const abort = new AbortController(); controller.current = abort;
    void (async () => {
      try {
        refreshEntries();
        if (!sku.active) { setBlocked(true); setNotice('Varian sudah dihentikan. Pemeriksaan hasil permintaan lama tetap tersedia; foto baru tidak dapat ditetapkan.'); return; }
        const shown = await services.gateway.current(actorId, source, current, abort.signal);
        if (current()) setMetadata(shown);
      } catch (failure) { if (alive.current && !abort.signal.aborted) { setError(failure); services.onFailure(failure); } }
      finally { if (alive.current && !abort.signal.aborted) setBusy(false); }
    })();
    const disconnected = () => {
      controller.current?.abort(); closePreview(); setMetadata(null); setStatus(null); setFile(null); setConsent(false); setOffline(true); setBusy(false);
      if (fileInput.current) fileInput.current.value = '';
    };
    const connected = () => { setOffline(false); setNotice('Koneksi kembali. Muat ulang sumber foto sebelum melanjutkan.'); };
    window.addEventListener('offline', disconnected); window.addEventListener('online', connected);
    if (!navigator.onLine) disconnected();
    return () => {
      alive.current = false; controller.current?.abort();
      if (imageUrl.current) URL.revokeObjectURL(imageUrl.current);
      imageUrl.current = null;
      window.removeEventListener('offline', disconnected); window.removeEventListener('online', connected);
    };
  }, [actorId, closePreview, current, refreshEntries, services, source, sku.active]);

  async function run(action: (signal: AbortSignal) => Promise<void>) {
    if (inFlight.current || !current()) return;
    inFlight.current = true; setBusy(true); setError(null); setNotice('');
    const abort = new AbortController(); controller.current = abort;
    try { await action(abort.signal); }
    catch (failure) {
      if (alive.current) {
        closePreview(); setError(failure);
        if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.status === 409 || failure.code.endsWith('_invalid'))) {
          setMetadata(null); setStatus(null); setBlocked(true);
        }
        services.onFailure(failure);
      }
    } finally {
      inFlight.current = false;
      if (alive.current) { setBusy(false); try { refreshEntries(); } catch (failure) { setError(failure); services.onFailure(failure); } }
    }
  }
  async function load(signal: AbortSignal) {
    closePreview(); setMetadata(null); setStatus(null);
    if (!sku.active) { refreshEntries(); return; }
    const shown = await services.gateway.current(actorId, source, current, signal);
    if (current()) { setMetadata(shown); setBlocked(false); refreshEntries(); }
  }
  async function loadStatus(saved: CatalogPhotoReservation, signal: AbortSignal) {
    const shown = await services.evidenceGateway.status(actorId, source.skuId, saved.reservationId, current, signal);
    if (current()) setStatus(shown);
    return shown;
  }
  function chooseFile(value: File | undefined) {
    closePreview(); setError(null); setFile(null);
    if (!value) return;
    try { validateCatalogPhotoFile(value); setFile(value); }
    catch (failure) { if (fileInput.current) fileInput.current.value = ''; setError(failure); }
  }
  async function upload(signal: AbortSignal) {
    if (!file || !metadata || blocked) return;
    validateCatalogPhotoFile(file); closePreview();
    let saved = reservation;
    if (!saved) {
      if (entries.length) throw new AccessFailure(409, 'resolve_previous_attempt');
      const key = catalogPhotoReservationKey(source.skuId);
      const entry = services.journal.remember(actorId, catalogPhotoReserveOperation, key);
      selectedReservationEntry.current = entry; refreshEntries();
      try { saved = await services.evidenceGateway.reserve(actorId, source, key, reason, current, signal); }
      catch (failure) {
        if (failure instanceof RequestNotSentError || failure instanceof AccessFailure && failure.status >= 400 && failure.status < 500) {
          services.journal.confirmed(entry); selectedReservationEntry.current = null;
        }
        throw failure;
      }
      if (!current()) return;
      setReservation(saved);
    } else if (!status || status.state !== 'fresh') throw new AccessFailure(409, 'resolve_previous_attempt');
    try {
      await services.evidenceGateway.upload(actorId, source.skuId, saved, file, current, signal);
      await loadStatus(saved, signal);
      if (current()) setNotice('Unggah selesai. Periksa status keamanan dan format sebelum meninjau foto asli.');
    } finally { if (alive.current) { setFile(null); if (fileInput.current) fileInput.current.value = ''; } }
  }
  async function recoverReservation(entry: OperationReference, signal: AbortSignal) {
    closePreview();
    const saved = await services.evidenceGateway.recoverReservation(actorId, entry.key, current, signal);
    if (!saved) { setNotice('Tempat unggah belum terkonfirmasi. Referensi tetap disimpan; periksa lagi tanpa membuat permintaan pengganti.'); return; }
    if (!current()) return;
    selectedReservationEntry.current = entry; setReservation(saved);
    const shown = await loadStatus(saved, signal);
    if (current() && metadata?.photo?.reservationId === saved.reservationId && shown.reference &&
        metadata.photo.reference.id === shown.reference.id && metadata.photo.reference.version === shown.reference.version && metadata.photo.reference.sha256 === shown.reference.sha256) {
      services.journal.confirmed(entry); selectedReservationEntry.current = null; setReservation(null); setStatus(null);
      setNotice('Unggah ini sudah tercatat sebagai foto utama. Referensi pemulihan telah diselesaikan.');
    }
  }
  async function showOriginal(kind: 'current' | 'upload', signal: AbortSignal) {
    closePreview();
    if (!metadata || kind === 'upload' && (!status || !catalogPhotoEvidenceReady(status))) return;
    const blob = kind === 'current' ? await services.evidenceGateway.currentOriginal(actorId, metadata, current, signal) :
      await services.evidenceGateway.original(actorId, source, status!, current, signal);
    if (!current() || signal.aborted) return;
    if (blob === null) { setMetadata(null); setNotice('Belum ada foto yang berlaku saat ini. Muat ulang sumber foto untuk memeriksa perubahan.'); return; }
    const url = URL.createObjectURL(blob); imageUrl.current = url;
    setPreview({url, kind, identity: kind === 'upload' ? evidenceIdentity(status!) : metadata.photo!.associationId, loaded: false});
    requestAnimationFrame(() => { if (current()) previewHeading.current?.focus(); });
  }
  async function associate(signal: AbortSignal) {
    if (!metadata || !status?.reference || !catalogPhotoEvidenceReady(status) || !consent || preview?.kind !== 'upload' || !preview.loaded || preview.identity !== evidenceIdentity(status)) return;
    const frozen = prepareCatalogPhotoAssociation(actorId, source, metadata, status.reservationId, status.reference, reason);
    const result = await workflow.associate(frozen, current, signal);
    if (!current()) return;
    if (selectedReservationEntry.current) { try { services.journal.confirmed(selectedReservationEntry.current); } catch { /* Original-key lookup remains available. */ } }
    closePreview(); setReservation(null); setStatus(null); setNotice('Foto utama tersimpan untuk definisi kemasan ini.');
    services.onChanged(result);
  }
  async function recoverAssociation(entry: OperationReference, signal: AbortSignal) {
    const result = await workflow.recover(actorId, entry, current, signal);
    if (!current()) return;
    if (!result) { setNotice('Penyimpanan foto utama belum terkonfirmasi. Tetap gunakan pemeriksaan hasil; jangan kirim ulang.'); return; }
    closePreview();
    if (result.skuId === source.skuId && sku.active) {
      const shown = await services.gateway.current(actorId, source, current, signal);
      if (!current()) return;
      setMetadata(shown);
      if (shown.photo?.associationId === result.associationId && reservation?.reservationId === shown.photo.reservationId && selectedReservationEntry.current)
        services.journal.confirmed(selectedReservationEntry.current);
    }
    services.onChanged(result); setNotice('Hasil penyimpanan foto utama telah terkonfirmasi tanpa mengirim ulang.');
  }

  const reservePending = entries.filter(entry => entry.operation === catalogPhotoReserveOperation);
  const associationPending = entries.filter(entry => entry.operation === catalogPhotoOperation);
  const disabled = busy || offline || blocked || !metadata || associationPending.length > 0;
  const ready = status !== null && catalogPhotoEvidenceReady(status);
  const reviewed = ready && preview?.kind === 'upload' && preview.loaded && preview.identity === evidenceIdentity(status!);
  return <section className={styles.panel} aria-labelledby="catalog-photo-heading" aria-busy={busy}>
    <div className={styles.heading}><div><p className={styles.eyebrow}>Foto katalog · {sku.code}</p><h3 id="catalog-photo-heading">Foto utama varian</h3></div>
      <button type="button" className={common.secondary} disabled={busy} onClick={onClose}>Tutup pengelolaan foto</button></div>
    <p className={styles.package}>{sku.definition.packageLabel} · {sku.definition.baseUnitsPerPackage} {sku.definition.baseUnitCode} per kemasan</p>
    <p className={styles.help}>Foto berlaku untuk definisi kemasan yang sedang dibuka. Unggah, pemeriksaan keamanan, dan penetapan foto utama adalah langkah terpisah.</p>
    {offline && <p role="alert" className={styles.warning}>Koneksi terputus. Foto pribadi disembunyikan; referensi pemulihan tetap disimpan.</p>}
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {notice && <p role="status" className={common.success}>{notice}</p>}
    <div className={styles.current}><h4>Foto yang tercatat</h4>
      {!metadata ? <p>{busy ? 'Memuat sumber foto…' : 'Sumber foto belum tersedia.'}</p> : metadata.photo ? <>
        <p>{metadata.photo.matchesCurrentDefinition ? 'Foto utama sudah tercatat untuk kemasan ini.' : 'Foto sebelumnya berasal dari definisi kemasan lain. Tinjau dan tetapkan foto baru untuk kemasan ini.'}</p>
        <p className={styles.help}>Revisi foto {metadata.photoRevision} · tersimpan {new Date(metadata.photo.recordedAt).toLocaleString('id-ID')}</p>
        {metadata.photo.matchesCurrentDefinition && <button type="button" className={common.secondary} disabled={disabled} onClick={() => void run(signal => showOriginal('current', signal))}>Lihat foto yang tercatat</button>}
      </> : <p>Belum ada foto utama. Tidak ada gambar pengganti yang diasumsikan.</p>}
      <button type="button" className={common.secondary} disabled={busy || offline || !sku.active} onClick={() => void run(load)}>Muat ulang sumber foto</button>
    </div>
    {entries.length > 0 && <section className={styles.recovery} aria-label="Pemulihan foto katalog"><h4>Referensi unggah dan penyimpanan</h4>
      <p>Periksa dengan akun pemilik permintaan. Pemeriksaan hasil tidak mengirim ulang foto atau penetapan.</p>
      {reservePending.map(entry => <button type="button" key={entry.key} className={common.secondary} disabled={busy || offline} onClick={() => void run(signal => recoverReservation(entry, signal))}>Periksa tempat unggah foto</button>)}
      {associationPending.map(entry => <button type="button" key={entry.key} className={common.secondary} disabled={busy || offline} onClick={() => void run(signal => recoverAssociation(entry, signal))}>Periksa hasil foto utama</button>)}
    </section>}
    <div className={styles.fields}>
      <label className={common.field}><span>Foto asli PNG atau JPEG</span><input ref={fileInput} type="file" accept="image/png,image/jpeg" disabled={disabled || reservation !== null && status?.state !== 'fresh'} onChange={event => chooseFile(event.target.files?.[0])}/><span className={styles.help}>Satu foto, maksimal 10 MiB. File tetap di perangkat sampai Anda memilih unggah.</span></label>
      <label className={common.field}><span>Alasan penetapan foto utama</span><textarea maxLength={1000} rows={3} value={reason} disabled={disabled} onChange={event => { setReason(event.target.value); setConsent(false); }}/></label>
    </div>
    {file && <p className={styles.help}>{file.name} · {(file.size / (1024 * 1024)).toLocaleString('id-ID', {maximumFractionDigits: 2})} MiB · belum menjadi foto utama</p>}
    <button type="button" className={common.primary} disabled={disabled || !file || !reason || !reservation && reservePending.length > 0 || reservation !== null && status?.state !== 'fresh'} onClick={() => void run(upload)}>{reservation ? 'Lanjutkan unggah terpilih' : 'Unggah foto asli'}</button>
    {reservation && <section className={styles.status} aria-label="Status pemeriksaan foto"><h4>Status unggah dan pemeriksaan</h4>
      <p role="status">{status ? catalogPhotoEvidenceLabel(status) : 'Hasil unggah belum dapat dipastikan.'}</p>
      <div className={styles.actions}><button type="button" className={common.secondary} disabled={busy || offline} onClick={() => void run(async signal => { closePreview(); await loadStatus(reservation, signal); })}>Periksa status foto</button>
        {(!status || ['writing', 'publish_pending', 'interrupted'].includes(status.state)) && <button type="button" className={common.secondary} disabled={busy || offline} onClick={() => void run(async signal => { closePreview(); await services.evidenceGateway.recoverUpload(actorId, source.skuId, reservation.reservationId, current, signal); await loadStatus(reservation, signal); })}>Periksa hasil unggah</button>}
        {ready && <button type="button" className={common.secondary} disabled={disabled} onClick={() => void run(signal => showOriginal('upload', signal))}>Tinjau foto asli tervalidasi</button>}
        {status?.state === 'quarantined' && <button type="button" className={common.secondary} disabled={disabled} onClick={() => void run(async () => {
          if (selectedReservationEntry.current) services.journal.confirmed(selectedReservationEntry.current);
          selectedReservationEntry.current = null; closePreview(); setReservation(null); setStatus(null); setFile(null);
          if (fileInput.current) fileInput.current.value = '';
          setNotice('Pilih file pengganti. Foto yang sebelumnya diunggah tidak ditetapkan oleh tindakan ini.');
        })}>Gunakan foto lain</button>}
      </div></section>}
    {preview && <section className={styles.preview} aria-labelledby="catalog-photo-preview-heading"><h4 id="catalog-photo-preview-heading" ref={previewHeading} tabIndex={-1}>{preview.kind === 'upload' ? 'Tinjau foto untuk kemasan ini' : 'Foto yang tercatat'}</h4>
      {/* Private verified blob: no public URL or remote image optimizer. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img src={preview.url} alt={`Foto asli ${sku.code}, ${sku.definition.packageLabel}`} onLoad={() => setPreview(value => value?.url === preview.url ? {...value, loaded: true} : value)} onError={() => { closePreview(); setError(new AccessFailure(502, 'catalog_photo_evidence_invalid')); }}/>
      <button type="button" className={common.secondary} onClick={closePreview}>Tutup pratinjau foto</button>
      {reviewed && <div className={styles.review}><p>Pastikan foto memperlihatkan varian <strong>{sku.code}</strong> dan kemasan <strong>{sku.definition.packageLabel}</strong>. Penetapan tidak mengubah produk, stok, atau harga.</p>
        <label className={styles.consent}><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)}/><span>Saya sudah meninjau foto asli dan memastikan foto sesuai dengan varian serta kemasan ini.</span></label>
        <button type="button" className={common.primary} disabled={disabled || !consent || !reason} onClick={() => void run(associate)}>Simpan foto utama</button></div>}
    </section>}
  </section>;
}
