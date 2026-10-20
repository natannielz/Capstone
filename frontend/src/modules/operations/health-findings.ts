import type { StatusTone } from '@/lib/status/dictionary';
import type { OperatorHealth, QueueHealth } from './health-client';

export type HealthFinding = { tone: StatusTone; text: string };

// Background work normally finishes within seconds: ten minutes of waiting needs a look, an hour is an incident.
// The same reading applies to replica lag in seconds. Docs: docs/runbooks/operations-health.md.
const WAIT_WARNING_MINUTES = 10, WAIT_DANGER_MINUTES = 60, LAG_WARNING_SECONDS = 10, LAG_DANGER_SECONDS = 60;

export function waitLabel(minutes: number): string {
  if (minutes < 60) return `${minutes} menit`;
  return minutes < 48 * 60 ? `${Math.floor(minutes / 60)} jam` : `${Math.floor(minutes / 1440)} hari`;
}

export function waitedMinutes(since: string, observedAt: string): number {
  return Math.max(0, Math.floor((Date.parse(observedAt) - Date.parse(since)) / 60_000));
}

function queueFindings(name: string, queue: QueueHealth, observedAt: string): HealthFinding[] {
  const findings: HealthFinding[] = [];
  if (queue.dead > 0) findings.push({ tone: 'danger', text: `${queue.dead} pekerjaan ${name} gagal dan perlu ditangani petugas teknis.` });
  if (queue.retrying > 0) findings.push({ tone: 'warning', text: `${queue.retrying} pekerjaan ${name} sedang dicoba ulang.` });
  if (queue.oldestWaitingAt !== null) {
    const minutes = waitedMinutes(queue.oldestWaitingAt, observedAt);
    if (minutes >= WAIT_WARNING_MINUTES)
      findings.push({ tone: minutes >= WAIT_DANGER_MINUTES ? 'danger' : 'warning', text: `Pekerjaan ${name} tertua sudah menunggu ${waitLabel(minutes)}.` });
  }
  return findings;
}

/** Plain findings, most serious first. A healthy report has none (an unavailable replica status is only noted). */
export function healthFindings(report: OperatorHealth): HealthFinding[] {
  const findings: HealthFinding[] = [];
  if (!report.databaseWritable) findings.push({ tone: 'danger', text: 'Basis data tidak menerima penulisan. Periksa node utama replika.' });
  if (report.schemaVersion < report.targetSchemaVersion)
    findings.push({ tone: 'danger', text: `Migrasi belum selesai: versi skema ${report.schemaVersion} dari ${report.targetSchemaVersion}.` });
  if (report.schemaVersion > report.targetSchemaVersion)
    findings.push({ tone: 'danger', text: `Basis data memakai versi skema ${report.schemaVersion}, lebih baru dari aplikasi (${report.targetSchemaVersion}).` });
  findings.push(...queueFindings('latar', report.outbox, report.observedAt), ...queueFindings('OCR', report.ocr, report.observedAt));
  if (report.overdueDraftPurges > 0)
    findings.push({ tone: 'warning', text: `${report.overdueDraftPurges} draf pesanan melewati masa pemulihan lebih dari sehari dan belum dibersihkan.` });
  const lag = report.replicationLagSeconds;
  if (lag === null) findings.push({ tone: 'neutral', text: 'Status replikasi tidak tersedia untuk akun basis data ini.' });
  else if (lag >= LAG_WARNING_SECONDS)
    findings.push({ tone: lag >= LAG_DANGER_SECONDS ? 'danger' : 'warning', text: `Replika tertinggal ${Math.round(lag)} detik dari node utama.` });
  const rank = (tone: StatusTone) => tone === 'danger' ? 0 : tone === 'warning' ? 1 : 2;
  return findings.sort((a, b) => rank(a.tone) - rank(b.tone));
}

/** The page state: the worst finding, or success when nothing needs attention. */
export function overallTone(findings: readonly HealthFinding[]): StatusTone {
  if (findings.some(finding => finding.tone === 'danger')) return 'danger';
  return findings.some(finding => finding.tone === 'warning') ? 'warning' : 'success';
}
