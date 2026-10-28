'use client';

import {useEffect, useRef, useState} from 'react';
import Link from 'next/link';
import {AccessFailure} from '../administration/workflow';
import {RequestNotSentError} from '../../api/client';
import {createPaymentHandoverGateway, paymentHandoverMessage, preparePaymentHandover, type PaymentHandoverCommand, type PaymentHandoverPage} from './payment-handover-client';
import {PaymentHandoverJournal, paymentHandoverPrefix, parsePaymentHandoverReference, type PaymentHandoverReference} from './payment-handover-journal';
import type {PaymentDraft} from './payment-client';
import styles from '../administration/access.module.css';
import { Button } from '@/components/workspace-kit/button';

const states = {active: 'Delegasi masih dalam masa berlaku', expired: 'Delegasi telah berakhir; penanggung jawab perlu diperiksa',
  revoked: 'Delegasi telah dicabut; penanggung jawab perlu diperiksa', not_yet_effective: 'Delegasi belum berlaku'};
export function PaymentHandoverPanel({actorId, owner, allowed, fresh, locked, current, onLock, onSettled}: {
  actorId: string; owner: PaymentDraft | null; allowed: boolean; fresh: boolean; locked: boolean; current: () => boolean;
  onLock: (value: boolean) => void; onSettled: () => void;
}) {
  const [gateway] = useState(createPaymentHandoverGateway), [journal] = useState(() => new PaymentHandoverJournal(() => window.localStorage));
  const [pending, setPending] = useState<PaymentHandoverReference[]>([]), [storageFailed, setStorageFailed] = useState(false);
  const [page, setPage] = useState<PaymentHandoverPage | null>(null), [selected, setSelected] = useState(''), [reason, setReason] = useState('');
  const [review, setReview] = useState<PaymentHandoverCommand | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), working = useRef(false), epoch = useRef(0), heading = useRef<HTMLHeadingElement>(null);
  function clear() { epoch.current++; setPage(null); setReview(null); setSelected(''); setReason(''); }
  function sync() { try {setPending(journal.list(actorId)); setStorageFailed(false);} catch (failure) {setStorageFailed(true); setError(failure);} }
  useEffect(() => {
    alive.current = true; const lifecycle = epoch;
    const read = () => { try {setPending(journal.list(actorId)); setStorageFailed(false);} catch (failure) {setStorageFailed(true); setError(failure);} };
    const changed = (event: StorageEvent) => {if (event.key === null || event.key.startsWith(paymentHandoverPrefix(actorId))) {
      epoch.current++; setPage(null); setReview(null); setSelected(''); setReason(''); read(); setNotice('Status serah-terima berubah di tab lain. Muat sumber terbaru sebelum melanjutkan.'); onSettled();
    }};
    queueMicrotask(() => {if (alive.current) read();}); window.addEventListener('storage', changed);
    return () => {alive.current = false; lifecycle.current++; window.removeEventListener('storage', changed);};
  }, [actorId, journal, onSettled]);
  useEffect(() => {
    const version = ++epoch.current; let cancelled = false;
    queueMicrotask(() => {if (!cancelled && alive.current && version === epoch.current) {setPage(null); setReview(null); setSelected(''); setReason('');}});
    return () => {cancelled = true;};
  }, [owner?.id, owner?.revision]);
  useEffect(() => {onLock(busy || pending.length > 0 || storageFailed); return () => onLock(false);}, [busy, pending.length, storageFailed, onLock]);
  useEffect(() => {if (review) heading.current?.focus();}, [review]);
  const active = () => alive.current && current(), disabled = locked || busy || storageFailed;
  async function run(task: () => Promise<void>) {
    if (!active() || locked || working.current) return; working.current = true; setBusy(true); setError(null); setNotice('');
    try {await task();} catch (failure) {if (alive.current) setError(failure);}
    finally {working.current = false; if (alive.current) {setBusy(false); sync();}}
  }
  async function load(beforeId: string | null = null) {
    if (!owner) return; const version = ++epoch.current; setReview(null); setSelected(''); setReason('');
    const value = await gateway.read(owner, {actorId, isCurrent: () => active() && epoch.current === version}, beforeId);
    if (active() && epoch.current === version) setPage(value);
  }
  async function claim(command: PaymentHandoverCommand) {
    if (journal.list(actorId).length) throw new AccessFailure(409, 'resolve_previous_attempt');
    const ref = parsePaymentHandoverReference({actorId, declarationId: command.declarationId, submissionId: command.submissionId,
      reviewRevision: command.reviewRevision, ...command.handover, key: 'ph.' + command.declarationId + '.' + crypto.randomUUID(), createdAt: new Date().toISOString()}, actorId);
    journal.remember(ref); sync();
    try {
      await gateway.claim(command, ref, {actorId, isCurrent: active}); journal.confirmed(ref); clear();
      setNotice('Serah-terima tersimpan. Anda menjadi penanggung jawab pemeriksaan ini.'); onSettled();
    } catch (failure) {
      // Only an explicitly unsent request may discard its new reference. 403/409/unknown responses
      // and all recovery refusals retain the exact key, including after a browser restart.
      if (failure instanceof RequestNotSentError) journal.confirmed(ref); else setReview(null); throw failure;
    }
  }
  return <section className={styles.accountSection} aria-label="Serah-terima pemeriksaan pembayaran">
    <h2>Serah-terima pemeriksaan</h2><p>Pengambilalihan dilakukan oleh reviewer yang berwenang. Delegasi yang berakhir atau dicabut tidak memberikan akses baru.</p>
    {error !== null && <p role="alert">{paymentHandoverMessage(error)}</p>}
    {notice && <p role="status">{notice}</p>}{busy && <p role="status">Memeriksa serah-terima…</p>}
    {pending.length > 0 && <section aria-label="Pemulihan serah-terima"><h3>Hasil serah-terima belum dipastikan</h3><p>Periksa hasil memakai kunci asli. Jangan mengirim pengambilalihan baru sebelum hasilnya jelas.</p>
      {pending.map(ref => <div key={ref.key}><p>Pengajuan {ref.declarationId} · Delegasi {ref.delegationId} · Versi tugas {ref.reviewRevision}</p>
        <Button variant="primary" disabled={disabled || !fresh} onClick={() => void run(async () => {
          const result = await gateway.recover(ref, {actorId, isCurrent: active});
          if (!result) {setNotice('Hasil belum terkonfirmasi. Referensi asli tetap disimpan; periksa lagi setelah akses atau layanan pulih.'); return;}
          journal.confirmed(ref); clear(); setNotice('Serah-terima asli ditemukan; tidak ada pengambilalihan kedua.'); onSettled();
        })}>Periksa hasil serah-terima {ref.declarationId}</Button></div>)}
    </section>}
    {allowed && !fresh && <p><Link href="/identity/account?operation=payment.review.manage&returnPath=%2Fbilling%2Fpayment-reviews">Verifikasi dua langkah untuk serah-terima</Link></p>}
    {!owner ? <p>Pilih pengajuan untuk memeriksa kaitan delegasi dan penanggung jawabnya.</p> : <>
      <Button variant="secondary" disabled={disabled || pending.length > 0} onClick={() => void run(() => load())}>Muat pilihan serah-terima</Button>
      {page && <><p>Penanggung jawab saat dimuat: {page.assigneeId ? 'Akun ' + page.assigneeId : 'Belum ditetapkan'} · Versi tugas {page.reviewRevision}</p>
        {!page.open ? <p>Tidak ada tugas aktif yang dapat dialihkan pada pengajuan ini.</p> : page.items.length === 0 ? <p>Belum ada delegasi terkait untuk halaman ini. Koordinasikan pengganti dengan penanggung jawab operasional.</p> : <form onSubmit={event => {
          event.preventDefault(); if (disabled || pending.length || !allowed || !fresh) return;
          try {setReview(preparePaymentHandover(actorId, page, selected, reason.trim()));} catch (failure) {setError(failure);}
        }}><label htmlFor="handover-delegation">Delegasi terkait pemeriksaan</label><select id="handover-delegation" value={selected} disabled={disabled || !!review || pending.length > 0} onChange={event => setSelected(event.target.value)} required>
          <option value="">Pilih delegasi yang terkait</option>{page.items.map(item => <option key={item.delegationId} value={item.delegationId}>Delegasi {item.delegationId} · {states[item.state]}</option>)}</select>
          {selected && <p>{states[page.items.find(item => item.delegationId === selected)!.state]}. Tindakan berikutnya: reviewer yang memiliki akses aktif meninjau lalu mengambil tanggung jawab secara eksplisit.</p>}
          <label htmlFor="handover-reason">Alasan serah-terima pemeriksaan</label><textarea id="handover-reason" value={reason} required maxLength={1000} disabled={disabled || !!review || pending.length > 0} onChange={event => setReason(event.target.value)} />
          {!page.canClaim || !allowed ? <p>Akun ini belum dapat mengambil tugas tersebut. Kewenangan dan independensi reviewer harus masih berlaku.</p> : !review ? <Button variant="primary" type="submit" disabled={disabled || !fresh || pending.length > 0}>Tinjau serah-terima</Button> : <section className={styles.state} aria-label="Konfirmasi serah-terima pemeriksaan">
            <h3 ref={heading} tabIndex={-1}>Ambil tanggung jawab pemeriksaan</h3><p>Pengajuan {review.declarationId} · Versi tugas {review.reviewRevision} · Delegasi {review.handover.delegationId} versi {review.handover.delegationRevision}</p>
            <p>Dari akun {review.handover.originalAssigneeId} kepada akun Anda. Alasan: {review.body.reason}</p><p>Dana, invoice dan hasil verifikasi pembayaran tidak berubah.</p>
            <Button variant="primary" type="button" disabled={disabled || !fresh || pending.length > 0} onClick={() => void run(() => claim(review))}>Konfirmasi ambil pemeriksaan</Button>
            <Button variant="secondary" type="button" disabled={disabled} onClick={() => setReview(null)}>Batalkan serah-terima</Button>
          </section>}
        </form>}
        {page.nextId && <Button variant="secondary" disabled={disabled || !!review || pending.length > 0} onClick={() => void run(() => load(page.nextId))}>Delegasi lebih lama</Button>}
      </>}
    </>}
  </section>;
}
