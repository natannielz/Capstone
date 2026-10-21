'use client';

import { useEffect, useRef, useState } from 'react';
import { RequestNotSentError } from '../../api/client';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { createOrderDraftGateway, type DraftCommand, type DraftResult } from './order-draft-client';
import styles from '../administration/access.module.css';

export function draftMessage(error: unknown, reading = false) {
  if (error instanceof RequestNotSentError) return error.status === 429 ? 'Terlalu banyak permintaan. Tunggu sebentar, lalu coba lagi; perubahan ini belum dikirim.' : accessMessage(error);
  if (error instanceof AccessFailure) {
    if (error.status === 429) return 'Terlalu banyak permintaan. Tunggu sebentar, lalu muat kembali data. Penyimpanan yang sudah dikonfirmasi tetap tersimpan.';
    if (error.code === 'order_selection_changed') return 'Harga atau kemasan berubah. Pilih kembali kemasan dan periksa subtotal terbaru sebelum menyimpan.';
    if (error.code === 'invalid_order_draft') return 'Periksa jumlah, referensi, dan alamat. Nama, telepon, alamat, serta kota wajib diisi jika alamat pengiriman digunakan.';
    if (error.code === 'order_package_quantity_invalid') return 'Jumlah kemasan harus memenuhi minimum dan kelipatan yang tertera.';
    if (error.code === 'order_package_unavailable') return 'Sebagian kemasan di draf ini tidak lagi tersedia untuk cabang ini. Hapus baris tersebut atau pilih kembali kemasan dari katalog, lalu tinjau lagi.';
    if (error.status === 409) return 'Draf atau akses telah berubah. Buka kembali draf dan tinjau perubahan terbaru.';
    if (error.status < 500 && error.status !== 0) return accessMessage(error);
  }
  return reading ? 'Data belum dapat dimuat. Periksa koneksi, lalu muat kembali.' : 'Hasil penyimpanan belum dapat dipastikan. Gunakan Periksa hasil; jangan membuat draf pengganti untuk permintaan ini.';
}

export function useDraftSave(actorId: string, isCurrent: () => boolean, onSaved: (result: DraftResult) => void, onFailure?: (error: unknown) => void, reading = false) {
  const recovery = useOperationRecovery(actorId), [gateway] = useState(() => createOrderDraftGateway()), [ledger] = useState(() => new MutationLedger());
  const [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [error, setError] = useState<unknown>(null);
  const alive = useRef(false), pending = useRef(false), recovered = useRef<DraftResult | null>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const current = () => alive.current && isCurrent();
  const failed = (failure: unknown) => { if (current()) { setError(failure); onFailure?.(failure); } };
  async function submit(command: DraftCommand) {
    if (!current() || pending.current || recovering || reading || recovery.locked) return;
    pending.current = true; setBusy(true); setError(null);
    try {
      // Recheck persisted references immediately before sending, including another tab's requests.
      if (recovery.journal.list(actorId).length > 0) throw new AccessFailure(400, 'resolve_previous_attempt');
      const result = await submitTrackedRequest(ledger, recovery.journal, actorId, 'ordering.draft.save', 'ordering.draft.save', command,
        key => gateway.submit(command, key, current));
      if (current()) onSaved(result);
    } catch (failure) { failed(failure); }
    finally { pending.current = false; if (alive.current) { setBusy(false); recovery.refresh(); } }
  }
  return { busy, recovering, locked: busy || recovering || recovery.locked, submit,
    recoveryView: <>
      {error !== null && <p className={styles.alert} role="alert">{draftMessage(error)}</p>}
      {recovery.error !== null && <p className={styles.alert} role="alert">{accessMessage(recovery.error)}</p>}
      <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={busy || reading || !isCurrent()} onBusyChange={setRecovering} onFailure={failed}
        recoveryAdapters={{ 'ordering.draft': { lookup: async entry => {
          const receipt = await gateway.recover(entry.actorId, entry.key); recovered.current = receipt?.result ?? null; return receipt;
        } } }} onResolved={() => {
          recovery.refresh(); ledger.resetAfterReconciliation(); setError(null);
          if (current() && recovered.current) onSaved(recovered.current);
          recovered.current = null;
        }} />
    </> };
}
