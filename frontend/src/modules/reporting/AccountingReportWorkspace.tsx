'use client';

import Link from 'next/link';
import {useCallback, useEffect, useRef, useState} from 'react';
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
  return <main className={common.page} id="accounting-report-workspace">
    <a className={styles.skip} href="#accounting-report-content">Langsung ke laporan</a>
    <header className={common.header}><div><Link className={common.brand} href="/identity/account">Unit Toko</Link><h1>Laporan akun dan jurnal</h1>
      <p>Baca saldo kumulatif dari jurnal yang telah tersimpan pada buku operasional. Pilih organisasi, toko, kanal, mata uang, dan tanggal akhir yang jelas.</p></div>
      <Link className={common.secondary} href="/identity/account">Akun saya</Link></header>
    {error !== null && account !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    <div id="accounting-report-content">
      {loading ? <p role="status">Memeriksa sesi dan izin laporan…</p> : !account || sessionTiming(account.session, elapsed).expired
        ? <SessionAccessState error={error} returnPath={path} onRetry={() => setAttempt(value => value + 1)} />
        : <ReportDesk key={account.actorId + ':' + account.observedAt} account={account} onDenied={denied} />}
    </div>
  </main>;
}

function Scope({book}: {book: AccountingReportBook}) {
  return <dl className={common.facts}><div><dt>Organisasi</dt><dd>{book.organization.code} · {book.organization.name}</dd></div>
    <div><dt>Toko</dt><dd>{book.shop.code} · {book.shop.name}</dd></div><div><dt>Kanal</dt><dd>{book.channel.code} · {book.channel.name}</dd></div>
    <div><dt>Mata uang</dt><dd>{book.key.currency}</dd></div><div><dt>Buku operasional</dt><dd>{book.id}</dd></div></dl>;
}

function ReportDesk({account, onDenied}: {account: Account; onDenied: (error: unknown) => void}) {
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
  return <div className={styles.desk}>
    <section className={common.accountSection} aria-labelledby="report-selection-heading">
      <h2 id="report-selection-heading">Pilih cakupan laporan</h2>
      <p className={common.help}>Buku yang ditampilkan mengikuti izin laporan pada toko Anda. Pilihan tanggal bersifat inklusif menurut kalender Asia/Jakarta, sejak awal buku hingga tanggal tersebut.</p>
      {busy === 'books' && <p role="status">Memuat buku dalam cakupan laporan…</p>}
      {!online && <p role="status" className={common.alert}>Perangkat sedang luring. Sambungkan kembali sebelum menampilkan laporan atau mengunduh CSV.</p>}
      <form className={styles.selection} onSubmit={event => {event.preventDefault(); void read();}}>
        <label htmlFor="accounting-report-book">Buku organisasi, toko, kanal, dan mata uang<select id="accounting-report-book" required value={selectedId}
          disabled={busy === 'books' || !online || books.length === 0} onChange={event => {resetSelection(); setSelectedId(event.target.value);}}>
          <option value="">Pilih satu buku operasional</option>
          {books.map(book => <option key={book.id} value={book.id}>{book.organization.name} · {book.shop.name} · {book.channel.name} · {book.key.currency}</option>)}
        </select></label>
        <label htmlFor="accounting-report-end-date">Tanggal akhir inklusif (Asia/Jakarta)<input type="date" id="accounting-report-end-date" required value={endDate}
          disabled={!online || busy === 'books'} onChange={event => {resetSelection(); setEndDate(event.target.value);}} /></label>
        <button type="submit" className={common.primary} disabled={busy !== null || !selected || !online}>Tampilkan laporan</button>
      </form>
      {selected && <Scope book={selected} />}
      {busy !== 'books' && books.length === 0 && error === null && <p role="status">Belum ada buku terkonfigurasi dalam cakupan laporan Anda. Hubungi penanggung jawab untuk memeriksa konfigurasi dan akses.</p>}
      <button type="button" className={common.secondary} disabled={busy !== null || !online} onClick={() => void loadBooks()}>Muat ulang daftar buku</button>
    </section>
    {error !== null && <p className={common.alert} role="alert">{message(error)}</p>}
    {notice && <p className={common.success} role="status">{notice}</p>}
    {busy === 'read' && <section className={common.state} aria-busy="true"><p role="status">Membaca seluruh jurnal tersimpan dan saldo akun untuk pilihan ini…</p></section>}
    {busy === 'export' && <p role="status">Memeriksa laporan tersimpan dan menyiapkan CSV seluruh baris…</p>}
    {!report && busy !== 'read' && <section className={common.state}><h2>Laporan belum ditampilkan</h2><p>Pilih satu buku dan tanggal akhir, lalu tampilkan laporan. Saldo tidak berasal dari halaman pesanan, tagihan, atau stok saat ini.</p></section>}
    {report && <section className={common.detail} aria-labelledby="report-result-heading">
      <div className={common.form}><h2 id="report-result-heading" tabIndex={-1} ref={reportHeading}>Neraca saldo kumulatif</h2><Scope book={report.book} />
        <p>Dari awal buku sampai tanggal bisnis {report.endDate}, termasuk tanggal tersebut. Zona waktu: Asia/Jakarta.</p>
        <p className={common.scopeSummary}>Laporan dari jurnal tersimpan. Laporan ini bukan penutupan buku yang disetujui, dan tidak menerbitkan transaksi atau mengubah saldo.</p>
        <dl className={common.facts}><div><dt>Dibaca pada</dt><dd>{when(report.capturedAt)}</dd></div><div><dt>Batas unduhan</dt><dd>{when(report.expiresAt)}</dd></div>
          <div><dt>Total debit</dt><dd>{formatAccountingReportMoney(report.debit, report.book.key.currency)}</dd></div>
          <div><dt>Total kredit</dt><dd>{formatAccountingReportMoney(report.credit, report.book.key.currency)}</dd></div><div><dt>Referensi laporan</dt><dd>{report.readId}</dd></div></dl>
        {expired && <p role="status" className={common.alert}>Masa berlaku unduhan laporan telah berakhir. Tampilkan laporan baru untuk memperoleh CSV terbaru.</p>}
        <button type="button" className={common.secondary} disabled={busy !== null || !online || expired} onClick={() => void download()}>Unduh CSV lengkap</button>
        <p className={common.help}>CSV memuat seluruh akun dan jurnal dari laporan tersimpan yang sama. Tampilan jurnal dibagi per halaman; unduhan tidak mengikuti halaman yang sedang terbuka. Batas: 500 jurnal, 10.000 baris, 2.000 akun dan 8 MiB CSV.</p>
        <div className={styles.tableScroll} tabIndex={0} role="region" aria-label="Neraca saldo akun, dapat digeser mendatar">
          <table><caption>Saldo {report.accounts.length} akun pada buku yang dipilih</caption><thead><tr><th scope="col">Kode akun pada bagan terkait</th><th scope="col">Debit</th><th scope="col">Kredit</th><th scope="col">Saldo debit dikurangi kredit</th></tr></thead>
            <tbody>{report.accounts.map(row => <tr key={row.accountId}><th scope="row">{row.accountCodes.join(' / ')}<span className={styles.accountId}>Akun {row.accountId}</span></th>
              <td>{formatAccountingReportMoney(row.debit, report.book.key.currency)}</td><td>{formatAccountingReportMoney(row.credit, report.book.key.currency)}</td><td>{formatAccountingReportMoney(row.balance, report.book.key.currency)}</td></tr>)}</tbody></table>
        </div>
        {report.accounts.length === 0 && <p>Belum ada akun tercatat pada bagan yang terkait dengan laporan ini.</p>}
      </div>
      <div className={common.form}><h2>Jurnal tersimpan</h2>
        {report.journals.length === 0 ? <p role="status">Belum ada jurnal yang dibukukan sampai tanggal akhir pilihan ini. Akun pada bagan terkait tetap ditampilkan dengan saldo yang tercatat.</p>
          : <><p className={common.help}>Menampilkan jurnal {page * pageSize + 1}–{Math.min((page + 1) * pageSize, report.journals.length)} dari {report.journals.length}. Jumlah saldo mencakup seluruh laporan.</p>
            <ol className={styles.journals} start={page * pageSize + 1} aria-label="Rincian jurnal laporan">
              {journals.map(row => <li key={row.id}><details><summary>Jurnal {row.id} · {row.businessDate} · {row.description}</summary>
                <dl className={common.facts}><div><dt>Sumber</dt><dd>{row.source.namespace} · {row.source.documentId}</dd></div><div><dt>Peristiwa dan kejadian</dt><dd>{row.source.eventKind} · {row.source.occurrence}</dd></div>
                  <div><dt>Bagan tersimpan</dt><dd>{row.chartId}, versi {row.chartVersion}</dd></div><div><dt>Pembalikan</dt><dd>{row.reversalOf === null ? 'Bukan jurnal pembalikan' : `Membalik jurnal ${row.reversalOf}`}</dd></div></dl>
                <div className={styles.tableScroll} tabIndex={0} role="region" aria-label={`Baris jurnal ${row.id}, dapat digeser mendatar`}>
                  <table><caption>Baris asli jurnal {row.id}</caption><thead><tr><th scope="col">Akun dan pemetaan</th><th scope="col">Debit</th><th scope="col">Kredit</th></tr></thead>
                    <tbody>{row.lines.map((line, index) => <tr key={line.mappingKey + ':' + index}><th scope="row">{line.accountCode}<span className={styles.accountId}>{line.mappingKey} · Akun {line.accountId}</span></th>
                      <td>{formatAccountingReportMoney(line.debit, report.book.key.currency)}</td><td>{formatAccountingReportMoney(line.credit, report.book.key.currency)}</td></tr>)}</tbody></table>
                </div>
              </details></li>)}
            </ol><nav className={common.pagination} aria-label="Halaman jurnal"><button type="button" className={common.secondary} disabled={page === 0 || busy !== null}
              onClick={() => setPage(value => value - 1)}>Jurnal sebelumnya</button><span>Halaman {page + 1} dari {Math.ceil(report.journals.length / pageSize)}</span>
              <button type="button" className={common.secondary} disabled={(page + 1) * pageSize >= report.journals.length || busy !== null} onClick={() => setPage(value => value + 1)}>Jurnal berikutnya</button></nav></>}
      </div>
    </section>}
  </div>;
}
