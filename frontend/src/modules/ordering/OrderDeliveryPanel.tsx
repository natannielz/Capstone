'use client';

import { useId, useRef, useState, type FormEvent } from 'react';
import { MapPin } from 'lucide-react';
import { AccessFailure } from '../administration/workflow';
import { createOrderQuoteGateway } from './order-quote-client';
import { CartOutcomeUnknown, createCartGateway } from './cart-client';
import type { OrderDraft } from './order-draft-client';
import { checkDelivery, deliveryFields, deliveryFromApproved, type DeliveryField, type DeliveryFields } from './order-delivery';
import { addToCartMessage } from './AddToCartPanel';
import { Button } from '@/components/workspace-kit/button';
import { Field } from '@/components/workspace-kit/field';
import { StateView } from '@/components/workspace-kit/state-view';
import common from '../administration/access.module.css';
import styles from './order-checkout.module.css';

const labels: Array<[DeliveryField, string, 'text' | 'tel' | 'area', string]> = [
  ['recipientName', 'Nama penerima', 'text', 'name'], ['phone', 'Telepon penerima', 'tel', 'tel'], ['address', 'Alamat', 'area', 'street-address'],
  ['city', 'Kota', 'text', 'address-level2'], ['postalCode', 'Kode pos (opsional)', 'text', 'postal-code'], ['instructions', 'Catatan untuk pengantar (opsional)', 'area', 'off'],
  ['purchaseReference', 'Nomor referensi pembelian (opsional)', 'text', 'off'],
];

/** Where the order goes and who receives it, on the checkout page itself: filled once, then shown as a summary to change if needed. */
export function OrderDeliveryPanel({ actorId, draft, locked, onSaved, onFailure }: {
  actorId: string; draft: OrderDraft; locked: boolean; onSaved: () => Promise<void>; onFailure: (error: unknown) => void;
}) {
  const id = useId();
  const [cart] = useState(() => createCartGateway()), [quotes] = useState(() => createOrderQuoteGateway());
  const [editing, setEditing] = useState(draft.input.delivery === null), [fields, setFields] = useState<DeliveryFields>(() => deliveryFields(draft.input.delivery, draft.input.purchaseReference));
  const [errors, setErrors] = useState<Partial<Record<DeliveryField, string>>>({}), [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [notice, setNotice] = useState('');
  const [unknown, setUnknown] = useState<CartOutcomeUnknown | null>(null);
  const first = useRef<HTMLInputElement>(null);
  const delivery = draft.input.delivery;
  async function fillFromBranch() {
    setBusy(true); setError(null); setNotice('');
    try {
      const approved = await quotes.addresses(actorId, draft.input.context);
      setFields(current => deliveryFromApproved(current, approved.delivery.address)); setNotice('Alamat diisi dari alamat ' + approved.delivery.name + '. Lengkapi nama dan telepon penerima.');
    } catch (failure) {
      setError(failure instanceof AccessFailure && failure.code === 'organization_address_changed' ? new AccessFailure(409, 'branch_address_missing') : failure); onFailure(failure);
    } finally { setBusy(false); }
  }
  async function save(event: FormEvent) {
    event.preventDefault(); if (busy || locked) return;
    const checked = checkDelivery(fields);
    if ('errors' in checked) { setErrors(checked.errors); return; }
    setErrors({}); setBusy(true); setError(null);
    try {
      if (unknown) await cart.resend(unknown); else await cart.saveDetails(actorId, draft.id, checked.delivery, checked.purchaseReference);
      setUnknown(null); await onSaved(); setEditing(false); setNotice('');
    } catch (failure) { if (failure instanceof CartOutcomeUnknown) setUnknown(failure); setError(failure); onFailure(failure); }
    finally { setBusy(false); }
  }
  if (!editing && delivery) return <section className={styles.shipping} aria-labelledby={id + '-heading'}>
    <div><h2 id={id + '-heading'}><MapPin size={18} aria-hidden="true" />Pengiriman</h2>
      <p><strong>{delivery.recipientName}</strong> · {delivery.phone}</p>
      <p className={styles.shippingAddress}>{delivery.address}{'\n'}{delivery.city}{delivery.postalCode ? ' ' + delivery.postalCode : ''}</p>
      {delivery.instructions && <p className={common.help}>Catatan: {delivery.instructions}</p>}
      {draft.input.purchaseReference && <p className={common.help}>Referensi pembelian: {draft.input.purchaseReference}</p>}</div>
    <Button disabled={locked} onClick={() => { setFields(deliveryFields(delivery, draft.input.purchaseReference)); setEditing(true); }}>Ubah pengiriman</Button>
  </section>;
  return <section className={styles.shipping} aria-labelledby={id + '-heading'}>
    <form className={styles.shippingForm} onSubmit={event => void save(event)} noValidate>
      <h2 id={id + '-heading'}><MapPin size={18} aria-hidden="true" />Pengiriman</h2>
      <p className={common.help}>Tulis siapa yang menerima barang di cabang. Alamat dapat diisi dari alamat cabang yang sudah disetujui organisasi.</p>
      <div className="ui-actions"><Button disabled={busy || locked} onClick={() => void fillFromBranch()}>Isi dari alamat cabang</Button></div>
      {notice && <p role="status" className={common.help}>{notice}</p>}
      <div className={styles.shippingFields}>{labels.map(([field, label, kind, autoComplete], index) => {
        const props = { value: fields[field], disabled: busy || locked, autoComplete,
          onChange: (event: { target: { value: string } }) => setFields(current => ({ ...current, [field]: event.target.value })) };
        // Field ties the label, the id and the error (id + "-error", aria-describedby, aria-invalid) to the control.
        return <div key={field} className={kind === 'area' ? styles.wide : undefined}><Field id={id + '-' + field} label={label} error={errors[field]}>
          {kind === 'area' ? <textarea rows={field === 'address' ? 3 : 2} maxLength={1000} {...props} />
            : <input ref={index === 0 ? first : undefined} type={kind} inputMode={field === 'postalCode' ? 'numeric' : undefined} maxLength={field === 'phone' ? 32 : field === 'postalCode' ? 5 : 160} {...props} />}
        </Field></div>;
      })}</div>
      {error !== null && <StateView kind="error" title={error instanceof AccessFailure && error.code === 'branch_address_missing'
        ? 'Cabang ini belum punya alamat yang disetujui. Isi alamat secara manual.' : addToCartMessage(error)} />}
      <div className="ui-actions">
        <Button type="submit" variant="primary" busy={busy} disabled={busy || locked}>{busy ? 'Menyimpan…' : unknown ? 'Coba lagi' : 'Simpan pengiriman'}</Button>
        {delivery && <Button disabled={busy} onClick={() => { setEditing(false); setErrors({}); setError(null); }}>Batal</Button>}
      </div>
    </form>
  </section>;
}
