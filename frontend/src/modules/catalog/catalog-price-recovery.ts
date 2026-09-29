import { AccessFailure, MutationLedger } from '../administration/workflow';
import { priceId } from './catalog-price-workflow';

/** The SKU is part of the opaque request reference; no price, reason or credential is persisted. */
export function priceOperationKey(skuId: string, nonce: string = crypto.randomUUID()) {
  const key = 'price:' + priceId(skuId) + ':' + nonce; priceRecoverySku(key); return key;
}
export function priceRecoverySku(key: string): string {
  const match = /^price:([1-9][0-9]{0,18}):[A-Za-z0-9_-]{1,90}$(?![\s\S])/.exec(key);
  if (!match || key.length > 128) throw new AccessFailure(400, 'recovery_reference_invalid');
  return priceId(match[1]);
}
export class PriceMutationLedger extends MutationLedger {
  readonly useSku: (id: string) => void;
  constructor() {
    let skuId = '';
    super(() => priceOperationKey(skuId));
    this.useSku = id => { skuId = priceId(id); };
  }
}
