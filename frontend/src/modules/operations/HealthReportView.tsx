import { Badge } from '@/components/workspace-kit/badge';
import { DataTable } from '@/components/workspace-kit/data-table';
import { Facts } from '@/components/workspace-kit/facts';
import { Section } from '@/components/workspace-kit/section';
import { formatDateTime, formatDecimal, formatNumber } from '@/lib/format';
import type { StatusTone } from '@/lib/status/dictionary';
import type { OperatorHealth, QueueHealth } from './health-client';
import { healthFindings, overallTone, waitLabel, waitedMinutes } from './health-findings';
import styles from './health.module.css';

const headline: Partial<Record<StatusTone, string>> = { success: 'Semua pemeriksaan normal', warning: 'Ada yang perlu diperhatikan', danger: 'Perlu tindakan sekarang' };
const toneLabel: Record<StatusTone, string> = { danger: 'Tindakan', warning: 'Perhatian', neutral: 'Catatan', info: 'Catatan', success: 'Normal' };

type QueueRow = { name: string; queue: QueueHealth };

/** Q10 report: the overall state first, then the findings, the queues and the platform facts. */
export function HealthReportView({ report }: { report: OperatorHealth }) {
  const findings = healthFindings(report), tone = overallTone(findings);
  const queues: QueueRow[] = [{ name: 'Pekerjaan latar', queue: report.outbox }, { name: 'OCR dokumen', queue: report.ocr }];
  return (
    <div className="ui-stack">
      <Section title={headline[tone] ?? ''} id="health-summary" description={<>Diperiksa {formatDateTime(report.observedAt, { seconds: true })}.</>}>
        {findings.length > 0 && (
          <ul className={styles.findings}>
            {findings.map(finding => <li key={finding.text}><Badge tone={finding.tone} label={toneLabel[finding.tone]} /><span>{finding.text}</span></li>)}
          </ul>
        )}
      </Section>
      <Section title="Antrean latar" id="health-queues" variant="plain">
        <DataTable caption="Jumlah pekerjaan per antrean" rows={queues} getKey={row => row.name}
          columns={[
            { key: 'name', header: 'Antrean', render: row => row.name },
            { key: 'pending', header: 'Menunggu', align: 'end', render: row => formatNumber(row.queue.pending) },
            { key: 'retrying', header: 'Dicoba ulang', align: 'end', render: row => formatNumber(row.queue.retrying) },
            { key: 'leased', header: 'Sedang diproses', align: 'end', render: row => formatNumber(row.queue.leased) },
            { key: 'dead', header: 'Gagal', align: 'end', render: row => row.queue.dead > 0 ? <span className={styles.alertCell}>{formatNumber(row.queue.dead)}</span> : formatNumber(row.queue.dead) },
            { key: 'oldest', header: 'Terlama menunggu', align: 'end', render: row => row.queue.oldestWaitingAt === null ? 'Tidak ada yang menunggu' : waitLabel(waitedMinutes(row.queue.oldestWaitingAt, report.observedAt)) },
          ]} />
      </Section>
      <Section title="Basis data dan skema" id="health-platform">
        <Facts items={[
          { term: 'Basis data', value: report.databaseWritable ? 'Menerima penulisan' : 'Tidak menerima penulisan' },
          { term: 'Skema', value: `Versi skema ${report.schemaVersion} dari ${report.targetSchemaVersion}` },
          { term: 'Replikasi', value: report.replicationLagSeconds === null ? 'Tidak tersedia' : `Tertinggal ${formatDecimal(report.replicationLagSeconds, 1)} detik` },
          { term: 'Pembersihan draf terakhir', value: report.lastDraftPurgeAt === null ? 'Belum ada draf yang dibersihkan' : formatDateTime(report.lastDraftPurgeAt) },
          { term: 'Draf terlambat dibersihkan', value: formatNumber(report.overdueDraftPurges) },
        ]} />
      </Section>
    </div>
  );
}
