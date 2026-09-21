import { test } from "node:test";
import assert from "node:assert/strict";
import {
  CartConflictError,
  cartStorageKey,
  checkoutDraftKey,
  createCartStore,
  parseCart,
  restoreCheckoutDraft,
  type CartLock,
  type CartStorage,
  type CheckoutDraft,
} from "../lib/client/cart-storage";
import { chooseCatalogProduct, productFamilies } from "../lib/domain/catalog";
import { seedState } from "../lib/domain/seed";

class MemoryStorage implements CartStorage {
  values = new Map<string, string>();
  getItem(key: string) {
    return this.values.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.values.set(key, value);
  }
  removeItem(key: string) {
    this.values.delete(key);
  }
}

function sharedLock(): CartLock {
  const tails = new Map<string, Promise<unknown>>();
  return (name, action) => {
    const next = (tails.get(name) || Promise.resolve()).then(action);
    tails.set(
      name,
      next.catch(() => undefined),
    );
    return next;
  };
}

function tabs() {
  const storage = new MemoryStorage();
  const lock = sharedLock();
  const store = (clientId: string, accountId = "pic-a") =>
    createCartStore({ storage, lock, clientId, accountId });
  return {
    storage,
    a: store("a"),
    b: store("b"),
    anotherAccount: store("c", "pic-b"),
  };
}

test("legacy whole-cart writes reproduce the observed loss of four water units", () => {
  let persisted = { air: 1 } as Record<string, number>;
  const oldTabB = { ...persisted };
  persisted = { ...persisted, air: 5 };
  persisted = { ...oldTabB, teh: 1 };
  assert.deepEqual(persisted, { air: 1, teh: 1 });
  assert.notEqual(persisted.air, 5);
});

test("an exact SKU search overrides a previously selected package and uses that SKU's price", () => {
  const family = productFamilies(seedState().products).find(
    (item) => item.id === "air",
  )!;
  const result = chooseCatalogProduct(family, "air--6", "  omi-001-p3  ");
  assert.equal(result.id, "air--3");
  assert.equal(result.price, 162000);
  assert.equal(
    chooseCatalogProduct(family, "air--6", "air mineral").id,
    "air--6",
  );
  assert.equal(chooseCatalogProduct(family, "air--6", "paket 3").id, "air--3");
});

test("cross-tab add reads the latest cart instead of replacing a sibling's water edit", async () => {
  const { a, b } = tabs();
  await a.mutate({ type: "add", productId: "air", quantity: 1 });
  const stale = b.read();
  await a.mutate({
    type: "set",
    productId: "air",
    quantity: 5,
    expectedRevision: stale.itemRevisions.air,
  });
  await b.mutate({ type: "add", productId: "teh", quantity: 1 });
  assert.deepEqual(a.read().items, { air: 5, teh: 1 });
  assert.deepEqual(b.read().items, a.read().items);
});

test("simultaneous additive mutations keep both quantities under the account lock", async () => {
  const { a, b } = tabs();
  await Promise.all([
    a.mutate({ type: "add", productId: "air", quantity: 4 }),
    b.mutate({ type: "add", productId: "air", quantity: 7 }),
  ]);
  assert.equal(a.read().items.air, 11);
  assert.equal(a.read().revision, 2);
});

test("stale absolute edits and removals report a conflict without changing the newer cart", async () => {
  const { a, b } = tabs();
  await a.mutate({ type: "add", productId: "air", quantity: 1 });
  const stale = b.read();
  await a.mutate({
    type: "set",
    productId: "air",
    quantity: 5,
    expectedRevision: stale.itemRevisions.air,
  });
  const latest = structuredClone(a.read());
  await assert.rejects(
    b.mutate({
      type: "set",
      productId: "air",
      quantity: 2,
      expectedRevision: stale.itemRevisions.air,
    }),
    CartConflictError,
  );
  await assert.rejects(
    b.mutate({
      type: "remove",
      productId: "air",
      expectedRevision: stale.itemRevisions.air,
    }),
    CartConflictError,
  );
  assert.deepEqual(a.read(), latest);
});

test("queued typing from one tab can complete without a false cross-tab conflict", async () => {
  const { a } = tabs();
  await a.mutate({ type: "add", productId: "air", quantity: 1 });
  const expectedRevision = a.read().itemRevisions.air;
  await Promise.all([
    a.mutate({ type: "set", productId: "air", quantity: 2, expectedRevision }),
    a.mutate({ type: "set", productId: "air", quantity: 25, expectedRevision }),
  ]);
  assert.equal(a.read().items.air, 25);
});

test("account clear is visible to sibling tabs, preserves another account, and blocks stale resurrection", async () => {
  const { a, b, anotherAccount } = tabs();
  await a.mutate({ type: "add", productId: "air", quantity: 5 });
  await anotherAccount.mutate({ type: "add", productId: "teh", quantity: 3 });
  const stale = b.read();
  await a.clear();
  assert.deepEqual(b.read().items, {});
  assert.deepEqual(anotherAccount.read().items, { teh: 3 });
  await assert.rejects(
    b.mutate({
      type: "set",
      productId: "air",
      quantity: 1,
      expectedRevision: stale.itemRevisions.air,
    }),
    CartConflictError,
  );
  assert.deepEqual(a.read().items, {});
});

test("only one tab submits a reviewed cart; a queued add survives after successful checkout", async () => {
  const { a, b } = tabs();
  await a.mutate({ type: "add", productId: "air", quantity: 2 });
  const reviewed = a.read().revision;
  let unlock!: () => void;
  let began!: () => void;
  const waiting = new Promise<void>((resolve) => {
    unlock = resolve;
  });
  const started = new Promise<void>((resolve) => {
    began = resolve;
  });
  let submissions = 0;
  const first = a.checkout(reviewed, async () => {
    submissions++;
    began();
    await waiting;
    return "order-1";
  });
  await started;
  const duplicate = b.checkout(reviewed, async () => {
    submissions++;
    return "order-2";
  });
  const duplicateCheck = assert.rejects(duplicate, CartConflictError);
  const laterAdd = b.mutate({ type: "add", productId: "teh", quantity: 1 });
  unlock();
  assert.equal(await first, "order-1");
  await duplicateCheck;
  await laterAdd;
  assert.equal(submissions, 1);
  assert.deepEqual(a.read().items, { teh: 1 });
});

test("checkout rechecks the reviewed revision and retains cart data after a failed request", async () => {
  const { a, b } = tabs();
  await a.mutate({ type: "add", productId: "air", quantity: 2 });
  const oldReview = a.read().revision;
  await b.mutate({ type: "add", productId: "teh", quantity: 1 });
  let sent = false;
  await assert.rejects(
    a.checkout(oldReview, async () => {
      sent = true;
    }),
    CartConflictError,
  );
  assert.equal(sent, false);
  const latest = structuredClone(a.read());
  await assert.rejects(
    a.checkout(latest.revision, async () => {
      throw new Error("network unavailable");
    }),
    /network unavailable/,
  );
  assert.deepEqual(a.read(), latest);
});

test("blocked storage falls back to a usable serialized memory cart and never claims persistence", async () => {
  const storage: CartStorage = {
    getItem() {
      throw new Error("SecurityError");
    },
    setItem() {
      throw new Error("SecurityError");
    },
    removeItem() {
      throw new Error("SecurityError");
    },
  };
  const store = createCartStore({
    accountId: "private",
    clientId: "a",
    storage,
    lock: sharedLock(),
  });
  await Promise.all([
    store.mutate({ type: "add", productId: "air", quantity: 2 }),
    store.mutate({ type: "add", productId: "air", quantity: 3 }),
  ]);
  assert.equal(store.persistent, false);
  assert.equal(store.read().items.air, 5);
  store.acceptExternalClear("logout-other-tab");
  assert.deepEqual(store.read().items, {});
});

test("a write failure preserves all queued edits in memory", async () => {
  const storage = new MemoryStorage();
  storage.setItem = () => {
    throw new Error("QuotaExceededError");
  };
  const store = createCartStore({
    accountId: "quota",
    clientId: "a",
    storage,
    lock: sharedLock(),
  });
  await Promise.all([
    store.mutate({ type: "add", productId: "air", quantity: 2 }),
    store.mutate({ type: "add", productId: "teh", quantity: 3 }),
  ]);
  assert.equal(store.persistent, false);
  assert.deepEqual(store.read().items, { air: 2, teh: 3 });
});

test("legacy cart migration retains valid quantities and removes the old key only on a successful write", async () => {
  const { a, storage } = tabs();
  storage.setItem(
    "unit-toko-cart-pic-a",
    JSON.stringify({
      air: 5,
      zero: 0,
      negative: -1,
      fractional: 1.5,
      excessive: 10001,
    }),
  );
  assert.deepEqual(a.read().items, { air: 5 });
  await a.mutate({ type: "add", productId: "teh", quantity: 1 });
  assert.deepEqual(parseCart(storage.getItem(cartStorageKey("pic-a"))).items, {
    air: 5,
    teh: 1,
  });
  assert.equal(storage.getItem("unit-toko-cart-pic-a"), null);
  assert.deepEqual(parseCart("{broken").items, {});
  assert.deepEqual(
    parseCart(JSON.stringify({ revision: -1, items: { air: 7 } })).items,
    {},
  );
});

test("quantity validation leaves the existing cart unchanged on invalid or overflowing mutations", async () => {
  const { a } = tabs();
  await a.mutate({ type: "add", productId: "air", quantity: 10000 });
  const before = structuredClone(a.read());
  await assert.rejects(
    a.mutate({ type: "add", productId: "air", quantity: 1 }),
    /bilangan bulat/,
  );
  await assert.rejects(
    a.mutate({
      type: "set",
      productId: "air",
      quantity: 1.5,
      expectedRevision: before.itemRevisions.air,
    }),
    /bilangan bulat/,
  );
  assert.deepEqual(a.read(), before);
});

test("a draft from before another tab's checkout is not restored after a suspended tab reloads", () => {
  const storage = new MemoryStorage();
  const oldDraft: CheckoutDraft = {
    commandId: "old-command",
    values: {
      divisionId: "div-ops",
      date: "2026-09-17",
      neededAt: "2026-09-17",
      address: "Old delivery point",
      note: "Previous order",
    },
  };
  const fresh: CheckoutDraft = {
    commandId: "new-command",
    cartClearToken: "checkout:b:2",
    values: {
      ...oldDraft.values,
      address: "Default division address",
      note: "",
    },
  };
  storage.setItem(checkoutDraftKey("pic-a"), JSON.stringify(oldDraft));
  assert.deepEqual(restoreCheckoutDraft(storage, "pic-a", fresh), fresh);
  storage.setItem(
    checkoutDraftKey("pic-a"),
    JSON.stringify({ ...oldDraft, cartClearToken: fresh.cartClearToken }),
  );
  assert.deepEqual(restoreCheckoutDraft(storage, "pic-a", fresh), {
    ...oldDraft,
    cartClearToken: fresh.cartClearToken,
  });
});

test("checkout draft reload keeps all custom delivery fields and remains scoped to its account", () => {
  const storage = new MemoryStorage();
  const fallback: CheckoutDraft = {
    commandId: "new-command",
    values: {
      divisionId: "",
      date: "2026-09-17",
      neededAt: "2026-09-17",
      address: "",
      note: "",
    },
  };
  const edited: CheckoutDraft = {
    commandId: "retry-command",
    values: {
      divisionId: "div-ops",
      date: "2026-09-18",
      neededAt: "2026-09-25",
      address: "Lantai 8, Ruang Rapat B",
      note: "Hubungi penerima sebelum tiba",
    },
  };
  storage.setItem(
    checkoutDraftKey("kepala"),
    JSON.stringify({
      ...edited,
      password: "discarded",
      values: { ...edited.values, password: "discarded" },
    }),
  );
  assert.deepEqual(restoreCheckoutDraft(storage, "kepala", fallback), edited);
  assert.deepEqual(restoreCheckoutDraft(storage, "pic-a", fallback), fallback);
  storage.setItem(
    checkoutDraftKey("kepala"),
    JSON.stringify({
      ...edited,
      values: { ...edited.values, address: "", note: "" },
    }),
  );
  assert.equal(
    restoreCheckoutDraft(storage, "kepala", fallback).values.address,
    "",
  );
  storage.setItem(checkoutDraftKey("kepala"), "not json");
  assert.deepEqual(restoreCheckoutDraft(storage, "kepala", fallback), fallback);
});
