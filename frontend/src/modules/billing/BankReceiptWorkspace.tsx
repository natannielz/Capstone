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
import { createBankStatementGateway, type StatementSummary, type StatementDetail } from './bank-statement-client';
import { createBankReceiptGateway, prepareBankReceipt, type BankReceiptCommand, type BankReceiptPreview, type BankReceiptResult, type BankReceiptView } from './bank-receipt-client';
import common from '../administration/access.module.css';
import styles from './invoice.module.css';
import sheet from './bank-statement.module.css';
import { dateTimeFormatter } from '@/lib/format';

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const rupiah = (value: string) => orderAmount('IDR', value);
const at = (value: string) => dateTimeFormatter.format(new Date(value));
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Masuk kembali untuk memeriksa penerimaan bank.';
  if (error instanceof AccessFailure) {
    const messages: Record<string, string> = {
      bank_receipt_policy_unavailable: 'Sumber pemeriksaan dan pemetaan kas belum disetujui. Hubungi penanggung jawab Finance sebelum menerima dana nyata.',
      bank_receipt_observation_mismatch: 'Hasil pemeriksaan bank berbeda dari baris mutasi. Periksa sumber; jangan mengubah isian hanya untuk membuatnya cocok.',
      independent_person_required: 'Pemeriksa harus merupakan orang terverifikasi yang berbeda dari pengimpor, termasuk akun lain milik orang yang sama.',
      maker_cannot_approve: 'Pengimpor tidak dapat mengonfirmasi dana pada berkasnya sendiri.',
      bank_receipt_identity_unresolved: 'Identitas transfer belum dapat dibuktikan unik. Minta penyelesaian sumber bank sebelum konfirmasi.',
      bank_receipt_identity_conflict: 'Identitas bank yang sama memiliki nominal, tanggal atau cakupan berbeda. Konfirmasi diblokir untuk rekonsiliasi.',
      bank_receipt_namespace_locked: 'Identitas rekening sudah terikat pada aturan rekonsiliasi lain. Penanggung jawab perlu menyelesaikannya.',
      bank_receipt_row_already_reviewed: 'Baris ini telah diperiksa. Muat sumber terbaru untuk melihat penerimaannya.',
      bank_receipt_review_changed: 'Kebijakan atau sumber berubah. Muat ulang dan periksa kembali sebelum konfirmasi.',
      bank_statement_changed: 'Format atau versi rekening telah berubah. Gunakan sumber yang terikat pada versi terbaru.',
      accounting_period_closed: 'Periode pembukuan telah ditutup. Ikuti proses koreksi periode; tanggal bank tidak boleh diganti.',
      accounting_book_unavailable: 'Buku kas belum tersedia untuk cakupan ini. Hubungi penanggung jawab pembukuan.',
      accounting_chart_unavailable: 'Pemetaan pembukuan belum tersedia. Hubungi penanggung jawab pembukuan.',
      bank_receipt_unavailable: 'Riwayat penerimaan belum dapat dipastikan lengkap. Hubungi penanggung jawab sebelum melanjutkan.',
      invalid_bank_receipt: 'Lengkapi hasil pemeriksaan bank yang tepat, sumber dan alasan.'
    };
    if (messages[error.code]) return messages[error.code];
  }
  return accessMessage(error);
}
export function BankReceiptWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [refresh, setRefresh] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setLoading(true); setAccount(null); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!current.allowedActions.includes('bank.receipt.read')) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, refresh]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRefresh(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && failure.code !== 'step_up_required' && (failure.status === 401 || failure.status === 403 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); } }, []);
  return <main className={common.page}><header className={common.header}><div><Link href="/identity/account" className={common.brand}>Unit Toko</Link><h1>Penerimaan bank</h1><p>Periksa dana masuk secara independen dan telusuri pencatatan kasnya.</p></div></header>
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun…</p> : !account || sessionTiming(account.session, elapsed).expired ? <section className={common.state}><h2>Periksa sesi dan akses</h2>
      <Link href="/identity/login?returnPath=%2Fbilling%2Fbank-receipts">Masuk kembali</Link><button className={common.secondary} onClick={() => setRefresh(value => value + 1)}>Perbarui sesi dan akses</button></section>
      : <ReceiptDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </main>;
}

function ReceiptDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createBankReceiptGateway), [statements] = useState(createBankStatementGateway), recovery = useOperationRecovery(account.actorId);
  const [imports, setImports] = useState<{ items: StatementSummary[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [receipts, setReceipts] = useState<{ items: BankReceiptView[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [view, setView] = useState<StatementDetail | null>(null), [confirmed, setConfirmed] = useState<BankReceiptResult[]>([]), [rowPage, setRowPage] = useState(0);
  const [preview, setPreview] = useState<BankReceiptPreview | null>(null), [command, setCommand] = useState<BankReceiptCommand | null>(null);
  const [accountNumber, setAccountNumber] = useState(''), [transactionId, setTransactionId] = useState(''), [bookedDate, setBookedDate] = useState(''), [amount, setAmount] = useState('');
  const [sourceReference, setSourceReference] = useState(''), [reason, setReason] = useState(''), [attested, setAttested] = useState(false);
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const alive = useRef(false), pending = useRef(false), title = useRef<HTMLHeadingElement>(null), confirmTitle = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const locked = busy || recovering || offline, commandLocked = locked || recovery.locked || !fresh, canConfirm = account.actions.includes('bank.receipt.confirm');
  useEffect(() => { alive.current = true; const online = () => setOffline(!navigator.onLine); online(); window.addEventListener('online', online); window.addEventListener('offline', online);
    return () => { alive.current = false; window.removeEventListener('online', online); window.removeEventListener('offline', online); }; }, []);
  useEffect(() => { let active = true; void Promise.resolve().then(async () => {
    if (!active || !current()) return; setBusy(true);
    try { const [sources, history] = await Promise.all([statements.list(account.actorId), gateway.list(account.actorId)]); if (active && current()) { setImports(sources); setReceipts(history); } }
    catch (failure) { if (active && current()) { setError(failure); onDenied(failure); } } finally { if (active) setBusy(false); }
  }); return () => { active = false; }; }, [gateway, statements, account.actorId, current, onDenied]);
  useEffect(() => { if (preview) title.current?.focus(); }, [preview]); useEffect(() => { if (command) confirmTitle.current?.focus(); }, [command]);
  async function work(action: () => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; recovery.refresh(); if (alive.current) setBusy(false); }
  }
  function clearReview() { setPreview(null); setCommand(null); setAccountNumber(''); setTransactionId(''); setBookedDate(''); setAmount(''); setSourceReference(''); setReason(''); setAttested(false); }
  async function open(importId: string) {
    clearReview(); setView(null); setConfirmed([]); const [source, results] = await Promise.all([statements.read(account.actorId, importId), gateway.imports(account.actorId, importId)]);
    if (current()) { setView(source); setConfirmed(results); setRowPage(0); }
  }
  return <><AdministrationNavigation current="bank-receipts" actions={account.actions} locked={busy || recovering} />
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || offline} onBusyChange={setRecovering}
      recoveryAdapters={{ 'billing.bank-receipt': { lookup: async entry => { const result = await gateway.recover(entry.actorId, entry.key); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return result; } } }}
      onFailure={onDenied} onResolved={() => { recovery.refresh(); clearReview(); setView(null); setNotice('Hasil konfirmasi telah dipastikan. Muat data terbaru untuk melihat penerimaannya.'); }} />
    {recovery.error !== null && <p role="alert">{accessMessage(recovery.error)}</p>}{offline && <p role="status" className={common.alert}>Koneksi terputus. Hasil pemeriksaan belum dikirim.</p>}
    {error !== null && <p role="alert" className={common.alert}>{message(error)}</p>}{notice && <p role="status">{notice}</p>}{busy && <p role="status">Memeriksa sumber dan penerimaan…</p>}
    {canConfirm && (!fresh || error instanceof AccessFailure && error.code === 'step_up_required') && <p><Link href="/identity/account?operation=bank.receipt.confirm&returnPath=%2Fbilling%2Fbank-receipts">Verifikasi dua langkah untuk konfirmasi bank</Link></p>}
    <section className={common.accountSection} aria-label="Sumber pemeriksaan"><h2>Pilih mutasi untuk diperiksa</h2><p>Pemeriksa harus berbeda dari pengimpor. Bandingkan dengan sumber bank independen yang disetujui; gambar pembayaran dan hasil OCR tidak membuktikan dana masuk.</p>
      <button className={common.secondary} disabled={locked} onClick={() => void work(async () => { clearReview(); setView(null); const [sources, history] = await Promise.all([statements.list(account.actorId), gateway.list(account.actorId)]); if (current()) { setImports(sources); setReceipts(history); } })}>Muat data terbaru</button>
      {!busy && !imports.items.length && <p>Belum ada sumber dalam cakupan akses Anda.</p>}<ul className={styles.candidates}>{imports.items.map(item => <li key={item.importId}><strong>{item.filename}</strong><p>{item.profileName} · {item.maskedAccount} · {item.rowCount} baris · {at(item.recordedAt)}</p>
        <button className={common.secondary} disabled={locked} onClick={() => void work(() => open(item.importId))}>Tinjau sumber {item.importId}</button></li>)}</ul>
      {imports.hasMore && <button className={common.secondary} disabled={locked} onClick={() => void work(async () => { const next = await statements.list(account.actorId, imports.items.at(-1)!.importId); if (current()) { setImports(next); setView(null); clearReview(); } })}>Sumber berikutnya</button>}
    </section>
    {view && <section className={common.accountSection} aria-label="Baris bank"><h2>{view.filename}</h2><p>{view.account.details.bankCode} · {view.account.details.accountNumber} · {view.account.details.holderName} · Versi {view.account.version}</p>
      <p>Sumber impor: {view.sourceReference} · Pengimpor: akun {view.actorId}</p><p>Referensi yang sama dapat dipakai pada transaksi berbeda. Baris tanpa identitas bank yang unik tetap menunggu penyelesaian sumber.</p>
      <ul className={styles.candidates}>{view.preview.rows.slice(rowPage * 25, (rowPage + 1) * 25).map(row => { const saved = confirmed.find(item => item.rowNumber === row.number), eligible = row.direction === 'credit' && !!row.identityCandidate && !view.preview.issues.some(issue => issue.row === row.number && issue.code === 'identity_repeated');
        return <li key={row.number}><strong>Baris {row.number} · {row.bookedDate} · {row.direction === 'credit' ? 'Masuk' : 'Keluar'} {rupiah(row.amount)}</strong><p>Identitas bank: {row.transactionId ?? 'belum tersedia'} · Referensi: {row.reference ?? 'tidak tersedia'} · Pengirim: {row.payerName ?? 'belum diketahui'}</p>
          {saved ? <p role="status">Terhubung ke penerimaan {saved.receiptId}. {saved.createdReceipt ? 'Kas dicatat sekali pada pemeriksaan ini.' : 'Penerimaan sudah ada; kas tidak dicatat ulang.'}</p> : <>
            {!eligible && <p>Belum memenuhi syarat konfirmasi kredit; perlu penyelesaian sumber.</p>}{canConfirm && eligible && <button className={common.secondary} disabled={commandLocked || view.actorId === account.actorId} onClick={() => void work(async () => { clearReview(); const shown = await gateway.preview(account.actorId, view.importId, row.number); if (current()) setPreview(shown); })}>Periksa kredit baris {row.number}</button>}
            {canConfirm && eligible && view.actorId === account.actorId && <p>Minta orang lain yang berwenang memeriksa impor Anda.</p>}</>}</li>;
      })}</ul><div className={sheet.actions}><button className={common.secondary} disabled={locked || rowPage === 0} onClick={() => setRowPage(value => value - 1)}>Baris sebelumnya</button><span>Halaman {rowPage + 1} dari {Math.ceil(view.preview.rows.length / 25)}</span><button className={common.secondary} disabled={locked || (rowPage + 1) * 25 >= view.preview.rows.length} onClick={() => setRowPage(value => value + 1)}>Baris berikutnya</button></div>
    </section>}
    {preview && <section className={common.accountSection} aria-label="Pemeriksaan independen"><h2 ref={title} tabIndex={-1}>Periksa kredit dengan sumber bank</h2><p>Baris {preview.rowNumber} · {preview.row.bookedDate} · {rupiah(preview.row.amount)} · Identitas {preview.row.transactionId}</p>
      <div className={sheet.source}><strong>Kanal pemeriksaan: {preview.policy.trustedSourceReference}</strong><p>Penanggung jawab: {preview.policy.operatingOwnerReference}</p><p>Persetujuan: {preview.policy.approvalReference} · Berlaku hingga {at(preview.policy.expiresAt)}</p><p>Pemetaan buku: kas {preview.policy.cashMapping} → dana belum teralokasi {preview.policy.suspenseMapping}.</p></div>
      {preview.existingReceiptId ? <p role="status">Transfer ini sudah memiliki penerimaan {preview.existingReceiptId}. Pemeriksaan ini menambahkan tautan sumber tanpa mencatat kas lagi.</p> : <p>Konfirmasi akan mencatat penerimaan kas ke dana belum teralokasi. Identitas pembayar dan pelunasan tagihan memerlukan pemeriksaan terpisah.</p>}
      <form className={sheet.form} onSubmit={event => { event.preventDefault(); if (commandLocked) return; try { setError(null); setCommand(prepareBankReceipt(account.actorId, preview, { accountNumber, transactionId, bookedDate, amount }, sourceReference.trim(), attested, reason.trim())); } catch (failure) { setError(failure); } }}>
        <p>Masukkan hasil yang Anda baca dari kanal bank tersebut. Isian tidak diisi otomatis dari impor.</p>
        <label htmlFor="receipt-account">Nomor rekening pada sumber bank</label><input id="receipt-account" required inputMode="numeric" maxLength={34} value={accountNumber} disabled={commandLocked || !!command} onChange={event => setAccountNumber(event.target.value)} />
        <label htmlFor="receipt-transaction">Identitas transaksi bank yang diamati</label><input id="receipt-transaction" required maxLength={80} value={transactionId} disabled={commandLocked || !!command} onChange={event => setTransactionId(event.target.value)} />
        <label htmlFor="receipt-date">Tanggal pembukuan bank</label><input id="receipt-date" required type="date" value={bookedDate} disabled={commandLocked || !!command} onChange={event => setBookedDate(event.target.value)} />
        <label htmlFor="receipt-amount">Nominal masuk IDR</label><input id="receipt-amount" required inputMode="decimal" placeholder="1000.25" aria-describedby="receipt-amount-help" maxLength={18} value={amount} disabled={commandLocked || !!command} onChange={event => setAmount(event.target.value)} /><small id="receipt-amount-help">Gunakan titik desimal dan dua angka desimal, tanpa pemisah ribuan.</small>
        <label htmlFor="receipt-source">Referensi pemeriksaan bank independen</label><input id="receipt-source" required maxLength={256} value={sourceReference} disabled={commandLocked || !!command} onChange={event => setSourceReference(event.target.value)} />
        <label htmlFor="receipt-reason">Catatan keputusan</label><textarea id="receipt-reason" required maxLength={1000} value={reason} disabled={commandLocked || !!command} onChange={event => setReason(event.target.value)} />
        <label className={sheet.check}><input type="checkbox" required checked={attested} disabled={commandLocked || !!command} onChange={event => setAttested(event.target.checked)} />Saya memeriksa kredit aktual melalui kanal bank yang disetujui, secara independen dari pengimpor.</label>
        <button className={common.primary} disabled={commandLocked || !!command}>Tinjau keputusan bank</button>
      </form>
    </section>}
    {command && preview && <section className={common.accountSection} aria-label="Konfirmasi penerimaan"><h2 ref={confirmTitle} tabIndex={-1}>Konfirmasi hasil pemeriksaan</h2><p>{rupiah(command.body.observed.amount)} · {command.body.observed.bookedDate} · {command.body.observed.transactionId}</p><p>Rekening {command.body.observed.accountNumber} · Sumber {command.body.sourceReference}</p><p>{command.body.reason}</p>
      <p>{preview.existingReceiptId ? 'Tautkan sumber ke penerimaan yang sudah ada; tidak ada kas tambahan.' : 'Catat kas sekali ke dana belum teralokasi.'} Tagihan belum dilunasi oleh tindakan ini.</p>
      <div className={sheet.actions}><button className={common.primary} disabled={commandLocked} onClick={() => void work(async () => {
        const frozen = command, operation = 'billing.bank-receipt.confirm'; const ledger = new MutationLedger(() => `br.confirm.${frozen.body.importId}.${frozen.body.rowNumber}.${crypto.randomUUID()}`);
        const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, operation, operation, frozen, key => gateway.send(frozen, key, current));
        if (current()) { clearReview(); setView(null); setNotice(`Penerimaan ${result.receiptId} telah dipastikan. ${result.createdReceipt ? 'Kas tercatat sekali.' : 'Sumber terhubung tanpa kas tambahan.'} Muat data terbaru.`); }
      })}>Konfirmasi kredit bank</button><button className={common.secondary} disabled={locked} onClick={() => setCommand(null)}>Kembali memeriksa</button></div>
    </section>}
    <section className={common.accountSection} aria-label="Riwayat penerimaan"><h2>Penerimaan yang dikonfirmasi</h2><p>Riwayat kas mempertahankan identitas bank dan sumber pertamanya. Konfirmasi ini tidak menetapkan pembayar atau mengurangi tagihan.</p>
      {!busy && !receipts.items.length && <p>Belum ada penerimaan dalam cakupan akses Anda.</p>}<ul className={styles.candidates}>{receipts.items.map(item => <li key={item.receiptId}><strong>Penerimaan {item.receiptId} · {rupiah(item.amount)}</strong><p>{item.bookedDate} · Identitas bank {item.transactionId} · Jurnal {item.journalId}</p><p>Diperiksa {at(item.recordedAt)} oleh akun {item.actorId}</p><button className={common.secondary} disabled={locked} onClick={() => void work(() => open(item.importId))}>Lihat sumber penerimaan {item.receiptId}</button></li>)}</ul>
      {receipts.hasMore && <button className={common.secondary} disabled={locked} onClick={() => void work(async () => { const result = await gateway.list(account.actorId, receipts.items.at(-1)!.receiptId); if (current()) setReceipts(result); })}>Penerimaan berikutnya</button>}
    </section>
  </>;
}
