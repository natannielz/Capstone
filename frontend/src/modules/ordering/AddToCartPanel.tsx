'use client';

import Link from 'next/link';
import { useEffect, useId, useRef, useState } from 'react';
import { Check, ShoppingCart } from 'lucide-react';
import { RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import type { BuyerVariant } from '../catalog/catalog-buyer-client';
import { QuantityStepper, stepQuantity } from '@/components/workspace-kit/quantity-stepper';
import { formatDisplayMoney } from '@/lib/format';
import { createOrderQuoteGateway, orderContextKey, orderQuantity, type OrderContextOption } from './order-quote-client';
import { CartOutcomeUnknown } from './cart-client';
import { orderDraftSubmittedMessage } from './order-draft-client';
import { useCartSave } from './CartSave';
import { addToCart, cartReasons, lineAmount } from './cart-model';
import { announceCart, branchLabel, cartBranch, rememberCartBranch } from './cart-branch';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { Field } from '@/components/workspace-kit/field';
import styles from './cart.module.css';

export function addToCartMessage(error: unknown): string {
  if (error instanceof CartOutcomeUnknown) return 'Jawaban belum diterima. Tekan Coba lagi: barang tidak akan masuk dua kali.';
  if (error instanceof RequestNotSentError) return 'Belum terkirim. Periksa koneksi, lalu tekan Tambah ke keranjang lagi.';
  if (error instanceof AccessFailure) {
    if (error.code === 'resolve_previous_attempt') return 'Penyimpanan keranjang sebelumnya belum pasti. Tekan Periksa hasil di atas sebelum mengubah keranjang lagi.';
    if (error.code === 'order_draft_submitted') return orderDraftSubmittedMessage;
    if (error.code === 'cart_full') return 'Keranjang sudah berisi 100 barang. Ajukan atau kurangi isi keranjang lebih dulu.';
    if (error.code === 'order_package_quantity_invalid') return 'Jumlah belum sesuai minimum dan kelipatan kemasan.';
    if (error.code === 'order_selection_changed') return 'Harga barang lain di keranjang berubah. Buka keranjang untuk menyetujui harga terbaru, lalu tambahkan lagi.';
    if (error.status === 401 || error.code === 'actor_changed') return 'Sesi berakhir. Masuk kembali untuk menambah ke keranjang.';
    if (error.status === 403) return 'Barang ini belum dapat dipesan untuk cabang yang dipilih.';
    if (error.status === 429) return 'Terlalu banyak permintaan. Tunggu sebentar, lalu coba lagi.';
  }
  if (!(error instanceof AccessFailure) || error.status >= 500) return 'Jawaban belum diterima. Gunakan Periksa hasil untuk memastikan; barang tidak akan masuk dua kali.';
  return 'Keranjang belum dapat diperbarui. Periksa koneksi, lalu coba lagi.';
}

/** Marketplace order of work: choose how many, add to the cart, keep shopping. The branch is chosen once, not per product. */
export function AddToCartPanel({ actorId, item, isCurrent }: { actorId: string; item: BuyerVariant; isCurrent: () => boolean }) {
  const branchId = useId();
  const [quotes] = useState(() => createOrderQuoteGateway());
  const rule = { minimum: item.definition.minimumOrderPackages, increment: item.definition.orderIncrementPackages };
  const [options, setOptions] = useState<OrderContextOption[] | null>(null), [branch, setBranch] = useState<OrderContextOption | null>(null);
  const [quantity, setQuantity] = useState(() => stepQuantity('', rule.minimum, rule.increment, 1));
  const [busy, setBusy] = useState(false), [error, setError] = useState<unknown>(null), [added, setAdded] = useState<number | null>(null);
  const alive = useRef(true);
  const [confirmed, setConfirmed] = useState(false);
  const saving = useCartSave(actorId, isCurrent, () => { setError(null); setConfirmed(true); }), cart = saving.cart;
  useEffect(() => {
    alive.current = true; const controller = new AbortController();
    void quotes.contexts(actorId, controller.signal).then(value => {
      if (controller.signal.aborted) return; setOptions(value); setBranch(cartBranch(actorId, value));
    }).catch(failure => { if (!controller.signal.aborted) { setOptions([]); setError(failure); } });
    return () => { alive.current = false; controller.abort(); };
  }, [actorId, quotes]);
  const price = item.currentPrice;
  let packages: number | null = null;
  try { packages = orderQuantity(quantity.trim(), rule.minimum, rule.increment); } catch { packages = null; }
  async function add() {
    if (!price || !branch || packages === null || busy || saving.locked || !isCurrent()) return;
    setBusy(true); setError(null); setAdded(null);
    const choice = { skuId: item.skuId, definitionId: item.definition.id, definitionVersion: item.definition.version, priceId: price.id, priceVersion: price.version, packages };
    // Naming the branch and pressing add is the explicit choice the cart opens with, even when the save's answer is lost
    // and "Periksa hasil" confirms it later.
    rememberCartBranch(actorId, branch.context);
    try {
      await cart.apply(actorId, branch.context, current => addToCart(current, choice, rule), cartReasons.add);
      if (!alive.current || !isCurrent()) return;
      const saved = await cart.read(actorId, branch.context).catch(() => null);
      if (!alive.current) return;
      setAdded(packages); if (saved) announceCart(saved.input.selections.length);
    } catch (failure) { if (alive.current) setError(failure); }
    finally { if (alive.current) setBusy(false); }
  }
  if (!price) return <section className={styles.buyBox} aria-label="Pemesanan"><p>Harga untuk cabang Anda belum tersedia. Hubungi petugas koperasi.</p></section>;
  return <section className={styles.buyBox} aria-label="Pemesanan" aria-busy={busy || options === null}>
    {options === null ? <p role="status">Memeriksa cabang…</p> : options.length === 0 ? <p>Akun ini belum dapat memesan untuk cabang mana pun. Hubungi pengelola akses di unit Anda.</p> : <>
      {/* The buyer names the branch; the browser never picks it, not even the only one allowed. */}
      <div className={styles.branch}><Field id={branchId} label="Pesan untuk">
        <select value={branch ? orderContextKey(branch.context) : ''} disabled={busy}
          onChange={event => { setBranch(options.find(option => orderContextKey(option.context) === event.target.value) ?? null); setAdded(null); setError(null); }}>
          <option value="">Pilih cabang</option>
          {options.map(option => <option key={orderContextKey(option.context)} value={orderContextKey(option.context)}>{branchLabel(option)}</option>)}
        </select></Field></div>
      <QuantityStepper id={'cart-quantity-' + item.skuId} label="Jumlah kemasan" value={quantity} minimum={rule.minimum} increment={rule.increment} disabled={busy}
        onChange={text => { setQuantity(text); setAdded(null); setError(null); }} />
      <p className={styles.help}>{rule.minimum > 1 || rule.increment > 1 ? `Minimal ${rule.minimum}, kelipatan ${rule.increment}. ` : ''}Satu {item.definition.packageLabel} berisi {item.definition.baseUnitsPerPackage} {item.definition.baseUnitCode}.</p>
      <p className={styles.subtotal}><span>Subtotal</span><strong>{packages === null ? '—' : formatDisplayMoney(price.currency, lineAmount(price.unitPrice, packages))}</strong></p>
      <Button variant="primary" className={styles.addButton} busy={busy} disabled={busy || saving.locked || !branch || packages === null} onClick={() => void add()}>
        <ShoppingCart aria-hidden="true" />{busy ? 'Menambahkan…' : 'Tambah ke keranjang'}</Button>
      {!branch && <p className={styles.help}>Pilih cabang lebih dulu.</p>}
    </>}
    {saving.recoveryView}
    {confirmed && <div role="status" className={styles.added}><p><Check size={18} aria-hidden="true" />Barang sudah masuk keranjang.</p>
      <div className={styles.actions}><Link className={buttonClassName({ variant: 'secondary' })} href="/cart">Lihat keranjang</Link></div></div>}
    {error !== null && <p role="alert" className={styles.alert}>{addToCartMessage(error)}{error instanceof AccessFailure && error.code === 'order_selection_changed' && <> <Link href="/cart">Buka keranjang</Link></>}</p>}
    {added !== null && <div role="status" className={styles.added}><p><Check size={18} aria-hidden="true" />{added} {item.definition.packageLabel} masuk keranjang.</p>
      <div className={styles.actions}><Link className={buttonClassName({ variant: 'secondary' })} href="/cart">Lihat keranjang</Link><Link className={buttonClassName({ variant: 'tertiary' })} href="/shop">Lanjut belanja</Link></div></div>}
    <p className={styles.help}>Harga mengikuti kontrak cabang dan diperiksa lagi saat pengajuan. Stok belum dipesan sampai pesanan diajukan.</p>
  </section>;
}
