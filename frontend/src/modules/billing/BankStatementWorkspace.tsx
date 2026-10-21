'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { orderAmount } from '../ordering/order-quote-client';
import { createBankStatementGateway, prepareStatement, statementFile, type StatementProfile, type StatementAccount, type StatementPreview,
  type StatementSelection, type StatementSummary, type StatementDetail, type StatementCommand, type StatementParsed } from './bank-statement-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';
import sheet from './bank-statement.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const at = (value: string) => dateTimeFormatter.format(new Date(value));
const rupiah = (value: string) => orderAmount('IDR', value);
const issues: Record<string, string> = { identity_missing: 'Identitas transfer belum tersedia; perlu rekonsiliasi manual.', identity_repeated: 'Identitas transfer muncul lebih dari sekali; periksa kemungkinan baris berulang.', reference_repeated: 'Referensi digunakan lagi; periksa setiap transfer secara terpisah.', payer_unknown: 'Pengirim belum diketahui.', column_count: 'Jumlah kolom tidak sesuai format bank.', invalid_text: 'Teks berisi karakter atau spasi yang tidak didukung.', invalid_date: 'Tanggal tidak valid atau berada di masa depan.', invalid_direction: 'Penanda masuk/keluar tidak sesuai format.', invalid_amount: 'Nominal harus positif dengan paling banyak dua angka desimal.', account_mismatch: 'Nomor rekening berbeda dari rekening yang dipilih.', currency_mismatch: 'Mata uang berbeda dari rekening yang dipilih.', total_out_of_range: 'Total melebihi batas nominal yang didukung.' };
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
  if (error instanceof AccessFailure) {
    if (error.code === 'bank_statement_changed') return 'Format atau rekening berubah atau tidak lagi berlaku. Pilih data terbaru dan periksa ulang berkas.';
    if (error.code === 'bank_statement_header_mismatch') return 'Judul atau urutan kolom tidak sesuai format yang disetujui. Periksa pilihan format dan ekspor bank.';
    if (error.code === 'bank_statement_utf8_required') return 'Berkas harus menggunakan teks UTF-8 sesuai format yang disetujui.';
    if (error.code === 'bank_statement_file_limit' || error.code === 'bank_statement_row_limit') return 'Gunakan berkas maksimal 256 KiB dan 500 baris transaksi.';
    if (error.code === 'bank_statement_integrity_unavailable' || error.code === 'bank_statement_response_invalid') return 'Riwayat impor belum dapat dipastikan lengkap. Hubungi penanggung jawab sebelum melanjutkan.';
    if (error.code === 'invalid_bank_statement' || error.code.startsWith('bank_statement_')) return 'Periksa berkas, rekening, sumber bank dan isian yang diwajibkan.';
  }
  return accessMessage(error);
}
export function BankStatementWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [refresh, setRefresh] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!current.allowedActions.includes('bank.statement.read')) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, refresh]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRefresh(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && failure.code !== 'step_up_required' && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link><h1>Mutasi bank</h1><p>Siapkan sumber bank privat untuk pemeriksaan dan rekonsiliasi independen.</p></div></header>
    {error !== null && account !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState error={error} returnPath="/billing/bank-statements" onRetry={() => setRefresh(value => value + 1)} />
      : <StatementDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </main>;
}
function Parsed({ value }: { value: StatementParsed }) {
  const [page, setPage] = useState(0), [issueLimit, setIssueLimit] = useState(25);
  const rows = value.rows.slice(page * 25, (page + 1) * 25);
  return <><dl className={styles.facts}><div><dt>Baris terbaca</dt><dd>{value.rows.length}</dd></div><div><dt>Total masuk</dt><dd>{rupiah(value.credits)}</dd></div><div><dt>Total keluar</dt><dd>{rupiah(value.debits)}</dd></div><div><dt>Temuan</dt><dd>{value.issues.length}</dd></div></dl>
    <p>Total ini berasal dari berkas. Impor belum mengonfirmasi penerimaan dana atau mengurangi tagihan.</p>
    {!value.canStage && <p role="alert">Perbaiki sumber atau pilihan format sebelum menyimpan. Baris bermasalah tidak boleh dilewati.</p>}
    {value.issues.length > 0 && <section aria-label="Temuan mutasi"><h3>Perlu diperiksa</h3><ul>{value.issues.slice(0, issueLimit).map((issue, index) => <li key={index}>Baris {issue.row}: {issues[issue.code] ?? 'Periksa data sumber.'}</li>)}</ul>
      {issueLimit < value.issues.length && <button type="button" className={common.secondary} onClick={() => setIssueLimit(value => value + 25)}>Lihat temuan berikutnya</button>}</section>}
    <p className={sheet.scrollHint}>Geser tabel ke samping untuk melihat pengirim dan referensi bank.</p>
    <div className={sheet.tableScroll} tabIndex={0} role="region" aria-label="Rincian baris mutasi"><table className={sheet.table}><caption>Transaksi pada berkas sumber</caption><thead><tr><th scope="col">Baris / tanggal</th><th scope="col">Arah / nominal</th><th scope="col">Pengirim</th><th scope="col">Identitas / referensi bank</th></tr></thead>
      <tbody>{rows.map(row => <tr key={row.number}><td>{row.number}<br />{row.bookedDate}</td><td>{row.direction === 'credit' ? 'Masuk' : 'Keluar'}<br /><strong>{rupiah(row.amount)}</strong></td><td>{row.payerName ?? 'Belum diketahui'}</td><td>{row.transactionId ?? 'Identitas belum tersedia'}<br />{row.reference ?? 'Tanpa referensi'}<small>{row.identityCandidate ? 'Kandidat identitas; belum dikonfirmasi' : 'Memerlukan rekonsiliasi manual'}</small></td></tr>)}</tbody></table></div>
    <div className={sheet.actions}><button type="button" className={common.secondary} disabled={page === 0} onClick={() => setPage(value => value - 1)}>Baris sebelumnya</button><span>Halaman {page + 1} dari {Math.max(1, Math.ceil(value.rows.length / 25))}</span><button type="button" className={common.secondary} disabled={(page + 1) * 25 >= value.rows.length} onClick={() => setPage(value => value + 1)}>Baris berikutnya</button></div></>;
}
function StatementDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createBankStatementGateway), recovery = useOperationRecovery(account.actorId);
  const [page, setPage] = useState<{ items: StatementSummary[]; hasMore: boolean }>({ items: [], hasMore: false }), [profiles, setProfiles] = useState<{ items: StatementProfile[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [accounts, setAccounts] = useState<{ items: StatementAccount[]; hasMore: boolean }>({ items: [], hasMore: false }), [profileId, setProfileId] = useState(''), [versionId, setVersionId] = useState('');
  const [file, setFile] = useState<{ name: string; base64: string } | null>(null), [source, setSource] = useState(''), [reason, setReason] = useState(''), [attested, setAttested] = useState(false);
  const [preview, setPreview] = useState<StatementPreview | null>(null), [confirmation, setConfirmation] = useState<StatementCommand | null>(null), [draft, setDraft] = useState(false), [view, setView] = useState<StatementDetail | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), pending = useRef(false), title = useRef<HTMLHeadingElement>(null), confirmTitle = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const canStage = account.actions.includes('bank.statement.stage'), locked = busy || recovering || offline, commandLocked = locked || recovery.locked || !fresh;
  const profile = profiles.items.find(item => item.id === profileId), chosen = accounts.items.find(item => item.versionId === versionId);
  const selected = (): StatementSelection | null => profile && chosen ? { profileId: profile.id, profileRevision: profile.revision, accountId: chosen.accountId, accountVersionId: chosen.versionId } : null;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; void Promise.resolve().then(async () => {
    if (!active || !current()) return; setBusy(true);
    try { const result = await gateway.list(account.actorId); if (active && current()) setPage(result); }
    catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; }; }, [gateway, account.actorId, current, onDenied]);
  useEffect(() => { if (view) title.current?.focus(); }, [view]); useEffect(() => { if (confirmation) confirmTitle.current?.focus(); }, [confirmation]);
  async function work(action: () => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; recovery.refresh(); if (alive.current) setBusy(false); }
  }
  function clearDraft() { setDraft(false); setFile(null); setPreview(null); setConfirmation(null); setProfileId(''); setVersionId(''); setSource(''); setReason(''); setAttested(false); }
  async function load(afterId: string | null = null) { setView(null); clearDraft(); const result = await gateway.list(account.actorId, afterId); if (current()) setPage(result); }
  async function open(id: string) { setView(null); clearDraft(); const result = await gateway.read(account.actorId, id); if (current()) setView(result); }
  return <><AdministrationNavigation current="bank-statements" actions={account.actions} locked={busy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'billing.bank-statement': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } } }}
      onFailure={onDenied} onResolved={() => { recovery.refresh(); clearDraft(); setView(null); setNotice('Hasil impor telah dipastikan. Muat daftar terbaru.'); }} />
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}{offline && <p role="status" className={common.alert}>Koneksi terputus. Sambungkan kembali sebelum memeriksa atau menyimpan berkas.</p>}
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}{notice && <p role="status">{notice}</p>}{busy && <p role="status">Memproses mutasi bank…</p>}
    {canStage && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p><Link href="/identity/account?operation=bank.statement.stage&returnPath=%2Fbilling%2Fbank-statements">Verifikasi dua langkah untuk impor mutasi</Link></p>}
    <section className={common.accountSection} aria-label="Daftar impor bank"><h2>Sumber mutasi tersimpan</h2><p>Setiap berkas mempertahankan sumber asli dan versi rekening yang dipilih.</p><div className={sheet.actions}>
      <button className={common.secondary} disabled={locked} onClick={() => void work(() => load())}>Muat daftar terbaru</button>
      {canStage && <button className={common.primary} disabled={commandLocked} onClick={() => void work(async () => { clearDraft(); setView(null); const result = await gateway.profiles(account.actorId); if (current()) { setProfiles(result); setAccounts({ items: [], hasMore: false }); setDraft(true); } })}>Siapkan impor mutasi</button>}</div>
      {!busy && !page.items.length && <p>Belum ada impor dalam cakupan akses Anda.</p>}
      <ul className={styles.candidates}>{page.items.map(item => <li key={item.importId}><strong>{item.filename}</strong><p>{item.profileName} · {item.maskedAccount} · {item.rowCount} baris</p><p>Masuk {rupiah(item.credits)} · Keluar {rupiah(item.debits)} · {item.issueCount} temuan</p>
        <p>Disiapkan untuk rekonsiliasi · Akun {item.actorId} · {at(item.recordedAt)}</p><button className={common.secondary} disabled={locked} onClick={() => void work(() => open(item.importId))}>Periksa impor {item.importId}</button></li>)}</ul>
      {page.hasMore && <button className={common.secondary} disabled={locked} onClick={() => void work(() => load(page.items.at(-1)!.importId))}>Impor berikutnya</button>}
    </section>
    {draft && <section className={common.accountSection} aria-label="Siapkan impor bank"><h2>Pilih sumber dan rekening</h2><p>Ambil ekspor asli melalui sumber bank yang telah disetujui. Jangan mengganti rekening, nominal atau identitas transfer agar cocok dengan pengajuan pembayaran.</p>
      <form className={sheet.form} onSubmit={event => { event.preventDefault(); const selection = selected(); if (selection && file) void work(async () => { setPreview(null); setConfirmation(null); const result = await gateway.preview(account.actorId, selection, file.base64); if (current()) setPreview(result); }); }}>
        <label htmlFor="statement-profile">Format ekspor bank</label><select id="statement-profile" required disabled={commandLocked || !!confirmation} value={profileId} onChange={event => { const id = event.target.value; void work(async () => { setProfileId(id); setVersionId(''); setPreview(null); setAccounts({ items: [], hasMore: false }); const next = profiles.items.find(item => item.id === id); if (next) { const result = await gateway.accounts(account.actorId, next); if (current()) setAccounts(result); } }); }}>
          <option value="">Pilih format yang disetujui</option>{profiles.items.map(item => <option key={item.id} value={item.id} disabled={!item.active}>{item.name} · {item.id}/{item.revision}{!item.active ? ' (nonaktif)' : ''}</option>)}</select>
        {!profiles.items.length && <p role="status">Belum ada format dan sumber bank yang disetujui. Penanggung jawab Finance perlu menetapkannya sebelum impor nyata.</p>}
        {profiles.hasMore && <button type="button" className={common.secondary} disabled={locked} onClick={() => void work(async () => { const result = await gateway.profiles(account.actorId, profiles.items.at(-1)!.id); if (current()) setProfiles({ items: [...profiles.items, ...result.items], hasMore: result.hasMore }); })}>Format berikutnya</button>}
        {profile && <div className={sheet.source}><strong>Sumber bank: {profile.trustedSourceReference}</strong><p>Penanggung jawab: {profile.operatingOwnerReference}</p><p>Persetujuan: {profile.approvalReference} · Berlaku hingga {at(profile.expiresAt)}</p><p>Kolom berurutan: {profile.format.columns.join(' · ')}</p><p>Tanggal {profile.format.dateFormat}; pemisah kolom {profile.format.delimiter === '\t' ? 'tab' : profile.format.delimiter}; tanda desimal {profile.format.decimalSeparator}.</p></div>}
        <label htmlFor="statement-account">Rekening penerimaan dan versi</label><select id="statement-account" required disabled={commandLocked || !!confirmation || !profile} value={versionId} onChange={event => { setVersionId(event.target.value); setPreview(null); }}>
          <option value="">Pilih rekening secara eksplisit</option>{accounts.items.map(item => <option key={item.versionId} value={item.versionId}>{item.details.bankCode} · {item.details.accountNumber} · {item.details.holderName} · Versi {item.version}</option>)}</select>
        {profile && !accounts.items.length && !busy && <p>Belum ada rekening yang disetujui untuk format dan cakupan ini.</p>}
        {profile && accounts.hasMore && <button type="button" className={common.secondary} disabled={locked} onClick={() => void work(async () => { const result = await gateway.accounts(account.actorId, profile, accounts.items.at(-1)!.accountId); if (current()) setAccounts({ items: [...accounts.items, ...result.items], hasMore: result.hasMore }); })}>Rekening berikutnya</button>}
        <label htmlFor="statement-file">Berkas mutasi asli</label><input id="statement-file" type="file" accept=".csv,.tsv,.txt,text/csv,text/tab-separated-values,text/plain" disabled={commandLocked || !!confirmation} aria-describedby="statement-file-help" onChange={event => { const selectedFile = event.target.files?.[0]; setFile(null); setPreview(null); if (selectedFile) void work(async () => { const base64 = await statementFile(selectedFile); if (current()) setFile({ name: selectedFile.name, base64 }); }); }} />
        <small id="statement-file-help">UTF-8, maksimal 256 KiB dan 500 baris. Berkas tetap di memori halaman sampai Anda menyimpan impor.</small>
        <button className={common.secondary} disabled={commandLocked || !!confirmation || !selected() || !file}>Periksa isi berkas</button>
      </form>
      {preview && <div className={sheet.preview}><h3>Pratinjau {file?.name}</h3><Parsed key={preview.binding} value={preview.preview} />
        <form className={sheet.form} onSubmit={event => { event.preventDefault(); const selection = selected(); if (commandLocked || !selection || !file) return; try { setError(null); setConfirmation(prepareStatement(account.actorId, selection, file.base64, file.name, preview, source.trim(), attested, reason.trim())); } catch (failure) { setError(failure); } }}>
          <label htmlFor="statement-source">Referensi sumber bank</label><input id="statement-source" required maxLength={256} value={source} disabled={commandLocked || !!confirmation} onChange={event => setSource(event.target.value)} />
          <label htmlFor="statement-reason">Catatan impor</label><textarea id="statement-reason" required maxLength={1000} value={reason} disabled={commandLocked || !!confirmation} onChange={event => setReason(event.target.value)} />
          <label className={sheet.check}><input type="checkbox" required checked={attested} disabled={commandLocked || !!confirmation} onChange={event => setAttested(event.target.checked)} />Saya memperoleh berkas ini melalui sumber bank yang disetujui dan telah memeriksa rekening serta formatnya.</label>
          <button className={common.primary} disabled={commandLocked || !!confirmation || !preview.preview.canStage}>Tinjau sebelum menyimpan</button>
        </form></div>}
    </section>}
    {confirmation && preview && <section className={common.accountSection} aria-label="Konfirmasi impor"><h2 tabIndex={-1} ref={confirmTitle}>Simpan sumber mutasi untuk rekonsiliasi</h2><p>{confirmation.body.filename} · {preview.preview.rows.length} baris · Masuk {rupiah(preview.preview.credits)} · Keluar {rupiah(preview.preview.debits)}</p>
      <p>{chosen?.details.bankCode} · {chosen?.details.accountNumber} · {chosen?.details.holderName} · Versi {chosen?.version}</p><p>Sumber: {confirmation.body.sourceReference}</p><p>Catatan: {confirmation.body.reason}</p><p>Impor ini menyimpan berkas dan baris secara privat. Penerimaan dana tetap menunggu rekonsiliasi independen.</p>
      <div className={sheet.actions}><button className={common.primary} disabled={commandLocked} onClick={() => void work(async () => {
        const command = confirmation, operation = 'billing.bank-statement.stage';
        const ledger = new MutationLedger(() => 'bs.stage.0.' + crypto.randomUUID());
        const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, operation, operation, command, key => gateway.send(command, key, current));
        if (current()) { clearDraft(); setNotice('Impor ' + result.importId + ' tersimpan untuk rekonsiliasi. Muat daftar terbaru untuk membukanya.'); }
      })}>Simpan impor</button><button className={common.secondary} disabled={locked} onClick={() => setConfirmation(null)}>Kembali memeriksa</button></div>
    </section>}
    {view && <section className={common.accountSection} aria-label="Rincian impor"><h2 ref={title} tabIndex={-1}>Impor {view.importId}: {view.filename}</h2><p>Disiapkan untuk rekonsiliasi · {at(view.recordedAt)} · Akun {view.actorId}</p>
      <p>{view.profile.name} · {view.account.details.bankCode} · {view.account.details.accountNumber} · {view.account.details.holderName} · Versi {view.account.version}</p><p>Sumber: {view.sourceReference}</p><p>Catatan: {view.reason}</p><Parsed key={view.importId} value={view.preview} />
      <button className={common.secondary} disabled={locked} onClick={() => void work(async () => {
        const bytes = await gateway.original(account.actorId, view.importId); const digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(value => value.toString(16).padStart(2, '0')).join('');
        if (!current()) return; if (digest !== view.preview.sourceSha256) throw new AccessFailure(502, 'bank_statement_response_invalid');
        const url = URL.createObjectURL(new Blob([bytes], { type: 'application/octet-stream' })); const link = document.createElement('a'); link.href = url; link.download = 'mutasi-bank-' + view.importId + '.csv'; link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000);
      })}>Unduh berkas asli</button>
    </section>}
  </>;
}
