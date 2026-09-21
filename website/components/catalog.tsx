"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import {
  AlertCircle,
  ArrowLeft,
  Check,
  ChevronRight,
  Minus,
  Plus,
  Search,
  ShoppingBag,
  X,
} from "lucide-react";
import { toast } from "sonner";
import { Art } from "./art";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import {
  Field,
  FieldDescription,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field";
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from "@/components/ui/input-group";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from "@/components/ui/empty";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  CATALOG_COLLECTIONS,
  CATALOG_GROUPS,
  chooseCatalogProduct,
  isCatalogCollection,
  productDescription,
  productFamilies,
  productFamilyId,
  productGroup,
  productImage,
  productPackaging,
  resolveCatalogProduct,
  validCatalogQuantity,
  type CatalogCollection,
  type ProductFamily,
} from "@/lib/domain/catalog";
import { money, productAvailable, sum, today } from "@/lib/domain/selectors";
import type { CommandResult, Product, State } from "@/lib/domain/model";
import type { WorkspaceContext } from "./workspace";
import {
  CartConflictError,
  clearCheckoutDraft,
  getCartStore,
  loadCheckoutDraft,
  saveCheckoutDraft,
  subscribeCart,
  type CartMutation,
  type CheckoutDraft,
  type CheckoutValues,
} from "@/lib/client/cart-storage";

type BasketLine = { product: Product; qty: number };
type CatalogQuery = {
  q: string;
  category: string;
  collection: CatalogCollection | "";
  product: string;
};
const EMPTY_QUERY: CatalogQuery = {
  q: "",
  category: "Semua",
  collection: "",
  product: "",
};

function readQuery(products: Product[]): CatalogQuery {
  const query = new URLSearchParams(window.location.search);
  const category = query.get("category");
  const collection = query.get("collection");
  return {
    q: (query.get("q") || "").slice(0, 120),
    category: CATALOG_GROUPS.some((group) => group === category)
      ? category!
      : "Semua",
    collection: isCatalogCollection(collection) ? collection : "",
    product: resolveCatalogProduct(products, query.get("product"))?.id || "",
  };
}

function writeQuery(query: CatalogQuery) {
  const url = new URL(window.location.href);
  for (const [key, value] of Object.entries(query)) {
    if (value && !(key === "category" && value === "Semua"))
      url.searchParams.set(key, value);
    else url.searchParams.delete(key);
  }
  window.history.replaceState(
    window.history.state,
    "",
    `${url.pathname}${url.search}${url.hash}`,
  );
}

function QuantityInput({
  id,
  name,
  unit,
  value,
  onChange,
}: {
  id: string;
  name: string;
  unit: string;
  value: string;
  onChange: (value: string) => void;
}) {
  const valid = validCatalogQuantity(value);
  const quantity = valid ? Number(value) : 1;
  return (
    <Field
      className="catalog-quantity-field"
      data-invalid={!valid || undefined}
    >
      <FieldLabel htmlFor={id}>Jumlah ({unit})</FieldLabel>
      <InputGroup className="catalog-quantity-input">
        <InputGroupAddon align="inline-start">
          <InputGroupButton
            size="icon-sm"
            aria-label={`Kurangi ${name}`}
            disabled={valid && quantity <= 1}
            onClick={() => onChange(String(Math.max(1, quantity - 1)))}
          >
            <Minus data-icon="inline-start" />
          </InputGroupButton>
        </InputGroupAddon>
        <InputGroupInput
          id={id}
          type="text"
          inputMode="numeric"
          pattern="[0-9]+"
          value={value}
          aria-label={`Jumlah ${name} dalam ${unit}`}
          aria-invalid={!valid}
          aria-describedby={!valid ? `${id}-error` : undefined}
          onChange={(event) => onChange(event.target.value)}
        />
        <InputGroupAddon align="inline-end">
          <InputGroupButton
            size="icon-sm"
            aria-label={`Tambah ${name}`}
            disabled={valid && quantity >= 10000}
            onClick={() => onChange(String(Math.min(10000, quantity + 1)))}
          >
            <Plus data-icon="inline-start" />
          </InputGroupButton>
        </InputGroupAddon>
      </InputGroup>
      {!valid && (
        <FieldDescription id={`${id}-error`}>
          Masukkan bilangan bulat 1–10.000.
        </FieldDescription>
      )}
    </Field>
  );
}

function PackagingSelect({
  family,
  product,
  id,
  onChange,
}: {
  family: ProductFamily;
  product: Product;
  id: string;
  onChange: (id: string) => void;
}) {
  return (
    <Field className="catalog-packaging">
      <FieldLabel htmlFor={id}>Kemasan pesanan</FieldLabel>
      <select
        className="native-select"
        id={id}
        value={product.id}
        onChange={(event) => onChange(event.target.value)}
      >
        {family.products.map((variant) => (
          <option key={variant.id} value={variant.id}>
            {productPackaging(variant)}
          </option>
        ))}
      </select>
    </Field>
  );
}

function StockNotice({
  s,
  product,
  qty,
}: {
  s: State;
  product: Product;
  qty?: number;
}) {
  const stock = productAvailable(s, product.id);
  return (
    <p
      className={
        stock > 0 ? "catalog-stock" : "catalog-stock catalog-stock-pending"
      }
    >
      {stock > 0
        ? `${stock} ${product.unit} tersedia`
        : "Bisa dipesan · stok menyusul"}
      {qty !== undefined && qty > stock && (
        <span>
          {qty - stock} {product.unit} menunggu pemenuhan toko.
        </span>
      )}
    </p>
  );
}

export function Catalog(context: WorkspaceContext) {
  return <CatalogContent key={context.actor.id} {...context} />;
}

function CatalogContent({ s, actor, go, refresh }: WorkspaceContext) {
  const [store] = useState(() => getCartStore(actor.id));
  const [snapshot, setSnapshot] = useState(() => store.read());
  const observedClear = useRef(snapshot.lastClear);
  const cart = snapshot.items;
  const [query, setQuery] = useState<CatalogQuery>(EMPTY_QUERY);
  const [selected, setSelected] = useState<Record<string, string>>({});
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const draftRevisions = useRef<Record<string, number>>({});
  const [cartBusy, setCartBusy] = useState(0);
  const [cartError, setCartError] = useState("");
  const [checkoutOpen, setCheckoutOpen] = useState(false);
  const [detailQty, setDetailQty] = useState("1");
  const [checkoutDraft, setCheckoutDraft] = useState(() => {
    const fallback = freshCheckoutDraft(
      s,
      actor.divisionId,
      snapshot.lastClear,
    );
    const saved = loadCheckoutDraft(actor.id, fallback);
    const restored = { ...saved, values: { ...saved.values } };
    if (actor.role === "pic")
      restored.values.divisionId = actor.divisionId || "";
    if (
      !s.divisions.some(
        (division) => division.id === restored.values.divisionId,
      )
    )
      restored.values.divisionId = "";
    return restored;
  });
  const storageWarning = !store.persistent;
  const basketRef = useRef<HTMLElement>(null);
  const productsRef = useRef(s.products);
  const stateRef = useRef(s);
  useEffect(() => {
    productsRef.current = s.products;
    stateRef.current = s;
  }, [s]);

  useEffect(() => {
    function sync() {
      const next = readQuery(productsRef.current);
      setQuery(next);
      setDetailQty("1");
      const product = resolveCatalogProduct(productsRef.current, next.product);
      if (product)
        setSelected((previous) => ({
          ...previous,
          [productFamilyId(product)]: product.id,
        }));
    }
    sync();
    window.addEventListener("popstate", sync);
    return () => window.removeEventListener("popstate", sync);
  }, []);

  useEffect(() => {
    const sync = () => {
      const next = store.read();
      setSnapshot(next);
      if (next.lastClear && next.lastClear !== observedClear.current) {
        observedClear.current = next.lastClear;
        clearCheckoutDraft(actor.id);
        setDrafts({});
        draftRevisions.current = {};
        setCheckoutOpen(false);
        setCheckoutDraft(
          freshCheckoutDraft(
            stateRef.current,
            actor.divisionId,
            next.lastClear,
          ),
        );
      }
    };
    const unsubscribe = subscribeCart(actor.id, sync);
    sync();
    window.addEventListener("focus", sync);
    return () => {
      unsubscribe();
      window.removeEventListener("focus", sync);
    };
  }, [store, actor.id, actor.divisionId]);

  const families = productFamilies(s.products);
  const search = query.q.trim().toLocaleLowerCase("id-ID");
  const items = families.filter(
    (family) =>
      (query.category === "Semua" ||
        productGroup(family.products[0]) === query.category) &&
      (!query.collection ||
        (
          CATALOG_COLLECTIONS[query.collection].products as readonly string[]
        ).includes(family.id)) &&
      family.products.some((product) =>
        `${product.name} ${product.sku}`
          .toLocaleLowerCase("id-ID")
          .includes(search),
      ),
  );
  const entries: BasketLine[] = Object.entries(cart).flatMap(([id, qty]) => {
    const product = s.products.find((item) => item.id === id && item.active);
    return product && validCatalogQuantity(qty) ? [{ product, qty }] : [];
  });
  const invalidQuantity = entries.some(
    ({ product, qty }) => !validCatalogQuantity(drafts[product.id] ?? qty),
  );
  const total = sum(entries.map(({ product, qty }) => product.price * qty));
  const detail = resolveCatalogProduct(s.products, query.product);
  const detailFamily =
    detail && families.find((family) => family.id === productFamilyId(detail));

  function updateQuery(changes: Partial<CatalogQuery>) {
    const next = { ...query, ...changes };
    setQuery(next);
    writeQuery(next);
  }

  function chosenProduct(family: ProductFamily) {
    return chooseCatalogProduct(family, selected[family.id], search);
  }

  function selectPackaging(family: ProductFamily, id: string) {
    setSelected((previous) => ({ ...previous, [family.id]: id }));
    const exact = family.products.find(
      (product) => product.sku.toLocaleLowerCase("id-ID") === search,
    );
    if (exact && exact.id !== id) updateQuery({ q: "" });
  }

  async function mutateCart(mutation: CartMutation) {
    setCartBusy((count) => count + 1);
    setCartError("");
    try {
      await store.mutate(mutation);
      setSnapshot(store.read());
      return true;
    } catch (error) {
      setSnapshot(store.read());
      if (error instanceof CartConflictError) {
        setDrafts({});
        draftRevisions.current = {};
      }
      setCartError(
        error instanceof Error
          ? error.message
          : "Keranjang belum dapat diperbarui. Coba kembali.",
      );
      return false;
    } finally {
      setCartBusy((count) => count - 1);
    }
  }

  async function add(product: Product, quantity = 1) {
    const saved = await mutateCart({
      type: "add",
      productId: product.id,
      quantity,
    });
    if (saved) {
      setDrafts((previous) => {
        const next = { ...previous };
        delete next[product.id];
        return next;
      });
      delete draftRevisions.current[product.id];
      toast.success(`${product.name} ditambahkan ke keranjang.`);
    }
    return saved;
  }

  async function changeQuantity(id: string, value: string) {
    if (!(id in draftRevisions.current))
      draftRevisions.current[id] = snapshot.itemRevisions[id] || 0;
    setDrafts((previous) => ({ ...previous, [id]: value }));
    if (validCatalogQuantity(value)) {
      const saved = await mutateCart({
        type: "set",
        productId: id,
        quantity: Number(value),
        expectedRevision: draftRevisions.current[id],
      });
      if (saved)
        setDrafts((previous) => {
          if (previous[id] !== value) return previous;
          const next = { ...previous };
          delete next[id];
          delete draftRevisions.current[id];
          return next;
        });
    }
  }

  async function remove(id: string) {
    if (
      await mutateCart({
        type: "remove",
        productId: id,
        expectedRevision: snapshot.itemRevisions[id] || 0,
      })
    ) {
      setDrafts((previous) => {
        const next = { ...previous };
        delete next[id];
        return next;
      });
      delete draftRevisions.current[id];
    }
  }

  function updateCheckoutDraft(values: CheckoutValues) {
    const next = {
      values,
      commandId: crypto.randomUUID(),
      cartClearToken: snapshot.lastClear,
    };
    setCheckoutDraft(next);
    saveCheckoutDraft(actor.id, next);
  }

  function openDetail(product: Product) {
    setDetailQty("1");
    updateQuery({ product: product.id });
  }

  async function orderCreated(result: CommandResult) {
    setDrafts({});
    clearCheckoutDraft(actor.id);
    setCheckoutDraft(
      freshCheckoutDraft(s, actor.divisionId, store.read().lastClear),
    );
    setCheckoutOpen(false);
    await refresh();
    go("orders", result.id, { queue: null, q: null, orderPage: null });
    toast.success(result.message);
  }

  return (
    <div className="catalog-v5">
      {cartError && (
        <Alert variant="destructive">
          <AlertCircle />
          <AlertTitle>Perubahan keranjang belum diterapkan</AlertTitle>
          <AlertDescription>{cartError}</AlertDescription>
        </Alert>
      )}
      {storageWarning && (
        <Alert>
          <AlertCircle />
          <AlertTitle>Keranjang hanya tersimpan di tab ini</AlertTitle>
          <AlertDescription>
            Penyimpanan atau sinkronisasi antartab tidak tersedia. Selesaikan
            pesanan sebelum menutup tab.
          </AlertDescription>
        </Alert>
      )}
      <div className="catalog-toolbar">
        <div className="search-field">
          <Search size={17} aria-hidden="true" />
          <input
            aria-label="Cari barang atau SKU"
            placeholder="Cari barang atau SKU…"
            maxLength={120}
            value={query.q}
            onChange={(event) => updateQuery({ q: event.target.value })}
          />
        </div>
        <span className="catalog-result-count" aria-live="polite">
          {items.length} produk · pilihan kemasan tersedia
        </span>
      </div>
      <div className="catalog-filter-row">
        <ToggleGroup
          type="single"
          value={query.category}
          onValueChange={(category) => category && updateQuery({ category })}
          aria-label="Kategori barang"
          className="catalog-category-options"
          spacing={1}
        >
          {CATALOG_GROUPS.map((group) => (
            <ToggleGroupItem key={group} value={group}>
              {group}
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
        {(query.collection || query.q || query.category !== "Semua") && (
          <div className="catalog-active-filters">
            {query.collection && (
              <Badge variant="secondary">
                Kebutuhan {CATALOG_COLLECTIONS[query.collection].label}
              </Badge>
            )}
            <Button
              variant="ghost"
              size="sm"
              onClick={() =>
                updateQuery({ q: "", category: "Semua", collection: "" })
              }
            >
              <X data-icon="inline-start" />
              Hapus filter
            </Button>
          </div>
        )}
      </div>

      <div className="catalog-layout">
        <div>
          <div className="catalog-grid">
            {items.map((family, index) => {
              const product = chosenProduct(family);
              return (
                <article className="product-card" key={family.id}>
                  <button
                    className="product-art"
                    onClick={() => openDetail(product)}
                    aria-label={`Lihat ${family.name}`}
                  >
                    <Art
                      src={productImage(product)}
                      alt={family.name}
                      loading={index < 2 ? "eager" : "lazy"}
                    />
                  </button>
                  <div className="product-info">
                    <h2>
                      <button onClick={() => openDetail(product)}>
                        {family.name}
                      </button>
                    </h2>
                    <PackagingSelect
                      family={family}
                      product={product}
                      id={`pack-${family.id}`}
                      onChange={(id) => selectPackaging(family, id)}
                    />
                    <p className="catalog-price">
                      <strong>{money(product.price)}</strong> / {product.unit}
                    </p>
                    {(family.id === "air" || family.id === "cup") && (
                      <p className="catalog-package-note">
                        {productDescription(product)}
                      </p>
                    )}
                    <StockNotice s={s} product={product} />
                    <div className="catalog-card-action">
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={(cart[product.id] || 0) >= 10000}
                        onClick={() => add(product)}
                      >
                        <Plus data-icon="inline-start" />
                        Tambah
                      </Button>
                      {cart[product.id] > 0 && (
                        <span>
                          {cart[product.id]} {product.unit} di keranjang
                        </span>
                      )}
                    </div>
                  </div>
                </article>
              );
            })}
          </div>
          {!items.length && (
            <Empty>
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <Search />
                </EmptyMedia>
                <EmptyTitle>Barang belum ditemukan</EmptyTitle>
                <EmptyDescription>
                  Coba nama, SKU, atau kategori lain.
                </EmptyDescription>
              </EmptyHeader>
              <Button
                variant="outline"
                onClick={() =>
                  updateQuery({ q: "", category: "Semua", collection: "" })
                }
              >
                Lihat semua barang
              </Button>
            </Empty>
          )}
        </div>

        <aside
          id="division-basket"
          ref={basketRef}
          tabIndex={-1}
          className="panel basket"
        >
          <div className="panel-title">
            <h2>Keranjang divisi</h2>
            <Badge variant="secondary">{entries.length}</Badge>
          </div>
          {entries.length ? (
            <>
              <ul className="catalog-basket-lines">
                {entries.map(({ product, qty }) => {
                  const value = drafts[product.id] ?? String(qty);
                  const valid = validCatalogQuantity(value);
                  return (
                    <li key={product.id} className="catalog-basket-line">
                      <Art
                        className="basket-thumb"
                        src={productImage(product)}
                        alt=""
                      />
                      <div className="catalog-basket-item">
                        <strong>
                          {families.find(
                            (family) => family.id === productFamilyId(product),
                          )?.name || product.name}
                        </strong>
                        <span>
                          {productPackaging(product)} · {money(product.price)} /{" "}
                          {product.unit}
                        </span>
                        <QuantityInput
                          id={`qty-${product.id}`}
                          name={product.name}
                          unit={product.unit}
                          value={value}
                          onChange={(value) =>
                            changeQuantity(product.id, value)
                          }
                        />
                        <div className="catalog-line-subtotal">
                          <span>Subtotal</span>
                          <strong>
                            {valid
                              ? money(product.price * qty)
                              : "Periksa jumlah"}
                          </strong>
                        </div>
                        <StockNotice
                          s={s}
                          product={product}
                          qty={valid ? qty : undefined}
                        />
                      </div>
                      <Button
                        className="catalog-remove"
                        variant="ghost"
                        size="icon"
                        aria-label={`Hapus ${product.name}`}
                        onClick={() => remove(product.id)}
                      >
                        <X />
                      </Button>
                    </li>
                  );
                })}
              </ul>
              <div className="basket-total">
                <span>Total estimasi</span>
                <strong>
                  {invalidQuantity ? "Periksa jumlah" : money(total)}
                </strong>
              </div>
              <div className="basket-action">
                <Button
                  disabled={invalidQuantity || cartBusy > 0}
                  onClick={() => {
                    saveCheckoutDraft(actor.id, checkoutDraft);
                    setCheckoutOpen(true);
                  }}
                >
                  {cartBusy > 0 ? "Menyimpan keranjang…" : "Lanjutkan pesanan"}
                  <ChevronRight data-icon="inline-end" />
                </Button>
                <small>
                  Jumlah dan jadwal pengiriman dikonfirmasi oleh toko.
                </small>
              </div>
            </>
          ) : (
            <Empty>
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <ShoppingBag />
                </EmptyMedia>
                <EmptyTitle>Keranjang masih kosong</EmptyTitle>
                <EmptyDescription>
                  Pilih kemasan barang, lalu klik Tambah.
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          )}
        </aside>
      </div>

      {entries.length > 0 && (
        <div className="mobile-cart-summary">
          <div>
            <strong>{invalidQuantity ? "Periksa jumlah" : money(total)}</strong>
            <span>{entries.length} pilihan barang</span>
          </div>
          <Button
            onClick={() => {
              basketRef.current?.scrollIntoView({
                behavior: window.matchMedia("(prefers-reduced-motion: reduce)")
                  .matches
                  ? "instant"
                  : "smooth",
                block: "start",
              });
              basketRef.current?.focus({ preventScroll: true });
            }}
          >
            Lihat keranjang
            <ChevronRight data-icon="inline-end" />
          </Button>
        </div>
      )}
      <p className="profile-note">
        Harga tercantum per satuan pesan. Katalog menggunakan data dan foto
        simulasi.
      </p>

      {detail && detailFamily && (
        <Dialog
          open
          onOpenChange={(open) => !open && updateQuery({ product: "" })}
        >
          <DialogContent className="catalog-product-dialog">
            <DialogHeader>
              <DialogTitle>{detailFamily.name}</DialogTitle>
              <DialogDescription>
                Pilih kemasan dan jumlah yang dibutuhkan divisi.
              </DialogDescription>
            </DialogHeader>
            <div className="catalog-detail-body">
              <Art
                className="catalog-detail-photo"
                src={productImage(detail)}
                alt={detailFamily.name}
              />
              <FieldGroup>
                <PackagingSelect
                  family={detailFamily}
                  product={detail}
                  id="detail-packaging"
                  onChange={(id) => {
                    updateQuery({ product: id });
                    setSelected((previous) => ({
                      ...previous,
                      [detailFamily.id]: id,
                    }));
                  }}
                />
                <p className="catalog-price">
                  <strong>{money(detail.price)}</strong> / {detail.unit}
                </p>
                <p className="catalog-package-note">
                  {productDescription(detail)}
                </p>
                <StockNotice
                  s={s}
                  product={detail}
                  qty={
                    validCatalogQuantity(detailQty)
                      ? Number(detailQty)
                      : undefined
                  }
                />
                <QuantityInput
                  id="detail-quantity"
                  name={detail.name}
                  unit={detail.unit}
                  value={detailQty}
                  onChange={setDetailQty}
                />
                <div className="catalog-line-subtotal">
                  <span>Subtotal tambahan</span>
                  <strong>
                    {validCatalogQuantity(detailQty)
                      ? money(detail.price * Number(detailQty))
                      : "Periksa jumlah"}
                  </strong>
                </div>
                <p className="catalog-source">
                  SKU {detail.sku} · Sumber barang {detail.category}
                </p>
              </FieldGroup>
            </div>
            <DialogFooter>
              <Button
                variant="outline"
                onClick={() => updateQuery({ product: "" })}
              >
                Tutup
              </Button>
              <Button
                disabled={
                  !validCatalogQuantity(detailQty) ||
                  (cart[detail.id] || 0) + Number(detailQty) > 10000
                }
                onClick={async () => {
                  if (await add(detail, Number(detailQty)))
                    updateQuery({ product: "" });
                }}
              >
                <Plus data-icon="inline-start" />
                Tambahkan ke keranjang
              </Button>
            </DialogFooter>
            {validCatalogQuantity(detailQty) &&
              (cart[detail.id] || 0) + Number(detailQty) > 10000 && (
                <p className="catalog-package-note" role="status">
                  Maksimal 10.000 {detail.unit} untuk barang ini dalam
                  keranjang.
                </p>
              )}
          </DialogContent>
        </Dialog>
      )}

      {checkoutOpen && (
        <CheckoutDialog
          s={s}
          actor={actor}
          lines={entries}
          draft={checkoutDraft}
          onDraftChange={updateCheckoutDraft}
          cartRevision={snapshot.revision}
          commitCheckout={(revision, submit) =>
            store.checkout(revision, submit)
          }
          close={() => setCheckoutOpen(false)}
          onSuccess={orderCreated}
        />
      )}
    </div>
  );
}

function recordingDate(s: State) {
  const closed = s.periods
    .filter((period) => period.status === "closed")
    .map((period) => period.id)
    .sort()
    .at(-1);
  if (!closed || closed < today().slice(0, 7)) return today();
  const [year, month] = closed.split("-").map(Number);
  return new Date(Date.UTC(year, month, 1)).toISOString().slice(0, 10);
}

function freshCheckoutDraft(
  s: State,
  divisionId?: string | null,
  cartClearToken?: string,
): CheckoutDraft {
  return {
    commandId: crypto.randomUUID(),
    cartClearToken,
    values: {
      divisionId: divisionId || "",
      date: recordingDate(s),
      neededAt: today(),
      address:
        s.divisions.find((division) => division.id === divisionId)?.address ||
        "",
      note: "",
    },
  };
}

function displayDate(date: string) {
  return new Date(`${date}T12:00:00`).toLocaleDateString("id-ID", {
    day: "numeric",
    month: "long",
    year: "numeric",
  });
}

function CheckoutDialog({
  s,
  actor,
  lines,
  close,
  onSuccess,
  draft,
  onDraftChange,
  cartRevision,
  commitCheckout,
}: Pick<WorkspaceContext, "s" | "actor"> & {
  lines: BasketLine[];
  close: () => void;
  onSuccess: (result: CommandResult) => Promise<void>;
  draft: CheckoutDraft;
  onDraftChange: (values: CheckoutValues) => void;
  cartRevision: number;
  commitCheckout: (
    revision: number,
    submit: () => Promise<CommandResult>,
  ) => Promise<CommandResult>;
}) {
  const [step, setStep] = useState<"details" | "review">("details");
  const { values, commandId } = draft;
  const [reviewRevision, setReviewRevision] = useState(cartRevision);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const submitting = useRef(false);
  const reviewHeading = useRef<HTMLHeadingElement>(null);
  const errorRef = useRef<HTMLDivElement>(null);
  const firstField = useRef<HTMLInputElement>(null);
  const total = sum(lines.map(({ product, qty }) => product.price * qty));
  const division = s.divisions.find((item) => item.id === values.divisionId);
  const hasShortage = lines.some(
    ({ product, qty }) => qty > productAvailable(s, product.id),
  );

  useEffect(() => {
    if (error) errorRef.current?.focus();
  }, [error]);

  function change(key: keyof typeof values, value: string) {
    onDraftChange({
      ...values,
      [key]: value,
      ...(key === "divisionId"
        ? {
            address:
              s.divisions.find((item) => item.id === value)?.address || "",
          }
        : {}),
    });
    setError("");
  }

  function review(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!values.address.trim()) {
      setError("Isi titik pengiriman untuk pesanan ini.");
      return;
    }
    setStep("review");
    setReviewRevision(cartRevision);
    requestAnimationFrame(() => reviewHeading.current?.focus());
  }

  async function submit() {
    if (submitting.current) return;
    submitting.current = true;
    setBusy(true);
    setError("");
    try {
      const result = await commitCheckout(reviewRevision, async () => {
        const response = await fetch("/api/commands", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            id: `${commandId}:${reviewRevision}`,
            type: "order.create",
            date: values.date,
            data: {
              ...(actor.role === "kepala"
                ? { divisionId: values.divisionId }
                : {}),
              neededAt: values.neededAt,
              address: values.address.trim(),
              note: values.note.trim(),
              lines: lines.map(({ product, qty }) => ({
                productId: product.id,
                qty,
              })),
            },
          }),
        });
        const result = (await response.json()) as CommandResult & {
          error?: string;
        };
        if (!response.ok)
          throw new Error(
            result.error ||
              "Pesanan belum dapat disimpan. Periksa data dan coba kembali.",
          );
        return result;
      });
      await onSuccess(result);
    } catch (cause) {
      setError(
        cause instanceof TypeError || cause instanceof SyntaxError
          ? "Koneksi atau layanan belum memberikan jawaban. Coba ajukan kembali; isi pesanan tetap tersimpan."
          : cause instanceof Error
            ? cause.message
            : "Pesanan belum dapat diajukan. Coba kembali; isi pesanan tetap tersimpan.",
      );
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && !busy && close()}>
      <DialogContent
        className="catalog-checkout-dialog"
        showCloseButton={!busy}
        onEscapeKeyDown={(event) => busy && event.preventDefault()}
        onInteractOutside={(event) => event.preventDefault()}
      >
        <DialogHeader>
          <DialogTitle>
            {step === "details"
              ? "Pengiriman pesanan"
              : "Periksa pesanan divisi"}
          </DialogTitle>
          <DialogDescription>
            {step === "details"
              ? "Isi tujuan dan tanggal kebutuhan sebelum memeriksa pesanan."
              : "Pastikan kemasan, jumlah, dan tujuan pengiriman sudah sesuai."}
          </DialogDescription>
        </DialogHeader>
        {step === "details" ? (
          <form className="catalog-checkout-form" onSubmit={review}>
            <div className="catalog-checkout-body">
              <FieldGroup>
                {actor.role === "kepala" ? (
                  <Field>
                    <FieldLabel htmlFor="checkout-division">
                      Divisi pemesan
                    </FieldLabel>
                    <select
                      id="checkout-division"
                      className="native-select"
                      required
                      value={values.divisionId}
                      onChange={(event) =>
                        change("divisionId", event.target.value)
                      }
                    >
                      <option value="">Pilih divisi…</option>
                      {s.divisions.map((item) => (
                        <option key={item.id} value={item.id}>
                          {item.name}
                        </option>
                      ))}
                    </select>
                  </Field>
                ) : (
                  <p className="catalog-checkout-division">
                    Divisi pemesan <strong>{division?.name}</strong>
                  </p>
                )}
                <Field>
                  <FieldLabel htmlFor="checkout-needed">
                    Tanggal kebutuhan
                  </FieldLabel>
                  <Input
                    ref={firstField}
                    id="checkout-needed"
                    type="date"
                    required
                    value={values.neededAt}
                    onChange={(event) => change("neededAt", event.target.value)}
                  />
                </Field>
                <Field>
                  <FieldLabel htmlFor="checkout-address">
                    Titik pengiriman
                  </FieldLabel>
                  <Textarea
                    id="checkout-address"
                    required
                    maxLength={2000}
                    value={values.address}
                    onChange={(event) => change("address", event.target.value)}
                  />
                  <FieldDescription>
                    Tulis gedung, lantai, atau ruangan penerima.
                  </FieldDescription>
                </Field>
                <Field>
                  <FieldLabel htmlFor="checkout-note">
                    Catatan pesanan <span>(opsional)</span>
                  </FieldLabel>
                  <Textarea
                    id="checkout-note"
                    maxLength={2000}
                    value={values.note}
                    onChange={(event) => change("note", event.target.value)}
                  />
                </Field>
                <Field>
                  <FieldLabel htmlFor="checkout-date">
                    Tanggal pencatatan
                  </FieldLabel>
                  <Input
                    id="checkout-date"
                    type="date"
                    required
                    value={values.date}
                    onChange={(event) => change("date", event.target.value)}
                  />
                </Field>
              </FieldGroup>
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={close}>
                Kembali ke keranjang
              </Button>
              <Button type="submit">
                Periksa pesanan
                <ChevronRight data-icon="inline-end" />
              </Button>
            </DialogFooter>
          </form>
        ) : (
          <>
            <div className="catalog-checkout-body">
              <h3
                className="catalog-review-heading"
                tabIndex={-1}
                ref={reviewHeading}
              >
                Ringkasan pengajuan
              </h3>
              <dl className="catalog-review-destination">
                <div>
                  <dt>Divisi pemesan</dt>
                  <dd>{division?.name}</dd>
                </div>
                <div>
                  <dt>Tanggal kebutuhan</dt>
                  <dd>{displayDate(values.neededAt)}</dd>
                </div>
                <div>
                  <dt>Titik pengiriman</dt>
                  <dd>{values.address}</dd>
                </div>
                {values.note.trim() && (
                  <div>
                    <dt>Catatan</dt>
                    <dd>{values.note}</dd>
                  </div>
                )}
                <div>
                  <dt>Tanggal pencatatan</dt>
                  <dd>{displayDate(values.date)}</dd>
                </div>
              </dl>
              <ul className="catalog-review-lines">
                {lines.map(({ product, qty }) => (
                  <li key={product.id}>
                    <Art src={productImage(product)} alt="" />
                    <div>
                      <strong>{product.name}</strong>
                      <span>
                        {productPackaging(product)} · {qty} {product.unit} ×{" "}
                        {money(product.price)}
                      </span>
                      <StockNotice s={s} product={product} qty={qty} />
                    </div>
                    <strong>{money(product.price * qty)}</strong>
                  </li>
                ))}
              </ul>
              <div className="catalog-review-total">
                <span>Total estimasi</span>
                <strong>{money(total)}</strong>
              </div>
              <Alert>
                <AlertCircle />
                <AlertTitle>
                  {hasShortage
                    ? "Sebagian barang menunggu pemenuhan"
                    : "Pesanan akan ditinjau toko"}
                </AlertTitle>
                <AlertDescription>
                  {hasShortage
                    ? "Pesanan tetap dapat diajukan. Toko akan mengonfirmasi ketersediaan dan dapat mengirim barang secara bertahap."
                    : "Toko akan mengonfirmasi ketersediaan dan jadwal pengiriman."}{" "}
                  Tagihan mengikuti barang yang diterima divisi.
                </AlertDescription>
              </Alert>
            </div>
            <DialogFooter>
              <Button
                type="button"
                variant="outline"
                disabled={busy}
                onClick={() => {
                  setStep("details");
                  requestAnimationFrame(() => firstField.current?.focus());
                }}
              >
                <ArrowLeft data-icon="inline-start" />
                Ubah pengiriman
              </Button>
              <Button
                type="button"
                disabled={
                  busy || !lines.length || reviewRevision !== cartRevision
                }
                onClick={submit}
              >
                {busy ? "Mengajukan…" : "Ajukan pesanan"}
                {!busy && <Check data-icon="inline-end" />}
              </Button>
            </DialogFooter>
            {!busy && (
              <Button
                variant="link"
                className="catalog-edit-items"
                onClick={close}
              >
                Ubah barang di keranjang
              </Button>
            )}
          </>
        )}
        {step === "review" && reviewRevision !== cartRevision && (
          <Alert>
            <AlertCircle />
            <AlertTitle>Keranjang berubah di tab lain</AlertTitle>
            <AlertDescription>
              Periksa barang terbaru sebelum mengajukan. Isian pengiriman tetap
              tersimpan.
              <Button variant="link" onClick={close}>
                Kembali ke keranjang
              </Button>
            </AlertDescription>
          </Alert>
        )}
        {error && (
          <Alert variant="destructive" ref={errorRef} tabIndex={-1}>
            <AlertCircle />
            <AlertTitle>Pesanan belum tersimpan</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
      </DialogContent>
    </Dialog>
  );
}
