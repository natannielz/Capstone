import type { BranchRequest, BranchRequestPreview } from './branch-request-client';
import { branchRequestNumber, requestStateLabel } from './branch-request-state';
import { Badge } from '@/components/workspace-kit/badge';
import { Facts } from '@/components/workspace-kit/facts';
import styles from './branch-request.module.css';
import { dateTimeFormatter, formatNumber } from '@/lib/format';

export const requestDate = (value: string) => dateTimeFormatter.format(new Date(value));
export function BranchRequestFacts({ value }: { value: BranchRequest | BranchRequestPreview }) {
  return <>
    {'number' in value && <div className={styles.row}><h2>{branchRequestNumber(value.id)}</h2><Badge tone={value.state === 'accepted' ? 'success' : value.state === 'rejected' ? 'danger' : 'neutral'} label={requestStateLabel(value.state)} /></div>}
    <Facts items={[{ term: 'Cabang pemesan', value: <>{value.labels.buyingOrganization} · {value.labels.branch}{value.labels.division && ' · ' + value.labels.division}</> },
      { term: 'Toko tujuan', value: <>{value.labels.shop} · {value.labels.channel}</> },
      ...'recordedAt' in value ? [{ term: 'Diajukan pada', value: requestDate(value.recordedAt) }] : []]} />
    <h3>Kemasan yang diminta</h3><ul className={styles.list}>{value.lines.map(line => <li className={styles.item} key={line.skuId}><strong>{line.familyName} — {line.packageLabel}</strong><small>{line.skuCode}</small>
      <p>{formatNumber(line.packages)} {line.packageLabel} · isi {formatNumber(line.baseUnitsPerPackage)} {line.baseUnitCode} per kemasan</p>
      <p>Total {formatNumber(BigInt(line.packages) * BigInt(line.baseUnitsPerPackage))} {line.baseUnitCode}</p></li>)}</ul>
    {value.input.note && <><h3>Catatan pemesan</h3><p className={styles.note}>{value.input.note}</p></>}
    {'decision' in value && value.decision && <div className={styles.scope}><h3>Hasil peninjauan toko</h3><p>{value.decision.action === 'accept' ? 'Permintaan diterima untuk ditindaklanjuti.' : 'Permintaan ditolak oleh toko.'}</p><p className={styles.note}>{value.decision.reason}</p><p>{requestDate(value.decision.recordedAt)}</p></div>}
  </>;
}
