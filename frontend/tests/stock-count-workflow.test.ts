import { test } from "node:test";
import assert from "node:assert/strict";
import {
  parseStockWarehouses, parseStockPackages, parseStockObservation, parseStockHistory,
  stockCountReceipt, stockCountQuantity, prepareStockCount, stockLocationKey,
} from "../src/modules/inventory/stock-count-workflow";
import { actorId, location, warehouse, choice, observation, reference } from "./stock-count-fixtures";

test("zero is explicit and base units use catalog contents without guessing from the label", () => {
  assert.deepEqual(stockCountQuantity("0", 12), { packages: 0, baseUnits: "0" });
  assert.deepEqual(stockCountQuantity("1000000", 2147483647), { packages: 1000000, baseUnits: "2147483647000000" });
  for (const text of ["", " ", "00", "01", "-0", "-1", "1.5", "1e3", "1,000", "1000001", "2\n"])
    assert.throws(() => stockCountQuantity(text, 12), /stock_count_input_invalid/);
  for (const contents of [0, -1, 1.5, 2147483648, NaN])
    assert.throws(() => stockCountQuantity("1", contents), /stock_count_input_invalid/);
});
test("named warehouse choices preserve large IDs and omit private fields", () => {
  const parsed = parseStockWarehouses({ items: [{ ...warehouse, issuer: "private" }] });
  assert.deepEqual(parsed, [warehouse]);
  assert.equal(parsed[0].location.warehouseId, "9007199254740993");
  assert.equal(stockLocationKey(parsed[0].location), stockLocationKey(location));
});
test("warehouse choices reject ambiguous profiles, topology and duplicate scopes", () => {
  for (const bad of [
    { ...warehouse, warehouseName: "" }, { ...warehouse, channelName: " OMI" },
    { ...warehouse, shopName: "Toko\n" }, { ...warehouse, organizationName: "\ud800" },
    { ...warehouse, location: { ...location, kind: "fulfillmentShop" } },
    { ...warehouse, location: { ...location, branchId: "3" } },
    { ...warehouse, location: { ...location, warehouseId: 9007199254740993 } },
    { ...warehouse, location: { ...location, warehouseId: "9223372036854775808" } },
    { ...warehouse, location: { ...location, warehouseId: "1\n" } },
  ]) assert.throws(() => parseStockWarehouses({ items: [bad] }), /stock_count_response_invalid/);
  assert.throws(() => parseStockWarehouses({ items: [warehouse, warehouse] }), /stock_count_response_invalid/);
  assert.throws(() => parseStockWarehouses({ items: Array(257).fill(warehouse) }), /stock_count_response_invalid/);
});
test("package pages retain exact revisions and validate explicit packaging before selection", () => {
  assert.deepEqual(parseStockPackages({ total: "1", items: [choice] }, 0, 25), { total: "1", items: [choice] });
  for (const change of [
    { skuRevision: 9007199254740993 }, { definitionVersion: "01" }, { definitionId: "0" },
    { packageKind: "" }, { packageLabel: "Dus\n" }, { baseUnitsPerPackage: "12" },
    { baseUnitsPerPackage: 0 }, { baseUnitsPerPackage: 2147483648 }, { skuCode: "lower case" },
  ]) assert.throws(() => parseStockPackages({ total: "1", items: [{ ...choice, ...change }] }, 0, 25), /stock_count_response_invalid/);
  for (const page of [{ total: "0", items: [choice] }, { total: "2", items: [choice, choice] }, { total: 1, items: [choice] }])
    assert.throws(() => parseStockPackages(page, 0, 25), /stock_count_response_invalid/);
  assert.throws(() => parseStockPackages({ total: "1", items: [choice] }, 1, 25), /stock_count_response_invalid/);
});
test("review freezes the complete original command including location and package facts", () => {
  const draft = { packages: "0", reason: " Physical count ", observedLotLabel: "  " };
  const selected = { ...choice }, place = { ...location };
  const attempt = prepareStockCount(actorId, place, selected, draft);
  selected.packageLabel = "Changed"; place.warehouseId = "999"; draft.packages = "3";
  assert.equal(attempt.body.packages, 0); assert.equal(attempt.body.observedLotLabel, null);
  assert.equal(attempt.body.reason, "Physical count"); assert.equal(attempt.body.expectedSkuRevision, choice.skuRevision);
  assert.equal(attempt.choice.packageLabel, choice.packageLabel); assert.deepEqual(attempt.body.location, location);
  assert.ok(Object.isFrozen(attempt)); assert.ok(Object.isFrozen(attempt.body)); assert.ok(Object.isFrozen(attempt.body.location)); assert.ok(Object.isFrozen(attempt.choice));
  assert.equal(attempt.operation, "inventory.stock-count.record");
  assert.throws(() => prepareStockCount(actorId, location, choice, { ...draft, reason: "" }), /stock_count_input_invalid/);
  assert.throws(() => prepareStockCount(actorId, location, choice, { ...draft, observedLotLabel: "x\n" }), /stock_count_input_invalid/);
});
test("public observations retain explicit zero and discard internal identity and provenance", () => {
  assert.deepEqual(parseStockObservation({ ...observation, issuer: "private", subject: "secret", countedBy: "1", physicalItemKey: "private", requestKey: "private" }), observation);
  for (const change of [
    { version: "2" }, { packages: undefined }, { packages: null }, { packages: "0" },
    { packages: -1 }, { baseUnits: "1" }, { baseUnits: "00" }, { baseUnitsPerPackage: 0 },
    { baseline: "known" }, { coverage: "all-stock" }, { observedLotLabel: "" }, { reason: "" },
    { recordedAt: "yesterday" }, { recordedAt: "2026-02-30T01:00:00Z" }, { definitionVersion: "0" },
  ]) assert.throws(() => parseStockObservation({ ...observation, ...change }), /stock_count_response_invalid/);
});
test("history is bound to its requested warehouse and selected ID", () => {
  assert.deepEqual(parseStockHistory({ total: "1", items: [observation] }, location, 0, 25), { total: "1", items: [observation] });
  assert.throws(() => parseStockHistory({ total: "1", items: [{ ...observation, location: { ...location, channelId: "99" } }] }, location, 0, 25), /stock_count_response_invalid/);
});
test("recovery accepts only a committed immutable receipt or a fully empty not-confirmed result", () => {
  const receipt = { state: "committed", result: { id: observation.id, version: "1" }, recordedAt: observation.recordedAt };
  assert.deepEqual(stockCountReceipt({ ...receipt, private: "discard" }, reference), receipt);
  assert.equal(stockCountReceipt({ state: "not_confirmed", result: null, recordedAt: null }, reference), null);
  for (const bad of [null, {}, { state: "closed", result: null, recordedAt: null },
    { state: "not_confirmed", result: receipt.result, recordedAt: null },
    { ...receipt, result: { ...receipt.result, version: "2" } }, { ...receipt, recordedAt: "bad" }])
    assert.throws(() => stockCountReceipt(bad, reference), /stock_count_response_invalid/);
  assert.throws(() => stockCountReceipt(receipt, { ...reference, operation: "catalog.sku.revise" }), /stock_count_input_invalid/);
});
