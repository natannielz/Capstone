import { buyerKey, buyerLabel } from "./buyers";
import type { Payment, State } from "./model";

export const PAYMENT_STATUS_LABELS = {
  all: "Semua status",
  recorded: "Menunggu verifikasi",
  verified: "Terverifikasi",
  unidentified: "Belum teridentifikasi",
  rejected: "Ditolak",
} as const;

export type PaymentStatusFilter = keyof typeof PAYMENT_STATUS_LABELS;
export type PaymentListQuery = { search: string; status: PaymentStatusFilter; page: number };
export const PAYMENT_PAGE_SIZE = 12;

export function readPaymentQuery(query: URLSearchParams): PaymentListQuery {
  const status = query.get("paymentStatus") || "all";
  const page = Number(query.get("paymentPage") || 1);
  return {
    search: query.get("paymentSearch") || "",
    status: Object.hasOwn(PAYMENT_STATUS_LABELS, status) ? status as PaymentStatusFilter : "all",
    page: Number.isSafeInteger(page) && page > 0 ? page : 1,
  };
}

/** Matches the status actually displayed; an unverified transfer stays in verification. */
export function paymentDisplayStatus(payment: Payment): Exclude<PaymentStatusFilter, "all"> {
  if (payment.status !== "verified") return payment.status;
  return buyerKey(payment) ? "verified" : "unidentified";
}

/** Operates only on the state already scoped to the account by the server. */
export function paymentListView(s: State, query: PaymentListQuery) {
  const needle = query.search.trim().toLocaleLowerCase("id-ID");
  const matches = [...s.payments].reverse().filter(payment =>
    (query.status === "all" || paymentDisplayStatus(payment) === query.status)
    && (!needle || `${payment.payer} ${payment.reference} ${buyerLabel(s, payment)}`.toLocaleLowerCase("id-ID").includes(needle)));
  const pages = Math.max(1, Math.ceil(matches.length / PAYMENT_PAGE_SIZE));
  const page = Math.min(query.page, pages);
  const offset = (page - 1) * PAYMENT_PAGE_SIZE;
  return {
    items: matches.slice(offset, offset + PAYMENT_PAGE_SIZE),
    total: s.payments.length,
    count: matches.length,
    filtered: Boolean(needle) || query.status !== "all",
    page,
    pages,
    start: matches.length ? offset + 1 : 0,
    end: Math.min(offset + PAYMENT_PAGE_SIZE, matches.length),
  };
}
