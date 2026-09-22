import assert from "node:assert/strict";
import test from "node:test";
import {
  CheckoutRejected,
  CustomerCartConflict,
  createCheckoutIntent,
  createCustomerCartStore,
  customerOwner,
  customerRequestHash,
  clearCustomerDraft,
  loadCustomerDraft,
  loadCustomerIntent,
  saveCustomerDraft,
  saveCustomerIntent,
} from "../lib/client/customer-cart";
import {
  familyProduct,
  safeShopBack,
  shopFamilies,
  type PublicProduct,
} from "../components/shop/shop-data";

function storagePort() {
  const data = new Map<string, string>();
  return {
    data,
    getItem: (key: string) => data.get(key) ?? null,
    setItem: (key: string, value: string) => {
      data.set(key, value);
    },
    removeItem: (key: string) => {
      data.delete(key);
    },
  };
}
function sharedLock() {
  let tail: Promise<unknown> = Promise.resolve();
  return <T>(_name: string, action: () => Promise<T>): Promise<T> => {
    const next = tail.then(action, action);
    tail = next.catch(() => undefined);
    return next;
  };
}
function setup() {
  const storage = storagePort(),
    lock = sharedLock();
  return {
    storage,
    a: createCustomerCartStore({ storage, lock }),
    b: createCustomerCartStore({ storage, lock }),
  };
}
const owner = customerOwner("customer-a");
const secondOwner = customerOwner("customer-b");
const line = (productId: string, qty: number, unitPrice = 10000) => ({
  productId,
  qty,
  unitPrice,
});
const success = async () => ({ id: "order-a", message: "Pesanan dibuat." });

test("v7: shared tabs retain concurrent additions to same and different SKUs", async () => {
  const { a, b } = setup();
  await Promise.all([
    a.mutate(owner, { type: "add", productId: "air", qty: 4 }),
    b.mutate(owner, { type: "add", productId: "teh", qty: 2 }),
    b.mutate(owner, { type: "add", productId: "air", qty: 1 }),
  ]);
  assert.deepEqual(a.read(owner).items, { air: 5, teh: 2 });
  assert.deepEqual(b.read(owner).items, a.read(owner).items);
});

test("v7: stale absolute edits and removal reject without erasing another tab's quantity", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 1 });
  const oldRevision = b.read(owner).itemRevisions.air;
  await a.mutate(owner, { type: "add", productId: "air", qty: 4 });
  const before = JSON.stringify(a.read(owner));
  for (const type of ["set", "remove"] as const) {
    await assert.rejects(
      b.mutate(owner, {
        type,
        productId: "air",
        qty: 2,
        expectedRevision: oldRevision,
      }),
      CustomerCartConflict,
    );
    assert.equal(JSON.stringify(a.read(owner)), before);
  }
});

test("v7: guest cart imports atomically once across tabs and never into a second account", async () => {
  const { a, b, storage } = setup();
  await a.mutate("guest", { type: "add", productId: "air", qty: 2 });
  await a.mutate(owner, { type: "add", productId: "air", qty: 1 });
  const results = await Promise.all([
    a.importGuest("customer-a"),
    b.importGuest("customer-a"),
  ]);
  assert.equal(results.filter((result) => result.imported).length, 1);
  assert.deepEqual(a.read(owner).items, { air: 3 });
  assert.deepEqual(b.read("guest").items, {});
  await b.importGuest("customer-b");
  assert.deepEqual(b.read(secondOwner).items, {});
  assert.equal(
    [...storage.data.values()].some((value) =>
      /recipient|address|password/.test(value),
    ),
    false,
  );
});

test("v7: over-limit guest merge retains both carts unchanged", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 9999 });
  await a.mutate("guest", { type: "add", productId: "air", qty: 2 });
  const before = JSON.stringify([a.read(owner), a.read("guest")]);
  await assert.rejects(b.importGuest("customer-a"), CustomerCartConflict);
  assert.equal(JSON.stringify([a.read(owner), a.read("guest")]), before);
});

test("v7: replacing a package merges existing SKU and rejects stale or overflowing changes", async () => {
  const { a } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 3 });
  await a.mutate(owner, { type: "add", productId: "air-p3", qty: 2 });
  const stale = a.read(owner).itemRevisions.air;
  await a.replaceSku(owner, "air", "air-p3", stale);
  assert.deepEqual(a.read(owner).items, { "air-p3": 5 });
  await assert.rejects(
    a.replaceSku(owner, "air", "air-p3", stale),
    CustomerCartConflict,
  );
  await a.mutate(owner, { type: "add", productId: "air", qty: 10000 });
  const before = JSON.stringify(a.read(owner));
  await assert.rejects(
    a.replaceSku(owner, "air", "air-p3", a.read(owner).itemRevisions.air),
    /10.000/,
  );
  assert.equal(JSON.stringify(a.read(owner)), before);
});

test("v7: selected checkout removes only submitted quantities and preserves other SKUs", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 5 });
  await a.mutate(owner, { type: "add", productId: "teh", qty: 2 });
  const intent = createCheckoutIntent(a.read(owner), [line("air", 2)], "cart");
  await b.mutate(owner, { type: "add", productId: "kopi", qty: 1 });
  await a.checkout(owner, intent, "command-selected", "payload-a", success);
  assert.deepEqual(b.read(owner).items, { air: 3, teh: 2, kopi: 1 });
});

test("v7: buy now preserves the cart and one intent cannot submit twice from duplicate tabs", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 5 });
  const intent = createCheckoutIntent(a.read(owner), [line("teh", 1)], "buy");
  let submits = 0;
  const submit = async () => {
    submits++;
    return success();
  };
  const results = await Promise.all([
    a.checkout(owner, intent, "command-buy-one", "payload-a", submit),
    b.checkout(owner, intent, "command-buy-two", "payload-a", submit),
  ]);
  assert.equal(submits, 1);
  assert.equal(results[0].id, results[1].id);
  assert.deepEqual(b.read(owner).items, { air: 5 });
});

test("v7: successful command retry returns original order without a second deduction", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 4 });
  const intent = createCheckoutIntent(a.read(owner), [line("air", 2)], "cart");
  await a.checkout(
    owner,
    intent,
    "command-retry-success",
    "payload-a",
    success,
  );
  const result = await b.checkout(
    owner,
    intent,
    "command-retry-success",
    "payload-a",
    async () => {
      assert.fail("must use existing result");
    },
  );
  assert.equal(result.id, "order-a");
  assert.equal(a.read(owner).items.air, 2);
  await assert.rejects(
    b.checkout(
      owner,
      intent,
      "command-retry-success",
      "changed-payload",
      success,
    ),
    CustomerCartConflict,
  );
});

test("v7: uncertain response locks payload and cart, same command retry can recover once", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 3 });
  const intent = createCheckoutIntent(a.read(owner), [line("air", 2)], "cart");
  let created = 0;
  const server = new Map<string, Awaited<ReturnType<typeof success>>>();
  const idempotentServer = async () => {
    if (!server.has("command-lost-response")) {
      created++;
      server.set("command-lost-response", await success());
    }
    return server.get("command-lost-response")!;
  };
  await assert.rejects(
    a.checkout(
      owner,
      intent,
      "command-lost-response",
      "payload-a",
      async () => {
        await idempotentServer();
        throw new TypeError("connection lost after commit");
      },
    ),
    /connection lost/,
  );
  assert.equal(a.read(owner).items.air, 3);
  await assert.rejects(
    b.mutate(owner, { type: "add", productId: "teh", qty: 1 }),
    CustomerCartConflict,
  );
  await assert.rejects(
    b.checkout(owner, intent, "command-new-unsafe", "payload-a", success),
    CustomerCartConflict,
  );
  await assert.rejects(
    b.checkout(
      owner,
      intent,
      "command-lost-response",
      "changed-payload",
      success,
    ),
    CustomerCartConflict,
  );
  await b.checkout(
    owner,
    intent,
    "command-lost-response",
    "payload-a",
    idempotentServer,
  );
  assert.equal(created, 1);
  assert.equal(a.read(owner).items.air, 1);
  assert.equal(a.read(owner).pending, undefined);
});

test("v7: definitive stale-price rejection releases lock and retains cart", async () => {
  const { a } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 2 });
  const intent = createCheckoutIntent(a.read(owner), [line("air", 2)], "cart");
  await assert.rejects(
    a.checkout(
      owner,
      intent,
      "command-stale-price",
      "payload-old",
      async () => {
        throw new CheckoutRejected("Harga berubah", 409);
      },
    ),
    CheckoutRejected,
  );
  assert.deepEqual(a.read(owner).items, { air: 2 });
  assert.equal(a.read(owner).pending, undefined);
  await a.mutate(owner, { type: "add", productId: "teh", qty: 1 });
  assert.equal(a.read(owner).items.teh, 1);
});

test("v7: edit queued during checkout survives selected-item deduction", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 3 });
  const intent = createCheckoutIntent(a.read(owner), [line("air", 2)], "cart");
  let release!: () => void;
  let entered!: () => void;
  const wait = new Promise<void>((resolve) => {
    release = resolve;
  });
  const started = new Promise<void>((resolve) => {
    entered = resolve;
  });
  const checkout = a.checkout(
    owner,
    intent,
    "command-queued",
    "payload-a",
    async () => {
      entered();
      await wait;
      return success();
    },
  );
  await started;
  const addition = b.mutate(owner, { type: "add", productId: "air", qty: 4 });
  release();
  await Promise.all([checkout, addition]);
  assert.equal(a.read(owner).items.air, 5);
});

test("v7: logout clearing one account invalidates old intent and leaves another account intact", async () => {
  const { a, b } = setup();
  await a.mutate(owner, { type: "add", productId: "air", qty: 2 });
  await a.mutate(secondOwner, { type: "add", productId: "teh", qty: 4 });
  const intent = createCheckoutIntent(a.read(owner), [line("air", 1)], "cart");
  await b.clear(owner);
  assert.deepEqual(a.read(owner).items, {});
  assert.deepEqual(a.read(secondOwner).items, { teh: 4 });
  await assert.rejects(
    a.checkout(owner, intent, "command-old-logout", "payload-a", success),
    CustomerCartConflict,
  );
});

test("v7: blocked persistence and unavailable Web Locks retain a usable honest memory cart", async () => {
  const blocked = {
    getItem: () => {
      throw new Error("blocked");
    },
    setItem: () => {
      throw new Error("blocked");
    },
    removeItem: () => undefined,
  };
  const memory = createCustomerCartStore({
    storage: blocked,
    lock: sharedLock(),
  });
  await memory.mutate(owner, { type: "add", productId: "air", qty: 2 });
  assert.equal(memory.persistent, false);
  assert.equal(memory.read(owner).items.air, 2);
  const denied = createCustomerCartStore({
    storage: storagePort(),
    lock: async () => {
      throw new Error("lock denied");
    },
  });
  await denied.mutate(owner, { type: "add", productId: "teh", qty: 3 });
  assert.equal(denied.persistent, false);
  assert.equal(denied.read(owner).items.teh, 3);
});

test("v7: callback failure inside lock is not mistaken for denied lock and replayed", async () => {
  const { a } = setup();
  let submits = 0;
  const intent = createCheckoutIntent(a.read(owner), [line("air", 1)], "buy");
  await assert.rejects(
    a.checkout(owner, intent, "command-server-down", "payload-a", async () => {
      submits++;
      throw new Error("server unavailable");
    }),
  );
  assert.equal(submits, 1);
  assert.equal(a.persistent, true);
});

test("v7: invalid quantities and duplicate checkout rows fail before submit", async () => {
  const { a } = setup();
  for (const qty of [0, -1, 0.5, 10001, Number.NaN])
    await assert.rejects(
      a.mutate(owner, { type: "add", productId: "air", qty }),
    );
  await assert.rejects(
    a.mutate(owner, { type: "add", productId: "__proto__", qty: 1 }),
  );
  const intent = createCheckoutIntent(
    a.read(owner),
    [line("air", 1), line("air", 1)],
    "buy",
  );
  await assert.rejects(
    a.checkout(owner, intent, "command-invalid", "payload-a", async () => {
      assert.fail("invalid checkout cannot submit");
    }),
  );
  assert.deepEqual(a.read(owner).items, {});
});

test("v7: checkout draft is private to its owner and survives reloading its intent", () => {
  const intent = createCheckoutIntent(
    createCustomerCartStore({}).read(owner),
    [line("air", 1)],
    "buy",
  );
  saveCustomerIntent(owner, intent);
  const draft = {
    intent,
    values: {
      recipientName: "Penerima uji",
      recipientPhone: "080000000",
      address: "Alamat uji",
      note: "Catatan",
    },
    commandId: "command-private-draft",
  };
  saveCustomerDraft(owner, draft);
  assert.equal(loadCustomerIntent(owner)?.id, intent.id);
  assert.equal(loadCustomerDraft(owner, intent)?.values.address, "Alamat uji");
  assert.equal(loadCustomerDraft(secondOwner, intent), undefined);
  clearCustomerDraft(owner);
  assert.equal(loadCustomerIntent(owner), undefined);
  assert.equal(loadCustomerDraft(owner, intent), undefined);
});

test("v7: returning to change selected goods retains the same owner's address with a fresh command", () => {
  const cart = createCustomerCartStore({}).read(owner);
  const first = createCheckoutIntent(cart, [line("air", 1)], "buy");
  saveCustomerIntent(owner, first);
  saveCustomerDraft(owner, {
    intent: first,
    values: {
      recipientName: "Nama uji",
      recipientPhone: "080000000",
      address: "Alamat yang sudah diketik",
      note: "Catatan pengiriman",
    },
    commandId: "previous-command",
  });
  const changed = createCheckoutIntent(cart, [line("teh", 2)], "cart");
  saveCustomerIntent(owner, changed);
  const draft = loadCustomerDraft(owner, changed);
  assert.equal(draft?.values.address, "Alamat yang sudah diketik");
  assert.equal(draft?.values.note, "Catatan pengiriman");
  assert.notEqual(draft?.commandId, "previous-command");
  assert.deepEqual(draft?.intent.lines, [line("teh", 2)]);
  assert.equal(loadCustomerDraft(secondOwner, changed), undefined);
  clearCustomerDraft(owner);
});

test("v7: payload fingerprint is stable, changes with recipient and contains no cleartext", async () => {
  const body = {
    recipientName: "Nama uji",
    address: "Alamat privat uji",
    lines: [line("air", 1)],
  };
  const hash = await customerRequestHash(body);
  assert.equal(hash, await customerRequestHash(body));
  assert.match(hash, /^[a-f0-9]{64}$/);
  assert.notEqual(
    hash,
    await customerRequestHash({ ...body, address: "Alamat lain" }),
  );
});

test("v7: family display follows exact SKU and back links reject external destinations", () => {
  const base: PublicProduct = {
    id: "air",
    familyId: "air",
    sku: "OMI-001",
    name: "Air mineral",
    unit: "dus",
    price: 54000,
    active: true,
    category: "OMI",
    image: "/water.png",
    description: "",
    packaging: "1 dus",
    available: 10,
  };
  const p3 = {
    ...base,
    id: "air-p3",
    sku: "OMI-001-P3",
    name: "Air mineral · paket 3 dus",
    price: 162000,
    available: 4,
  };
  const families = shopFamilies([base, p3]);
  assert.equal(families.length, 1);
  assert.equal(familyProduct(families[0], "omi-001-p3").id, p3.id);
  assert.equal(
    safeShopBack("/shop?collection=pantry&sort=price-low"),
    "/shop?collection=pantry&sort=price-low",
  );
  for (const input of [
    "https://example.com/shop",
    "//example.com/shop",
    "/workspace",
    "javascript:alert(1)",
  ])
    assert.equal(safeShopBack(input), "/shop");
});
