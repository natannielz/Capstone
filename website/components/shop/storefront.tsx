"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ShopMotion } from "./shop-motion";
import { useShopLocation } from "./use-shop-location";
import {
  ArrowRight,
  ArrowUpRight,
  Check,
  Coffee,
  Gift,
  PackageCheck,
  Plus,
  ReceiptText,
  Search,
  SlidersHorizontal,
  Truck,
  Users,
  X,
} from "lucide-react";
import { Art } from "../art";
import { Button } from "../ui/button";
import { catalogPage, paginateCatalog } from "@/lib/domain/catalog-pagination";
import { CatalogPagination } from "../catalog-pagination";
import {
  CatalogError,
  ShopEmpty,
  ShopLoading,
  ShopShell,
  useShop,
} from "./shop-shell";
import {
  SHOP_CATEGORIES,
  SHOP_COLLECTIONS,
  familyProduct,
  inShopCollection,
  rupiah,
  shopCategory,
  shopFamilies,
  type PublicFamily,
  type PublicProduct,
} from "./shop-data";

function rememberScroll(back: string) {
  try {
    sessionStorage.setItem(`shop-scroll:${back}`, String(window.scrollY));
  } catch {
    /* Navigation still works. */
  }
}
export function ProductCard({
  family,
  query = "",
  back = "/shop",
}: {
  family: PublicFamily;
  query?: string;
  back?: string;
}) {
  const product = familyProduct(family, query);
  const { actor, owner, cart, cartReady, busy, act } = useShop();
  const [added, setAdded] = useState(false);
  useEffect(() => {
    if (!added) return;
    const timer = window.setTimeout(() => setAdded(false), 1800);
    return () => window.clearTimeout(timer);
  }, [added]);
  const href = `/shop/${encodeURIComponent(product.id)}?${new URLSearchParams({ from: back })}`;
  return (
    <article className={`shop-product-card${added ? " is-added" : ""}`}>
      <Link
        href={href}
        className="shop-product-photo"
        onClick={() => rememberScroll(back)}
      >
        <Art
          src={product.image}
          alt={family.name}
          loading="lazy"
          sizes="(max-width: 600px) 45vw, (max-width: 1000px) 30vw, 22vw"
        />
        <span className="shop-photo-arrow" aria-hidden="true"><ArrowUpRight size={18} /></span>
        {added && <span className="shop-photo-added" role="status"><Check size={14} /> Di keranjang</span>}
      </Link>
      <div className="shop-product-copy">
        <span className="shop-product-category">{shopCategory(product)}</span>
        <h3>
          <Link href={href} onClick={() => rememberScroll(back)}>
            {family.name}
          </Link>
        </h3>
        <p className="shop-product-price">
          {rupiah(product.price)}
          <span>/ {product.unit}</span>
        </p>
        <p className="shop-packaging">
          {product.packaging}{family.products.length > 1 && <span> · {family.products.length} pilihan kemasan</span>}
        </p>
        <div className="shop-card-bottom">
          <span
            className={
              product.available > 0 ? "shop-stock" : "shop-stock is-empty"
            }
          >
            {product.available > 0
              ? `${product.available} ${product.unit} tersedia`
              : "Stok habis"}
          </span>
          <button
            className="shop-quick-add"
            aria-label={`Tambah ${product.name}, ${product.packaging} ke keranjang`}
            disabled={
              !cartReady ||
              busy ||
              Boolean(cart.pending) ||
              product.available <= (cart.items[product.id] || 0) ||
              Boolean(actor && actor.role !== "customer")
            }
            onClick={async () => {
              const committed = await act(
                (store) =>
                  store.mutate(owner, {
                    type: "add",
                    productId: product.id,
                    qty: 1,
                  }),
                `${product.packaging} ${family.name.toLowerCase()} ditambahkan.`,
              );
              if (committed) setAdded(true);
            }}
          >
            {added ? <Check size={19} /> : <Plus size={19} />}
            <span>{added ? "Ditambah" : "Tambah"}</span>
          </button>
        </div>
      </div>
    </article>
  );
}

export function StorefrontHome() {
  return (
    <ShopShell>
      <HomeContent />
    </ShopShell>
  );
}
function HomeContent() {
  const { products, loading, error } = useShop();
  const families = shopFamilies(products);
  const picks = [
    "kopi",
    "snack",
    "tumbler",
    "teh",
    "air",
    "biskuit",
    "tas",
    "tisu",
  ].flatMap((id) => {
    const family = families.find((item) => item.id === id);
    return family ? [family] : [];
  });
  return (
    <>
      <section className="shop-hero">
        <div className="shop-hero-copy">
          <p className="shop-kicker">Etalase Unit Toko</p>
          <h1>
            Belanja kebutuhan
            <br />
            <span>kerja & harian.</span>
          </h1>
          <p>
            Kopi untuk persediaan, konsumsi untuk rapat, hingga merchandise
            untuk kegiatan Anda.
          </p>
          <Button asChild className="shop-button-orange">
            <Link href="/shop">
              Jelajahi produk <ArrowRight size={18} />
            </Link>
          </Button>
          <span className="shop-hero-caption">
            Pilih kemasan dan jumlah sesuai kebutuhan.
          </span>
        </div>
        <div className="shop-hero-photo">
          <Art
            src="/images/editorial/hero-still-life-v3.png"
            alt="Kopi, snack, tumbler, tas kanvas, dan kaos polo pilihan Unit Toko"
            loading="eager"
            fetchPriority="high"
            sizes="(max-width: 700px) 100vw, 60vw"
          />
          <span>Pantry · Rapat · Merchandise</span>
        </div>
      </section>
      <section className="shop-section">
        <div className="shop-section-heading">
          <h2>Pilih sesuai kebutuhan</h2>
          <Link href="/shop">
            Semua kategori <ArrowUpRight size={17} />
          </Link>
        </div>
        <div className="shop-collection-grid">
          {SHOP_COLLECTIONS.map((collection, index) => {
            const Icon = [Coffee, Users, Gift][index];
            return (
              <Link
                key={collection.id}
                href={`/shop?collection=${collection.id}`}
                className={`shop-collection shop-collection-${collection.id}`}
              >
                <div>
                  <Icon size={23} />
                  <h3>{collection.label}</h3>
                  <p>{collection.description}</p>
                  <span>
                    Lihat produk <ArrowRight size={17} />
                  </span>
                </div>
                <Art
                  src={`/images/products/${collection.image}.png`}
                  alt=""
                  loading="lazy"
                />
              </Link>
            );
          })}
        </div>
      </section>
      <section className="shop-section">
        <div className="shop-section-heading">
          <div>
            <h2>Pilihan dari etalase</h2>
            <p>Harga dan kemasan langsung dari katalog toko.</p>
          </div>
          <Link href="/shop">
            Lihat semua <ArrowUpRight size={17} />
          </Link>
        </div>
        {error ? (
          <CatalogError heading="h2" />
        ) : loading && !products.length ? (
          <ShopLoading />
        ) : (
          <div className="shop-product-grid">
            {picks.map((family) => (
              <ProductCard key={family.id} family={family} back="/" />
            ))}
          </div>
        )}
      </section>
      <section className="shop-how">
        <div>
          <PackageCheck size={25} />
          <h3>Pilih kemasan</h3>
          <p>Periksa jumlah, harga, dan stok sebelum membuat pesanan.</p>
        </div>
        <div>
          <Truck size={25} />
          <h3>Pantau pesanan</h3>
          <p>Lihat proses penyiapan dan pengiriman melalui akun Anda.</p>
        </div>
        <div>
          <ReceiptText size={25} />
          <h3>Bayar setelah invoice</h3>
          <p>
            Tagihan mengikuti barang yang diterima. Pembayaran dalam demo
            bersifat simulasi.
          </p>
        </div>
      </section>
    </>
  );
}

type ShopQuery = {
  q: string;
  category: string;
  collection: string;
  stock: string;
  sort: string;
  page: string;
};
const INITIAL_QUERY: ShopQuery = {
  q: "",
  category: "Semua",
  collection: "",
  stock: "all",
  sort: "name",
  page: "1",
};

function CatalogEditorial({ products, back }: { products: PublicProduct[]; back: string }) {
  const featured = ["demo-141", "demo-001", "demo-151"].flatMap(id => {
    const product = products.find(item => item.id === id && item.active);
    return product ? [product] : [];
  });
  return <ShopMotion refreshKey={featured.map(item => item.id).join(",")} className="shop-editorial-motion">
    <section className="shop-editorial" aria-labelledby="shop-editorial-title">
      <div className="shop-editorial-copy">
        <p className="shop-editorial-eyebrow"><span /> UNIT TOKO / ETALASE PELANGGAN</p>
        <h1 id="shop-editorial-title"><span data-shop-headline>Isi pantry.</span><span data-shop-headline>Siapkan hari.</span></h1>
        <p className="shop-editorial-description">Kopi untuk pagi. Perlengkapan untuk rapat.<br />Pilihan untuk kegiatan Anda.</p>
        <a href="#shop-products" className="shop-editorial-cta">Jelajahi etalase <ArrowRight size={18} /></a>
      </div>
      <div className="shop-editorial-shelf" aria-label="Pilihan produk dari etalase">
        <span className="shop-shelf-caption">Dari meja kerja, untuk keseharian.</span>
        {featured.map((product, index) => <Link key={product.id} data-shop-object href={`/shop/${encodeURIComponent(product.id)}?${new URLSearchParams({ from: back })}`} onClick={() => rememberScroll(back)} className={`shop-shelf-product shop-shelf-product-${index}`}>
          <Art src={product.image} alt={product.name} loading="eager" sizes="(max-width: 700px) 35vw, 22vw" />
          <span><strong>{product.name}</strong><ArrowUpRight size={15} /></span>
        </Link>)}
      </div>
    </section>
  </ShopMotion>;
}

function CategoryBrowse({ products, category, update }: { products: PublicProduct[]; category: string; update: (changes: Partial<ShopQuery>) => void }) {
  const preferred = ["demo-001", "demo-041", "demo-101", "demo-145"];
  return <nav className="shop-category-browse" aria-label="Jelajahi jenis produk">
    {SHOP_CATEGORIES.slice(1).map((name, index) => {
      const product = products.find(item => item.id === preferred[index] && item.active) || products.find(item => item.active && shopCategory(item) === name);
      return <button key={name} type="button" aria-pressed={category === name} onClick={() => update({ category: category === name ? "Semua" : name })}>
        {product && <Art src={product.image} alt="" sizes="64px" />}
        <span>{name}</span><ArrowUpRight size={16} aria-hidden="true" />
      </button>;
    })}
  </nav>;
}
function readQuery(location: string): ShopQuery {
  const params = new URLSearchParams(location.split("?")[1] || "");
  return {
    q: (params.get("q") || "").slice(0, 120),
    category: SHOP_CATEGORIES.includes(params.get("category") || "")
      ? params.get("category")!
      : "Semua",
    collection: SHOP_COLLECTIONS.some(
      (item) => item.id === params.get("collection"),
    )
      ? params.get("collection")!
      : "",
    stock: params.get("stock") === "available" ? "available" : "all",
    page: String(catalogPage(params.get("page"))),
    sort: ["price-low", "price-high", "name"].includes(params.get("sort") || "")
      ? params.get("sort")!
      : "name",
  };
}
export function StorefrontCatalog() {
  return (
    <ShopShell>
      <CatalogContent />
    </ShopShell>
  );
}
function CatalogContent() {
  const { products, loading, error } = useShop();
  const location = useShopLocation();
  const query = readQuery(location);
  const pendingNavigation = useRef(false);
  useEffect(() => {
    if (loading) return;
    if (pendingNavigation.current) {
      pendingNavigation.current = false;
      const frame = requestAnimationFrame(() => document.getElementById("shop-products")?.scrollIntoView({ behavior: "instant", block: "start" }));
      return () => cancelAnimationFrame(frame);
    }
    try {
      const y = sessionStorage.getItem(
        `shop-scroll:${window.location.pathname}${window.location.search}`,
      );
      if (y) {
        sessionStorage.removeItem(
          `shop-scroll:${window.location.pathname}${window.location.search}`,
        );
        const frame = requestAnimationFrame(() => window.scrollTo({ top: Number(y), behavior: "instant" }));
        return () => cancelAnimationFrame(frame);
      }
    } catch {
      /* Default browser scroll remains available. */
    }
  }, [loading, location]);
  function update(changes: Partial<ShopQuery>) {
    const filtering = ["q", "category", "collection", "stock", "sort"].some((key) => key in changes);
    const next = { ...query, ...(filtering ? {page: "1"} : {}), ...changes };
    const params = new URLSearchParams();
    Object.entries(next).forEach(([key, value]) => {
      if (value && value !== INITIAL_QUERY[key as keyof ShopQuery])
        params.set(key, value);
    });
    // Next copies its router state; passing history.state skips search-param updates.
    pendingNavigation.current = true;
    window.history.pushState(
      null,
      "",
      `/shop${params.size ? `?${params}` : ""}`,
    );
  }
  const search = query.q.trim().toLocaleLowerCase("id-ID");
  const collection = SHOP_COLLECTIONS.find(
    (item) => item.id === query.collection,
  );
  const families = shopFamilies(products).filter(
    (family) =>
      (!collection || inShopCollection(family.products[0], collection.id)) &&
      (query.category === "Semua" ||
        shopCategory(family.products[0]) === query.category) &&
      family.products.some((product) =>
        `${product.name} ${product.sku}`
          .toLocaleLowerCase("id-ID")
          .includes(search),
      ) &&
      (query.stock !== "available" ||
        familyProduct(family, search).available > 0),
  );
  families.sort((a, b) =>
    query.sort === "name"
      ? a.name.localeCompare(b.name, "id")
      : (familyProduct(a, search).price - familyProduct(b, search).price) *
        (query.sort === "price-high" ? -1 : 1),
  );
  const pagination = paginateCatalog(families, query.page);
  const params = new URLSearchParams();
  Object.entries(query).forEach(([key, value]) => {
    if (value && value !== INITIAL_QUERY[key as keyof ShopQuery])
      params.set(key, value);
  });
  const back = `/shop${params.size ? `?${params}` : ""}`;
  const filtered = Boolean(
    search || collection || query.category !== "Semua" || query.stock !== "all",
  );
  const showEditorial = !filtered && query.page === "1" && query.sort === "name";
  return (
    <div className="shop-list-page">
      <div className="shop-breadcrumb">
        <Link href="/">Beranda</Link>
        <span>/</span>
        <span>Belanja</span>
      </div>
      {showEditorial && <CatalogEditorial products={products} back={back} />}
      <CategoryBrowse products={products} category={query.category} update={update} />
      <div className="shop-page-heading shop-catalog-heading" id="shop-products">
        <div>
          {showEditorial ? <h2>Temukan kebutuhan Anda.</h2> : <h1>{search ? `Hasil pencarian “${query.q}”` : collection?.label || (query.category !== "Semua" ? query.category : "Semua produk")}</h1>}
          <p>
            {collection?.description ||
              "Pilih barang dan kemasan untuk kebutuhan Anda."}
          </p>
        </div>
        <span className="shop-result-total" aria-live="polite">
          {loading ? "Memuat…" : `${families.length} produk`}
        </span>
      </div>
      <div className="shop-filter-panel">
        <div className="shop-filter-label">
          <SlidersHorizontal size={18} />
          <span>Filter & urutkan</span>
        </div>
        <label>
          Kategori
          <select
            value={query.category}
            onChange={(event) => update({ category: event.target.value })}
          >
            {SHOP_CATEGORIES.map((item) => (
              <option key={item}>{item}</option>
            ))}
          </select>
        </label>
        <label>
          Ketersediaan
          <select
            value={query.stock}
            onChange={(event) => update({ stock: event.target.value })}
          >
            <option value="all">Semua stok</option>
            <option value="available">Stok tersedia</option>
          </select>
        </label>
        <label>
          Urutkan
          <select
            value={query.sort}
            onChange={(event) => update({ sort: event.target.value })}
          >
            <option value="name">Nama A–Z</option>
            <option value="price-low">Harga terendah</option>
            <option value="price-high">Harga tertinggi</option>
          </select>
        </label>
      </div>
      {filtered && (
        <div className="shop-filter-chips">
          {search && (
            <span>
              <Search size={14} />“{query.q}”
            </span>
          )}
          {collection && <span>{collection.label}</span>}
          {query.category !== "Semua" && <span>{query.category}</span>}
          {query.stock === "available" && (
            <span>
              <Check size={14} />
              Stok tersedia
            </span>
          )}
          <button onClick={() => update(INITIAL_QUERY)}>
            <X size={15} /> Hapus filter
          </button>
        </div>
      )}
      {error ? (
        <CatalogError heading="h2" />
      ) : loading && !products.length ? (
        <ShopLoading />
      ) : !families.length ? (
        <ShopEmpty
          heading="h2"
          title="Produk belum ditemukan"
          description="Coba kata kunci atau kategori lain. Anda juga dapat melihat seluruh etalase."
          href="/shop"
          action="Hapus filter & lihat produk"
        />
      ) : (
        <><ShopMotion refreshKey={`${back}:${pagination.items.map(item => item.id).join(",")}`} className="shop-product-grid">
          {pagination.items.map((family) => (
            <ProductCard
              key={family.id}
              family={family}
              query={search}
              back={back}
            />
          ))}
        </ShopMotion><CatalogPagination {...pagination} onChange={(page) => update({page: String(page)})} /></>
      )}
    </div>
  );
}
