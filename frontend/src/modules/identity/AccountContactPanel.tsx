"use client";

import {useCallback, useEffect, useRef, useState} from 'react';
import Link from 'next/link';
import {RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {OperationJournal, type OperationReference} from '../administration/operation-journal';
import {createAccountContactGateway, createAccountContactWorkflow, prepareAccountContact,
  type AccountContactCurrent, type AccountContactGateway, type AccountContactHistory} from './account-contact-client';
import styles from './identity.module.css';
import { dateTimeFormatter } from '@/lib/format';

const displayTime = (value: string) => dateTimeFormatter.format(new Date(value));
const emptyFields = {name: '', phone: '', position: '', address: ''};
function message(error: unknown): string {
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.status === 401 || error.code === 'actor_changed') return 'Sesi Anda perlu diperbarui. Masuk kembali sebagai pemilik kontak sebelum melanjutkan.';
    if (error.status === 403) return 'Akses kontak pribadi belum tersedia untuk akun ini. Muat ulang untuk memeriksa akses terbaru.';
    if (error.code === 'invalid_account_contact') return 'Isi nama dan alasan perubahan. Nama dan jabatan paling banyak 160 karakter, telepon 40, alamat dan alasan 1.000. Hindari karakter kendali.';
    if (error.code === 'recovery_storage_unavailable') return 'Referensi pemulihan tidak dapat disimpan di perangkat. Perubahan belum dikirim; periksa penyimpanan peramban.';
    if (error.code === 'account_contact_response_invalid') return 'Jawaban server tidak sesuai. Muat ulang kontak dan periksa hasil permintaan sebelumnya sebelum membuat perubahan baru.';
    if (error.status === 409 && error.code !== 'resolve_previous_attempt') return 'Kontak atau sesi telah berubah. Muat ulang kontak sebelum menyimpan versi berikutnya.';
    if (error.code === 'resolve_previous_attempt') return 'Periksa hasil perubahan sebelumnya terlebih dahulu. Permintaan yang hasilnya belum pasti tidak dikirim ulang.';
  }
  return 'Kontak belum dapat diperiksa. Jika perubahan sudah dikirim, gunakan pemeriksaan hasil permintaan di bawah; jangan mengirim ulang otomatis.';
}

/** Keyed by the verified actor on the account page; unmount aborts every pending read and write. */
export function AccountContactPanel({actorId, gateway: providedGateway}: {actorId: string; gateway?: AccountContactGateway}) {
  const [gateway] = useState(() => providedGateway ?? createAccountContactGateway());
  const [journal] = useState(() => new OperationJournal(() => window.sessionStorage));
  const [workflow] = useState(() => createAccountContactWorkflow(gateway, journal));
  const [contact, setContact] = useState<AccountContactCurrent | null>(null);
  const [history, setHistory] = useState<AccountContactHistory | null>(null), [historyError, setHistoryError] = useState<unknown>(null);
  const [fields, setFields] = useState(emptyFields), [reason, setReason] = useState('');
  const [entries, setEntries] = useState<OperationReference[]>([]);
  const [busy, setBusy] = useState(true), [online, setOnline] = useState(true);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), inFlight = useRef(false), controller = useRef<AbortController | null>(null);
  const current = useCallback(() => alive.current && navigator.onLine, []);
  const refreshEntries = useCallback(() => {
    const saved = workflow.pending(actorId); setEntries(saved); return saved;
  }, [actorId, workflow]);
  const load = useCallback(async (signal: AbortSignal) => {
    refreshEntries();
    const results = await Promise.allSettled([gateway.current(actorId, current, signal), gateway.history(actorId, null, 10, current, signal)]);
    if (!current() || signal.aborted) return;
    const [latest, changes] = results;
    if (latest.status === 'fulfilled') {
      setContact(latest.value); setFields({name: latest.value.name ?? '', phone: latest.value.phone ?? '', position: latest.value.position ?? '', address: latest.value.address ?? ''}); setError(null);
    } else {setContact(null); setError(latest.reason);}
    if (changes.status === 'fulfilled') {setHistory(changes.value); setHistoryError(null);}
    else {setHistory(null); setHistoryError(changes.reason);}
  }, [actorId, current, gateway, refreshEntries]);
  useEffect(() => {
    alive.current = true; inFlight.current = true;
    const request = new AbortController(); controller.current = request;
    const connection = () => setOnline(navigator.onLine);
    void Promise.resolve().then(() => {
      if (!alive.current || request.signal.aborted) return;
      connection(); return load(request.signal);
    }).catch(failure => {if (alive.current && !request.signal.aborted) setError(failure);})
      .finally(() => {if (alive.current && !request.signal.aborted) {inFlight.current = false; setBusy(false);}});
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => {alive.current = false; request.abort(); controller.current?.abort(); window.removeEventListener('online', connection); window.removeEventListener('offline', connection);};
  }, [load]);
  async function perform(action: (signal: AbortSignal) => Promise<void>) {
    if (inFlight.current || !current()) return;
    inFlight.current = true; setBusy(true); setError(null); setNotice('');
    const request = new AbortController(); controller.current = request;
    try {await action(request.signal);}
    catch (failure) {
      if (alive.current && !request.signal.aborted) {
        setError(failure);
        if (failure instanceof AccessFailure && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) {setContact(null); setHistory(null);}
      }
    } finally {
      if (alive.current && !request.signal.aborted) {
        try {refreshEntries();} catch (failure) {setError(failure);}
        inFlight.current = false; setBusy(false);
      }
    }
  }
  const reload = () => void perform(signal => load(signal));
  const save = () => void perform(async signal => {
    if (!contact) throw new RequestNotSentError(409, 'account_contact_view_changed');
    const result = await workflow.save(prepareAccountContact(actorId, contact, fields, reason), current, signal);
    if (!current() || signal.aborted) return;
    setReason(''); await load(signal);
    if (current() && !signal.aborted) setNotice(`Kontak pribadi tersimpan. Versi perubahan ${result.revision}; kontak terbaru dan riwayat dimuat terpisah.`);
  });
  const recover = (entry: OperationReference) => void perform(async signal => {
    const result = await workflow.recover(actorId, entry, current, signal);
    if (!current() || signal.aborted) return;
    if (result === null) {setNotice('Hasil permintaan asli belum ditemukan. Referensi tetap disimpan; periksa lagi setelah layanan tersedia.'); return;}
    setReason(''); await load(signal);
    if (current() && !signal.aborted) setNotice(`Perubahan asli ditemukan pada versi ${result.revision}. Kontak terbaru dimuat terpisah dan mungkin memiliki perubahan berikutnya.`);
  });
  const loadOlder = () => void perform(async signal => {
    const before = history?.nextBeforeHistoryId;
    if (!before || !history) return;
    const previous = history, page = await gateway.history(actorId, before, 10, current, signal);
    if (!current() || signal.aborted) return;
    const last = previous.items.at(-1), first = page.items[0];
    if (last && first && BigInt(first.revision) >= BigInt(last.revision)) throw new AccessFailure(502, 'account_contact_response_invalid');
    setHistory({items: [...previous.items, ...page.items], nextBeforeHistoryId: page.nextBeforeHistoryId}); setHistoryError(null);
  });
  const disabled = busy || !online || entries.length > 0 || !contact || contact.revision === '256';
  return <section className={`${styles.card} ${styles.workspace}`} id="account-contact" aria-labelledby="account-contact-heading" aria-busy={busy}>
    <h2 id="account-contact-heading">Kontak pribadi</h2>
    <p>Catat nama kontak, telepon, jabatan, dan alamat Anda. Data ini diisi sendiri; nama dan email terverifikasi, keanggotaan, kata sandi, serta verifikasi dua langkah tetap dikelola melalui proses akun resmi.</p>
    {busy && <p role="status">Memeriksa kontak atau perubahan yang sedang diproses…</p>}
    {!online && <p className={styles.alert} role="status">Perangkat sedang luring. Sambungkan kembali sebelum memeriksa atau menyimpan kontak.</p>}
    {notice && <p className={styles.success} role="status">{notice}</p>}
    {error !== null && <div className={styles.alert} role="alert"><p>{message(error)}</p>
      {error instanceof AccessFailure && error.status === 401 && <Link href="/identity/login" className={styles.secondary}>Masuk kembali</Link>}</div>}
    <button type="button" className={styles.secondary} disabled={busy || !online} onClick={reload}>Muat ulang kontak dan riwayat</button>
    {entries.length > 0 && <section className={styles.support} aria-labelledby="account-contact-recovery-heading">
      <h3 id="account-contact-recovery-heading">Perubahan yang hasilnya belum pasti</h3>
      <p>Periksa permintaan yang sudah dikirim. Pemeriksaan ini memakai referensi aslinya dan tidak mengirim perubahan baru.</p>
      {entries.map(entry => <div key={entry.key}><p className={styles.help}>Dikirim {displayTime(entry.createdAt)}</p>
        <button type="button" className={styles.secondary} disabled={busy || !online} onClick={() => recover(entry)}>Periksa hasil perubahan kontak</button></div>)}
    </section>}
    {contact && <>
      {contact.revision === '0' ? <p className={styles.help}>Kontak pribadi belum pernah disimpan. Isi nama kontak dan alasan pencatatan pertama.</p>
        : <p className={styles.help}>Versi kontak saat ini: {contact.revision}. Diperbarui {displayTime(contact.updatedAt!)}.</p>}
      {contact.revision === '256' && <p className={styles.alert}>Batas perubahan kontak akun telah tercapai. Hubungi pengelola melalui proses bantuan resmi.</p>}
      <form className={styles.navigationForm} onSubmit={event => {event.preventDefault(); save();}}>
        <label htmlFor="account-contact-name">Nama kontak<input id="account-contact-name" autoComplete="name" required disabled={disabled} value={fields.name} onChange={event => setFields(value => ({...value, name: event.target.value}))} /></label>
        <label htmlFor="account-contact-phone">Nomor telepon (opsional)<input id="account-contact-phone" type="tel" autoComplete="tel" disabled={disabled} value={fields.phone} onChange={event => setFields(value => ({...value, phone: event.target.value}))} /></label>
        <label htmlFor="account-contact-position">Jabatan (opsional)<input id="account-contact-position" autoComplete="organization-title" disabled={disabled} value={fields.position} onChange={event => setFields(value => ({...value, position: event.target.value}))} /></label>
        <label htmlFor="account-contact-address">Alamat kontak (opsional)<textarea id="account-contact-address" autoComplete="street-address" rows={3} disabled={disabled} value={fields.address} onChange={event => setFields(value => ({...value, address: event.target.value}))} /></label>
        <label htmlFor="account-contact-reason">Alasan perubahan kontak<textarea id="account-contact-reason" rows={2} required disabled={disabled} value={reason} onChange={event => setReason(event.target.value)} /></label>
        <p className={styles.help}>Nama dan jabatan paling banyak 160 karakter, telepon 40, alamat dan alasan 1.000. Alamat dan alasan ditulis dalam satu paragraf tanpa baris baru.</p>
        <button type="submit" className={styles.primary} disabled={disabled}>Simpan kontak pribadi</button>
      </form>
    </>}
    <section className={styles.support} aria-labelledby="account-contact-history-heading">
      <h3 id="account-contact-history-heading">Riwayat kontak pribadi</h3>
      <p className={styles.help}>Setiap perubahan menyimpan rincian dan alasan pada saat itu. Riwayat tidak mengubah identitas yang diverifikasi layanan akun.</p>
      {historyError !== null && <p role="alert">Riwayat belum dapat dimuat. Gunakan muat ulang kontak dan riwayat untuk memeriksa kembali.</p>}
      {history && history.items.length === 0 && <p>Belum ada perubahan kontak pribadi.</p>}
      {history?.items.map(item => <article key={item.historyId} className={styles.support}>
        <h3>Perubahan versi {item.revision}</h3><p className={styles.help}>{displayTime(item.recordedAt)}</p>
        <dl className={styles.facts}><div><dt>Nama kontak</dt><dd>{item.contact.name}</dd></div><div><dt>Telepon</dt><dd>{item.contact.phone ?? 'Belum diisi'}</dd></div>
          <div><dt>Jabatan</dt><dd>{item.contact.position ?? 'Belum diisi'}</dd></div><div><dt>Alamat</dt><dd>{item.contact.address ?? 'Belum diisi'}</dd></div><div><dt>Alasan</dt><dd>{item.reason}</dd></div></dl>
      </article>)}
      {history?.nextBeforeHistoryId && <button type="button" className={styles.secondary} disabled={busy || !online} onClick={loadOlder}>Lihat riwayat lebih lama</button>}
    </section>
  </section>;
}
