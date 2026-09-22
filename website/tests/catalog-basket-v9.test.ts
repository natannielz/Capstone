import {test} from "node:test";
import assert from "node:assert/strict";
import {catalogBasket} from "../lib/domain/catalog-basket";
import {createCartStore, type CartStorage} from "../lib/client/cart-storage";
import type {Product} from "../lib/domain/model";

const products: Product[] = [
  {id: "coffee", sku: "COFFEE", name: "Kopi", category: "OMI", unit: "pak", price: 20000, minimum: 2, returnMonths: 1, active: true},
  {id: "tea", sku: "TEA", name: "Teh", category: "OMI", unit: "pak", price: 15000, minimum: 2, returnMonths: 1, active: false},
];

test("v9 internal basket keeps inactive and missing selections visible and blocks partial checkout", () => {
  const items = {coffee: 2, tea: 3, discontinued: 4};
  const before = structuredClone(items);
  const view = catalogBasket(products, items);
  assert.equal(view.canCheckout, false);
  assert.deepEqual(view.lines.map(line => [line.product.id, line.qty]), [["coffee", 2]]);
  assert.deepEqual(view.unavailable.map(line => [line.id, line.qty, line.product?.name]), [["tea", 3, "Teh"], ["discontinued", 4, undefined]]);
  assert.deepEqual(items, before, "Projection must not silently prune persisted buyer intent");
  assert.equal(catalogBasket(products, {tea: 3}).unavailable.length, 1, "An entirely inactive basket is not presented as empty");
  assert.equal(catalogBasket(products, {}).canCheckout, false);
});

test("v9 restoring availability preserves quantity; explicit unavailable removal persists and permits checkout", async () => {
  const values = new Map<string, string>();
  const storage: CartStorage = {getItem: key => values.get(key) ?? null, setItem: (key, value) => {values.set(key, value);}, removeItem: key => {values.delete(key);}};
  const store = createCartStore({accountId: "v9-pic", clientId: "a", storage, lock: async (_name, action) => action()});
  await store.mutate({type: "add", productId: "coffee", quantity: 2});
  await store.mutate({type: "add", productId: "tea", quantity: 3});
  const original = structuredClone(store.read());
  assert.equal(catalogBasket(products, original.items).canCheckout, false);
  const reactivated = products.map(product => ({...product, active: true}));
  assert.equal(catalogBasket(reactivated, store.read().items).canCheckout, true);
  assert.deepEqual(store.read(), original, "Availability checks do not change the saved quantities or revision");
  await store.mutate({type: "remove", productId: "tea", expectedRevision: original.itemRevisions.tea});
  const restored = createCartStore({accountId: "v9-pic", clientId: "b", storage, lock: async (_name, action) => action()});
  assert.deepEqual(restored.read().items, {coffee: 2});
  const view = catalogBasket(products, restored.read().items);
  assert.equal(view.canCheckout, true);
  assert.equal(view.unavailable.length, 0);
  assert.deepEqual(catalogBasket(reactivated, restored.read().items).lines.map(line => line.product.id), ["coffee"], "A removed item cannot reappear merely because the product becomes active again");
});
