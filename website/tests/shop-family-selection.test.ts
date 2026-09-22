import test from "node:test";
import assert from "node:assert/strict";
import { familyProduct, matchingFamilyProducts, type PublicFamily, type PublicProduct } from "../components/shop/shop-data";

const base: PublicProduct = {
  id: "kopi", familyId: "kopi", sku: "KOPI-1", name: "Kopi Arabika",
  unit: "pak", price: 20000, active: true, category: "Minuman",
  image: "/images/products/coffee.png", description: "Kopi untuk pantry.",
  packaging: "1 pak", available: 0,
};
const family: PublicFamily = {
  id: "kopi", name: "Kopi Arabika", products: [base, {
    ...base, id: "kopi--6", sku: "KOPI-6", name: "Kopi Arabika · paket 6 pak",
    packaging: "Paket 6 pak", price: 115000, available: 8,
  }],
};

test("stock filtering retains a family with a stocked alternate package and displays that same package", () => {
  assert.equal(matchingFamilyProducts(family).some(product => product.available > 0), true);
  const displayed = familyProduct(family, "", true);
  assert.equal(displayed.id, "kopi--6");
  assert.equal(displayed.price, 115000);
  assert.equal(displayed.packaging, "Paket 6 pak");
  assert.equal(familyProduct(family).id, "kopi");
});

test("an exact SKU query does not substitute another stocked package", () => {
  assert.deepEqual(matchingFamilyProducts(family, "  kopi-1  ").map(product => product.id), ["kopi"]);
  assert.equal(matchingFamilyProducts(family, "KOPI-1").some(product => product.available > 0), false);
  assert.equal(matchingFamilyProducts(family, "KOPI-6").some(product => product.available > 0), true);
});

test("name search and partial SKU search include matching stock while unrelated and inactive items do not", () => {
  assert.equal(familyProduct(family, "ARABIKA", true).id, "kopi--6");
  assert.equal(familyProduct(family, "KOPI-", true).id, "kopi--6");
  assert.deepEqual(matchingFamilyProducts(family, "teh"), []);
  const inactive = { ...family, products: family.products.map(product => ({ ...product, active: false })) };
  assert.deepEqual(matchingFamilyProducts(inactive), []);
});

test("all sold-out packages yield no available-stock result without mutating package order", () => {
  const soldOut = { ...family, products: family.products.map(product => ({ ...product, available: 0 })) };
  assert.equal(matchingFamilyProducts(soldOut).some(product => product.available > 0), false);
  assert.deepEqual(family.products.map(product => product.id), ["kopi", "kopi--6"]);
});
