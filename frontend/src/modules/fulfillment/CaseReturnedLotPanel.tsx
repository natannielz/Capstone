'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { submitTrackedRequest, type OperationJournal } from '../administration/operation-journal';
import type { AfterSalesCase } from './case-client';
import type { InspectionView } from './inspection-client';
import { createReturnLotGateway, prepareReturnLot, returnLotOperation, type ReturnLotCommand, type ReturnLotView } from './return-lot-client';
import styles from '../administration/access.module.css';
import { recordNumber } from '@/lib/document-numbers';

function message(failure: unknown) {
  if (failure instanceof AccessFailure && failure.code === 'invalid_return_lot_verification') return 'Pilih lot pengiriman asli dan jumlah kemasan utuh yang sudah dicocokkan secara fisik. Isi label yang terlihat dan catatan petugas.';
  if (failure instanceof AccessFailure && failure.code === 'return_original_lot_quantity_exceeded') return 'Jumlah pada lot ini sudah dipakai oleh pemeriksaan lain. Perbarui hasil pemeriksaan; jumlah sisanya tetap perlu penanganan manual.';
  if (failure instanceof AccessFailure && ['return_lot_verification_changed', 'stock_source_revision_changed', 'return_lot_view_changed'].includes(failure.code)) return 'Hasil atau sesi pemeriksaan berubah. Perbarui hasil sebelum mencatat pencocokan.';
  if (failure instanceof AccessFailure && ['return_lot_response_invalid', 'aftersales_source_obligation_unavailable'].includes(failure.code)) return 'Sumber pengiriman atau hasil retur belum dapat diperiksa dengan aman. Perbarui halaman dan hubungi penanggung jawab jika masalah berlanjut.';
  return accessMessage(failure);
}
export function CaseReturnedLotPanel({ actorId, owner, inspection, fresh, locked, current, freshCurrent, journal, onSettled, onDenied, onBusyChange }: {
  actorId: string; owner: AfterSalesCase; inspection: InspectionView; fresh: boolean; locked: boolean; current: () => boolean; freshCurrent: () => boolean;
  journal: OperationJournal; onSettled: () => void; onDenied: (failure: unknown) => void; onBusyChange: (busy: boolean) => void;
}) {
  const [gateway] = useState(createReturnLotGateway), [ledger] = useState(() => new MutationLedger(() => 'cl.' + owner.id + '.' + inspection.receipt.id + '.' + crypto.randomUUID()));
  const [view, setView] = useState<ReturnLotView | null>(null), [selection, setSelection] = useState(''), [lotId, setLotId] = useState(''), [packages, setPackages] = useState('');
  const [observedLotLabel, setObservedLotLabel] = useState(''), [reason, setReason] = useState(''), [consent, setConsent] = useState(false), [review, setReview] = useState<ReturnLotCommand | null>(null);
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [saved, setSaved] = useState(false);
  const alive = useRef(false), pending = useRef(false), heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; onBusyChange(false); }; }, [onBusyChange]);
  useEffect(() => { if (review) heading.current?.focus(); }, [review]);
  const active = () => alive.current && current(), admitted = () => active() && freshCurrent(), disabled = locked || busy;
  const selected = view?.lines.find(item => item.receiptId + ':' + item.skuId === selection);
  const choice = selected?.choices.find(item => item.originalLotId === lotId);
  const product = (receiptId: string, skuId: string) => owner.source.lines.find(item => item.receiptId === receiptId && item.skuId === skuId)!;
  async function work(task: () => Promise<void>) {
    if (!admitted() || locked || pending.current) return; pending.current = true; setBusy(true); onBusyChange(true); setError(null);
    try { await task(); } catch (failure) { if (active()) { setReview(null); setConsent(false); setError(failure); if (!(failure instanceof AccessFailure && failure.code === 'step_up_required')) onDenied(failure); } }
    finally { pending.current = false; onBusyChange(false); onSettled(); if (alive.current) setBusy(false); }
  }
  return <section className={styles.state} aria-label={'Pencocokan lot penerimaan ' + recordNumber('return', inspection.receipt.id)}>
    <h4>Pencocokan lot oleh petugas</h4>
    <p>Cocokkan label fisik dan kemasan utuh dengan satu lot pengiriman asli. Pernyataan manual ini tercatat dengan akun petugas dan hasil pemeriksaan sebelumnya. Barang tetap dalam karantina.</p>
    <p>Pencocokan ini tidak menetapkan gudang tujuan retur, melepas stok, menyetujui pembayaran, atau menyelesaikan pengembalian.</p>
    {!fresh && <p role="status">Verifikasi dua langkah terbaru diperlukan. <Link href="/identity/account?operation=case.manage&returnPath=%2Ffulfillment%2Fcases">Verifikasi dua langkah untuk pencocokan lot</Link></p>}
    {error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {saved && !view && <p role="status">Pernyataan petugas berhasil tersimpan. Seluruh barang tetap dikarantina. Perbarui pencocokan untuk melihat jumlah tersimpan.</p>}
    {busy && <p role="status">Memeriksa pencocokan lot…</p>}
    <div className={styles.actions}><button className={styles.secondary} disabled={disabled || !fresh} onClick={() => void work(async () => {
      const value = await gateway.read(actorId, owner, inspection, admitted); if (active()) { setView(value); setReview(null); setSelection(''); setLotId(''); setPackages(''); setConsent(false); }
    })}>Perbarui pencocokan lot</button></div>
    {!view && !busy && <p>Perbarui pencocokan lot untuk melihat lot asli dan jumlah yang dapat diperiksa. Akses terbatas pada petugas keluhan dengan izin pemeriksaan stok.</p>}
    {view && <><ul className={styles.historyList} aria-label="Jumlah pencocokan dalam karantina">{view.lines.map(item => {
      const original = product(item.receiptId, item.skuId); return <li key={item.receiptId + ':' + item.skuId}><strong>{original.productName}</strong>
        <p>Diterima {item.receivedBaseUnits} {original.baseUnitCode}; dicocokkan {item.verifiedBaseUnits} {original.baseUnitCode}; belum dicocokkan {item.manualBaseUnits} {original.baseUnitCode}.</p>
        <p>{item.explanation}</p></li>;
    })}</ul>
      {view.revision === '1' && <p role="status">Pernyataan petugas sudah tersimpan. Seluruh barang tetap dikarantina; jumlah yang belum dicocokkan memerlukan penanganan manual.</p>}
      {view.revision === '0' && view.lines.every(item => item.choices.length === 0) && <p>Belum ada varian dan kemasan utuh yang dapat dicocokkan. Periksa kembali hasil pemeriksaan fisik bersama penanggung jawab; unit lepas dan identitas yang berbeda tetap ditangani secara manual.</p>}
    </>}
    {view?.revision === '0' && view.lines.some(item => item.choices.length > 0) && !review && <form className={styles.form} onSubmit={event => {
      event.preventDefault(); if (!admitted() || disabled || !selected) return;
      try { setReview(prepareReturnLot(actorId, owner, inspection, view, { receiptId: selected.receiptId, skuId: selected.skuId, originalLotId: lotId, packages }, observedLotLabel.trim(), reason.trim())); setConsent(false); setError(null); }
      catch (failure) { setError(failure); }
    }}><p>Satu pernyataan tersimpan untuk penerimaan ini. Pilih satu barang dan satu lot yang benar-benar cocok; jumlah atau barang lain tetap belum dicocokkan dan memerlukan penanganan manual.</p>
      <fieldset className={styles.choiceGroup} disabled={disabled || !fresh} style={{ minWidth: 0 }}><legend>Barang dan lot pengiriman asli</legend>
        <label>Barang yang dicocokkan<select required value={selection} onChange={event => { setSelection(event.target.value); setLotId(''); setPackages(''); }}>
          <option value="">Pilih barang yang sudah diperiksa</option>{view.lines.filter(item => item.choices.length > 0).map(item => <option key={item.receiptId + ':' + item.skuId} value={item.receiptId + ':' + item.skuId}>{product(item.receiptId, item.skuId).productName} — bukti {item.receiptId}</option>)}
        </select></label>
        <label>Lot pengiriman asli<select required value={lotId} disabled={!selected || disabled || !fresh} onChange={event => { setLotId(event.target.value); setPackages(''); }}>
          <option value="">Pilih lot setelah label fisik dicocokkan</option>{selected?.choices.map(option => <option key={option.originalLotId} value={option.originalLotId}>Lot {option.originalLotId} — {option.packageLabel}, {option.baseUnitsPerPackage} {option.baseUnitCode} per kemasan</option>)}
        </select></label>
        {choice && <p>{choice.packageLabel} berisi {choice.baseUnitsPerPackage} {choice.baseUnitCode}. Batas pengiriman lot: {choice.dispatchedBaseUnits} {choice.baseUnitCode}; diterima pada retur ini: {selected!.receivedBaseUnits} {choice.baseUnitCode}.</p>}
        <label>Jumlah kemasan utuh yang cocok<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={7} value={packages} onChange={event => setPackages(event.target.value)} /></label>
        <label>Label lot yang terlihat dan dicocokkan<input required maxLength={160} value={observedLotLabel} onChange={event => setObservedLotLabel(event.target.value)} /></label>
        <p>Label ini bersifat internal. Sistem menyimpan pernyataan pencocokan petugas; label yang diketik tidak menjadi pembuktian otomatis lot atau kedaluwarsa.</p>
        <label>Catatan petugas internal<textarea required maxLength={1000} value={reason} onChange={event => setReason(event.target.value)} /></label>
      </fieldset><div className={styles.actions}><button className={styles.primary} disabled={disabled || !fresh || !choice}>Tinjau pencocokan lot</button></div></form>}
    {review && <section className={styles.review} aria-label="Konfirmasi pencocokan lot"><h4 ref={heading} tabIndex={-1}>Catat pernyataan pencocokan manual</h4>
      <p>Keluhan {recordNumber('case', owner.id)} · Penerimaan {recordNumber('return', inspection.receipt.id)}</p>
      {review.body.lines.map(item => <p key={item.receiptId + ':' + item.skuId}>{product(item.receiptId, item.skuId).productName}: lot asli {item.originalLotId}, {item.verifiedBaseUnits} {product(item.receiptId, item.skuId).baseUnitCode} dalam kemasan utuh.</p>)}
      <p>Label yang dicocokkan: {review.body.observedLotLabel}</p><p>Catatan internal: {review.body.reason}</p>
      <label className={styles.checkbox}><input type="checkbox" checked={consent} disabled={disabled || !fresh} onChange={event => setConsent(event.target.checked)} />
        <span>Saya telah mencocokkan barang, label lot dan kemasan utuh secara fisik dengan lot pengiriman asli yang dipilih. Pernyataan ini tercatat dengan akun saya. Seluruh barang tetap dikarantina; jumlah yang belum dicocokkan memerlukan penanganan manual.</span></label>
      <div className={styles.actions}><button className={styles.primary} disabled={disabled || !fresh || !consent} onClick={() => void work(async () => {
        if (journal.list(actorId).length) throw new AccessFailure(400, 'resolve_previous_attempt');
        await submitTrackedRequest(ledger, journal, actorId, returnLotOperation, returnLotOperation, review, key => gateway.verify(review, key, admitted));
        if (active()) { setSaved(true); setView(null); setReview(null); setConsent(false); setObservedLotLabel(''); setReason(''); setSelection(''); setLotId(''); setPackages(''); }
        const value = await gateway.read(actorId, owner, inspection, admitted); if (active()) setView(value);
      })}>Catat pencocokan — tetap karantina</button><button className={styles.secondary} disabled={disabled} onClick={() => { setReview(null); setConsent(false); }}>Kembali ke isian</button></div>
    </section>}
  </section>;
}
