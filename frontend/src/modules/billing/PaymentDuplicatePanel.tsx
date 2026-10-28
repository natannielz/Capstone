'use client';

import { useEffect, useRef, useState } from 'react';
import { accessMessage } from '../administration/workflow';
import type { PaymentDraft } from './payment-client';
import { createPaymentReviewGateway, type PaymentDuplicatePage } from './payment-review-client';
import styles from './invoice.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { autoLoadEndsSession } from './payment-review-session';
import { Button } from '@/components/workspace-kit/button';

export function PaymentDuplicatePanel({ actorId, owner, locked, current, onOpen, onDenied, onBusyChange }: {
  actorId: string; owner: PaymentDraft; locked: boolean; current: () => boolean; onOpen: (id: string) => void;
  onDenied: (error: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createPaymentReviewGateway), [page, setPage] = useState<PaymentDuplicatePage | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  // Focus moves to the results only after a reload the reviewer asked for, never on the automatic load at open.
  const requested = useRef(false);
  useEffect(() => { if (page && requested.current) heading.current?.focus(); }, [page]);
  async function load(before: string | null, automatic = false) {
    if (locked || pending.current || !current()) return;
    pending.current = true; requested.current = !automatic; setBusy(true); if (!automatic) onBusyChange(true); setError(null); setPage(null);
    try { const result = await gateway.duplicates(actorId, owner, before); if (alive.current && current()) setPage(result); }
    catch (failure) { if (alive.current && current()) { setError(failure); if (!automatic || autoLoadEndsSession(failure)) onDenied(failure); } }
    finally { pending.current = false; if (alive.current) { setBusy(false); if (!automatic) onBusyChange(false); } }
  }
  // Opening a submission compares it once; the panel is keyed by submission and revision, so a remount discards late replies.
  const autoLoaded = useRef(false);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (locked || autoLoaded.current) return; autoLoaded.current = true; void Promise.resolve().then(() => load(null, true /* automatic */)); }, [locked]);
  return <section className={styles.correctionPanel} aria-label="Indikasi pengajuan berulang"><h3>Indikasi pengajuan berulang</h3>
    <p>Bandingkan gambar asli yang identik dan referensi pembeli yang sama dalam cakupan akses Anda. Hasil ini tidak membuktikan transfer ganda dan tidak mengubah pembayaran.</p>
    <Button variant="secondary" disabled={locked || busy} onClick={() => void load(null)}>Periksa ulang kecocokan pengajuan</Button>
    {busy && <p role="status">Memeriksa gambar dan referensi…</p>}
    {error !== null && <p role="alert">{accessMessage(error)}</p>}
    {page && <><h4 ref={heading} tabIndex={-1}>Hasil perbandingan pengajuan</h4>
      {page.items.length === 0 ? <p>Tidak ada kecocokan persis pada halaman ini dalam cakupan akses Anda. Gambar yang dipotong atau dikompresi ulang belum dibandingkan; tetap periksa bukti bank.</p> :
        <ul className={styles.lines}>{page.items.map(item => <li key={item.declarationId}><strong>Pengajuan {item.declarationId}</strong>
          <p>{item.signals.map(signal => signal === 'same_image' ? 'Berkas gambar asli identik' : 'Referensi yang dinyatakan pembeli sama').join(' · ')}</p>
          <p>Diajukan {dateTimeFormatter.format(new Date(item.submittedAt))}</p>
          <Button variant="secondary" disabled={locked || busy} onClick={() => onOpen(item.declarationId)}>Buka pengajuan pembanding {item.declarationId}</Button>
        </li>)}</ul>}
      {page.hasMore && <Button variant="secondary" disabled={locked || busy} onClick={() => void load(page.items.at(-1)!.declarationId)}>Kecocokan lebih lama</Button>}
    </>}
  </section>;
}
