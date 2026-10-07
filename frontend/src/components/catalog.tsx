"use client";

import type { CatalogCollection } from "@/lib/domain/catalog";
import { applicationHistoryState } from "@/lib/client/native-history";

type CatalogQuery = {
  q: string;
  category: string;
  collection: CatalogCollection | "";
  product: string;
  page: string;
};
function writeQuery(query: CatalogQuery) {
  const url = new URL(window.location.href);
  for (const [key, value] of Object.entries(query)) {
    if (value && !(key === "category" && value === "Semua") && !(key === "page" && value === "1"))
      url.searchParams.set(key, value);
    else url.searchParams.delete(key);
  }
  window.history.replaceState(
    applicationHistoryState(window.history.state),
    "",
    `${url.pathname}${url.search}${url.hash}`,
  );
}
