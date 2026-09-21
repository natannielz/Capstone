"use client";

import { useState } from "react";
import Link from "next/link";
import { ArrowUpRight, Coffee, Users, Gift } from "lucide-react";
import { Art } from "./art";
import { catalogLoginHref, type CatalogCollection } from "@/lib/domain/catalog";

const moments = [
  {
    collection: "pantry",
    label: "Pantry",
    icon: Coffee,
    title: "Kebutuhan pantry",
    text: "Kopi, teh, dan biskuit untuk persediaan harian divisi.",
    products: [
      { id: "kopi", image: "coffee", name: "Kopi sachet" },
      { id: "teh", image: "tea", name: "Teh celup" },
      { id: "biskuit", image: "biscuits", name: "Biskuit" },
    ],
    link: "Pesan kebutuhan pantry",
  },
  {
    collection: "rapat",
    label: "Rapat",
    icon: Users,
    title: "Konsumsi rapat",
    text: "Snack, air mineral, dan gelas untuk pertemuan atau pelatihan divisi.",
    products: [
      { id: "snack", image: "snack", name: "Snack box" },
      { id: "air", image: "water", name: "Air mineral" },
      { id: "cup", image: "cups", name: "Gelas kertas" },
    ],
    link: "Pesan kebutuhan rapat",
  },
  {
    collection: "merchandise",
    label: "Merchandise",
    icon: Gift,
    title: "Merchandise divisi",
    text: "Tumbler, tas kanvas, dan kaos polo untuk kegiatan divisi.",
    products: [
      { id: "tumbler", image: "tumbler", name: "Tumbler" },
      { id: "tas", image: "tote", name: "Tas kanvas" },
      { id: "kaos", image: "polo", name: "Kaos polo" },
    ],
    link: "Pesan merchandise",
  },
] satisfies {
  collection: CatalogCollection;
  label: string;
  icon: typeof Coffee;
  title: string;
  text: string;
  products: { id: string; image: string; name: string }[];
  link: string;
}[];

export function MomentShowcase() {
  const [active, setActive] = useState(0);
  const moment = moments[active];

  function select(index: number, focus = false) {
    setActive(index);
    if (focus) document.getElementById(`moment-tab-${index}`)?.focus();
  }

  return (
    <div className="moment-showcase">
      <div
        className="moment-tabs"
        role="tablist"
        aria-label="Jenis kebutuhan divisi"
      >
        {moments.map((item, index) => (
          <button
            id={`moment-tab-${index}`}
            key={item.label}
            role="tab"
            aria-selected={active === index}
            aria-controls="moment-panel"
            tabIndex={active === index ? 0 : -1}
            onClick={() => select(index)}
            onKeyDown={(event) => {
              if (
                ["ArrowRight", "ArrowLeft", "Home", "End"].includes(event.key)
              ) {
                event.preventDefault();
                select(
                  event.key === "Home"
                    ? 0
                    : event.key === "End"
                      ? moments.length - 1
                      : (index +
                          (event.key === "ArrowRight"
                            ? 1
                            : moments.length - 1)) %
                        moments.length,
                  true,
                );
              }
            }}
          >
            <item.icon size={19} aria-hidden="true" />
            {item.label}
          </button>
        ))}
      </div>
      <div
        id="moment-panel"
        role="tabpanel"
        aria-labelledby={`moment-tab-${active}`}
        tabIndex={0}
        className="moment-panel moment-selection-panel"
      >
        <div className="moment-selection-art">
          {moment.products.map((product, index) => (
            <Link
              key={product.id}
              className={
                index === 0
                  ? "moment-selection-primary"
                  : "moment-selection-secondary"
              }
              href={catalogLoginHref({
                product: product.id,
                collection: moment.collection,
              })}
            >
              <Art
                loading="lazy"
                src={`/images/products/${product.image}.png`}
                alt={product.name}
                sizes="(max-width: 760px) 50vw, 25vw"
              />
              <span>
                {product.name}
                <ArrowUpRight size={17} aria-hidden="true" />
              </span>
            </Link>
          ))}
        </div>
        <div className="moment-story">
          <h3>{moment.title}</h3>
          <p>{moment.text}</p>
          <Link
            className="text-arrow"
            href={catalogLoginHref({ collection: moment.collection })}
          >
            {moment.link}
            <ArrowUpRight size={19} aria-hidden="true" />
          </Link>
          <p className="moment-selection-note">
            Pilih kemasan dan jumlah setelah masuk ke katalog.
          </p>
        </div>
      </div>
    </div>
  );
}
