import { validCatalogQuantity } from "../domain/catalog";

export type CartSnapshot = {
  revision: number;
  items: Record<string, number>;
  itemRevisions: Record<string, number>;
  writers: Record<string, string>;
  lastClear?: string;
};
export type CartMutation =
  | { type: "add"; productId: string; quantity: number }
  | {
      type: "set";
      productId: string;
      quantity: number;
      expectedRevision: number;
    }
  | { type: "remove"; productId: string; expectedRevision: number };
export type CartStorage = Pick<Storage, "getItem" | "setItem" | "removeItem">;
export type CartLock = <T>(
  name: string,
  action: () => Promise<T>,
) => Promise<T>;
export type CartNotice = {
  type: "change" | "clear";
  accountId: string;
  persistent: boolean;
  clearToken?: string;
};
export type CheckoutValues = {
  divisionId: string;
  date: string;
  neededAt: string;
  address: string;
  note: string;
};
export type CheckoutDraft = {
  values: CheckoutValues;
  commandId: string;
  cartClearToken?: string;
};

const emptyCart = (): CartSnapshot => ({
  revision: 0,
  items: {},
  itemRevisions: {},
  writers: {},
});
export const cartStorageKey = (accountId: string) =>
  `unit-toko-cart-v6-${encodeURIComponent(accountId)}`;
export const checkoutDraftKey = (accountId: string) =>
  `unit-toko-checkout-v6-${encodeURIComponent(accountId)}`;
const legacyKey = (accountId: string) => `unit-toko-cart-${accountId}`;

export class CartConflictError extends Error {
  constructor(
    message = "Keranjang berubah di tab lain. Periksa jumlah terbaru sebelum mengubahnya kembali.",
  ) {
    super(message);
    this.name = "CartConflictError";
  }
}

function safeItems(value: unknown): Record<string, number> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  return Object.fromEntries(
    Object.entries(value).filter(
      ([id, quantity]) =>
        id !== "__proto__" &&
        id !== "constructor" &&
        id !== "prototype" &&
        typeof quantity === "number" &&
        validCatalogQuantity(quantity),
    ),
  );
}

export function parseCart(
  raw: string | null,
  legacy: string | null = null,
): CartSnapshot {
  if (!raw) {
    let items = {};
    try {
      items = safeItems(JSON.parse(legacy || "{}"));
    } catch {
      /* An invalid legacy cart is empty. */
    }
    return { ...emptyCart(), items };
  }
  try {
    const value = JSON.parse(raw) as Partial<CartSnapshot>;
    if (
      !value ||
      typeof value !== "object" ||
      !Number.isSafeInteger(value.revision) ||
      Number(value.revision) < 0
    )
      return emptyCart();
    const itemRevisions = Object.fromEntries(
      Object.entries(value.itemRevisions || {}).filter(
        ([id, revision]) =>
          id !== "__proto__" && Number.isSafeInteger(revision) && revision >= 0,
      ),
    );
    const writers = Object.fromEntries(
      Object.entries(value.writers || {}).filter(
        ([id, writer]) => id !== "__proto__" && typeof writer === "string",
      ),
    );
    return {
      revision: value.revision!,
      items: safeItems(value.items),
      itemRevisions,
      writers,
      lastClear:
        typeof value.lastClear === "string" ? value.lastClear : undefined,
    };
  } catch {
    return emptyCart();
  }
}

/** Called only inside an account's exclusive transaction. Unrelated products are preserved. */
export function applyCartMutation(
  snapshot: CartSnapshot,
  mutation: CartMutation,
  writer: string,
): CartSnapshot {
  const { productId } = mutation;
  if (
    !productId ||
    ["__proto__", "constructor", "prototype"].includes(productId)
  )
    throw new Error("Barang tidak valid.");
  const previous = snapshot.items[productId] || 0;
  if (
    mutation.type !== "add" &&
    (snapshot.itemRevisions[productId] || 0) !== mutation.expectedRevision &&
    snapshot.writers[productId] !== writer
  )
    throw new CartConflictError();
  const quantity =
    mutation.type === "remove"
      ? 0
      : mutation.type === "add"
        ? previous + mutation.quantity
        : mutation.quantity;
  if (
    mutation.type !== "remove" &&
    (!validCatalogQuantity(quantity) ||
      !validCatalogQuantity(mutation.quantity))
  )
    throw new Error("Masukkan bilangan bulat 1–10.000 untuk setiap barang.");
  const revision = snapshot.revision + 1;
  const items = { ...snapshot.items };
  if (quantity) items[productId] = quantity;
  else delete items[productId];
  return {
    ...snapshot,
    revision,
    items,
    itemRevisions: { ...snapshot.itemRevisions, [productId]: revision },
    writers: { ...snapshot.writers, [productId]: writer },
  };
}

function emptiedCart(snapshot: CartSnapshot, writer: string): CartSnapshot {
  const revision = snapshot.revision + 1;
  const itemRevisions = { ...snapshot.itemRevisions };
  const writers = { ...snapshot.writers };
  for (const id of Object.keys(snapshot.items)) {
    itemRevisions[id] = revision;
    writers[id] = writer;
  }
  return {
    revision,
    items: {},
    itemRevisions,
    writers,
    lastClear: `${writer}:${revision}`,
  };
}

export function createCartStore(options: {
  accountId: string;
  clientId: string;
  storage?: CartStorage;
  lock?: CartLock;
  notify?: (notice: CartNotice) => void;
}) {
  const key = cartStorageKey(options.accountId);
  let persistent = Boolean(options.storage && options.lock);
  let memory = emptyCart();
  let memoryTail: Promise<unknown> = Promise.resolve();
  if (!persistent && options.storage) {
    try {
      memory = parseCart(
        options.storage.getItem(key),
        options.storage.getItem(legacyKey(options.accountId)),
      );
    } catch {
      /* A blocked store starts with a memory-only cart. */
    }
  }

  function read() {
    if (persistent) {
      try {
        memory = parseCart(
          options.storage!.getItem(key),
          options.storage!.getItem(legacyKey(options.accountId)),
        );
      } catch {
        persistent = false;
      }
    }
    return memory;
  }

  function commit(snapshot: CartSnapshot, type: CartNotice["type"]) {
    memory = snapshot;
    if (persistent) {
      try {
        options.storage!.setItem(key, JSON.stringify(snapshot));
        options.storage!.removeItem(legacyKey(options.accountId));
      } catch {
        persistent = false;
      }
    }
    options.notify?.({
      type,
      accountId: options.accountId,
      persistent,
      clearToken: snapshot.lastClear,
    });
    return snapshot;
  }

  async function exclusive<T>(action: () => Promise<T>): Promise<T> {
    const run = () =>
      persistent
        ? options.lock!(`unit-toko-cart:${options.accountId}`, action)
        : action();
    const next = memoryTail.then(run, run);
    memoryTail = next.catch(() => undefined);
    return next;
  }

  return {
    read,
    acceptExternalClear: (token: string) => {
      if (!persistent && memory.lastClear !== token)
        memory = { ...emptiedCart(memory, "external"), lastClear: token };
    },
    get persistent() {
      return persistent;
    },
    mutate: (mutation: CartMutation) =>
      exclusive(async () =>
        commit(applyCartMutation(read(), mutation, options.clientId), "change"),
      ),
    clear: () =>
      exclusive(async () =>
        commit(emptiedCart(read(), `clear:${options.clientId}`), "clear"),
      ),
    checkout: <T>(expectedRevision: number, submit: () => Promise<T>) =>
      exclusive(async () => {
        const latest = read();
        if (latest.revision !== expectedRevision)
          throw new CartConflictError(
            "Keranjang berubah sejak diperiksa. Tutup ringkasan dan periksa barang terbaru sebelum mengajukan.",
          );
        if (!Object.keys(latest.items).length)
          throw new CartConflictError(
            "Keranjang sudah kosong atau sudah diajukan dari tab lain.",
          );
        const result = await submit();
        commit(emptiedCart(latest, `checkout:${options.clientId}`), "clear");
        return result;
      }),
  };
}

type CartStore = ReturnType<typeof createCartStore>;
const stores = new Map<string, CartStore>();
const memoryDrafts = new Map<string, CheckoutDraft>();
const subscribers = new Map<string, Set<(notice: CartNotice) => void>>();
const lastClears = new Map<string, string>();
let channel: BroadcastChannel | null = null;
let listening = false;

function receive(notice: CartNotice) {
  const isNewClear =
    notice.clearToken && notice.clearToken !== lastClears.get(notice.accountId);
  if (isNewClear) {
    lastClears.set(notice.accountId, notice.clearToken!);
    stores.get(notice.accountId)?.acceptExternalClear(notice.clearToken!);
    clearCheckoutDraft(notice.accountId);
  }
  subscribers
    .get(notice.accountId)
    ?.forEach((listener) =>
      listener({ ...notice, type: isNewClear ? "clear" : "change" }),
    );
}

function listen() {
  if (listening || typeof window === "undefined") return;
  listening = true;
  if (typeof BroadcastChannel !== "undefined") {
    try {
      channel = new BroadcastChannel("unit-toko-cart-v6");
      channel.onmessage = (event) => {
        const notice = event.data as CartNotice;
        if (
          notice &&
          typeof notice.accountId === "string" &&
          ["change", "clear"].includes(notice.type)
        )
          receive(notice);
      };
    } catch {
      /* Storage events still synchronize persistent carts. */
    }
  }
  window.addEventListener("storage", (event) => {
    for (const accountId of subscribers.keys()) {
      if (event.key === cartStorageKey(accountId) || event.key === null) {
        const snapshot = parseCart(event.newValue);
        receive({
          accountId,
          type: "change",
          persistent: true,
          clearToken: snapshot.lastClear,
        });
      }
    }
  });
}

export function getCartStore(accountId: string): CartStore {
  listen();
  const existing = stores.get(accountId);
  if (existing) {
    const latest = existing.read();
    if (latest.lastClear && latest.lastClear !== lastClears.get(accountId))
      receive({
        accountId,
        type: "clear",
        persistent: existing.persistent,
        clearToken: latest.lastClear,
      });
    return existing;
  }
  let storage: Storage | undefined;
  try {
    storage = window.localStorage;
  } catch {
    /* The tab keeps a memory-only cart. */
  }
  const lock: CartLock | undefined =
    typeof navigator !== "undefined" && navigator.locks
      ? async (name, action) =>
          await navigator.locks.request(name, { mode: "exclusive" }, action)
      : undefined;
  const store = createCartStore({
    accountId,
    storage,
    lock,
    clientId: crypto.randomUUID(),
    notify: (notice) => {
      receive(notice);
      try {
        if (notice.persistent || notice.type === "clear")
          channel?.postMessage(notice);
      } catch {
        /* The cart remains usable when broadcast is blocked. */
      }
    },
  });
  stores.set(accountId, store);
  const initial = store.read();
  if (initial.lastClear) lastClears.set(accountId, initial.lastClear);
  return store;
}

export function subscribeCart(
  accountId: string,
  listener: (notice: CartNotice) => void,
) {
  listen();
  const listeners = subscribers.get(accountId) || new Set();
  listeners.add(listener);
  subscribers.set(accountId, listeners);
  return () => {
    listeners.delete(listener);
    if (!listeners.size) subscribers.delete(accountId);
  };
}

/** Call only after a successful logout; it also invalidates drafts in other open tabs. */
export async function clearCartForAccount(accountId: string) {
  await getCartStore(accountId).clear();
  clearCheckoutDraft(accountId);
}

export function restoreCheckoutDraft(
  storage: CartStorage | undefined,
  accountId: string,
  fallback: CheckoutDraft,
): CheckoutDraft {
  const memoryOrFallback = () => {
    const memory = memoryDrafts.get(accountId);
    return memory && memory.cartClearToken === fallback.cartClearToken
      ? memory
      : fallback;
  };
  try {
    const raw = storage?.getItem(checkoutDraftKey(accountId));
    if (!raw) return memoryOrFallback();
    const parsed = JSON.parse(raw) as CheckoutDraft;
    if (
      !parsed ||
      typeof parsed.commandId !== "string" ||
      parsed.cartClearToken !== fallback.cartClearToken ||
      !parsed.values ||
      Object.keys(fallback.values).some(
        (key) => typeof parsed.values[key as keyof CheckoutValues] !== "string",
      )
    )
      return fallback;
    return {
      commandId: parsed.commandId,
      ...(typeof parsed.cartClearToken === "string"
        ? { cartClearToken: parsed.cartClearToken }
        : {}),
      values: Object.fromEntries(
        Object.keys(fallback.values).map((key) => [
          key,
          parsed.values[key as keyof CheckoutValues].slice(0, 2000),
        ]),
      ) as CheckoutValues,
    };
  } catch {
    return memoryOrFallback();
  }
}

function sessionStorageOrUndefined() {
  try {
    return window.sessionStorage;
  } catch {
    return undefined;
  }
}
export function loadCheckoutDraft(accountId: string, fallback: CheckoutDraft) {
  return restoreCheckoutDraft(sessionStorageOrUndefined(), accountId, fallback);
}
export function saveCheckoutDraft(accountId: string, draft: CheckoutDraft) {
  memoryDrafts.set(accountId, draft);
  try {
    sessionStorageOrUndefined()?.setItem(
      checkoutDraftKey(accountId),
      JSON.stringify(draft),
    );
  } catch {
    /* Retain the draft in this tab's memory. */
  }
}
export function clearCheckoutDraft(accountId: string) {
  memoryDrafts.delete(accountId);
  try {
    sessionStorageOrUndefined()?.removeItem(checkoutDraftKey(accountId));
  } catch {
    /* Memory is already cleared. */
  }
}
