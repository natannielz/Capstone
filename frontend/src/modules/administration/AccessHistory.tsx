"use client";

import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { useEffect, useState, type FormEvent } from "react";
import Link from "next/link";
import type { components } from "../../api/generated/schema";
import { describeScope, validReference } from "../organizations/access-scope";
import { createAccessGateway } from "./access-client";
import { AdministrationNavigation } from "./AdministrationNavigation";
import { OperationRecovery, useOperationRecovery } from "./OperationRecovery";
import { accessMessage } from "./workflow";
import styles from "./access.module.css";
import { formatDateTime } from "@/lib/format";

type Schemas = components["schemas"];
export function AccessHistory() {
  const [gateway] = useState(() => createAccessGateway());
  const [current, setCurrent] = useState<Schemas["CurrentAccessDto"] | null>(null);
  const [page, setPage] = useState<Schemas["AccessHistoryPageDto"] | null>(null);
  const [purpose, setPurpose] = useState("access-review");
  const [subjectId, setSubjectId] = useState("");
  const [offset, setOffset] = useState(0);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const recovery = useOperationRecovery(current?.userId);
  useEffect(() => {
    let active = true;
    void gateway.current().then(actor => { if (active) setCurrent(actor); }, failure => { if (active) setError(failure); })
      .finally(() => { if (active) setBusy(false); });
    return () => { active = false; };
  }, [gateway]);

  async function read(nextOffset: number, event?: FormEvent) {
    event?.preventDefault();
    if (!current || subjectId && !validReference(subjectId)) return;
    setBusy(true); setError(null); setPage(null);
    try { setPage(await gateway.history({ purpose, offset: nextOffset, limit: 25, subjectId: subjectId || null }, current.userId)); setOffset(nextOffset); }
    catch (failure) { setError(failure); }
    finally { setBusy(false); }
  }

  return <main className={styles.page}>
    <header className={styles.header}><div><Link href="/identity/account" className={styles.brand}>Unit Toko</Link><h1>Riwayat perubahan akses</h1>
      <p>Periksa keputusan yang tersimpan sesuai cakupan tugas Anda. Tujuan pemeriksaan dicatat setiap kali riwayat dibuka.</p></div></header>
    {current && <AdministrationNavigation current="history" actions={current.allowedActions} />}
    {error !== null && current && <p className={styles.alert} role="alert">{accessMessage(error)}</p>}
    {recovery.error !== null && <p className={styles.alert} role="alert">{accessMessage(recovery.error)}</p>}
    {current && <OperationRecovery key={current.userId} entries={recovery.entries} journal={recovery.journal} disabled={busy} onResolved={recovery.refresh} />}
    {!current ? busy ? <section className={styles.state} aria-busy="true"><h2>Memeriksa sesi</h2></section>
      : <SessionAccessState error={error} returnPath="/administration/history" onRetry={() => window.location.reload()} detail={error !== null ? accessMessage(error) : undefined} /> : <>
      <form className={styles.accountSection} onSubmit={event => void read(0, event)}>
        <h2>Tujuan pemeriksaan</h2><div className={styles.twoFields}>
          <label>Tujuan<select value={purpose} disabled={busy} onChange={event => { setPurpose(event.target.value); setPage(null); }}>
            <option value="access-review">Tinjauan akses berkala</option>{current.allowedActions.includes("access.audit.read") && <>
              <option value="account-support">Bantuan akun</option><option value="security-investigation">Pemeriksaan keamanan</option></>}
          </select></label>
          <label>Referensi subjek <span className={styles.optional}>(opsional)</span><input inputMode="numeric" autoComplete="off" maxLength={19} value={subjectId} disabled={busy}
            onChange={event => { setSubjectId(event.target.value); setPage(null); }} placeholder="Referensi akun atau unit terkait" /></label>
        </div><p className={styles.help}>Hasil menampilkan tindakan, pelaksana, alasan, dan versi data. Akun di luar cakupan tidak disertakan.</p>
        <button className={styles.primary} disabled={busy || !!subjectId && !validReference(subjectId)}>Tampilkan riwayat</button>
      </form>
      {busy && <p role="status">Memuat riwayat yang diizinkan…</p>}
      {page && <section className={styles.accountSection} aria-labelledby="history-results-heading">
        <h2 id="history-results-heading">Hasil pemeriksaan</h2><p>{page.total} catatan dalam cakupan dan pilihan ini.</p>
        {page.items.length === 0 ? <p>Tidak ada catatan yang dapat ditampilkan.</p> : <ol className={styles.historyList}>
          {page.items.map(item => <li key={item.id}><div className={styles.historyHeading}><h3>{historyOperation(item.operation)}</h3><time dateTime={item.createdAt}>{formatDateTime(item.createdAt)}</time></div>
            <p>{item.scope ? describeScope(item.scope) : "Cakupan riwayat lama tidak dapat dipastikan"}</p><dl className={styles.facts}>
              <div><dt>Pelaksana</dt><dd>Akun {item.actorId}</dd></div><div><dt>Referensi subjek</dt><dd>{item.subjectId}</dd></div>
              <div><dt>Data terkait</dt><dd>{item.resourceId ?? "—"}</dd></div><div><dt>Perubahan versi</dt><dd>{item.beforeVersion ?? "—"} → {item.afterVersion ?? "—"}</dd></div>
            </dl><p className={styles.preserve}>{item.reason}</p><small>Catatan {item.id}{item.status ? ` · ${item.status}` : ""}</small>
          </li>)}
        </ol>}
        <nav className={styles.pagination} aria-label="Halaman riwayat"><button className={styles.secondary} disabled={busy || offset === 0} onClick={() => void read(Math.max(0, offset - 25))}>Sebelumnya</button>
          <button className={styles.secondary} disabled={busy || page.items.length < 25 || offset >= 9975} onClick={() => void read(offset + 25)}>Berikutnya</button></nav>
      </section>}
    </>}
  </main>;
}

function historyOperation(operation: string) {
  const translations: Record<string, string> = { "access.grant.proposed": "Izin akses diajukan", "access.grant.approved": "Izin akses disetujui",
    "access.grant.revoked": "Izin akses dicabut", "access.membership.proposed": "Keanggotaan diajukan", "access.membership.approved": "Keanggotaan disetujui",
    "access.membership.suspended": "Keanggotaan ditangguhkan", "access.identity.suspended": "Akun ditangguhkan", "access.scope.suspended": "Unit ditangguhkan",
    "access.proposal.rejected": "Pengajuan ditolak", "access.proposal.withdrawn": "Pengajuan ditarik", "access.operation.closed": "Permintaan ditutup setelah pemeriksaan",
    "organization.proposed": "Perubahan organisasi diajukan", "organization.approved": "Perubahan organisasi disetujui", "organization.rejected": "Perubahan organisasi ditolak", "organization.withdrawn": "Perubahan organisasi ditarik" };
  return translations[operation] ?? "Perubahan administrasi tercatat";
}
