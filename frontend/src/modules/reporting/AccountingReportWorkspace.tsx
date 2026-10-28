'use client';

import {useCallback, useEffect, useRef, useState, type ReactNode} from 'react';
import {RequestNotSentError} from '../../api/client';
import {createIdentityGateway} from '../identity/identity-client';
import {IdentityFailure, sessionTiming, type IdentitySession} from '../identity/identity-workflow';
import {createAccessGateway} from '../administration/access-client';
import {AccessFailure} from '../administration/workflow';
import {CatalogReadGate} from '../catalog/catalog-editor-state';
import {accountingReportBookKey, createAccountingReportGateway, formatAccountingReportMoney,
  type AccountingReportBook, type AccountingReportSnapshot} from './accounting-report-client';
import common from '../administration/access.module.css';
import styles from './reporting.module.css';
import { dateTimeFormatter, jakartaDateKey } from "@/lib/format";
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Field } from '@/components/workspace-kit/field';
import { Facts } from '@/components/workspace-kit/facts';
import { DataTable } from '@/components/workspace-kit/data-table';

type Account = {actorId: string; session: IdentitySession; observedAt: number};
const path = '/reporting/accounts', pageSize = 20;
const when = (value: string) => dateTimeFormatter.format(new Date(value));
function todayInJakarta(): string {
  return jakartaDateKey(new Date());
}
function message(error: unknown): string {
  if (error instanceof IdentityFailure) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
  if (error instanceof AccessFailure || error instanceof RequestNotSentError) {
    if (error.code === 'actor_changed' || error.code === 'session_expired') return 'Sesi Anda perlu diperbarui. Masuk kembali untuk melanjutkan.';
    if (error.status === 401) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
    if (error.status === 403) return 'Akun ini belum memiliki akses laporan untuk toko yang dipilih. Laporan organisasi lain tidak ditampilkan.';
    if (error.code === 'invalid_accounting_report_date') return 'Pilih tanggal kalender yang sah. Tanggal akhir mencakup seluruh tanggal bisnis tersebut di Asia/Jakarta.';
    if (error.code === 'accounting_report_too_large') return 'Pilihan kumulatif melebihi batas laporan: 500 jurnal, 10.000 baris, atau 2.000 akun. Pilih tanggal akhir lebih awal atau hubungi penanggung jawab untuk pelaporan jumlah besar. Data tidak dipotong diam-diam.';
    if (error.code === 'accounting_report_snapshot_stale' || error.code === 'accounting_report_view_stale') return 'Sumber, pilihan, atau masa berlaku laporan berubah. Tampilkan laporan baru sebelum mengunduh CSV.';
    if (error.code === 'accounting_report_response_invalid' || error.code === 'accounting_report_integrity_unavailable') return 'Kelengkapan laporan belum dapat dipastikan. Data tidak digunakan; muat ulang atau hubungi penanggung jawab.';
    if (error.code === 'report_download_unavailable') return 'Unduhan belum dapat dimulai di peramban ini. Periksa izin unduhan dan gunakan kembali laporan yang masih berlaku.';
  }
  return 'Laporan belum dapat dimuat. Periksa koneksi, lalu tampilkan kembali laporan. Tidak ada perubahan jurnal atau transaksi yang dikirim.';
}

export function AccountingReportWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true);
  const [attempt, setAttempt] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setAccount(null); setLoading(true); setError(null);
      try {
        const session = await identity.session(controller.signal), observedAt = performance.now();
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const scope = await access.current(); if (!active) return;
        if (scope.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!scope.allowedActions.includes('report.read')) throw new AccessFailure(403, 'scope_denied');
        if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
        setAccount({actorId: scope.userId, session, observedAt}); setElapsed(performance.now() - observedAt);
      } catch (failure) {if (active) setError(failure);} finally {if (active) setLoading(false);}
    });
    return () => {active = false; controller.abort();};
  }, [access, identity, attempt]);
  useEffect(() => {
    const timer = window.setInterval(() => {if (account) setElapsed(performance.now() - account.observedAt);}, 1000);
    const restore = (event: PageTransitionEvent) => {if (event.persisted) {setAccount(null); setAttempt(value => value + 1);}};
    window.addEventListener('pageshow', restore); return () => {window.clearInterval(timer); window.removeEventListener('pageshow', restore);};
  }, [account]);
  const denied = useCallback((failure: unknown) => {
    if ((failure instanceof AccessFailure || failure instanceof RequestNotSentError) &&
      (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) {setAccount(null); setError(failure);}
  }, []);
  // The desk owns the book refresh, so it renders the header with it; the access states render it without.
  const header = (action?: ReactNode) => <PageHeader title="Laporan akun dan jurnal" secondaryAction={action}
    description="Baca saldo kumulatif dari jurnal yang telah tersimpan pada buku operasional. Pilih organisasi, toko, kanal, mata uang, dan tanggal akhir yang jelas." />;
  return <main className={common.page} id="accounting-report-workspace">
    {account !== null && !loading && !sessionTiming(account.session, elapsed).expired
      ? <ReportDesk key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} header={header} />
      : <>{header()}<div className="ui-stack" id="accounting-report-content">
        {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
        {loading ? <StateView kind="loading" title="Memeriksa sesi dan izin laporan…" /> : <SessionAccessState error={error} returnPath={path} onRetry={() => setAttempt(value => value + 1)} />}
      </div></>}
  </main>;
}

function Scope({book}: {book: AccountingReportBook}) {
  return <Facts items={[{term: 'Organisasi', value: <>{book.organization.code} · {book.organization.name}</>}, {term: 'Toko', value: <>{book.shop.code} · {book.shop.name}</>},
    {term: 'Kanal', value: <>{book.channel.code} · {book.channel.name}</>}, {term: 'Mata uang', value: book.key.currency}, {term: 'Buku operasional', value: book.id}]} />;
}

function ReportDesk({account, onDenied, header}: {account: Account; onDenied: (error: unknown) => void; header: (action?: ReactNode) => ReactNode}) {
  const [gateway] = useState(createAccountingReportGateway), [gate] = useState(() => new CatalogReadGate());
  const [books, setBooks] = useState<AccountingReportBook[]>([]), [selectedId, setSelectedId] = useState(''), [endDate, setEndDate] = useState(todayInJakarta);
  const [report, setReport] = useState<AccountingReportSnapshot | null>(null), [page, setPage] = useState(0);
  const [busy, setBusy] = useState<'books' | 'read' | 'export' | null>('books'), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const [online, setOnline] = useState(true), [now, setNow] = useState(() => Date.now());
  const alive = useRef(false), urls = useRef(new Set<string>()), reportHeading = useRef<HTMLHeadingElement>(null);
  const live = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const loadBooks = useCallback(async () => {
    const request = gate.begin(account.actorId, 'books');
    if (!live()) return; setBusy('books'); setError(null); setReport(null); setSelectedId(''); setBooks([]); setNotice('');
    try {
      const result = await gateway.books(account.actorId, () => live() && request.current(), request.signal);
      if (live() && request.current()) setBooks(result.items);
    } catch (failure) {if (alive.current && request.current()) {setError(failure); onDenied(failure);}}
    finally {if (alive.current && request.current()) setBusy(null);}
  }, [account.actorId, gate, gateway, live, onDenied]);
  useEffect(() => {
    alive.current = true;
    const connection = () => setOnline(navigator.onLine);
    void Promise.resolve().then(() => {if (alive.current) {connection(); return loadBooks();}});
    const timer = window.setInterval(() => setNow(Date.now()), 15_000);
    window.addEventListener('online', connection); window.addEventListener('offline', connection);
    const retainedUrls = urls.current;
    return () => {alive.current = false; gate.cancel(); window.clearInterval(timer); window.removeEventListener('online', connection); window.removeEventListener('offline', connection);
      for (const url of retainedUrls) URL.revokeObjectURL(url); retainedUrls.clear();};
  }, [gate, loadBooks]);
  const selected = books.find(book => book.id === selectedId) ?? null;
  const resetSelection = () => {gate.cancel(); setReport(null); setPage(0); setBusy(null); setError(null); setNotice('');};
  async function read() {
    if (!selected || !live() || busy !== null) return;
    const source = selected, date = endDate, request = gate.begin(account.actorId, accountingReportBookKey(source.key) + ':' + date);
    setBusy('read'); setReport(null); setError(null); setNotice(''); setPage(0);
    try {
      const result = await gateway.read(account.actorId, source, date, () => live() && request.current(), request.signal);
      if (!live() || !request.current()) return;
      setReport(result); setNow(Date.now());
      window.requestAnimationFrame(() => {if (live() && request.current()) reportHeading.current?.focus();});
    } catch (failure) {if (alive.current && request.current()) {setError(failure); onDenied(failure);}}
    finally {if (alive.current && request.current()) setBusy(null);}
  }
  async function download() {
    if (!report || !live() || busy !== null) return;
    const source = report, request = gate.begin(account.actorId, 'export:' + source.readId + ':' + source.snapshotHash);
    setBusy('export'); setError(null); setNotice('');
    try {
      const result = await gateway.export(account.actorId, source, () => live() && request.current(), request.signal);
      if (!live() || !request.current()) return;
      const url = URL.createObjectURL(new Blob([new Uint8Array(result.bytes)], {type: 'text/csv;charset=utf-8'})); urls.current.add(url);
      const link = document.createElement('a'); link.href = url; link.download = result.fileName;
      try {document.body.appendChild(link); link.click();}
      catch {throw new AccessFailure(503, 'report_download_unavailable');}
      finally {link.remove(); window.setTimeout(() => {if (urls.current.delete(url)) URL.revokeObjectURL(url);}, 1000);}
      setNotice(`CSV lengkap disiapkan dari laporan ini: seluruh ${source.accounts.length} akun dan ${source.journals.length} jurnal, termasuk jurnal pada halaman lain.`);
    } catch (failure) {
      if (alive.current && request.current()) {setError(failure); onDenied(failure);
        if ((failure instanceof AccessFailure || failure instanceof RequestNotSentError) && failure.code === 'accounting_report_snapshot_stale') setReport(null);}
    } finally {if (alive.current && request.current()) setBusy(null);}
  }
  const expired = report !== null && now >= Date.parse(report.expiresAt), journals = report?.journals.slice(page * pageSize, (page + 1) * pageSize) ?? [];
  const currency = report?.book.key.currency ?? 'IDR';
  return <>
    {header(<Button variant="secondary" type="button" disabled={busy !== null || !online} onClick={() => void loadBooks()}>Muat ulang daftar buku</Button>)}
    <div className="ui-stack" id="accounting-report-content">
    <Section title="Pilih cakupan laporan" id="report-selection-heading" busy={busy === 'books'}
      description="Buku yang ditampilkan mengikuti izin laporan pada toko Anda. Pilihan tanggal bersifat inklusif menurut kalender Asia/Jakarta, sejak awal buku hingga tanggal tersebut.">
      {busy === 'books' && <StateView kind="loading" title="Memuat buku dalam cakupan laporan…" rows={1} />}
      {!online && <StateView kind="offline" title="Perangkat sedang luring. Sambungkan kembali sebelum menampilkan laporan atau mengunduh CSV." />}
      <form className="ui-form" onSubmit={event => {event.preventDefault(); void read();}}>
        <Field id="accounting-report-book" label="Buku organisasi, toko, kanal, dan mata uang"><select required value={selectedId}
          disabled={busy === 'books' || !online || books.length === 0} onChange={event => {resetSelection(); setSelectedId(event.target.value);}}>
          <option value="">Pilih satu buku operasional</option>
          {books.map(book => <option key={book.id} value={book.id}>{book.organization.name} · {book.shop.name} · {book.channel.name} · {book.key.currency}</option>)}
        </select></Field>
        <Field id="accounting-report-end-date" label="Tanggal akhir inklusif (Asia/Jakarta)" width="date"><input type="date" required value={endDate}
          disabled={!online || busy === 'books'} onChange={event => {resetSelection(); setEndDate(event.target.value);}} /></Field>
        <Button variant="primary" type="submit" disabled={busy !== null || !selected || !online}>Tampilkan laporan</Button>
      </form>
      {selected && <Scope book={selected} />}
      {busy !== 'books' && books.length === 0 && error === null && <StateView kind="empty" title="Belum ada buku terkonfigurasi dalam cakupan laporan Anda. Hubungi penanggung jawab untuk memeriksa konfigurasi dan akses." />}
    </Section>
    {error !== null && <StateView kind="error" title={message(error)} />}
    {notice && <StateView kind="success" title={notice} />}
    {busy === 'read' && <StateView kind="loading" title="Membaca seluruh jurnal tersimpan dan saldo akun untuk pilihan ini…" />}
    {busy === 'export' && <StateView kind="loading" title="Memeriksa laporan tersimpan dan menyiapkan CSV seluruh baris…" rows={1} />}
    {!report && busy !== 'read' && <section className="ui-section" aria-label="Hasil laporan"><StateView kind="empty" title="Laporan belum ditampilkan"
      message="Pilih satu buku dan tanggal akhir, lalu tampilkan laporan. Saldo tidak berasal dari halaman pesanan, tagihan, atau stok saat ini." /></section>}
    {report && <>
      <section className="ui-section" aria-labelledby="report-result-heading"><div className="ui-stack">
        <div className="ui-form"><h2 id="report-result-heading" tabIndex={-1} ref={reportHeading}>Neraca saldo kumulatif</h2><Scope book={report.book} />
          <p>Dari awal buku sampai tanggal bisnis {report.endDate}, termasuk tanggal tersebut. Zona waktu: Asia/Jakarta.</p>
          <p className={common.scopeSummary}>Laporan dari jurnal tersimpan. Laporan ini bukan penutupan buku yang disetujui, dan tidak menerbitkan transaksi atau mengubah saldo.</p>
          <Facts items={[{term: 'Dibaca pada', value: when(report.capturedAt)}, {term: 'Batas unduhan', value: when(report.expiresAt)},
            {term: 'Total debit', value: formatAccountingReportMoney(report.debit, currency)}, {term: 'Total kredit', value: formatAccountingReportMoney(report.credit, currency)},
            {term: 'Referensi laporan', value: report.readId}]} />
          {expired && <p role="status" className={common.help}>Masa berlaku unduhan laporan telah berakhir. Tampilkan laporan baru untuk memperoleh CSV terbaru.</p>}
          <Button variant="secondary" type="button" disabled={busy !== null || !online || expired} onClick={() => void download()}>Unduh CSV lengkap</Button>
          <p className={common.help}>CSV memuat seluruh akun dan jurnal dari laporan tersimpan yang sama. Tampilan jurnal dibagi per halaman; unduhan tidak mengikuti halaman yang sedang terbuka. Batas: 500 jurnal, 10.000 baris, 2.000 akun dan 8 MiB CSV.</p>
        </div>
      </div></section>
      <section className="ui-section" data-variant="plain" aria-label="Saldo akun"><div className="ui-stack">
        <DataTable caption={`Saldo ${report.accounts.length} akun pada buku yang dipilih`} rows={report.accounts} getKey={row => row.accountId}
          empty={{title: 'Belum ada akun tercatat pada bagan yang terkait dengan laporan ini.'}}
          columns={[
            {key: 'code', header: 'Kode akun pada bagan terkait', render: row => <>{row.accountCodes.join(' / ')}<span className={styles.accountId}>Akun {row.accountId}</span></>},
            {key: 'debit', header: 'Debit', align: 'end', render: row => formatAccountingReportMoney(row.debit, currency)},
            {key: 'credit', header: 'Kredit', align: 'end', render: row => formatAccountingReportMoney(row.credit, currency)},
            {key: 'balance', header: 'Saldo debit dikurangi kredit', align: 'end', render: row => formatAccountingReportMoney(row.balance, currency)},
          ]} />
      </div></section>
      <Section title="Jurnal tersimpan" id="report-journals-heading" variant="plain">
        {report.journals.length === 0 ? <StateView kind="empty" title="Belum ada jurnal yang dibukukan sampai tanggal akhir pilihan ini. Akun pada bagan terkait tetap ditampilkan dengan saldo yang tercatat." />
          : <>
            <p className={common.help}>Menampilkan jurnal {page * pageSize + 1}–{Math.min((page + 1) * pageSize, report.journals.length)} dari {report.journals.length}. Jumlah saldo mencakup seluruh laporan.</p>
            <ol className={styles.journals} start={page * pageSize + 1} aria-label="Rincian jurnal laporan">
              {journals.map(row => <li key={row.id}><details><summary>Jurnal {row.id} · {row.businessDate} · {row.description}</summary>
                <div className={styles.journalBody}>
                  <Facts items={[{term: 'Sumber', value: <>{row.source.namespace} · {row.source.documentId}</>}, {term: 'Peristiwa dan kejadian', value: <>{row.source.eventKind} · {row.source.occurrence}</>},
                    {term: 'Bagan tersimpan', value: <>{row.chartId}, versi {row.chartVersion}</>}, {term: 'Pembalikan', value: row.reversalOf === null ? 'Bukan jurnal pembalikan' : `Membalik jurnal ${row.reversalOf}`}]} />
                  <DataTable caption={`Baris asli jurnal ${row.id}`} rows={row.lines.map((line, index) => ({line, index}))} getKey={({line, index}) => line.mappingKey + ':' + index}
                    columns={[
                      {key: 'account', header: 'Akun dan pemetaan', render: ({line}) => <>{line.accountCode}<span className={styles.accountId}>{line.mappingKey} · Akun {line.accountId}</span></>},
                      {key: 'debit', header: 'Debit', align: 'end', render: ({line}) => formatAccountingReportMoney(line.debit, currency)},
                      {key: 'credit', header: 'Kredit', align: 'end', render: ({line}) => formatAccountingReportMoney(line.credit, currency)},
                    ]} />
                </div>
              </details></li>)}
            </ol>
            {report.journals.length > pageSize && <nav className="ui-actions" aria-label="Halaman jurnal"><Button variant="secondary" type="button" disabled={page === 0 || busy !== null}
              onClick={() => setPage(value => value - 1)}>Jurnal sebelumnya</Button><span>Halaman {page + 1} dari {Math.ceil(report.journals.length / pageSize)}</span>
              <Button variant="secondary" type="button" disabled={(page + 1) * pageSize >= report.journals.length || busy !== null} onClick={() => setPage(value => value + 1)}>Jurnal berikutnya</Button></nav>}
          </>}
      </Section>
    </>}
    </div>
  </>;
}
