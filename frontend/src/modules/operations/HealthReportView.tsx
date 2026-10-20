import { formatDateTime, formatDecimal, formatNumber } from '@/lib/format';
import type { StatusTone } from '@/lib/status/dictionary';
import type { OperatorHealth, QueueHealth } from './health-client';
import { healthFindings, overallTone, waitLabel, waitedMinutes } from './health-findings';
import styles from './health.module.css';

const headline: Partial<Record<StatusTone, string>> = { success: 'Semua pemeriksaan normal', warning: 'Ada yang perlu diperhatikan', danger: 'Perlu tindakan sekarang' };
const toneLabel: Record<StatusTone, string> = { danger: 'Tindakan', warning: 'Perhatian', neutral: 'Catatan', info: 'Catatan', success: 'Normal' };

function QueueRow({ name, queue, observedAt }: { name: string; queue: QueueHealth; observedAt: string }) {
  return (
    <tr>
      <th scope="row">{name}</th>
      <td>{formatNumber(queue.pending)}</td>
      <td>{formatNumber(queue.retrying)}</td>
      <td>{formatNumber(queue.leased)}</td>
      <td data-alert={queue.dead > 0 || undefined}>{formatNumber(queue.dead)}</td>
      <td>{queue.oldestWaitingAt === null ? 'Tidak ada yang menunggu' : waitLabel(waitedMinutes(queue.oldestWaitingAt, observedAt))}</td>
    </tr>
  );
}

/** Q10 report: the overall state first, then the findings, the queues and the platform facts. */
export function HealthReportView({ report }: { report: OperatorHealth }) {
  const findings = healthFindings(report), tone = overallTone(findings);
  return (
    <div className={styles.report}>
      <section className={styles.summary} data-tone={tone} aria-labelledby="health-summary">
        <h2 id="health-summary">{headline[tone]}</h2>
        <p>Diperiksa {formatDateTime(report.observedAt, { seconds: true })}.</p>
        {findings.length > 0 && (
          <ul className={styles.findings}>
            {findings.map(finding => <li key={finding.text}><span className="status-badge" data-tone={finding.tone}>{toneLabel[finding.tone]}</span><span>{finding.text}</span></li>)}
          </ul>
        )}
      </section>
      {/* Narrow screens scroll the table sideways; the frame takes focus so keyboards can scroll it too. */}
      <div className={styles.tableFrame} role="region" aria-label="Antrean latar" tabIndex={0}>
        <table className={styles.queues}>
          <caption>Antrean latar</caption>
          <thead>
            <tr><th scope="col">Antrean</th><th scope="col">Menunggu</th><th scope="col">Dicoba ulang</th><th scope="col">Sedang diproses</th><th scope="col">Gagal</th><th scope="col">Terlama menunggu</th></tr>
          </thead>
          <tbody>
            <QueueRow name="Pekerjaan latar" queue={report.outbox} observedAt={report.observedAt} />
            <QueueRow name="OCR dokumen" queue={report.ocr} observedAt={report.observedAt} />
          </tbody>
        </table>
      </div>
      <dl className={styles.facts}>
        <div><dt>Basis data</dt><dd>{report.databaseWritable ? 'Menerima penulisan' : 'Tidak menerima penulisan'}</dd></div>
        <div><dt>Skema</dt><dd>Versi skema {report.schemaVersion} dari {report.targetSchemaVersion}</dd></div>
        <div><dt>Replikasi</dt><dd>{report.replicationLagSeconds === null ? 'Tidak tersedia' : `Tertinggal ${formatDecimal(report.replicationLagSeconds, 1)} detik`}</dd></div>
        <div><dt>Pembersihan draf terakhir</dt><dd>{report.lastDraftPurgeAt === null ? 'Belum ada draf yang dibersihkan' : formatDateTime(report.lastDraftPurgeAt)}</dd></div>
        <div><dt>Draf terlambat dibersihkan</dt><dd>{formatNumber(report.overdueDraftPurges)}</dd></div>
      </dl>
    </div>
  );
}
