import { isRole, ROLE_LABELS, type Role } from "./accounts";
import type { State } from "./model";

export type PurchaseListStatus = "all" | "open" | "draft" | "ordered" | "complete" | "closed";
export type AdminSection = "users" | "divisions" | "suppliers";
export type PurchaseListQuery = { search: string; status: PurchaseListStatus; page: number };
export type AccountListQuery = { search: string; role: Role | "all"; status: "all" | "active" | "inactive"; page: number; section: AdminSection };

function readPage(value: string | null) {
  const page = Number(value);
  return Number.isSafeInteger(page) && page > 0 ? page : 1;
}
const normalize = (value: string) => value.trim().toLocaleLowerCase("id-ID");

export function readPurchaseListQuery(query: URLSearchParams): PurchaseListQuery {
  const status = query.get("purchaseStatus") || "all";
  return {
    search: query.get("purchaseQ") || "",
    status: (["all", "open", "draft", "ordered", "complete", "closed"].includes(status) ? status : "all") as PurchaseListStatus,
    page: readPage(query.get("purchasePage")),
  };
}

export function readAccountListQuery(query: URLSearchParams): AccountListQuery {
  const role = query.get("accountRole"), status = query.get("accountStatus"), section = query.get("adminSection");
  return {
    search: query.get("accountQ") || "", role: isRole(role) ? role : "all",
    status: status === "active" || status === "inactive" ? status : "all",
    page: readPage(query.get("accountPage")),
    section: section === "divisions" || section === "suppliers" ? section : "users",
  };
}

/** Clamp the effective page after filtering or a record update; retain source ordering. */
export function paginateOperations<T>(rows: T[], requestedPage: number, pageSize: number) {
  const size = Number.isSafeInteger(pageSize) && pageSize > 0 ? pageSize : 12;
  const pages = Math.max(1, Math.ceil(rows.length / size));
  const page = Math.min(pages, Number.isSafeInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1);
  const start = (page - 1) * size;
  return { rows: rows.slice(start, start + size), total: rows.length, page, pages, first: rows.length ? start + 1 : 0, last: Math.min(start + size, rows.length) };
}

export function purchaseListRows(s: State, query: PurchaseListQuery) {
  const needle = normalize(query.search);
  const suppliers = new Map(s.suppliers.map(row => [row.id, row.name]));
  const orders = new Map(s.orders.map(row => [row.id, row.number]));
  const products = new Map(s.products.map(row => [row.id, `${row.name} ${row.sku}`]));
  const contents = new Map<string, string[]>();
  for (const line of s.purchaseLines) {
    const names = contents.get(line.purchaseId) || [];
    names.push(products.get(line.productId) || "");
    contents.set(line.purchaseId, names);
  }
  return [...s.purchases].reverse().filter(purchase => {
    const matchesStatus = query.status === "all" || (query.status === "open" ? ["draft", "ordered"].includes(purchase.status) : purchase.status === query.status);
    const text = `${purchase.number} ${purchase.kind} ${suppliers.get(purchase.supplierId) || ""} ${orders.get(purchase.orderId || "") || ""} ${(contents.get(purchase.id) || []).join(" ")}`;
    return matchesStatus && (!needle || normalize(text).includes(needle));
  });
}

export function accountListRows(s: State, query: AccountListQuery) {
  const needle = normalize(query.search);
  const divisions = new Map(s.divisions.map(row => [row.id, row.name]));
  return s.users.filter(user => {
    const matchesRole = query.role === "all" || user.role === query.role;
    const matchesStatus = query.status === "all" || (query.status === "active" ? user.active : !user.active);
    const division = user.role === "customer" ? "Pelanggan" : divisions.get(user.divisionId || "") || "Lintas unit";
    return matchesRole && matchesStatus && (!needle || normalize(`${user.name} ${user.email || ""} ${ROLE_LABELS[user.role]} ${division}`).includes(needle));
  });
}
