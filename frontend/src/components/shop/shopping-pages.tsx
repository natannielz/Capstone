"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useShopLocation } from "./use-shop-location";
import { ShoppingProgress } from "./shopping-progress";
import {
  ArrowLeft,
  ArrowRight,
  Check,
  CheckCircle2,
  CircleAlert,
  Minus,
  Plus,
  ReceiptText,
  ShoppingCart,
  Trash2,
  Truck,
} from "lucide-react";
import { Art } from "../art";
import { Button } from "../ui/button";
import { validCatalogQuantity } from "@/lib/domain/catalog";
import {
  createCheckoutIntent,
  saveCustomerIntent,
} from "@/lib/client/customer-cart";
import {
  CatalogError,
  ShopEmpty,
  ShopLoading,
  ShopShell,
  useShop,
} from "./shop-shell";
import { rupiah, safeShopBack, shopCategory, shopFamilies } from "./shop-data";

export function ShopQuantity({
  value,
  onChange,
  onCommit,
  label,
  max = 10000,
  disabled = false,
  id,
}: {
  value: string;
  onChange: (value: string) => void;
  onCommit?: (value: string) => void;
  label: string;
  max?: number;
  disabled?: boolean;
  id: string;
}) {
  const validNumber = validCatalogQuantity(value);
  const valid = validNumber && Number(value) <= max;
  const unavailable = max < 1;
  function adjust(delta: number) {
    if (unavailable) return;
    const next = String(
      Math.min(max, Math.max(1, validNumber ? Number(value) + delta : 1)),
    );
    onChange(next);
    onCommit?.(next);
  }
  return (
    <div className="shop-quantity-wrap">
      <label htmlFor={id}>{label}</label>
      <div className="shop-quantity">
        <button
          type="button"
          aria-label={`Kurangi ${label.toLowerCase()}`}
          disabled={disabled || unavailable || (validNumber && Number(value) <= 1)}
          onClick={() => adjust(-1)}
        >
          <Minus size={16} />
        </button>
        <input
          id={id}
          type="text"
          inputMode="numeric"
          pattern="[0-9]+"
          value={value}
          disabled={disabled || unavailable}
          aria-invalid={!valid && !unavailable}
          aria-describedby={!valid ? `${id}-error` : undefined}
          onChange={(event) => onChange(event.target.value)}
          onBlur={() => onCommit?.(value)}
        />
        <button
          type="button"
          aria-label={`Tambah ${label.toLowerCase()}`}
          disabled={disabled || unavailable || (validNumber && Number(value) >= max)}
          onClick={() => adjust(1)}
        >
          <Plus size={16} />
        </button>
      </div>
      {!valid && (
        <p className="shop-field-error" id={`${id}-error`}>
          {unavailable ? "Stok kemasan ini habis." : `Isi bilangan bulat 1–${max.toLocaleString("id-ID")}.`}
        </p>
      )}
    </div>
  );
}

export function StorefrontProduct({ productId }: { productId: string }) {
  return (
    <ShopShell>
      <ProductContent key={productId} productId={productId} />
    </ShopShell>
  );
}
function ProductContent({ productId }: { productId: string }) {
  const {
    products,
    loading,
    error,
    actor,
    owner,
    cart,
    store,
    cartReady,
    busy,
    act,
    setProblem,
  } = useShop();
  const [qty, setQty] = useState("1");
  const location = useShopLocation();
  const selectedId = location.split("?")[0].split("/").at(-1) || productId;
  const back = safeShopBack(
    new URLSearchParams(location.split("?")[1] || "").get("from"),
  );
  const router = useRouter();
  const product = products.find(
    (item) => item.id === selectedId || item.sku === selectedId,
  );
  const family =
    product &&
    shopFamilies(products).find((item) => item.id === product.familyId);
  const qtyValid = Boolean(
    product && validCatalogQuantity(qty) && Number(qty) <= product.available,
  );
  const canBuy =
    qtyValid &&
    cartReady &&
    !busy &&
    !cart.pending &&
    (!actor || actor.role === "customer");
  function packaging(id: string) {
    setQty("1");
    // Let Next preserve router state and notify usePathname about the SKU URL.
    window.history.replaceState(
      null,
      "",
      `/shop/${encodeURIComponent(id)}?${new URLSearchParams({ from: back })}`,
    );
  }
  function buyNow() {
    if (!product || !store || !canBuy) return;
    try {
      saveCustomerIntent(
        owner,
        createCheckoutIntent(
          store.read(owner),
          [
            {
              productId: product.id,
              qty: Number(qty),
              unitPrice: product.price,
            },
          ],
          "buy",
        ),
      );
      router.push(
        actor?.role === "customer" ? "/checkout" : "/customer/login?next=%2Fcheckout",
      );
    } catch (cause) {
      setProblem(
        cause instanceof Error
          ? cause.message
          : "Pilihan belum dapat disimpan.",
      );
    }
  }
  if (error) return <CatalogError />;
  if (loading && !products.length)
    return <ShopLoading label="Memuat rincian produk…" />;
  if (!product || !family)
    return (
      <ShopEmpty
        title="Produk tidak tersedia"
        description="Produk ini tidak ditemukan atau sedang tidak ditawarkan. Anda dapat memilih produk lain dari etalase."
      />
    );
  const addExceedsStock =
    (cart.items[product.id] || 0) + Number(qty) > product.available;
  return (
    <div className="shop-product-page">
      <div className="shop-breadcrumb">
        <Link href={back}>
          <ArrowLeft size={15} /> Kembali ke produk
        </Link>
        <span>/</span>
        <span>{shopCategory(product)}</span>
      </div>
      <div className="shop-product-detail">
        <div className="shop-detail-image">
          <Art
            src={product.image}
            alt={family.name}
            loading="eager"
            fetchPriority="high"
            sizes="(max-width: 700px) 100vw, 50vw"
          />
          <p>Foto ilustrasi. Isi pesanan mengikuti kemasan yang dipilih.</p>
        </div>
        <div className="shop-detail-copy">
          <p className="shop-kicker">{shopCategory(product)}</p>
          <h1>{family.name}</h1>
          <p className="shop-detail-introduction">{product.description}</p>
          <div className="shop-detail-price">
            <strong>{rupiah(product.price)}</strong>
            <span>/ {product.unit}</span>
          </div>
          <div
            className={`shop-detail-stock${product.available <= 0 ? " is-empty" : ""}`}
          >
            {product.available > 0 ? <Check size={15} aria-hidden="true" /> : <CircleAlert size={15} aria-hidden="true" />}
            {product.available > 0
              ? `${product.available} ${product.unit} tersedia`
              : "Stok kemasan ini habis"}
          </div>
          <fieldset className="shop-variant-picker">
            <legend>Pilih kemasan</legend>
            <div>
              {family.products.map((variant) => (
                <button
                  key={variant.id}
                  type="button"
                  aria-pressed={product.id === variant.id}
                  onClick={() => packaging(variant.id)}
                  className={product.id === variant.id ? "is-selected" : ""}
                >
                  <span className="shop-package-name">{variant.packaging}{product.id === variant.id && <Check size={15} aria-hidden="true" />}</span>
                  <span className="shop-package-price">{rupiah(variant.price)}<span> / {variant.unit}</span></span>
                  <span className={`shop-package-stock${variant.available <= 0 ? " is-empty" : ""}`}>{variant.available > 0 ? `${variant.available} ${variant.unit} tersedia` : "Stok habis"}</span>
                </button>
              ))}
            </div>
          </fieldset>
          <div className="shop-detail-quantity">
            <ShopQuantity
              id="product-qty"
              value={qty}
              onChange={setQty}
              label={`Jumlah (${product.unit})`}
              max={Math.max(0, Math.min(10000, product.available))}
              disabled={busy || Boolean(cart.pending) || product.available <= 0}
            />
            <div>
              <span>Subtotal</span>
              <strong>
                {product.available <= 0 ? "Stok habis" : qtyValid
                  ? rupiah(Number(qty) * product.price)
                  : "Periksa jumlah"}
              </strong>
            </div>
          </div>
          {cart.items[product.id] > 0 && (
            <p className="shop-muted">
              {cart.items[product.id]} {product.unit} kemasan ini sudah ada di
              keranjang.
            </p>
          )}
          {addExceedsStock && qtyValid && (
            <p className="shop-field-error">
              Jumlah tambahan dan isi keranjang melebihi stok tersedia. Ubah
              keranjang atau gunakan Beli sekarang untuk pesanan terpisah.
            </p>
          )}
          <div className="shop-product-actions" data-shopping-action>
            <Button
              variant="outline"
              disabled={!canBuy || addExceedsStock}
              onClick={() =>
                void act(
                  (next) =>
                    next.mutate(owner, {
                      type: "add",
                      productId: product.id,
                      qty: Number(qty),
                    }),
                  `${Number(qty)} ${product.unit} ditambahkan ke keranjang.`,
                )
              }
            >
              <ShoppingCart size={18} />
              Tambah ke keranjang
            </Button>
            <Button
              className="shop-button-orange"
              disabled={!canBuy}
              onClick={buyNow}
            >
              Beli sekarang <ArrowRight size={18} />
            </Button>
          </div>
          {actor && actor.role !== "customer" && (
            <Button asChild variant="outline">
              <Link href={`/workspace?view=catalog&product=${product.id}`}>
                Pesan melalui portal divisi
              </Link>
            </Button>
          )}
          <div className="shop-detail-terms">
            <p>
              <Truck size={18} />
              <span>
                Pengiriman ditangani Unit Toko. Jadwal dikonfirmasi setelah
                pesanan ditinjau.
              </span>
            </p>
            <p>
              <ReceiptText size={18} />
              <span>
                Pembayaran simulasi dilakukan setelah invoice diterbitkan sesuai
                barang yang diterima.
              </span>
            </p>
          </div>
        </div>
      </div>
      <section className="shop-product-description">
        <h2>Rincian produk</h2>
        <dl>
          <div>
            <dt>Kemasan dipilih</dt>
            <dd>{product.packaging}</dd>
          </div>
          <div>
            <dt>Satuan pemesanan</dt>
            <dd>{product.unit}</dd>
          </div>
          <div>
            <dt>Kode barang</dt>
            <dd>{product.sku}</dd>
          </div>
        </dl>
        <p className="shop-muted">
          Periksa nama kemasan dan satuan sebelum membeli. Foto bukan keterangan
          isi per paket.
        </p>
      </section>
    </div>
  );
}

export function StorefrontCart() {
  return (
    <ShopShell active="cart">
      <CartContent />
    </ShopShell>
  );
}
function CartContent() {
  const {
    sessionError,
    products,
    loading,
    error,
    actor,
    owner,
    cart,
    store,
    cartReady,
    busy,
    act,
    setProblem,
  } = useShop();
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [draftQty, setDraftQty] = useState<
    Record<string, { value: string; revision: number }>
  >({});
  const router = useRouter();
  const entries = Object.entries(cart.items).map(([id, qty]) => ({
    id,
    qty,
    product: products.find((product) => product.id === id),
    checked: selected[id] !== false,
  }));
  const chosen = entries.filter((entry) => entry.checked && entry.product);
  const invalid = chosen.some(
    ({ id, qty, product }) =>
      !validCatalogQuantity(draftQty[id]?.value ?? qty) ||
      Number(draftQty[id]?.value ?? qty) !== qty ||
      qty > product!.available,
  );
  const total = chosen.reduce(
    (sum, line) => sum + line.qty * line.product!.price,
    0,
  );
  const unavailable = entries.some((entry) => entry.checked && !entry.product);
  async function commitQty(id: string, value: string) {
    if (
      !validCatalogQuantity(value) ||
      Number(value) === cart.items[id] ||
      busy
    )
      return;
    const success = await act((next) =>
      next.mutate(owner, {
        type: "set",
        productId: id,
        qty: Number(value),
        expectedRevision: draftQty[id]?.revision ?? cart.itemRevisions[id] ?? 0,
      }),
    );
    if (success)
      setDraftQty((previous) => {
        const next = { ...previous };
        delete next[id];
        return next;
      });
    else
      setDraftQty((previous) => {
        const next = { ...previous };
        delete next[id];
        return next;
      });
  }
  function checkout() {
    if (!store || !chosen.length || invalid || unavailable || cart.pending)
      return;
    try {
      saveCustomerIntent(
        owner,
        createCheckoutIntent(
          store.read(owner),
          chosen.map(({ product, qty }) => ({
            productId: product!.id,
            qty,
            unitPrice: product!.price,
          })),
          "cart",
        ),
      );
      router.push(
        actor?.role === "customer" ? "/checkout" : "/customer/login?next=%2Fcheckout",
      );
    } catch (cause) {
      setProblem(
        cause instanceof Error
          ? cause.message
          : "Keranjang belum siap checkout.",
      );
    }
  }
  if (error) return <CatalogError />;
  if (sessionError)
    return (
      <ShopEmpty
        title="Sesi belum dapat diperiksa"
        description="Periksa koneksi lalu muat ulang untuk membuka keranjang."
        action="Muat ulang"
        onAction={() => window.location.reload()}
      />
    );
  if ((loading && !products.length) || !cartReady)
    return <ShopLoading label="Menyiapkan keranjang…" />;
  if (!entries.length)
    return (
      <ShopEmpty
        title="Keranjang masih kosong"
        description="Temukan kebutuhan pantry, rapat, atau merchandise. Pilihan Anda akan tampil di sini."
      />
    );
  return (
    <div className="shop-cart-page">
      <div className="shop-breadcrumb">
        <Link href="/shop">
          <ArrowLeft size={15} /> Lanjut belanja
        </Link>
        <span>/</span>
        <span>Keranjang</span>
      </div>
      <div className="shop-page-heading">
        <div>
          <h1>Keranjang belanja</h1>
          <p>
            {entries.length} jenis barang. Pilih barang yang ingin dipesan
            sekarang.
          </p>
        </div>
      </div>
      <ShoppingProgress stage="cart" />
      {!actor && (
        <div className="shop-notice">
          <ShoppingCart size={19} />
          <span>
            Keranjang pengunjung. Masuk sebagai pelanggan saat melanjutkan
            checkout; pilihan barang tetap dibawa.
          </span>
        </div>
      )}
      {cart.pending && (
        <div className="shop-notice" role="status">
          <CircleAlert size={19} />
          <span>
            Pengajuan sebelumnya belum dipastikan. Isi keranjang dijaga sampai
            pengajuan selesai.
          </span>
          <Link href="/checkout">Buka checkout</Link>
        </div>
      )}
      <div className="shop-cart-layout">
        <section className="shop-cart-items" aria-label="Barang di keranjang">
          <label className="shop-select-all">
            <input
              type="checkbox"
              checked={entries.every((entry) => entry.checked)}
              onChange={(event) =>
                setSelected(
                  Object.fromEntries(
                    entries.map((entry) => [entry.id, event.target.checked]),
                  ),
                )
              }
            />
            Pilih semua ({entries.length} barang)
          </label>
          {entries.map(({ id, qty, product, checked }) => (
            <article className="shop-cart-row" key={id}>
              <label className="shop-item-select">
                <input
                  type="checkbox"
                  aria-label={`Pilih ${product?.name || id}`}
                  checked={checked}
                  onChange={(event) =>
                    setSelected((previous) => ({
                      ...previous,
                      [id]: event.target.checked,
                    }))
                  }
                />
              </label>
              {product ? (
                <>
                  <Link
                    href={`/shop/${id}?from=%2Fshop`}
                    className="shop-cart-photo"
                    aria-label={`Lihat ${product.name}`}
                  >
                    <Art src={product.image} alt="" />
                  </Link>
                  <div className="shop-cart-copy">
                    <h2>
                      <Link href={`/shop/${id}`}>
                        {product.name.replace(/ · paket \d+ .+$/, "")}
                      </Link>
                    </h2>
                    <label className="shop-cart-packaging">
                      Kemasan
                      <select
                        value={id}
                        disabled={busy || Boolean(cart.pending)}
                        onChange={(event) =>
                          void act(
                            (next) =>
                              next.replaceSku(
                                owner,
                                id,
                                event.target.value,
                                cart.itemRevisions[id] || 0,
                              ),
                            "Kemasan keranjang diperbarui.",
                          )
                        }
                      >
                        {products
                          .filter((item) => item.familyId === product.familyId)
                          .map((item) => (
                            <option key={item.id} value={item.id}>
                              {item.packaging} — {rupiah(item.price)}
                            </option>
                          ))}
                      </select>
                    </label>
                    <p className="shop-muted">
                      {rupiah(product.price)} / {product.unit}
                    </p>
                    {qty > product.available && (
                      <p className="shop-field-error">
                        Stok saat ini {product.available} {product.unit}.
                        Kurangi jumlah sebelum checkout.
                      </p>
                    )}
                    <div className="shop-cart-row-controls">
                      <ShopQuantity
                        id={`cart-${id}`}
                        value={draftQty[id]?.value ?? String(qty)}
                        onChange={(value) =>
                          setDraftQty((previous) => ({
                            ...previous,
                            [id]: {
                              value,
                              revision:
                                previous[id]?.revision ??
                                cart.itemRevisions[id] ??
                                0,
                            },
                          }))
                        }
                        onCommit={(value) => void commitQty(id, value)}
                        label={`Jumlah ${product.unit}`}
                        max={Math.max(0, Math.min(10000, product.available))}
                        disabled={busy || Boolean(cart.pending)}
                      />
                      <button
                        className="shop-remove-item"
                        aria-label={`Hapus ${product.name}`}
                        disabled={busy || Boolean(cart.pending)}
                        onClick={() =>
                          void act(
                            (next) =>
                              next.mutate(owner, {
                                type: "remove",
                                productId: id,
                                expectedRevision: cart.itemRevisions[id] || 0,
                              }),
                            "Barang dihapus dari keranjang.",
                          )
                        }
                      >
                        <Trash2 size={17} />
                        <span>Hapus</span>
                      </button>
                    </div>
                  </div>
                  <strong className="shop-cart-row-total">
                    {rupiah(qty * product.price)}
                  </strong>
                </>
              ) : (
                <div className="shop-cart-copy">
                  <h2>Barang tidak tersedia</h2>
                  <p>
                    SKU {id} tidak lagi ditawarkan. Hapus barang ini untuk
                    melanjutkan.
                  </p>
                  <Button
                    variant="outline"
                    disabled={busy}
                    onClick={() =>
                      void act((next) =>
                        next.mutate(owner, {
                          type: "remove",
                          productId: id,
                          expectedRevision: cart.itemRevisions[id] || 0,
                        }),
                      )
                    }
                  >
                    Hapus barang
                  </Button>
                </div>
              )}
            </article>
          ))}
        </section>
        <aside className="shop-order-summary">
          <h2>Ringkasan belanja</h2>
          <dl>
            <div>
              <dt>Barang dipilih</dt>
              <dd>{chosen.length} jenis</dd>
            </div>
            <div className="shop-summary-total">
              <dt>Subtotal</dt>
              <dd>{rupiah(total)}</dd>
            </div>
          </dl>
          <p>
            Tagihan diterbitkan setelah barang diterima. Biaya dan pembayaran
            tidak ditambahkan otomatis.
          </p>
          {invalid && (
            <p className="shop-field-error">
              Periksa jumlah dan stok barang yang dipilih sebelum melanjutkan.
            </p>
          )}
          <div className="shop-cart-checkout" data-shopping-action>
            <div>
              <span>Subtotal ({chosen.length} barang)</span>
              <strong>{rupiah(total)}</strong>
            </div>
            <Button
              className="shop-button-orange"
              disabled={
                !chosen.length ||
                invalid ||
                unavailable ||
                busy ||
                Boolean(cart.pending) ||
                Boolean(actor && actor.role !== "customer")
              }
              onClick={checkout}
            >
              Lanjut checkout <ArrowRight size={17} />
            </Button>
          </div>
          <span className="shop-summary-note">
            <CheckCircle2 size={15} />
            Barang lain tetap di keranjang.
          </span>
        </aside>
      </div>
    </div>
  );
}
