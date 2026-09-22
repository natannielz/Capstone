import { validCatalogQuantity } from "../domain/catalog";

export type ShopLine = { productId: string; qty: number; unitPrice: number };
export type CustomerCart = {
  revision: number;
  epoch: string;
  items: Record<string, number>;
  itemRevisions: Record<string, number>;
  pending?: { id: string; hash: string; intentId: string };
  completed: Record<
    string,
    { id: string; message: string; intentId?: string; hash?: string }
  >;
};
export type CheckoutIntent = {
  id: string;
  mode: "cart" | "buy";
  epoch: string;
  lines: ShopLine[];
  itemRevisions: Record<string, number>;
};
export type CustomerDetails = {
  recipientName: string;
  recipientPhone: string;
  address: string;
  note: string;
};
export type CustomerDraft = {
  intent: CheckoutIntent;
  values: CustomerDetails;
  commandId: string;
};
type Envelope = {
  guestId: string;
  carts: Record<string, CustomerCart>;
  imports: Record<string, string>;
};
type StoragePort = Pick<Storage, "getItem" | "setItem" | "removeItem">;
type LockPort = <T>(name: string, action: () => Promise<T>) => Promise<T>;
const KEY = "unit-toko-customer-cart-v7";
const GUEST = "guest";
const validId = (id: string) =>
  /^[a-zA-Z0-9_-]{1,120}$/.test(id) &&
  !["__proto__", "constructor", "prototype"].includes(id);
export const customerOwner = (accountId?: string) =>
  accountId ? `account:${accountId}` : GUEST;
export const emptyCustomerCart = (epoch = "initial"): CustomerCart => ({
  revision: 0,
  epoch,
  items: {},
  itemRevisions: {},
  completed: {},
});

export class CustomerCartConflict extends Error {}
/** A server response has definitively rejected the command; retry may use corrected data. */
export class CheckoutRejected extends Error {
  constructor(
    message: string,
    public status = 400,
  ) {
    super(message);
  }
}

function parse(raw: string | null, fallback: Envelope): Envelope {
  if (!raw) return fallback;
  try {
    const data = JSON.parse(raw) as Envelope;
    if (
      !data ||
      typeof data.guestId !== "string" ||
      !data.carts ||
      typeof data.carts !== "object"
    )
      return fallback;
    const carts: Envelope["carts"] = {};
    for (const [owner, value] of Object.entries(data.carts)) {
      if (owner !== GUEST && !/^account:[a-zA-Z0-9_-]{1,120}$/.test(owner))
        continue;
      if (
        !value ||
        !Number.isSafeInteger(value.revision) ||
        value.revision < 0 ||
        typeof value.epoch !== "string"
      )
        continue;
      const items = Object.fromEntries(
        Object.entries(value.items || {}).filter(
          ([id, qty]) => validId(id) && validCatalogQuantity(qty),
        ),
      );
      const itemRevisions = Object.fromEntries(
        Object.entries(value.itemRevisions || {}).filter(
          ([id, revision]) =>
            validId(id) && Number.isSafeInteger(revision) && revision >= 0,
        ),
      );
      const completed = Object.fromEntries(
        Object.entries(value.completed || {})
          .filter(
            ([id, result]) =>
              validId(id) &&
              result &&
              typeof result.id === "string" &&
              typeof result.message === "string",
          )
          .slice(-30),
      );
      const pending =
        value.pending &&
        typeof value.pending.id === "string" &&
        typeof value.pending.hash === "string" &&
        typeof value.pending.intentId === "string"
          ? value.pending
          : undefined;
      carts[owner] = {
        revision: value.revision,
        epoch: value.epoch,
        items,
        itemRevisions,
        completed,
        pending,
      };
    }
    return {
      guestId: data.guestId,
      carts,
      imports:
        data.imports && typeof data.imports === "object" ? data.imports : {},
    };
  } catch {
    return fallback;
  }
}

export function createCustomerCartStore(options: {
  storage?: StoragePort;
  lock?: LockPort;
  notify?: () => void;
  uuid?: () => string;
}) {
  const uuid = options.uuid || (() => crypto.randomUUID());
  let memory: Envelope = { guestId: uuid(), carts: {}, imports: {} };
  let persistent = Boolean(options.storage && options.lock);
  let tail: Promise<unknown> = Promise.resolve();
  function load() {
    if (persistent) {
      try {
        memory = parse(options.storage!.getItem(KEY), memory);
      } catch {
        persistent = false;
      }
    }
    return memory;
  }
  if (!persistent && options.storage) {
    try {
      memory = parse(options.storage.getItem(KEY), memory);
    } catch {
      /* Memory remains available. */
    }
  }
  function write(envelope: Envelope) {
    memory = envelope;
    if (persistent) {
      try {
        options.storage!.setItem(KEY, JSON.stringify(envelope));
      } catch {
        persistent = false;
      }
    }
    options.notify?.();
  }
  function read(owner: string) {
    return load().carts[owner] || emptyCustomerCart();
  }
  function transaction<T>(action: () => Promise<T>) {
    const run = async () => {
      if (!persistent) return action();
      let entered = false;
      try {
        return await options.lock!(KEY, async () => {
          entered = true;
          return action();
        });
      } catch (error) {
        if (entered) throw error;
        persistent = false;
        return action();
      }
    };
    const next = tail.then(run, run);
    tail = next.catch(() => undefined);
    return next;
  }
  function writable(owner: string) {
    const envelope = load();
    const cart = envelope.carts[owner] || emptyCustomerCart();
    if (cart.pending)
      throw new CustomerCartConflict(
        "Pengajuan sebelumnya belum dipastikan. Buka checkout untuk mencoba pengajuan yang sama kembali.",
      );
    return { envelope, cart };
  }
  function changed(
    envelope: Envelope,
    owner: string,
    cart: CustomerCart,
    items: Record<string, number>,
    ids: string[],
  ) {
    const revision = cart.revision + 1;
    const itemRevisions = { ...cart.itemRevisions };
    ids.forEach((id) => {
      itemRevisions[id] = revision;
    });
    const next = { ...cart, revision, items, itemRevisions };
    write({ ...envelope, carts: { ...envelope.carts, [owner]: next } });
    return next;
  }
  return {
    read,
    get persistent() {
      return persistent;
    },
    mutate: (
      owner: string,
      mutation: {
        type: "add" | "set" | "remove";
        productId: string;
        qty?: number;
        expectedRevision?: number;
      },
    ) =>
      transaction(async () => {
        if (!validId(mutation.productId))
          throw new Error("Barang tidak valid.");
        const { envelope, cart } = writable(owner);
        if (
          mutation.type !== "add" &&
          (cart.itemRevisions[mutation.productId] || 0) !==
            mutation.expectedRevision
        )
          throw new CustomerCartConflict(
            "Jumlah barang berubah di tab lain. Periksa jumlah terbaru lalu coba kembali.",
          );
        const qty =
          mutation.type === "remove"
            ? 0
            : mutation.type === "add"
              ? (cart.items[mutation.productId] || 0) + Number(mutation.qty)
              : Number(mutation.qty);
        if (
          mutation.type !== "remove" &&
          (!validCatalogQuantity(qty) || !validCatalogQuantity(mutation.qty!))
        )
          throw new Error("Jumlah harus bilangan bulat 1–10.000.");
        const items = { ...cart.items };
        if (qty) items[mutation.productId] = qty;
        else delete items[mutation.productId];
        return changed(envelope, owner, cart, items, [mutation.productId]);
      }),
    replaceSku: (
      owner: string,
      from: string,
      to: string,
      expectedRevision: number,
    ) =>
      transaction(async () => {
        if (!validId(from) || !validId(to))
          throw new Error("Kemasan tidak valid.");
        const { envelope, cart } = writable(owner);
        if (
          (cart.itemRevisions[from] || 0) !== expectedRevision ||
          !cart.items[from]
        )
          throw new CustomerCartConflict(
            "Barang berubah di tab lain. Periksa kembali keranjang.",
          );
        if (from === to) return cart;
        const qty = (cart.items[to] || 0) + cart.items[from];
        if (!validCatalogQuantity(qty))
          throw new Error(
            "Gabungan jumlah melebihi 10.000. Kurangi jumlah lebih dahulu.",
          );
        const items = { ...cart.items, [to]: qty };
        delete items[from];
        return changed(envelope, owner, cart, items, [from, to]);
      }),
    importGuest: (accountId: string) =>
      transaction(async () => {
        const owner = customerOwner(accountId);
        const envelope = load();
        const cart = envelope.carts[owner] || emptyCustomerCart();
        const guest = envelope.carts[GUEST] || emptyCustomerCart();
        if (
          !Object.keys(guest.items).length ||
          envelope.imports[envelope.guestId]
        )
          return { imported: false, cart };
        if (cart.pending)
          throw new CustomerCartConflict(
            "Keranjang pengunjung belum digabung karena pengajuan akun sebelumnya belum dipastikan. Selesaikan checkout tersebut dahulu.",
          );
        const items = { ...cart.items };
        for (const [id, qty] of Object.entries(guest.items)) {
          const total = (items[id] || 0) + qty;
          if (!validCatalogQuantity(total))
            throw new CustomerCartConflict(
              "Keranjang pengunjung belum digabung karena jumlah barang melebihi 10.000. Gunakan keranjang akun atau kurangi jumlah pengunjung lebih dahulu.",
            );
          items[id] = total;
        }
        const revision = cart.revision + 1;
        const next = {
          ...cart,
          revision,
          items,
          itemRevisions: {
            ...cart.itemRevisions,
            ...Object.fromEntries(
              Object.keys(guest.items).map((id) => [id, revision]),
            ),
          },
        };
        write({
          guestId: uuid(),
          imports: { ...envelope.imports, [envelope.guestId]: owner },
          carts: {
            ...envelope.carts,
            [owner]: next,
            [GUEST]: emptyCustomerCart(uuid()),
          },
        });
        return { imported: true, cart: next };
      }),
    clear: (owner: string) =>
      transaction(async () => {
        const envelope = load();
        write({
          ...envelope,
          carts: { ...envelope.carts, [owner]: emptyCustomerCart(uuid()) },
        });
      }),
    checkout: (
      owner: string,
      intent: CheckoutIntent,
      commandId: string,
      hash: string,
      submit: () => Promise<{ id: string; message: string }>,
    ) =>
      transaction(async () => {
        if (owner === GUEST)
          throw new CustomerCartConflict(
            "Masuk sebagai pelanggan untuk membuat pesanan.",
          );
        let envelope = load();
        let cart = envelope.carts[owner] || emptyCustomerCart();
        if (cart.completed[commandId]) {
          if (
            cart.completed[commandId].hash &&
            cart.completed[commandId].hash !== hash
          )
            throw new CustomerCartConflict(
              "Identitas pengajuan sudah digunakan untuk isian berbeda. Periksa pesanan yang sudah dibuat.",
            );
          return cart.completed[commandId];
        }
        const completedIntent = Object.values(cart.completed).find(
          (result) => result.intentId === intent.id,
        );
        if (completedIntent) return completedIntent;
        if (
          cart.pending &&
          (cart.pending.id !== commandId || cart.pending.hash !== hash)
        )
          throw new CustomerCartConflict(
            "Ada pengajuan yang belum dipastikan dari tab lain. Ulangi pengajuan dari tab asal agar pesanan tidak tercatat dua kali.",
          );
        if (intent.epoch !== cart.epoch)
          throw new CustomerCartConflict(
            "Keranjang sudah dikosongkan. Pilih barang kembali sebelum checkout.",
          );
        if (
          !intent.lines.length ||
          intent.lines.some(
            (line) =>
              !validId(line.productId) ||
              !validCatalogQuantity(line.qty) ||
              !Number.isSafeInteger(line.unitPrice) ||
              line.unitPrice < 0,
          ) ||
          new Set(intent.lines.map((line) => line.productId)).size !==
            intent.lines.length
        )
          throw new Error("Pilihan checkout tidak valid.");
        if (
          intent.mode === "cart" &&
          intent.lines.some(
            (line) =>
              (cart.items[line.productId] || 0) < line.qty ||
              (cart.itemRevisions[line.productId] || 0) !==
                (intent.itemRevisions[line.productId] || 0),
          )
        )
          throw new CustomerCartConflict(
            "Barang yang dipilih berubah di tab lain. Periksa keranjang dan lanjutkan checkout kembali.",
          );
        cart = {
          ...cart,
          pending: { id: commandId, hash, intentId: intent.id },
        };
        envelope = { ...envelope, carts: { ...envelope.carts, [owner]: cart } };
        write(envelope);
        let result: { id: string; message: string };
        try {
          result = await submit();
        } catch (error) {
          if (error instanceof CheckoutRejected)
            write({
              ...envelope,
              carts: {
                ...envelope.carts,
                [owner]: { ...cart, pending: undefined },
              },
            });
          throw error;
        }
        const items = { ...cart.items };
        const itemRevisions = { ...cart.itemRevisions };
        const revision = cart.revision + 1;
        if (intent.mode === "cart")
          for (const line of intent.lines) {
            const left = items[line.productId] - line.qty;
            if (left) items[line.productId] = left;
            else delete items[line.productId];
            itemRevisions[line.productId] = revision;
          }
        const completed = Object.fromEntries(
          [
            ...Object.entries(cart.completed),
            [commandId, { ...result, intentId: intent.id, hash }],
          ].slice(-30),
        );
        write({
          ...envelope,
          carts: {
            ...envelope.carts,
            [owner]: {
              ...cart,
              revision,
              items,
              itemRevisions,
              completed,
              pending: undefined,
            },
          },
        });
        return result;
      }),
  };
}

export type CustomerCartStore = ReturnType<typeof createCustomerCartStore>;
let browserStore: CustomerCartStore | undefined;
let channel: BroadcastChannel | undefined;
const listeners = new Set<() => void>();
const drafts = new Map<string, CustomerDraft>();
const intents = new Map<string, CheckoutIntent>();
function publish() {
  listeners.forEach((listener) => listener());
}
export function getCustomerCartStore() {
  if (browserStore) return browserStore;
  let storage: Storage | undefined;
  try {
    storage = window.localStorage;
  } catch {
    /* Use the tab's memory. */
  }
  const lock: LockPort | undefined =
    typeof navigator !== "undefined" && navigator.locks
      ? async (name, action) =>
          await navigator.locks.request(name, { mode: "exclusive" }, action)
      : undefined;
  if (typeof window !== "undefined") {
    window.addEventListener("storage", (event) => {
      if (event.key === KEY || event.key === null) publish();
    });
    try {
      channel = new BroadcastChannel(KEY);
      channel.onmessage = publish;
    } catch {
      /* Storage events remain available. */
    }
  }
  browserStore = createCustomerCartStore({
    storage,
    lock,
    notify: () => {
      publish();
      try {
        channel?.postMessage("changed");
      } catch {
        /* Local changes remain available. */
      }
    },
  });
  return browserStore;
}
export function subscribeCustomerCart(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
const sessionKey = (kind: string, owner: string) =>
  `unit-toko-customer-${kind}-v7:${owner}`;
function sessionRead(kind: string, owner: string) {
  try {
    return window.sessionStorage.getItem(sessionKey(kind, owner));
  } catch {
    return null;
  }
}
function sessionWrite(kind: string, owner: string, value: unknown) {
  try {
    window.sessionStorage.setItem(
      sessionKey(kind, owner),
      JSON.stringify(value),
    );
  } catch {
    /* In-memory fallback stays in this tab. */
  }
}
function sessionRemove(kind: string, owner: string) {
  try {
    window.sessionStorage.removeItem(sessionKey(kind, owner));
  } catch {
    /* Memory is cleared separately. */
  }
}
export function createCheckoutIntent(
  cart: CustomerCart,
  lines: ShopLine[],
  mode: "cart" | "buy",
): CheckoutIntent {
  return {
    id: crypto.randomUUID(),
    mode,
    epoch: cart.epoch,
    lines: lines.map((line) => ({ ...line })),
    itemRevisions: { ...cart.itemRevisions },
  };
}
export function saveCustomerIntent(owner: string, intent: CheckoutIntent) {
  const previousIntent = loadCustomerIntent(owner);
  const previousDraft =
    owner !== GUEST && previousIntent?.epoch === intent.epoch
      ? loadCustomerDraft(owner, previousIntent)
      : undefined;
  intents.set(owner, intent);
  sessionWrite("intent", owner, intent);
  if (previousDraft) {
    saveCustomerDraft(owner, {
      intent,
      values: { ...previousDraft.values },
      commandId: crypto.randomUUID(),
    });
  } else {
    drafts.delete(owner);
    sessionRemove("draft", owner);
  }
}
export function loadCustomerIntent(owner: string): CheckoutIntent | undefined {
  try {
    const value = JSON.parse(
      sessionRead("intent", owner) || "null",
    ) as CheckoutIntent;
    if (
      value &&
      typeof value.id === "string" &&
      validId(value.id) &&
      typeof value.epoch === "string" &&
      ["cart", "buy"].includes(value.mode) &&
      Array.isArray(value.lines) &&
      value.lines.length > 0 &&
      value.lines.length <= 100 &&
      value.lines.every(
        (line) =>
          line &&
          typeof line.productId === "string" &&
          validId(line.productId) &&
          validCatalogQuantity(line.qty) &&
          Number.isSafeInteger(line.unitPrice) &&
          line.unitPrice >= 0,
      ) &&
      new Set(value.lines.map((line) => line.productId)).size ===
        value.lines.length &&
      value.itemRevisions &&
      typeof value.itemRevisions === "object" &&
      Object.values(value.itemRevisions).every(
        (revision) => Number.isSafeInteger(revision) && revision >= 0,
      )
    )
      return value;
  } catch {
    /* Use this tab's memory. */
  }
  return intents.get(owner);
}
export function handoffGuestIntent(accountId: string) {
  const guest = loadCustomerIntent(GUEST);
  if (!guest) return;
  const owner = customerOwner(accountId);
  const cart = getCustomerCartStore().read(owner);
  saveCustomerIntent(owner, {
    ...createCheckoutIntent(cart, guest.lines, guest.mode),
    id: guest.id,
  });
  intents.delete(GUEST);
  sessionRemove("intent", GUEST);
}
export function loadCustomerDraft(
  owner: string,
  intent: CheckoutIntent,
): CustomerDraft | undefined {
  let draft = drafts.get(owner);
  try {
    draft = JSON.parse(sessionRead("draft", owner) || "null") || draft;
  } catch {
    /* Use this tab's memory. */
  }
  if (
    draft?.intent?.id === intent.id &&
    draft.values &&
    typeof draft.commandId === "string" &&
    ["recipientName", "recipientPhone", "address", "note"].every(
      (key) => typeof draft?.values[key as keyof CustomerDetails] === "string",
    )
  )
    return { ...draft, intent };
}
export function saveCustomerDraft(owner: string, draft: CustomerDraft) {
  drafts.set(owner, draft);
  sessionWrite("draft", owner, draft);
}
export function clearCustomerDraft(owner: string) {
  drafts.delete(owner);
  intents.delete(owner);
  sessionRemove("draft", owner);
  sessionRemove("intent", owner);
}
export async function clearCustomerCart(accountId: string) {
  const owner = customerOwner(accountId);
  await getCustomerCartStore().clear(owner);
  clearCustomerDraft(owner);
}
export async function customerRequestHash(value: unknown) {
  const bytes = await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(JSON.stringify(value)),
  );
  return [...new Uint8Array(bytes)]
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
}
