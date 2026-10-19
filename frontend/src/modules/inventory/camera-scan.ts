import { canonicalIdentifier } from '../catalog/catalog-identifier-client';

/** Retail GTIN symbologies plus Code 128 for internal SKU labels. */
export const CAMERA_FORMATS = ['ean_13', 'ean_8', 'upc_a', 'upc_e', 'code_128'] as const;
const gtinFormats = new Set(['ean_13', 'ean_8', 'upc_a', 'upc_e']);

/** Which ScanInput code kind a camera read belongs to; null when it cannot be trusted (bad checksum, empty). */
export function codeKindFromScan(raw: string, format: string): 'gtin' | 'sku' | null {
  const value = raw.trim();
  if (!value) return null;
  if (gtinFormats.has(format) || /^[0-9]{8}$|^[0-9]{12,14}$/.test(value)) {
    try { canonicalIdentifier('gtin', value); return 'gtin'; } catch { return null; }
  }
  try { canonicalIdentifier('skuCode', value); return 'sku'; } catch { return null; }
}

/** Camera scanning needs the native BarcodeDetector (Chrome on Android, not iOS Safari) and a camera API. */
export function cameraScanSupported(scope: { BarcodeDetector?: unknown; navigator?: { mediaDevices?: { getUserMedia?: unknown } } }): boolean {
  return typeof scope.BarcodeDetector === 'function' && typeof scope.navigator?.mediaDevices?.getUserMedia === 'function';
}
