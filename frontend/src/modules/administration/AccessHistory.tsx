"use client";

import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { useEffect, useState, type FormEvent } from "react";
import type { components } from "../../api/generated/schema";
import { describeScope, validReference } from "../organizations/access-scope";
import { createAccessGateway } from "./access-client";
import { AdministrationNavigation } from "./AdministrationNavigation";
import { OperationRecovery, useOperationRecovery } from "./OperationRecovery";
import { accessMessage } from "./workflow";
import styles from "./access.module.css";
import { formatDateTime } from "@/lib/format";
import { Button } from '@/components/workspace-kit/button';
import { PageHeader } from '@/components/workspace-kit/page-header';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { Field } from '@/components/workspace-kit/field';
import { Facts } from '@/components/workspace-kit/facts';
import { Pagination } from '@/components/workspace-kit/pagination';

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
    <PageHeader title="Riwayat perubahan akses" description="Periksa keputusan yang tersimpan sesuai cakupan tugas Anda. Tujuan pemeriksaan dicatat setiap kali riwayat dibuka." />
    <div className="ui-stack">
    {current && <AdministrationNavigation current="history" actions={current.allowedActions} />}
    {error !== null && current && <StateView kind="error" title={accessMessage(error)} />}
    {recovery.error !== null && <StateView kind="error" title={accessMessage(recovery.error)} />}
    {current && <OperationRecovery key={current.userId} entries={recovery.entries} journal={recovery.journal} disabled={busy} onResolved={recovery.refresh} />}
    {!current ? busy ? <StateView kind="loading" title="Memeriksa sesi" />
      : <SessionAccessState error={error} returnPath="/administration/history" onRetry={() => window.location.reload()} detail={error !== null ? accessMessage(error) : undefined} /> : <>
      <Section title="Tujuan pemeriksaan" description="Hasil menampilkan tindakan, pelaksana, alasan, dan versi data. Akun di luar cakupan tidak disertakan.">
        <form className="ui-form" onSubmit={event => void read(0, event)}><div className={styles.twoFields}>
          <Field id="history-purpose" label="Tujuan"><select value={purpose} disabled={busy} onChange={event => { setPurpose(event.target.value); setPage(null); }}>
            <option value="access-review">Tinjauan akses berkala</option>{current.allowedActions.includes("access.audit.read") && <>
              <option value="account-support">Bantuan akun</option><option value="security-investigation">Pemeriksaan keamanan</option></>}
          </select></Field>
          <Field id="history-subject" label="Referensi subjek" optional><input inputMode="numeric" autoComplete="off" maxLength={19} value={subjectId} disabled={busy}
            onChange={event => { setSubjectId(event.target.value); setPage(null); }} placeholder="Referensi akun atau unit terkait" /></Field>
        </div>
        <Button variant="primary" type="submit" disabled={busy || !!subjectId && !validReference(subjectId)} busy={busy}>Tampilkan riwayat</Button>
        </form>
      </Section>
      {busy && <StateView kind="loading" title="Memuat riwayat yang diizinkan…" />}
      {page && <Section title="Hasil pemeriksaan" id="history-results-heading" description={<>{page.total} catatan dalam cakupan dan pilihan ini.</>}>
        {page.items.length === 0 ? <StateView kind="empty" title="Tidak ada catatan yang dapat ditampilkan." /> : <ol className={styles.historyList}>
          {page.items.map(item => <li key={item.id}><div className={styles.historyHeading}><h3>{historyOperation(item.operation)}</h3><time dateTime={item.createdAt}>{formatDateTime(item.createdAt)}</time></div>
            <p>{item.scope ? describeScope(item.scope) : "Cakupan riwayat lama tidak dapat dipastikan"}</p><Facts items={[
              { term: "Pelaksana", value: <>Akun {item.actorId}</> }, { term: "Referensi subjek", value: item.subjectId },
              { term: "Data terkait", value: item.resourceId ?? "—" }, { term: "Perubahan versi", value: <>{item.beforeVersion ?? "—"} → {item.afterVersion ?? "—"}</> }]} />
            <p className={styles.preserve}>{item.reason}</p><small>Catatan {item.id}{item.status ? ` · ${item.status}` : ""}</small>
          </li>)}
        </ol>}
        <Pagination label="Halaman riwayat" start={offset + 1} end={offset + page.items.length} total={Number(page.total)} disabled={busy}
          hasPrevious={offset > 0} hasNext={page.items.length >= 25 && offset < 9975} onPrevious={() => void read(Math.max(0, offset - 25))} onNext={() => void read(offset + 25)} />
      </Section>}
    </>}
    </div>
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
