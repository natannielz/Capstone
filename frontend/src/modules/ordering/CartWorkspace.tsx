'use client';

import Link from 'next/link';
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { PackageOpen, ShoppingCart, Trash2 } from 'lucide-react';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure } from '../administration/workflow';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure } from '../identity/identity-workflow';
import { createBuyerCatalogGateway, type BuyerVariant } from '../catalog/catalog-buyer-client';
import { SessionAccessState } from '@/components/workspace-kit/session-access-state';
import { QuantityStepper } from '@/components/workspace-kit/quantity-stepper';
import { formatDisplayMoney, formatNumber } from '@/lib/format';
import { createOrderQuoteGateway, orderContextKey, orderQuantity, type OrderContextOption, type OrderSelection } from './order-quote-client';
import type { OrderDraft } from './order-draft-client';
import { useCartSave } from './CartSave';
import { acceptCurrent, cartAmount, cartLineState, cartReasons, lineAmount, removeFromCart, setCartQuantity, type CartLineState } from './cart-model';
import { announceCart, branchLabel, cartBranch, rememberCartBranch } from './cart-branch';
import { addToCartMessage } from './AddToCartPanel';
import styles from './cart.module.css';

type Account = { actorId: string };
export type CartRow = { saved: OrderSelection; current: BuyerVariant | null; state: CartLineState };

/** Lines in saved order, each with what the buyer may see now and whether it can go to checkout as it is. */
export function cartRows(selections: readonly OrderSelection[], current: ReadonlyMap<string, BuyerVariant>): CartRow[] {
  return selections.map(saved => {
    const variant = current.get(saved.skuId) ?? null;
    return { saved, current: variant, state: cartLineState(saved, variant && { skuId: variant.skuId, definitionId: variant.definition.id,
      definitionVersion: variant.definition.version, priceId: variant.currentPrice?.id ?? null, priceVersion: variant.currentPrice?.version ?? null }) };
  });
}

export function CartWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null), [loading, setLoading] = useState(true), [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true; const controller = new AbortController();
    void Promise.resolve().then(async () => {
      if (!active) return; setLoading(true); setAccount(null); setError(null);
      try {
        const session = await identity.session(controller.signal);
        if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
        const current = await access.current(); if (!active) return;
        if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
        if (!current.allowedActions.includes('order.submit')) throw new AccessFailure(403, 'scope_denied');
        setAccount({ actorId: current.userId });
      } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
    });
    return () => { active = false; controller.abort(); };
  }, [identity, access, attempt]);
  const lost = useCallback((failure: unknown) => {
    if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed')) { setAccount(null); setError(failure); }
  }, []);
  return <main className={styles.page}>
    <h1 className={styles.title}>Keranjang</h1>
    {loading ? <p role="status">Memuat keranjang…</p> : account ? <CartDesk key={account.actorId} actorId={account.actorId} onLost={lost} />
      : <SessionAccessState error={error} returnPath="/cart" onRetry={() => setAttempt(value => value + 1)} />}
  </main>;
}

function CartDesk({ actorId, onLost }: { actorId: string; onLost: (failure: unknown) => void }) {
  const [quotes] = useState(() => createOrderQuoteGateway()), [catalog] = useState(() => createBuyerCatalogGateway());
  const [options, setOptions] = useState<OrderContextOption[] | null>(null), [branch, setBranch] = useState<OrderContextOption | null>(null);
  const [draft, setDraft] = useState<OrderDraft | null>(null), [variants, setVariants] = useState<Map<string, BuyerVariant>>(new Map());
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading'), [reload, setReload] = useState(0);
  const [typed, setTyped] = useState<Record<string, string>>({}), [saving, setSaving] = useState(false), [error, setError] = useState<unknown>(null);
  const timers = useRef(new Map<string, number>()), alive = useRef(true), branchId = useId();
  const tracked = useCartSave(actorId, () => alive.current, () => setReload(value => value + 1)), cart = tracked.cart;
  const [skipped] = useState(() => { try { const value = Number(new URLSearchParams(window.location.search).get('terlewat')); return Number.isSafeInteger(value) && value > 0 && value <= 100 ? value : 0; } catch { return 0; } });
  useEffect(() => { alive.current = true; const pending = timers.current; return () => { alive.current = false; pending.forEach(timer => window.clearTimeout(timer)); }; }, []);
  useEffect(() => {
    const controller = new AbortController();
    void quotes.contexts(actorId, controller.signal).then(value => { if (!controller.signal.aborted) { setOptions(value); setBranch(cartBranch(actorId, value)); } })
      .catch(failure => { if (!controller.signal.aborted) { setOptions([]); setError(failure); onLost(failure); } });
    return () => controller.abort();
  }, [actorId, quotes, onLost]);
  const contextKey = branch ? orderContextKey(branch.context) : null;
  useEffect(() => {
    if (!branch) return;
    const controller = new AbortController();
    void Promise.resolve().then(async () => {
      setState('loading');
      try {
        const saved = await cart.read(actorId, branch.context, controller.signal);
        const current = saved ? await catalog.bySku(actorId, saved.input.selections.map(line => line.skuId), controller.signal) : new Map<string, BuyerVariant>();
        if (controller.signal.aborted) return;
        setDraft(saved); setVariants(current); setTyped({}); setState('ready'); announceCart(saved?.input.selections.length ?? 0);
      } catch (failure) { if (!controller.signal.aborted) { setState('error'); onLost(failure); } }
    });
    return () => controller.abort();
    // contextKey stands for the branch; the object itself changes identity on every contexts read.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [actorId, cart, catalog, contextKey, reload, onLost]);
  const rows = useMemo(() => cartRows(draft?.input.selections ?? [], variants), [draft, variants]);
  async function change(update: (current: OrderSelection[]) => OrderSelection[], reason: string) {
    if (!branch) return;
    setSaving(true); setError(null);
    try { await cart.apply(actorId, branch.context, update, reason); if (alive.current) setReload(value => value + 1); }
    catch (failure) { if (alive.current) { setError(failure); onLost(failure); setReload(value => value + 1); } }
    finally { if (alive.current) setSaving(false); }
  }
  function typeQuantity(row: CartRow, text: string) {
    setTyped(values => ({ ...values, [row.saved.skuId]: text }));
    const pending = timers.current.get(row.saved.skuId); if (pending) window.clearTimeout(pending);
    const definition = row.current?.definition; if (!definition) return;
    let packages: number;
    try { packages = orderQuantity(text.trim(), definition.minimumOrderPackages, definition.orderIncrementPackages); } catch { return; }
    if (packages === row.saved.packages) return;
    timers.current.set(row.saved.skuId, window.setTimeout(() => {
      timers.current.delete(row.saved.skuId);
      void change(current => setCartQuantity(current, row.saved.skuId, packages, { minimum: definition.minimumOrderPackages, increment: definition.orderIncrementPackages }), cartReasons.quantity);
    }, 700));
  }
  if (options === null) return <p role="status">Memeriksa cabang…</p>;
  if (options.length === 0) return <section className={styles.empty}><h2>Belum ada cabang untuk dipesan</h2><p>Akun ini belum dapat memesan untuk cabang mana pun. Hubungi pengelola akses di unit Anda.</p></section>;
  const ready = rows.filter(row => row.state === 'ready' && row.current?.currentPrice);
  const currency = ready[0]?.current?.currentPrice?.currency ?? 'IDR';
  const total = cartAmount(ready.map(row => lineAmount(row.current!.currentPrice!.unitPrice, row.saved.packages)));
  const typedValid = (row: CartRow) => {
    const text = typed[row.saved.skuId], definition = row.current?.definition;
    if (text === undefined || !definition) return true;
    try { orderQuantity(text.trim(), definition.minimumOrderPackages, definition.orderIncrementPackages); return true; } catch { return false; }
  };
  const blocked = rows.some(row => row.state !== 'ready' || !typedValid(row));
  const editing = rows.some(row => typed[row.saved.skuId] !== undefined && typedValid(row) && Number(typed[row.saved.skuId].trim()) !== row.saved.packages);
  return <>
    <div className={styles.branch}><label htmlFor={branchId}>Keranjang untuk</label>
      <select id={branchId} value={contextKey ?? ''} disabled={saving || tracked.locked} onChange={event => {
        const next = options.find(option => orderContextKey(option.context) === event.target.value) ?? null;
        setBranch(next); setDraft(null); setVariants(new Map()); if (next) rememberCartBranch(actorId, next.context);
      }}><option value="">Pilih cabang</option>{options.map(option => <option key={orderContextKey(option.context)} value={orderContextKey(option.context)}>{branchLabel(option)}</option>)}</select></div>
    {skipped > 0 && <p role="status" className={styles.notice}>{skipped} barang dari pesanan sebelumnya tidak lagi tersedia untuk cabang Anda dan tidak dimasukkan.</p>}
    {tracked.recoveryView}
    {error !== null && <p role="alert" className={styles.alert}>{addToCartMessage(error)}</p>}
    {!branch ? <p>Pilih cabang untuk melihat keranjangnya.</p> : state === 'loading' && !draft ? <p role="status">Memuat keranjang…</p>
      : state === 'error' && !draft ? <section className={styles.empty}><h2>Keranjang belum dapat dimuat</h2><p>Periksa koneksi, lalu coba lagi.</p>
        <button type="button" className={styles.primary} onClick={() => setReload(value => value + 1)}>Coba lagi</button></section>
      : rows.length === 0 ? <section className={styles.empty}><ShoppingCart size={40} strokeWidth={1.4} aria-hidden="true" /><h2>Keranjang masih kosong</h2>
        <p>Cari barang di katalog, pilih jumlahnya, lalu tekan Tambah ke keranjang.</p><Link className={styles.primary} href="/shop">Mulai belanja</Link></section>
      : <div className={styles.layout}>
        <section aria-label="Barang di keranjang"><ul className={styles.lines}>{rows.map(row => {
          const variant = row.current, definition = variant?.definition, price = variant?.currentPrice;
          const text = typed[row.saved.skuId] ?? String(row.saved.packages);
          return <li key={row.saved.skuId} className={styles.line} data-state={row.state}>
            <span className={styles.thumb}><PackageOpen size={30} strokeWidth={1.3} aria-hidden="true" /></span>
            <div className={styles.lineInfo}>
              {variant ? <Link href={'/shop/' + variant.publicId} className={styles.lineName}>{variant.familyName}</Link> : <span className={styles.lineName}>Barang tidak tersedia</span>}
              {definition && <span className={styles.linePack}>{definition.packageLabel} · isi {formatNumber(definition.baseUnitsPerPackage)} {definition.baseUnitCode} · {variant!.code}</span>}
              {price && <span className={styles.unitPrice}>{formatDisplayMoney(price.currency, price.unitPrice)} <small>per kemasan</small></span>}
              {row.state === 'price-changed' && <p className={styles.notice}>Harga berubah menjadi {formatDisplayMoney(price!.currency, price!.unitPrice)}.
                <button type="button" className={styles.secondary} disabled={saving || tracked.locked} onClick={() => void change(current => current.map(line => line.skuId === row.saved.skuId ? acceptCurrent(line, variant!) : line), cartReasons.refresh)}>Pakai harga terbaru</button></p>}
              {row.state === 'package-changed' && <p className={styles.notice}>Kemasan diperbarui menjadi {definition!.packageLabel}.
                <button type="button" className={styles.secondary} disabled={saving || tracked.locked} onClick={() => void change(current => current.map(line => line.skuId === row.saved.skuId ? acceptCurrent(line, variant!) : line), cartReasons.refresh)}>Pakai kemasan terbaru</button></p>}
              {row.state === 'unavailable' && <p className={styles.notice}>Barang ini tidak lagi tersedia untuk cabang Anda. Hapus dari keranjang untuk melanjutkan.</p>}
            </div>
            <div className={styles.lineControls}>
              {definition && row.state === 'ready' && <QuantityStepper id={'cart-line-' + row.saved.skuId} label="Jumlah" value={text} disabled={saving || tracked.locked}
                minimum={definition.minimumOrderPackages} increment={definition.orderIncrementPackages} onChange={value => typeQuantity(row, value)} />}
              {definition && !typedValid(row) && <p className={styles.notice}>Minimal {definition.minimumOrderPackages}, kelipatan {definition.orderIncrementPackages}.</p>}
              {price && row.state === 'ready' && <strong className={styles.lineTotal}>{formatDisplayMoney(price.currency, lineAmount(price.unitPrice, row.saved.packages))}</strong>}
              <button type="button" className={styles.remove} disabled={saving || tracked.locked} aria-label={'Hapus ' + (variant?.familyName ?? 'barang') + ' dari keranjang'}
                onClick={() => void change(current => removeFromCart(current, row.saved.skuId), cartReasons.remove)}><Trash2 size={18} aria-hidden="true" />Hapus</button>
            </div>
          </li>;
        })}</ul>
          <p className={styles.help}><Link href="/shop">Tambah barang lain</Link> · <Link href="/ordering/drafts">Draf tersimpan</Link></p></section>
        <aside className={styles.summary} aria-label="Ringkasan belanja">
          <h2>Ringkasan belanja</h2>
          <p className={styles.summaryRow}><span>{rows.length} barang</span><strong>{formatDisplayMoney(currency, total)}</strong></p>
          <p className={styles.help + " " + styles.summaryNote}>Ongkos kirim, pajak dan syarat pembayaran dihitung di langkah berikutnya. Pesanan yang memerlukan persetujuan belanja akan dikirim ke penyetuju cabang.</p>
          {blocked && <p className={styles.notice}>Periksa barang yang ditandai sebelum melanjutkan.</p>}
          {draft && !blocked && !saving && !tracked.locked && !editing
            ? <Link className={styles.primary + ' ' + styles.checkout} href={'/ordering/checkout?draft=' + encodeURIComponent(draft.id)}>Lanjut ke pengajuan</Link>
            : <button type="button" className={styles.primary + ' ' + styles.checkout} disabled>{saving || editing ? 'Menyimpan…' : 'Lanjut ke pengajuan'}</button>}
          <p className={styles.saved} aria-live="polite">{saving || editing ? 'Menyimpan perubahan…' : 'Keranjang tersimpan otomatis.'}</p>
        </aside>
      </div>}
  </>;
}
