'use client';

import Link from 'next/link';
import {useEffect, useRef, useState, type ReactNode} from 'react';
import {AccessFailure} from '../administration/workflow';
import {createOrderTimelineGateway, type TimelinePage, type TimelinePaymentOutcome, type TimelineCaseOutcome} from './order-timeline-client';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';
import {formatDate, formatDateTime} from '@/lib/format';
import { recordNumber } from '@/lib/document-numbers';

type Context = {actorId: string; orderId: string; isCurrent: () => boolean; onFailure: (error: unknown) => void};
const date = (value: string) => formatDateTime(value);
const calendarDate = (value: string) => formatDate(value + 'T00:00:00+07:00');
const caseStatus = {requested: 'Kasus diajukan', return_authorized: 'Retur diizinkan', rejected: 'Kasus ditolak'};
const reviewStatus = {not_submitted: 'Belum diajukan', pending: 'Menunggu pemeriksaan', assigned: 'Sedang diperiksa', rejected: 'Pengajuan ditolak', replacement_requested: 'Perlu bukti pengganti'};
const eventStatus = {received: 'Retur diterima', inspected: 'Retur diperiksa', credit_requested: 'Penyesuaian kredit diminta', refund_requested: 'Refund diminta',
  replacement_requested: 'Penggantian barang diminta', entitlement_requested: 'Hak pengembalian diajukan', credit_approved: 'Penyesuaian kredit disetujui', refund_approved: 'Refund disetujui',
  payout_recorded: 'Pembayaran refund dicatat', refund_settled: 'Penyelesaian refund diverifikasi bank'};
function failureMessage(error: unknown) {
  if (error instanceof AccessFailure && [401, 403].includes(error.status)) return 'Akses bagian riwayat ini tidak tersedia. Periksa sesi dan akses akun.';
  if (error instanceof AccessFailure && error.status === 409) return 'Catatan berubah atau belum dapat diverifikasi. Muat ulang sebelum melanjutkan.';
  return 'Riwayat belum dapat dimuat. Periksa koneksi, lalu coba kembali.';
}

export function OrderTimelinePanel(props: Context & {actions: readonly string[]}) {
  const [gateway] = useState(() => createOrderTimelineGateway()), [reload, setReload] = useState(0);
  useEffect(() => {
    const refresh = () => setReload(value => value + 1);
    window.addEventListener('focus', refresh); window.addEventListener('storage', refresh);
    return () => {window.removeEventListener('focus', refresh); window.removeEventListener('storage', refresh);};
  }, []);
  return <section aria-label="Riwayat pemenuhan dan penyelesaian" className={common.accountSection}>
    <h3>Riwayat pemenuhan dan penyelesaian</h3>
    <p>Setiap bagian memuat catatan yang tersedia untuk akun Anda. Invoice dapat menggabungkan beberapa pesanan; pembayaran dan retur ditampilkan sesuai invoice asalnya.</p>
    <button className={common.secondary} onClick={() => setReload(value => value + 1)}>Perbarui seluruh riwayat</button>
    <div key={props.orderId + ':' + reload}>
      <TimelineSection {...props} title="Pengiriman" allowed={props.actions.includes('shipment.read')} load={gateway.shipments}
        href="/fulfillment/shipments" linkLabel="Buka pengiriman dan bukti penerimaan" render={row => <>
          <strong>Pengiriman {row.number}</strong><p>Diberangkatkan {date(row.dispatchedAt)} · Bagian toko {row.suborderId}</p>
          <p>Kurir yang bertugas: {row.currentAssignment.courierName} · sejak {date(row.currentAssignment.effectiveAt)}</p><p>Kurir saat keberangkatan: {row.courierName}</p>
          <p>Pemberangkatan belum menyatakan barang telah diterima.</p></>} />
      <TimelineSection {...props} title="Invoice" allowed={props.actions.includes('invoice.read')} load={gateway.invoices}
        href="/billing/invoices" linkLabel="Buka invoice dan dokumennya" render={row => <>
          <strong>Invoice {row.number}</strong><p>Diterbitkan {date(row.issuedAt)} · Jatuh tempo {calendarDate(row.dueDate)}</p>
          {row.orderIds.length > 1 && <p>Invoice gabungan untuk {row.orderIds.length} pesanan. Catatan pembayarannya berlaku pada invoice ini.</p>}</>} />
      <TimelineSection {...props} title="Pengajuan pembayaran saya" allowed={props.actions.includes('payment.evidence')} load={gateway.payments}
        href="/billing/payments" linkLabel="Buka pengajuan dan bukti pembayaran" render={row => <>
          <strong>Pengajuan {row.id}</strong><p>{row.state === 'draft' ? 'Draf belum diajukan' : 'Bukti pembayaran telah diajukan'} · {date(row.updatedAt)}</p>
          <TimelineDetail {...props} recordId={row.id} label={'Periksa pembayaran ' + row.id} load={gateway.payment} render={outcome => <PaymentOutcome row={outcome} />} />
        </>} />
      <TimelineSection {...props} title="Kasus, retur, dan refund" allowed={props.actions.includes('case.read')} load={gateway.cases}
        href="/fulfillment/cases" linkLabel="Buka kasus dan retur" render={row => <>
          <strong>Kasus {recordNumber('case', row.id)}</strong><p>{caseStatus[row.state]} · {date(row.requestedAt)} · Invoice {row.invoiceId}</p>
          <TimelineDetail {...props} recordId={row.id} label={'Periksa kasus ' + recordNumber('case', row.id)} load={gateway.case} render={outcome => <CaseOutcome row={outcome} />} />
        </>} />
    </div>
  </section>;
}

function TimelineSection<T extends {id: string}>({actorId, orderId, isCurrent, onFailure, title, allowed, load, href, linkLabel, render}: Context & {
  title: string; allowed: boolean; load: (actor: string, order: string, before: string | null, signal?: AbortSignal) => Promise<TimelinePage<T>>;
  href: string; linkLabel: string; render: (row: T) => ReactNode;
}) {
  const [page, setPage] = useState<TimelinePage<T> | null>(null), [before, setBefore] = useState<string | null>(null), [reload, setReload] = useState(0);
  const [busy, setBusy] = useState(allowed), [error, setError] = useState<unknown>(null), current = useRef(isCurrent);
  useEffect(() => {current.current = isCurrent;}, [isCurrent]);
  useEffect(() => {
    if (!allowed) return; let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active || !current.current()) return; setPage(null); setError(null); setBusy(true);
      try {const found = await load(actorId, orderId, before, controller.signal); if (active && current.current()) setPage(found);}
      catch (failure) {if (active && current.current()) {setPage(null); setError(failure); onFailure(failure);}}
      finally {if (active) setBusy(false);}
    }); return () => {active = false; controller.abort();};
  }, [actorId, orderId, before, reload, allowed, load, onFailure]);
  return <section aria-label={title} aria-busy={busy} className={common.accountSection}><h4>{title}</h4>
    {!allowed ? <p>Akun ini belum memiliki akses untuk melihat bagian ini.</p> : <>
      {busy && <p role="status">Memuat {title.toLowerCase()}…</p>}
      {error !== null && <p role="alert" className={common.alert}>{failureMessage(error)}</p>}
      {page && page.items.length === 0 && <p>Belum ada catatan yang dapat ditampilkan pada halaman ini.</p>}
      {page && <ul className={styles.lines}>{page.items.map(row => <li className={styles.line} key={row.id}>{render(row)}</li>)}</ul>}
      <div className={common.actions}><button className={common.secondary} disabled={busy} onClick={() => {setPage(null); setBefore(null); setReload(value => value + 1);}}>Muat {title.toLowerCase()} terbaru</button>
        {page?.hasMore && <button className={common.secondary} disabled={busy} onClick={() => {setPage(null); setBefore(page.items.at(-1)!.id);}}>Catatan {title.toLowerCase()} lebih lama</button>}
        <Link className={common.secondary} href={href}>{linkLabel}</Link></div>
    </>}
  </section>;
}

function TimelineDetail<T>({actorId, orderId, recordId, isCurrent, onFailure, load, label, render}: Context & {
  recordId: string; label: string; load: (actor: string, order: string, id: string, signal?: AbortSignal) => Promise<T>; render: (row: T) => ReactNode;
}) {
  const [row, setRow] = useState<T | null>(null), [error, setError] = useState<unknown>(null), [busy, setBusy] = useState(false);
  const pending = useRef(false), active = useRef(false), controller = useRef<AbortController | null>(null);
  useEffect(() => {active.current = true; return () => {active.current = false; controller.current?.abort();};}, []);
  async function read() {
    if (pending.current || !isCurrent()) return; pending.current = true; setBusy(true); setRow(null); setError(null); controller.current = new AbortController();
    try {const result = await load(actorId, orderId, recordId, controller.current.signal); if (active.current && isCurrent()) setRow(result);}
    catch (failure) {if (active.current && isCurrent()) {setError(failure); onFailure(failure);}}
    finally {pending.current = false; if (active.current) setBusy(false);}
  }
  return <div aria-busy={busy}><button className={common.secondary} disabled={busy} onClick={() => void read()}>{label}</button>
    {busy && <p role="status">Memeriksa catatan terbaru…</p>}{error !== null && <p role="alert" className={common.alert}>{failureMessage(error)}</p>}
    {row !== null && render(row)}</div>;
}

function PaymentOutcome({row}: {row: TimelinePaymentOutcome}) {
  return <div><p>{reviewStatus[row.reviewState]} · Data dimuat {date(row.observedAt)}</p>
    {row.currentAssigneeAccountId && <p>Penanggung jawab pemeriksaan saat ini: akun {row.currentAssigneeAccountId}.</p>}
    {row.reviewActorAccountId && <p>Keputusan pemeriksaan dicatat oleh akun {row.reviewActorAccountId}.</p>}
    {row.publicExplanation && <p>{row.publicExplanation}</p>}
    {row.verifiedAt === null ? <p>Belum ada verifikasi penerimaan bank yang dapat ditampilkan.</p> : <p>Penerimaan bank diverifikasi {date(row.verifiedAt)} oleh akun {row.verificationActorAccountId}.</p>}
    {row.invoiceEvents.length === 0 ? <p>Belum ada alokasi pembayaran yang dapat ditampilkan.</p> : <ul>{row.invoiceEvents.map(event =>
      <li key={[event.invoiceId, event.kind, event.eventId].join(':')}>{event.kind === 'allocation' ? 'Pembayaran dialokasikan' : 'Alokasi pembayaran dibalik'} ke invoice {event.invoiceId} · {date(event.recordedAt)}
        {event.originalAllocationId && <> · Alokasi asal {event.originalAllocationId}</>} · Dicatat akun {event.actorAccountId}</li>)}</ul>}
    <p>Peristiwa ini tidak menyatakan seluruh pesanan atau invoice sudah lunas.</p>
  </div>;
}
function CaseOutcome({row}: {row: TimelineCaseOutcome}) {
  return <div><p>{caseStatus[row.caseState]} · Data dimuat {date(row.observedAt)}</p>{row.publicExplanation && <p>{row.publicExplanation}</p>}
    {row.decisionActorAccountId && <p>Keputusan kasus dicatat oleh akun {row.decisionActorAccountId}.</p>}
    {row.returns.length === 0 ? <p>Belum ada penerimaan retur yang dapat ditampilkan.</p> : row.returns.map(returned => <div key={returned.returnId}><h5>Retur {recordNumber('return', returned.returnId)}</h5>
      <ul>{returned.events.map(event => <li key={event.kind + ':' + event.eventId}>{eventStatus[event.kind]} · {date(event.recordedAt)} · Dicatat akun {event.actorAccountId}</li>)}</ul></div>)}
    <p>Persetujuan refund dan pencatatan pembayarannya belum menyatakan penyelesaian bank. Permintaan penggantian belum menyatakan barang pengganti telah dikirim.</p>
  </div>;
}
