import { test } from "node:test";
import assert from "node:assert/strict";
import { cameraScanSupported, codeKindFromScan, CAMERA_FORMATS } from "../src/modules/inventory/camera-scan";

test("a scanned value with a valid GTIN length and check digit is a GTIN, anything else is an SKU code", () => {
  assert.equal(codeKindFromScan("8992761111014", "ean_13"), "gtin");
  assert.equal(codeKindFromScan("96385074", "ean_8"), "gtin");
  assert.equal(codeKindFromScan("036000291452", "upc_a"), "gtin");
  assert.equal(codeKindFromScan("8992761111015", "ean_13"), null, "bad check digit is refused, not guessed");
  assert.equal(codeKindFromScan("SKU-PENA-12", "code_128"), "sku");
  assert.equal(codeKindFromScan("  ", "code_128"), null);
});

test("camera scanning is offered only where the browser has a BarcodeDetector and a camera API", () => {
  assert.equal(cameraScanSupported({ BarcodeDetector: class {}, navigator: { mediaDevices: { getUserMedia: () => undefined } } }), true);
  assert.equal(cameraScanSupported({ navigator: { mediaDevices: { getUserMedia: () => undefined } } }), false, "iOS Safari: no detector");
  assert.equal(cameraScanSupported({ BarcodeDetector: class {}, navigator: {} }), false);
});

test("the detector asks only for retail and internal label formats", () => {
  assert.deepEqual([...CAMERA_FORMATS].sort(), ["code_128", "ean_13", "ean_8", "upc_a", "upc_e"]);
});
