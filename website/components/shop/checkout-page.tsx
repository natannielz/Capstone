"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  ArrowLeft,
  ArrowRight,
  CircleAlert,
  LockKeyhole,
  MapPin,
  ReceiptText,
  Truck,
} from "lucide-react";
import { Art } from "../art";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Textarea } from "../ui/textarea";
import {
  CheckoutRejected,
  clearCustomerDraft,
  createCheckoutIntent,
  customerRequestHash,
  loadCustomerDraft,
  loadCustomerIntent,
  saveCustomerDraft,
  saveCustomerIntent,
  type CustomerDetails,
  type CustomerDraft,
} from "@/lib/client/customer-cart";
import {
  CatalogError,
  ShopEmpty,
  ShopLoading,
  ShopShell,
  useShop,
} from "./shop-shell";
import { rupiah, type PublicProduct } from "./shop-data";

export function StorefrontCheckout() {
  return (
    <ShopShell active="cart">
      <CheckoutContent />
    </ShopShell>
  );
}
function CheckoutContent() {
  const {
    actor,
    sessionLoading,
    sessionError,
    error,
    loading,
    products,
    cartReady,
    owner,
  } = useShop();
  if (sessionLoading) return <ShopLoading label="Memeriksa akun pelanggan…" />;
  if (sessionError)
    return (
      <ShopEmpty
        title="Sesi belum dapat diperiksa"
        description="Muat ulang halaman untuk mencoba kembali. Pilihan barang Anda tetap disimpan di perangkat ini."
        href="/checkout"
        action="Muat ulang"
        onAction={() => window.location.reload()}
      />
    );
  if (!actor)
    return (
      <ShopEmpty
        title="Masuk untuk melanjutkan"
        description="Gunakan akun pelanggan. Pilihan barang tetap tersedia setelah Anda masuk."
        href="/login?next=%2Fcheckout"
        action="Masuk sebagai pelanggan"
      />
    );
  if (actor.role !== "customer")
    return (
      <ShopEmpty
        title="Gunakan portal divisi"
        description="Akun Anda digunakan untuk operasional atau kebutuhan divisi. Checkout ini khusus akun pelanggan."
        href="/workspace?view=catalog"
        action="Buka portal"
      />
    );
  if (error) return <CatalogError />;
  if (!cartReady || (loading && !products.length))
    return <ShopLoading label="Menyiapkan checkout…" />;
  return <CheckoutForm key={owner} />;
}
function CheckoutForm() {
  const { actor, products, refreshCatalog, owner, cart, store } = useShop();
  const [draft, setDraft] = useState<CustomerDraft | null>(() => {
    const intent = loadCustomerIntent(owner);
    if (!intent || !actor) return null;
    return (
      loadCustomerDraft(owner, intent) || {
        intent,
        values: {
          recipientName: actor.name,
          recipientPhone: actor.phone || "",
          address: actor.address || "",
          note: "",
        },
        commandId: crypto.randomUUID(),
      }
    );
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [fieldErrors, setFieldErrors] = useState<
    Partial<Record<keyof CustomerDetails, string>>
  >({});
  const sending = useRef(false);
  const errorRef = useRef<HTMLDivElement>(null);
  const router = useRouter();
  useEffect(() => {
    if (draft) saveCustomerDraft(owner, draft);
  }, [owner, draft]);
  useEffect(() => {
    if (error) errorRef.current?.focus();
  }, [error]);
  const ownPending = Boolean(draft && cart.pending?.id === draft.commandId);
  const otherPending = Boolean(cart.pending && !ownPending);
  const changedPrices =
    draft?.intent.lines.some(
      (line) =>
        products.find((product) => product.id === line.productId)?.price !==
        line.unitPrice,
    ) || false;
  const stockProblem =
    draft?.intent.lines.some((line) => {
      const product = products.find((product) => product.id === line.productId);
      return !product || !product.active || line.qty > product.available;
    }) || false;
  const cartChanged = Boolean(
    draft &&
    (draft.intent.epoch !== cart.epoch ||
      (draft.intent.mode === "cart" &&
        draft.intent.lines.some(
          (line) =>
            (cart.items[line.productId] || 0) < line.qty ||
            (cart.itemRevisions[line.productId] || 0) !==
              (draft.intent.itemRevisions[line.productId] || 0),
        ))),
  );
  function change(key: keyof CustomerDetails, value: string) {
    if (!draft || cart.pending || busy) return;
    const next = {
      ...draft,
      values: { ...draft.values, [key]: value },
      commandId: crypto.randomUUID(),
    };
    setDraft(next);
    saveCustomerDraft(owner, next);
    setFieldErrors((previous) => ({ ...previous, [key]: undefined }));
    setError("");
  }
  function useLatestPrices() {
    if (!draft || !store || cart.pending) return;
    const intent = createCheckoutIntent(
      store.read(owner),
      draft.intent.lines.map((line) => ({
        ...line,
        unitPrice: products.find((product) => product.id === line.productId)!
          .price,
      })),
      draft.intent.mode,
    );
    const next = { ...draft, intent, commandId: crypto.randomUUID() };
    saveCustomerIntent(owner, intent);
    saveCustomerDraft(owner, next);
    setDraft(next);
    setError("");
  }
  function validateStock(fresh: PublicProduct[]) {
    if (!draft) return;
    if (
      draft.intent.lines.some(
        (line) =>
          fresh.find((product) => product.id === line.productId)?.price !==
          line.unitPrice,
      )
    )
      throw new CheckoutRejected(
        "Harga barang berubah. Periksa rincian terbaru sebelum membuat pesanan.",
        409,
      );
    if (
      draft.intent.lines.some((line) => {
        const product = fresh.find((product) => product.id === line.productId);
        return !product || !product.active || line.qty > product.available;
      })
    )
      throw new CheckoutRejected(
        "Stok barang berubah. Kembali ke keranjang untuk menyesuaikan jumlah.",
        409,
      );
  }
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (sending.current || !draft || !store || actor?.role !== "customer")
      return;
    const errors: typeof fieldErrors = {};
    if (!draft.values.recipientName.trim())
      errors.recipientName = "Isi nama penerima.";
    if (!draft.values.recipientPhone.trim())
      errors.recipientPhone = "Isi nomor kontak penerima.";
    if (!draft.values.address.trim())
      errors.address = "Isi alamat lengkap pengiriman.";
    setFieldErrors(errors);
    if (Object.keys(errors).length) {
      document.getElementById(`checkout-${Object.keys(errors)[0]}`)?.focus();
      return;
    }
    sending.current = true;
    setBusy(true);
    setError("");
    try {
      const sessionResponse = await fetch("/api/session", {
        cache: "no-store",
        signal: AbortSignal.timeout(15000),
      });
      if (!sessionResponse.ok)
        throw new Error(
          "Sesi belum dapat diperiksa. Coba kembali sebelum membuat pesanan.",
        );
      const session = (await sessionResponse.json()) as {
        actor: { id: string; role: string } | null;
      };
      if (session.actor?.role !== "customer" || session.actor.id !== actor?.id)
        throw new CheckoutRejected(
          "Sesi akun berubah atau berakhir. Muat ulang halaman dan masuk dengan akun pemilik keranjang sebelum mencoba kembali.",
          401,
        );
      if (!ownPending) validateStock(await refreshCatalog());
      const data = {
        lines: draft.intent.lines.map((line) => ({
          productId: line.productId,
          qty: line.qty,
          unitPrice: line.unitPrice,
        })),
        recipientName: draft.values.recipientName.trim(),
        recipientPhone: draft.values.recipientPhone.trim(),
        address: draft.values.address.trim(),
        note: draft.values.note.trim(),
      };
      const hash = await customerRequestHash(data);
      const result = await store.checkout(
        owner,
        draft.intent,
        draft.commandId,
        hash,
        async () => {
          const response = await fetch("/api/commands", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              id: draft.commandId,
              type: "order.create",
              data,
            }),
            signal: AbortSignal.timeout(30000),
          });
          const result = (await response.json()) as {
            id: string;
            message: string;
            error?: string;
          };
          if (!response.ok) {
            if (response.status < 500)
              throw new CheckoutRejected(
                result.error || "Periksa data pesanan dan coba kembali.",
                response.status,
              );
            throw new Error(
              result.error || "Layanan belum memastikan pengajuan.",
            );
          }
          return result;
        },
      );
      clearCustomerDraft(owner);
      router.replace(
        `/account/orders/${encodeURIComponent(result.id)}?created=1`,
      );
    } catch (cause) {
      setError(
        cause instanceof CheckoutRejected
          ? cause.message
          : cause instanceof Error &&
              cause.name !== "TypeError" &&
              cause.name !== "TimeoutError"
            ? cause.message
            : "Jawaban server belum diterima. Isi pesanan tetap disimpan; coba pengajuan yang sama kembali agar tidak tercatat dua kali.",
      );
      if (cause instanceof CheckoutRejected && cause.status === 409)
        void refreshCatalog().catch(() => undefined);
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }
  if (!draft)
    return (
      <ShopEmpty
        title="Belum ada barang untuk dipesan"
        description="Pilih barang dari keranjang atau gunakan Beli sekarang pada halaman produk."
        href="/cart"
        action="Buka keranjang"
      />
    );
  const total = draft.intent.lines.reduce(
    (sum, line) => sum + line.qty * line.unitPrice,
    0,
  );
  const disabled =
    busy ||
    otherPending ||
    (!ownPending && (changedPrices || stockProblem || cartChanged));
  return (
    <div className="shop-checkout-page">
      <div className="shop-breadcrumb">
        <Link
          href={
            draft.intent.mode === "cart"
              ? "/cart"
              : `/shop/${draft.intent.lines[0]?.productId}`
          }
        >
          <ArrowLeft size={15} /> Ubah barang
        </Link>
        <span>/</span>
        <span>Checkout</span>
      </div>
      <div className="shop-page-heading">
        <div>
          <h1>Periksa pesanan Anda</h1>
          <p>Lengkapi penerima, lalu periksa barang sebelum membuat pesanan.</p>
        </div>
        <span className="shop-secure">
          <LockKeyhole size={16} />
          Akun {actor?.name}
        </span>
      </div>
      {error && (
        <div
          className="shop-notice shop-notice-error"
          ref={errorRef}
          tabIndex={-1}
          role="alert"
        >
          <CircleAlert size={20} />
          <span>{error}</span>
        </div>
      )}
      {cart.pending && (
        <div className="shop-notice">
          <CircleAlert size={20} />
          <span>
            {ownPending
              ? "Pengajuan sebelumnya belum dipastikan. Isian dijaga agar Anda dapat mencoba pengajuan yang sama kembali."
              : "Ada pengajuan yang belum dipastikan di tab lain. Lanjutkan dari tab tersebut sebelum membuat pesanan baru."}
          </span>
        </div>
      )}
      {!ownPending && (cartChanged || stockProblem) && (
        <div className="shop-notice shop-notice-error" role="alert">
          <CircleAlert size={20} />
          <span>
            {cartChanged
              ? "Barang yang dipilih berubah di tab lain. Periksa kembali pilihan Anda."
              : "Sebagian barang tidak tersedia dalam jumlah yang dipilih."}
          </span>
          <Link
            href={
              draft.intent.mode === "cart"
                ? "/cart"
                : `/shop/${draft.intent.lines[0]?.productId}`
            }
          >
            Ubah barang
          </Link>
        </div>
      )}
      {!ownPending && changedPrices && !stockProblem && (
        <div className="shop-price-change" role="alert">
          <div>
            <CircleAlert size={20} />
            <span>
              Harga telah diperbarui. Periksa rincian di bawah sebelum
              melanjutkan.
            </span>
          </div>
          <Button
            variant="outline"
            onClick={useLatestPrices}
            disabled={Boolean(cart.pending)}
          >
            Gunakan harga terbaru
          </Button>
        </div>
      )}
      <form
        method="post"
        onSubmit={submit}
        noValidate
        className="shop-checkout-layout"
      >
        <div className="shop-checkout-sections">
          <section className="shop-form-section">
            <h2>
              <MapPin size={21} />
              Penerima & alamat
            </h2>
            <div className="shop-recipient-grid">
              {(
                [
                  {
                    key: "recipientName",
                    label: "Nama penerima",
                    type: "text",
                    autoComplete: "name",
                    max: 100,
                  },
                  {
                    key: "recipientPhone",
                    label: "Nomor kontak",
                    type: "tel",
                    autoComplete: "tel",
                    max: 30,
                  },
                ] as const
              ).map((field) => (
                <div className="shop-form-field" key={field.key}>
                  <label htmlFor={`checkout-${field.key}`}>{field.label}</label>
                  <Input
                    id={`checkout-${field.key}`}
                    type={field.type}
                    autoComplete={field.autoComplete}
                    maxLength={field.max}
                    required
                    value={draft.values[field.key]}
                    disabled={busy || Boolean(cart.pending)}
                    aria-invalid={Boolean(fieldErrors[field.key])}
                    aria-describedby={
                      fieldErrors[field.key] ? `${field.key}-error` : undefined
                    }
                    onChange={(event) => change(field.key, event.target.value)}
                  />
                  {fieldErrors[field.key] && (
                    <p className="shop-field-error" id={`${field.key}-error`}>
                      {fieldErrors[field.key]}
                    </p>
                  )}
                </div>
              ))}
            </div>
            <div className="shop-form-field">
              <label htmlFor="checkout-address">Alamat pengiriman</label>
              <Textarea
                id="checkout-address"
                autoComplete="street-address"
                maxLength={2000}
                required
                rows={3}
                value={draft.values.address}
                disabled={busy || Boolean(cart.pending)}
                aria-invalid={Boolean(fieldErrors.address)}
                aria-describedby="address-help"
                onChange={(event) => change("address", event.target.value)}
              />
              <p
                id="address-help"
                className={
                  fieldErrors.address ? "shop-field-error" : "shop-field-help"
                }
              >
                {fieldErrors.address ||
                  "Cantumkan jalan, gedung, lantai, kota, dan petunjuk penerima bila perlu."}
              </p>
            </div>
            <div className="shop-form-field">
              <label htmlFor="checkout-note">
                Catatan pesanan <span>(opsional)</span>
              </label>
              <Textarea
                id="checkout-note"
                rows={2}
                maxLength={2000}
                value={draft.values.note}
                disabled={busy || Boolean(cart.pending)}
                onChange={(event) => change("note", event.target.value)}
              />
            </div>
          </section>
          <section className="shop-form-section">
            <h2>
              <ShoppingBagIcon />
              Barang pesanan
            </h2>
            <ul className="shop-checkout-lines">
              {draft.intent.lines.map((line) => {
                const product = products.find(
                  (product) => product.id === line.productId,
                );
                return (
                  <li key={line.productId}>
                    {product && <Art src={product.image} alt="" />}
                    <div>
                      <strong>
                        {product?.name || "Barang tidak tersedia"}
                      </strong>
                      <span>
                        {product?.packaging} · {line.qty} {product?.unit}
                      </span>
                      <span>
                        {rupiah(line.unitPrice)} / {product?.unit}
                        {product &&
                          product.price !== line.unitPrice &&
                          !ownPending && (
                            <b className="shop-new-price">
                              Harga terbaru {rupiah(product.price)}
                            </b>
                          )}
                      </span>
                    </div>
                    <strong>{rupiah(line.qty * line.unitPrice)}</strong>
                  </li>
                );
              })}
            </ul>
          </section>
        </div>
        <aside className="shop-order-summary shop-checkout-summary">
          <h2>Ringkasan pesanan</h2>
          <dl>
            <div>
              <dt>Barang</dt>
              <dd>{draft.intent.lines.length} jenis</dd>
            </div>
            <div className="shop-summary-total">
              <dt>Total estimasi</dt>
              <dd>{rupiah(total)}</dd>
            </div>
          </dl>
          <div className="shop-checkout-payment">
            <ReceiptText size={22} />
            <div>
              <h3>Bayar setelah invoice terbit</h3>
              <p>
                Belum ada pembayaran pada tahap ini. Tagihan mengikuti jumlah
                barang yang diterima dan difinalkan toko.
              </p>
            </div>
          </div>
          <p className="shop-checkout-shipping">
            <Truck size={18} />
            Pengiriman internal Unit Toko. Jadwal dikonfirmasi oleh toko setelah
            pesanan ditinjau.
          </p>
          <div className="shop-checkout-submit" data-shopping-action>
            <div>
              <span>Total estimasi</span>
              <strong>{rupiah(total)}</strong>
            </div>
            <Button
              type="submit"
              className="shop-button-orange"
              disabled={disabled}
            >
              {busy
                ? "Membuat pesanan…"
                : ownPending
                  ? "Coba pengajuan lagi"
                  : "Buat pesanan"}
              {!busy && <ArrowRight size={17} />}
            </Button>
          </div>
          <p className="shop-summary-note">
            Transaksi demo, tanpa pembayaran nyata.
          </p>
        </aside>
      </form>
    </div>
  );
}
function ShoppingBagIcon() {
  return <ReceiptText size={21} />;
}
