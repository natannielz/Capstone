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
import sheet from './bank-statement.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { DataList } from '@/components/workspace-kit/data-list';
import { DataTable } from '@/components/workspace-kit/data-table';
import { Field } from '@/components/workspace-kit/field';
import { Facts } from '@/components/workspace-kit/facts';

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
  return <main className={common.page}><PageHeader title="Mutasi bank" description="Siapkan sumber bank privat untuk pemeriksaan dan rekonsiliasi independen." /><div className="ui-stack">
    {error !== null && account !== null && <StateView kind="error" title={message(error)} />}
    {loading ? <StateView kind="loading" title="Memeriksa akun…" /> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState kind={account ? "expired" : undefined} error={error} returnPath="/billing/bank-statements" onRetry={() => setRefresh(value => value + 1)} />
      : <StatementDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </div></main>;
}
type StatementRow = StatementParsed['rows'][number];
function Parsed({ value }: { value: StatementParsed }) {
  const [page, setPage] = useState(0), [issueLimit, setIssueLimit] = useState(25);
  const rows = value.rows.slice(page * 25, (page + 1) * 25);
  return <><Facts items={[{ term: 'Baris terbaca', value: value.rows.length }, { term: 'Total masuk', value: rupiah(value.credits) }, { term: 'Total keluar', value: rupiah(value.debits) }, { term: 'Temuan', value: value.issues.length }]} />
    <p>Total ini berasal dari berkas. Impor belum mengonfirmasi penerimaan dana atau mengurangi tagihan.</p>
    {!value.canStage && <StateView kind="error" title="Perbaiki sumber atau pilihan format sebelum menyimpan. Baris bermasalah tidak boleh dilewati." />}
    {value.issues.length > 0 && <section aria-label="Temuan mutasi"><h3>Perlu diperiksa</h3><ul>{value.issues.slice(0, issueLimit).map((issue, index) => <li key={index}>Baris {issue.row}: {issues[issue.code] ?? 'Periksa data sumber.'}</li>)}</ul>
      {issueLimit < value.issues.length && <Button variant="secondary" type="button" onClick={() => setIssueLimit(value => value + 25)}>Lihat temuan berikutnya</Button>}</section>}
    {/* A DataTable: labelled cards below 640 px, so the rows never scroll sideways on a phone (U-012). */}
    <div role="region" aria-label="Rincian baris mutasi"><DataTable<StatementRow> caption="Transaksi pada berkas sumber" rows={rows} getKey={row => String(row.number)} columns={[
      { key: 'row', header: 'Baris / tanggal', render: row => <>{row.number}<br />{row.bookedDate}</> },
      { key: 'amount', header: 'Arah / nominal', align: 'end', render: row => <>{row.direction === 'credit' ? 'Masuk' : 'Keluar'}<br /><strong>{rupiah(row.amount)}</strong></> },
      { key: 'payer', header: 'Pengirim', render: row => row.payerName ?? 'Belum diketahui' },
      { key: 'identity', header: 'Identitas / referensi bank', render: row => <>{row.transactionId ?? 'Identitas belum tersedia'}<br />{row.reference ?? 'Tanpa referensi'}<small className={sheet.note}>{row.identityCandidate ? 'Kandidat identitas; belum dikonfirmasi' : 'Memerlukan rekonsiliasi manual'}</small></> },
    ]} /></div>
    <div className={sheet.pager}><Button variant="secondary" type="button" disabled={page === 0} onClick={() => setPage(value => value - 1)}>Baris sebelumnya</Button><span>Halaman {page + 1} dari {Math.max(1, Math.ceil(value.rows.length / 25))}</span><Button variant="secondary" type="button" disabled={(page + 1) * 25 >= value.rows.length} onClick={() => setPage(value => value + 1)}>Baris berikutnya</Button></div></>;
}
function StatementDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createBankStatementGateway), recovery = useOperationRecovery(account.actorId);
  const [page, setPage] = useState<{ items: StatementSummary[]; hasMore: boolean }>({ items: [], hasMore: false }), [profiles, setProfiles] = useState<{ items: StatementProfile[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [accounts, setAccounts] = useState<{ items: StatementAccount[]; hasMore: boolean }>({ items: [], hasMore: false }), [profileId, setProfileId] = useState(''), [versionId, setVersionId] = useState('');
  const [file, setFile] = useState<{ name: string; base64: string } | null>(null), [source, setSource] = useState(''), [reason, setReason] = useState(''), [attested, setAttested] = useState(false);
  const [preview, setPreview] = useState<StatementPreview | null>(null), [confirmation, setConfirmation] = useState<StatementCommand | null>(null), [draft, setDraft] = useState(false), [view, setView] = useState<StatementDetail | null>(null);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), pending = useRef(false), title = useRef<HTMLHeadingElement>(null), confirmTitle = useRef<HTMLHeadingElement>(null);
  // A failed first read (not a failed command) replaces the list with one error body (U-036).
  const [readFailed, setReadFailed] = useState(false);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const canStage = account.actions.includes('bank.statement.stage'), locked = busy || recovering || offline, commandLocked = locked || recovery.locked || !fresh;
  const profile = profiles.items.find(item => item.id === profileId), chosen = accounts.items.find(item => item.versionId === versionId);
  const selected = (): StatementSelection | null => profile && chosen ? { profileId: profile.id, profileRevision: profile.revision, accountId: chosen.accountId, accountVersionId: chosen.versionId } : null;
  useEffect(() => { alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; void Promise.resolve().then(async () => {
    if (!active || !current()) return; setBusy(true);
    try { const result = await gateway.list(account.actorId); if (active && current()) setPage(result); }
    catch (failure) { if (active && current()) { setError(failure); setReadFailed(true); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; }; }, [gateway, account.actorId, current, onDenied]);
  useEffect(() => { if (view) title.current?.focus(); }, [view]); useEffect(() => { if (confirmation) confirmTitle.current?.focus(); }, [confirmation]);
  async function work(action: () => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; recovery.refresh(); if (alive.current) setBusy(false); }
  }
  function clearDraft() { setDraft(false); setFile(null); setPreview(null); setConfirmation(null); setProfileId(''); setVersionId(''); setSource(''); setReason(''); setAttested(false); }
  async function load(afterId: string | null = null) { setView(null); clearDraft(); const result = await gateway.list(account.actorId, afterId); if (current()) { setPage(result); setReadFailed(false); } }
  async function open(id: string) { setView(null); clearDraft(); const result = await gateway.read(account.actorId, id); if (current()) setView(result); }
  return <><AdministrationNavigation current="bank-statements" actions={account.actions} locked={busy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'billing.bank-statement': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } } }}
      onFailure={onDenied} onResolved={() => { recovery.refresh(); clearDraft(); setView(null); setNotice('Hasil impor telah dipastikan. Muat daftar terbaru.'); }} />
    {recovery.error !== null && <StateView kind="error" title={accessMessage(recovery.error)} />}{offline && <StateView kind="offline" title="Koneksi terputus. Sambungkan kembali sebelum memeriksa atau menyimpan berkas." />}
    {error !== null && !readFailed && <StateView kind="error" title={message(error)} />}{notice && <p role="status">{notice}</p>}{busy && <p role="status">Memproses mutasi bank…</p>}
    {canStage && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p><Link href="/identity/account?operation=bank.statement.stage&returnPath=%2Fbilling%2Fbank-statements">Verifikasi dua langkah untuk impor mutasi</Link></p>}
    <Section title="Sumber mutasi tersimpan" label="Daftar impor bank" description="Setiap berkas mempertahankan sumber asli dan versi rekening yang dipilih."
      actions={<><Button variant="secondary" disabled={locked} onClick={() => void work(() => load())}>Muat daftar terbaru</Button>
        {canStage && <Button variant="primary" disabled={commandLocked} onClick={() => void work(async () => { clearDraft(); setView(null); const result = await gateway.profiles(account.actorId); if (current()) { setProfiles(result); setAccounts({ items: [], hasMore: false }); setDraft(true); } })}>Siapkan impor mutasi</Button>}</>}>
      {readFailed ? <StateView kind="error" title={message(error)} action={<Button disabled={locked} onClick={() => void work(() => load())}>Coba lagi</Button>} />
        : <DataList label="Sumber mutasi tersimpan" items={page.items} getKey={item => item.importId} loading={busy && !page.items.length} empty={{ title: 'Belum ada impor dalam cakupan akses Anda.' }}
          renderTitle={item => item.filename} renderMeta={item => <>{item.profileName} · {item.maskedAccount} · {item.rowCount} baris · Masuk {rupiah(item.credits)} · Keluar {rupiah(item.debits)} · {item.issueCount} temuan</>}
          renderContent={item => <p className={sheet.note}>Disiapkan untuk rekonsiliasi · Akun {item.actorId} · {at(item.recordedAt)}</p>}
          renderActions={item => <Button variant="secondary" disabled={locked} onClick={() => void work(() => open(item.importId))}>Periksa impor {item.importId}</Button>} />}
      {page.hasMore && <Button variant="secondary" disabled={locked} onClick={() => void work(() => load(page.items.at(-1)!.importId))}>Impor berikutnya</Button>}
    </Section>
    {draft && <Section title="Pilih sumber dan rekening" label="Siapkan impor bank" description="Ambil ekspor asli melalui sumber bank yang telah disetujui. Jangan mengganti rekening, nominal atau identitas transfer agar cocok dengan pengajuan pembayaran.">
      <form className="ui-form" onSubmit={event => { event.preventDefault(); const selection = selected(); if (selection && file) void work(async () => { setPreview(null); setConfirmation(null); const result = await gateway.preview(account.actorId, selection, file.base64); if (current()) setPreview(result); }); }}>
        <Field id="statement-profile" label="Format ekspor bank"><select required disabled={commandLocked || !!confirmation} value={profileId} onChange={event => { const id = event.target.value; void work(async () => { setProfileId(id); setVersionId(''); setPreview(null); setAccounts({ items: [], hasMore: false }); const next = profiles.items.find(item => item.id === id); if (next) { const result = await gateway.accounts(account.actorId, next); if (current()) setAccounts(result); } }); }}>
          <option value="">Pilih format yang disetujui</option>{profiles.items.map(item => <option key={item.id} value={item.id} disabled={!item.active}>{item.name} · {item.id}/{item.revision}{!item.active ? ' (nonaktif)' : ''}</option>)}</select></Field>
        {!profiles.items.length && <p role="status">Belum ada format dan sumber bank yang disetujui. Penanggung jawab Finance perlu menetapkannya sebelum impor nyata.</p>}
        {profiles.hasMore && <Button variant="secondary" type="button" disabled={locked} onClick={() => void work(async () => { const result = await gateway.profiles(account.actorId, profiles.items.at(-1)!.id); if (current()) setProfiles({ items: [...profiles.items, ...result.items], hasMore: result.hasMore }); })}>Format berikutnya</Button>}
        {profile && <div className={sheet.source}><strong>Sumber bank: {profile.trustedSourceReference}</strong><p>Penanggung jawab: {profile.operatingOwnerReference}</p><p>Persetujuan: {profile.approvalReference} · Berlaku hingga {at(profile.expiresAt)}</p><p>Kolom berurutan: {profile.format.columns.join(' · ')}</p><p>Tanggal {profile.format.dateFormat}; pemisah kolom {profile.format.delimiter === '\t' ? 'tab' : profile.format.delimiter}; tanda desimal {profile.format.decimalSeparator}.</p></div>}
        <Field id="statement-account" label="Rekening penerimaan dan versi"><select required disabled={commandLocked || !!confirmation || !profile} value={versionId} onChange={event => { setVersionId(event.target.value); setPreview(null); }}>
          <option value="">Pilih rekening secara eksplisit</option>{accounts.items.map(item => <option key={item.versionId} value={item.versionId}>{item.details.bankCode} · {item.details.accountNumber} · {item.details.holderName} · Versi {item.version}</option>)}</select></Field>
        {profile && !accounts.items.length && !busy && <p>Belum ada rekening yang disetujui untuk format dan cakupan ini.</p>}
        {profile && accounts.hasMore && <Button variant="secondary" type="button" disabled={locked} onClick={() => void work(async () => { const result = await gateway.accounts(account.actorId, profile, accounts.items.at(-1)!.accountId); if (current()) setAccounts({ items: [...accounts.items, ...result.items], hasMore: result.hasMore }); })}>Rekening berikutnya</Button>}
        <Field id="statement-file" label="Berkas mutasi asli" help="UTF-8, maksimal 256 KiB dan 500 baris. Berkas tetap di memori halaman sampai Anda menyimpan impor."><input type="file" accept=".csv,.tsv,.txt,text/csv,text/tab-separated-values,text/plain" disabled={commandLocked || !!confirmation} onChange={event => { const selectedFile = event.target.files?.[0]; setFile(null); setPreview(null); if (selectedFile) void work(async () => { const base64 = await statementFile(selectedFile); if (current()) setFile({ name: selectedFile.name, base64 }); }); }} /></Field>
        <Button variant="secondary" type="submit" disabled={commandLocked || !!confirmation || !selected() || !file}>Periksa isi berkas</Button>
      </form>
      {preview && <div className={sheet.preview}><h3>Pratinjau {file?.name}</h3><Parsed key={preview.binding} value={preview.preview} />
        <form className="ui-form" onSubmit={event => { event.preventDefault(); const selection = selected(); if (commandLocked || !selection || !file) return; try { setError(null); setConfirmation(prepareStatement(account.actorId, selection, file.base64, file.name, preview, source.trim(), attested, reason.trim())); } catch (failure) { setError(failure); } }}>
          <Field id="statement-source" label="Referensi sumber bank"><input required maxLength={256} value={source} disabled={commandLocked || !!confirmation} onChange={event => setSource(event.target.value)} /></Field>
          <Field id="statement-reason" label="Catatan impor"><textarea required maxLength={1000} value={reason} disabled={commandLocked || !!confirmation} onChange={event => setReason(event.target.value)} /></Field>
          <label className={sheet.check}><input type="checkbox" required checked={attested} disabled={commandLocked || !!confirmation} onChange={event => setAttested(event.target.checked)} />Saya memperoleh berkas ini melalui sumber bank yang disetujui dan telah memeriksa rekening serta formatnya.</label>
          <Button variant="primary" type="submit" disabled={commandLocked || !!confirmation || !preview.preview.canStage}>Tinjau sebelum menyimpan</Button>
        </form></div>}
    </Section>}
    {confirmation && preview && <section className="ui-section" aria-label="Konfirmasi impor"><h2 tabIndex={-1} ref={confirmTitle}>Simpan sumber mutasi untuk rekonsiliasi</h2><p>{confirmation.body.filename} · {preview.preview.rows.length} baris · Masuk {rupiah(preview.preview.credits)} · Keluar {rupiah(preview.preview.debits)}</p>
      <p>{chosen?.details.bankCode} · {chosen?.details.accountNumber} · {chosen?.details.holderName} · Versi {chosen?.version}</p><p>Sumber: {confirmation.body.sourceReference}</p><p>Catatan: {confirmation.body.reason}</p><p>Impor ini menyimpan berkas dan baris secara privat. Penerimaan dana tetap menunggu rekonsiliasi independen.</p>
      <div className="ui-actions"><Button variant="primary" disabled={commandLocked} onClick={() => void work(async () => {
        const command = confirmation, operation = 'billing.bank-statement.stage';
        const ledger = new MutationLedger(() => 'bs.stage.0.' + crypto.randomUUID());
        const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, operation, operation, command, key => gateway.send(command, key, current));
        if (current()) { clearDraft(); setNotice('Impor ' + result.importId + ' tersimpan untuk rekonsiliasi. Muat daftar terbaru untuk membukanya.'); }
      })}>Simpan impor</Button><Button variant="secondary" disabled={locked} onClick={() => setConfirmation(null)}>Kembali memeriksa</Button></div>
    </section>}
    {view && <section className="ui-section" aria-label="Rincian impor"><h2 ref={title} tabIndex={-1}>Impor {view.importId}: {view.filename}</h2><p>Disiapkan untuk rekonsiliasi · {at(view.recordedAt)} · Akun {view.actorId}</p>
      <p>{view.profile.name} · {view.account.details.bankCode} · {view.account.details.accountNumber} · {view.account.details.holderName} · Versi {view.account.version}</p><p>Sumber: {view.sourceReference}</p><p>Catatan: {view.reason}</p><Parsed key={view.importId} value={view.preview} />
      <Button variant="secondary" disabled={locked} onClick={() => void work(async () => {
        const bytes = await gateway.original(account.actorId, view.importId); const digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(value => value.toString(16).padStart(2, '0')).join('');
        if (!current()) return; if (digest !== view.preview.sourceSha256) throw new AccessFailure(502, 'bank_statement_response_invalid');
        const url = URL.createObjectURL(new Blob([bytes], { type: 'application/octet-stream' })); const link = document.createElement('a'); link.href = url; link.download = 'mutasi-bank-' + view.importId + '.csv'; link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000);
      })}>Unduh berkas asli</Button>
    </section>}
  </>;
}
