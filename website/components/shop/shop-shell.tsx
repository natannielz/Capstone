"use client";

import Link from "next/link";
import {
  createContext,
  Suspense,
  useCallback,
  useContext,
  useEffect,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import {
  ArrowUpRight,
  Check,
  CircleAlert,
  ClipboardList,
  Package,
  Search,
  ShoppingBag,
  ShoppingCart,
  Store,
  UserRound,
} from "lucide-react";
import type { Actor } from "@/lib/domain/accounts";
import { Art } from "../art";
import { Button } from "../ui/button";
import { Toaster } from "../ui/sonner";
import {
  customerOwner,
  emptyCustomerCart,
  getCustomerCartStore,
  handoffGuestIntent,
  subscribeCustomerCart,
  type CustomerCart,
  type CustomerCartStore,
} from "@/lib/client/customer-cart";
import type { PublicProduct } from "./shop-data";
import { useShopLocation } from "./use-shop-location";

async function fetchCatalogProducts() {
  const response = await fetch("/api/catalog", {
    cache: "no-store",
    signal: AbortSignal.timeout(15000),
  });
  if (!response.ok)
    throw new Error("Katalog belum dapat dimuat. Coba muat ulang.");
  return ((await response.json()) as { products: PublicProduct[] }).products;
}
function subscribeCartFocus(listener: () => void) {
  const stop = subscribeCustomerCart(listener);
  window.addEventListener("focus", listener);
  return () => {
    stop();
    window.removeEventListener("focus", listener);
  };
}

type ShopContextValue = {
  actor: Actor | null;
  sessionLoading: boolean;
  sessionError: string;
  products: PublicProduct[];
  loading: boolean;
  error: string;
  refreshCatalog: () => Promise<PublicProduct[]>;
  owner: string;
  cart: CustomerCart;
  store: CustomerCartStore | null;
  cartReady: boolean;
  busy: boolean;
  message: string;
  problem: string;
  setProblem: (message: string) => void;
  act: (
    action: (store: CustomerCartStore) => Promise<unknown>,
    success?: string,
  ) => Promise<boolean>;
};
const ShopContext = createContext<ShopContextValue | null>(null);
export function useShop() {
  const context = useContext(ShopContext);
  if (!context) throw new Error("ShopShell is required");
  return context;
}

type ShopShellProps = {
  children: ReactNode;
  actor?: Actor | null;
  cartCount?: number;
  active?: "shop" | "cart" | "orders" | "account";
  actionBar?: ReactNode;
};
export function ShopShell(props: ShopShellProps) {
  return (
    <Suspense
      fallback={
        <div className="shop-v7">
          <ShopLoading label="Menyiapkan halaman…" />
        </div>
      }
    >
      <ShopShellContent {...props} />
    </Suspense>
  );
}
function ShopShellContent({
  children,
  actor: suppliedActor,
  cartCount,
  active = "shop",
  actionBar,
}: ShopShellProps) {
  const [fetchedActor, setActor] = useState<Actor | null>(null);
  const actor = suppliedActor === undefined ? fetchedActor : suppliedActor;
  const [fetchingSession, setSessionLoading] = useState(
    suppliedActor === undefined,
  );
  const sessionLoading = suppliedActor === undefined && fetchingSession;
  const [sessionError, setSessionError] = useState("");
  const [products, setProducts] = useState<PublicProduct[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [importedOwner, setImportedOwner] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [problem, setProblem] = useState("");
  const location = useShopLocation();
  const [searchDraft, setSearchDraft] = useState({ location: "", value: "" });
  const search =
    searchDraft.location === location
      ? searchDraft.value
      : new URLSearchParams(location.split("?")[1] || "").get("q") || "";
  const owner = customerOwner(
    actor?.role === "customer" ? actor.id : undefined,
  );
  const cartJson = useSyncExternalStore(
    subscribeCartFocus,
    () =>
      !sessionLoading && !sessionError
        ? JSON.stringify(getCustomerCartStore().read(owner))
        : "",
    () => "",
  );
  const cart: CustomerCart = cartJson
    ? JSON.parse(cartJson)
    : emptyCustomerCart();
  const store = cartJson ? getCustomerCartStore() : null;
  const cartReady = Boolean(
    store && (actor?.role !== "customer" || importedOwner === owner),
  );
  const refreshCatalog = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const result = await fetchCatalogProducts();
      setProducts(result);
      return result;
    } catch (cause) {
      const message =
        cause instanceof Error ? cause.message : "Katalog belum dapat dimuat.";
      setError(message);
      throw cause;
    } finally {
      setLoading(false);
    }
  }, []);
  useEffect(() => {
    let mounted = true;
    fetchCatalogProducts()
      .then((result) => {
        if (mounted) setProducts(result);
      })
      .catch((cause) => {
        if (mounted)
          setError(
            cause instanceof Error
              ? cause.message
              : "Katalog belum dapat dimuat.",
          );
      })
      .finally(() => {
        if (mounted) setLoading(false);
      });
    return () => {
      mounted = false;
    };
  }, []);
  useEffect(() => {
    let mounted = true;
    if (suppliedActor !== undefined) {
      return;
    }
    fetch("/api/session", {
      cache: "no-store",
      signal: AbortSignal.timeout(15000),
    })
      .then(async (response) => {
        if (!response.ok)
          throw new Error(
            "Sesi belum dapat diperiksa. Muat ulang halaman sebelum melanjutkan belanja.",
          );
        const data = (await response.json()) as { actor: Actor | null };
        if (mounted) setActor(data.actor);
      })
      .catch((cause) => {
        if (mounted)
          setSessionError(
            cause instanceof Error
              ? cause.message
              : "Sesi belum dapat diperiksa.",
          );
      })
      .finally(() => {
        if (mounted) setSessionLoading(false);
      });
    return () => {
      mounted = false;
    };
  }, [suppliedActor]);
  useEffect(() => {
    if (sessionLoading || sessionError || actor?.role !== "customer") return;
    let mounted = true;
    const next = getCustomerCartStore();
    const start = async () => {
      try {
        if (actor?.role === "customer") {
          const result = await next.importGuest(actor.id);
          if (result.imported && mounted)
            setMessage("Pilihan pengunjung sudah digabung ke keranjang Anda.");
          handoffGuestIntent(actor.id);
        }
      } catch (cause) {
        if (mounted)
          setProblem(
            cause instanceof Error
              ? cause.message
              : "Keranjang belum dapat disiapkan.",
          );
      } finally {
        if (mounted) {
          setImportedOwner(owner);
        }
      }
    };
    void start();
    return () => {
      mounted = false;
    };
  }, [sessionLoading, sessionError, actor?.id, actor?.role, owner]);
  async function act(
    action: (store: CustomerCartStore) => Promise<unknown>,
    success = "",
  ) {
    if (!store || !cartReady || busy) return false;
    setBusy(true);
    setProblem("");
    setMessage("");
    try {
      await action(store);
      setMessage(success);
      return true;
    } catch (cause) {
      setProblem(
        cause instanceof Error
          ? cause.message
          : "Perubahan belum tersimpan. Coba kembali.",
      );
      return false;
    } finally {
      setBusy(false);
    }
  }
  const count = cartCount ?? Object.keys(cart.items).length;
  const customer = actor?.role === "customer";
  const accountHref = actor
    ? customer
      ? "/account"
      : "/workspace"
    : "/login?next=%2Faccount";
  const ordersHref =
    actor && !customer ? "/workspace?view=orders" : "/account/orders";
  const context: ShopContextValue = {
    actor,
    sessionLoading,
    sessionError,
    products,
    loading,
    error,
    refreshCatalog,
    owner,
    cart,
    store,
    cartReady,
    busy,
    message,
    problem,
    setProblem,
    act,
  };
  return (
    <ShopContext.Provider value={context}>
      <div className={`shop-v7${actionBar ? " shop-has-action" : ""}`}>
        <a className="shop-skip" href="#shop-main">
          Lewati navigasi
        </a>
        <div className="shop-utility">
          <div>
            <span>Unit Toko · Demo capstone</span>
            <Link href="/workspace">
              Portal divisi & petugas <ArrowUpRight size={13} />
            </Link>
          </div>
        </div>
        <header className="shop-header">
          <div className="shop-header-inner">
            <Link
              className="shop-brand"
              href="/"
              aria-label="Unit Toko, beranda"
            >
              <span>
                <Store size={26} />
              </span>
              <div>
                Unit Toko<small>Kebutuhan kerja & harian</small>
              </div>
            </Link>
            <form
              action="/shop"
              method="get"
              className="shop-search"
              role="search"
            >
              <label className="sr-only" htmlFor="shop-search">
                Cari produk atau SKU
              </label>
              <input
                id="shop-search"
                name="q"
                type="search"
                placeholder="Cari kopi, snack, atau merchandise"
                value={search}
                onChange={(event) =>
                  setSearchDraft({ location, value: event.target.value })
                }
                maxLength={120}
              />
              <button aria-label="Cari produk">
                <Search size={21} />
              </button>
            </form>
            <div className="shop-header-actions">
              <Link
                href="/cart"
                className="shop-cart-link"
                aria-label={`Keranjang, ${count} jenis barang`}
              >
                <ShoppingCart size={24} />
                <span className="shop-cart-count">{count}</span>
                <span className="shop-header-label">Keranjang</span>
              </Link>
              <Link
                href={accountHref}
                className="shop-account-link"
                aria-label={actor ? "Akun saya" : "Masuk"}
              >
                {actor?.avatar ? (
                  <Art src={actor.avatar} alt="" />
                ) : (
                  <UserRound size={23} />
                )}
                <span>
                  {actor
                    ? customer
                      ? actor.name.split(" ")[0]
                      : "Portal saya"
                    : "Masuk"}
                </span>
              </Link>
            </div>
          </div>
        </header>
        <nav className="shop-category-nav" aria-label="Kategori produk">
          <div>
            <Link href="/shop">Semua produk</Link>
            <Link href="/shop?collection=pantry">Pantry</Link>
            <Link href="/shop?collection=rapat">Kebutuhan rapat</Link>
            <Link href="/shop?collection=merchandise">Merchandise</Link>
          </div>
        </nav>
        <main id="shop-main" className="shop-main" tabIndex={-1}>
          {(sessionError || problem) && (
            <div className="shop-notice shop-notice-error" role="alert">
              <CircleAlert size={19} />
              <span>{sessionError || problem}</span>
              <button
                aria-label={
                  sessionError
                    ? "Muat ulang untuk memeriksa sesi"
                    : "Tutup pemberitahuan"
                }
                onClick={() =>
                  sessionError ? window.location.reload() : setProblem("")
                }
              >
                {sessionError ? "Muat ulang" : "×"}
              </button>
            </div>
          )}
          {message && (
            <div className="shop-notice shop-notice-success" role="status">
              <Check size={19} />
              <span>{message}</span>
              <button
                aria-label="Tutup pemberitahuan"
                onClick={() => setMessage("")}
              >
                ×
              </button>
            </div>
          )}
          {store && !store.persistent && (
            <div className="shop-notice" role="status">
              <CircleAlert size={19} />
              <span>
                Keranjang hanya tersedia di tab ini. Penyimpanan dan
                sinkronisasi antar-tab tidak tersedia.
              </span>
            </div>
          )}
          {actor && !customer && (
            <div className="shop-portal-note">
              <Package size={19} />
              <span>
                Anda masuk sebagai petugas atau PIC. Pesanan divisi dibuat
                melalui portal.
              </span>
              <Link href="/workspace?view=catalog">
                Buka portal <ArrowUpRight size={15} />
              </Link>
            </div>
          )}
          {children}
        </main>
        <Toaster position="top-center" richColors />
        <footer className="shop-footer">
          <div>
            <Link href="/" className="shop-footer-brand">
              Unit Toko
            </Link>
            <p>Pantry, perlengkapan rapat, dan merchandise.</p>
          </div>
          <div>
            <Link href="/shop">Jelajahi produk</Link>
            <Link href={ordersHref}>Pesanan saya</Link>
            <Link href="/workspace">Portal divisi & petugas</Link>
          </div>
          <p className="shop-demo-note">
            Demo capstone. Data, foto, pengiriman, dan pembayaran adalah
            simulasi. Bukan situs resmi BNI.
          </p>
        </footer>
        {actionBar ? (
          <div className="shop-mobile-action">{actionBar}</div>
        ) : (
          <nav className="shop-mobile-nav" aria-label="Navigasi belanja">
            {(
              [
                {
                  id: "shop",
                  href: "/shop",
                  label: "Belanja",
                  icon: ShoppingBag,
                },
                {
                  id: "cart",
                  href: "/cart",
                  label: "Keranjang",
                  icon: ShoppingCart,
                },
                {
                  id: "orders",
                  href: ordersHref,
                  label: "Pesanan",
                  icon: ClipboardList,
                },
                {
                  id: "account",
                  href: accountHref,
                  label: "Akun",
                  icon: UserRound,
                },
              ] as const
            ).map((item) => (
              <Link
                key={item.id}
                href={item.href}
                aria-current={active === item.id ? "page" : undefined}
              >
                <item.icon size={21} />
                <span>
                  {item.label}
                  {item.id === "cart" && count > 0 ? ` (${count})` : ""}
                </span>
              </Link>
            ))}
          </nav>
        )}
      </div>
    </ShopContext.Provider>
  );
}

export function ShopLoading({ label = "Memuat produk…" }: { label?: string }) {
  return (
    <div className="shop-loading" aria-busy="true" aria-label={label}>
      <p>{label}</p>
      <div className="shop-product-grid">
        {Array.from({ length: 8 }, (_, index) => (
          <div className="shop-skeleton" key={index}>
            <div />
            <span />
            <span />
          </div>
        ))}
      </div>
    </div>
  );
}
export function ShopEmpty({
  title,
  description,
  href = "/shop",
  action = "Jelajahi produk",
  onAction,
}: {
  title: string;
  description: string;
  href?: string;
  action?: string;
  onAction?: () => void;
}) {
  return (
    <div className="shop-empty">
      <ShoppingBag size={36} />
      <h1>{title}</h1>
      <p>{description}</p>
      {onAction ? (
        <Button onClick={onAction}>{action}</Button>
      ) : (
        <Button asChild>
          <Link href={href}>{action}</Link>
        </Button>
      )}
    </div>
  );
}
export function CatalogError() {
  const { error, refreshCatalog } = useShop();
  return (
    <div className="shop-empty" role="alert">
      <CircleAlert size={32} />
      <h2>Katalog belum tersedia</h2>
      <p>{error}</p>
      <Button onClick={() => void refreshCatalog().catch(() => undefined)}>
        Muat ulang katalog
      </Button>
    </div>
  );
}
