'use client';

import {useEffect, useRef, useState} from 'react';
import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {sessionTiming, type IdentitySession} from '../identity/identity-workflow';
import {createAnomalyGateway, parseAnomalyPolicyAttempt, retainAnomalyPolicyReference, type AnomalyChoice, type AnomalyEvaluation, type AnomalyPolicyAttempt} from './anomaly-client';
import {AnomalyJournal, type AnomalyReference} from './anomaly-journal';
import styles from './anomaly.module.css';

type Props = {actorId: string; session: IdentitySession; observedAt: number; source: AnomalyChoice; evaluation: AnomalyEvaluation | null;
  disabled: boolean; journal: AnomalyJournal; reference: AnomalyReference | null; onReferences: () => void;
  onBusy: (value: boolean) => void; onSaved: () => Promise<void>};
export function AnomalyPolicyPanel({actorId, session, observedAt, source, evaluation, disabled, journal, reference, onReferences, onBusy, onSaved}: Props) {
  const [gateway] = useState(createAnomalyGateway), alive = useRef(true);
  const [fields, setFields] = useState({version: '', approvalReference: '', maximumPackages: '', maximumUnitPrice: '', currency: '', maximumRefund: '', reason: ''});
  const [draft, setDraft] = useState<{input: AnomalyPolicyAttempt; key: string} | null>(null);
  const [notice, setNotice] = useState(reference ? 'Ada permintaan kebijakan yang perlu diperiksa dengan referensi aslinya.' : ''), [reenter, setReenter] = useState(false);
  const priorReference = useRef(reference?.key ?? null);
  const context = () => ({actorId, isCurrent: () => alive.current && !sessionTiming(session, performance.now() - observedAt).expired});
  useEffect(() => {
    alive.current = true;
    return () => {alive.current = false;};
  }, []);
  useEffect(() => {
    const previous = priorReference.current; priorReference.current = reference?.key ?? null;
    if (previous !== null && reference === null || reference !== null && draft !== null && draft.key !== reference.key)
      void Promise.resolve().then(() => {if (alive.current) {setDraft(null); setReenter(false);}});
  }, [reference, draft]);
  const clear = (original: AnomalyReference) => {journal.confirmed(original); onReferences(); setDraft(null); setReenter(false);};
  function preview() {
    try {
      const input = parseAnomalyPolicyAttempt({sourceKind: source.kind, sourceId: source.id,
        expectedRevision: reference?.expectedRevision ?? evaluation?.policyRevision,
        policy: {version: fields.version.trim(), approvalReference: fields.approvalReference.trim(), maximumPackages: fields.maximumPackages.trim() || null,
          maximumUnitPrice: fields.maximumUnitPrice.trim() || null, currency: fields.currency.trim() || null, maximumRefund: fields.maximumRefund.trim() || null}, reason: fields.reason.trim()});
      setDraft({input, key: reference?.key ?? crypto.randomUUID()}); setNotice('Periksa kembali sumber, versi, satuan, dan alasan sebelum menyimpan.');
    } catch {setNotice('Isi versi, referensi kebijakan, dan alasan. Batas harus positif; nilai uang memerlukan kode mata uang. Kolom kosong berarti belum ada batas untuk aturan tersebut.');}
  }
  async function save() {
    if (!draft || disabled || reference !== null && draft.key !== reference.key) return; onBusy(true);
    const original: AnomalyReference = reference ?? {actorId, operation: 'configure', sourceKind: source.kind, sourceId: source.id,
      evaluationId: null, expectedRevision: draft.input.expectedRevision, key: draft.key, createdAt: new Date().toISOString()};
    try {
      try {journal.remember(original); onReferences();}
      catch {throw new RequestNotSentError(503, 'policy_recovery_storage_unavailable');}
      const version = await gateway.configure(draft.input, draft.key, context());
      if (context().isCurrent()) {clear(original); setNotice(`Versi ${version} telah tersimpan. Buat pemeriksaan baru untuk membaca kebijakan saat ini.`); await onSaved();}
    } catch (failure) {
      if (context().isCurrent()) {
        if (failure instanceof AccessFailure && failure.code === 'idempotency_key_reused') {
          setDraft(null); setReenter(false); setNotice('Isi berbeda dari permintaan asli. Periksa catatan dengan referensi yang tersimpan sebelum melanjutkan.');
        } else if (!retainAnomalyPolicyReference(failure, reference !== null)) {
          clear(original); setNotice('Permintaan ditolak tanpa perubahan kebijakan. Periksa sesi, izin, dan revisi terbaru sebelum menyusun ulang.'); await onSaved();
        } else if (failure instanceof AccessFailure && [400, 401, 403, 409].includes(failure.status)) {
          setDraft(null); setReenter(false); setNotice('Percobaan ulang ditolak. Hasil permintaan asli belum dapat dipastikan; referensi pemulihannya tetap tersimpan.');
        } else setNotice(failure instanceof RequestNotSentError ? 'Permintaan belum dikirim. Periksa sesi dan penyimpanan peramban; gunakan permintaan yang sama saat mencoba kembali.' :
          'Hasil penyimpanan belum dapat dipastikan. Periksa catatan dengan referensi asli sebelum membuat permintaan lain.');
      }
    } finally {if (alive.current) onBusy(false);}
  }
  async function recover() {
    if (!reference || disabled) return; onBusy(true);
    try {
      const result = await gateway.recoverPolicy(source, reference.key, context());
      if (!context().isCurrent()) return;
      if (result.status === 'confirmed') {clear(reference); setNotice(`Versi ${result.policyVersion} terkonfirmasi tersimpan dari permintaan asli.`); await onSaved();}
      else {setReenter(true); setDraft(null); setNotice('Permintaan belum terkonfirmasi. Periksa kembali, atau masukkan ulang isi permintaan semula; referensi aslinya tetap digunakan.');}
    } catch {if (context().isCurrent()) setNotice('Catatan belum dapat diperiksa. Periksa kembali sesi dan izin konfigurasi.');}
    finally {if (alive.current) onBusy(false);}
  }
  const editable = !disabled && (!reference || reenter) && (evaluation?.current || reference !== null);
  return <section className={styles.notice} aria-label="Kebijakan tinjauan">
    <h2>Kebijakan tinjauan</h2><p>Isi hanya batas yang telah ditetapkan penanggung jawab untuk toko sumber ini. Kolom kosong tetap dinyatakan belum tersedia.</p>
    {notice && <p role="status">{notice}</p>}
    {reference && <button type="button" disabled={disabled} onClick={() => void recover()}>Periksa penyimpanan kebijakan</button>}
    {!evaluation?.current && !reference && <p>Buat pemeriksaan baru untuk mendapatkan revisi kebijakan saat ini.</p>}
    <div className={styles.controls}>
      {([['version', 'Versi kebijakan'], ['approvalReference', 'Referensi penetapan kebijakan'], ['maximumPackages', 'Batas jumlah paket'],
        ['maximumUnitPrice', 'Batas harga per paket'], ['currency', 'Kode mata uang'], ['maximumRefund', 'Batas pengembalian dana'], ['reason', 'Alasan perubahan kebijakan']] as const)
        .map(([key, label]) => <label key={key}>{label}<input value={fields[key]} disabled={!editable || draft !== null}
          maxLength={key === 'reason' ? 1000 : key === 'approvalReference' ? 256 : 120} onChange={event => setFields(current => ({...current, [key]: event.target.value}))} /></label>)}
      <button type="button" disabled={!editable || draft !== null} onClick={preview}>Tinjau kebijakan</button>
    </div>
    {draft && <div><h3>Konfirmasi kebijakan {draft.input.policy.version}</h3><p>{source.label} · Revisi yang ditinjau {draft.input.expectedRevision}</p>
      <dl><dt>Jumlah paket maksimum</dt><dd>{draft.input.policy.maximumPackages ?? 'Belum ditetapkan'}</dd>
        <dt>Harga per paket maksimum</dt><dd>{draft.input.policy.maximumUnitPrice ?? 'Belum ditetapkan'} {draft.input.policy.currency}</dd>
        <dt>Pengembalian dana maksimum</dt><dd>{draft.input.policy.maximumRefund ?? 'Belum ditetapkan'} {draft.input.policy.currency}</dd>
        <dt>Referensi</dt><dd>{draft.input.policy.approvalReference}</dd><dt>Alasan</dt><dd>{draft.input.reason}</dd></dl>
      <button type="button" disabled={disabled || reference !== null && draft.key !== reference.key} onClick={() => void save()}>Simpan kebijakan tinjauan</button>
      {!reference && <button type="button" disabled={disabled} onClick={() => setDraft(null)}>Ubah isian kebijakan</button>}
    </div>}
  </section>;
}
