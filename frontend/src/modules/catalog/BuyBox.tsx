'use client';

import { AddToCartPanel } from '../ordering/AddToCartPanel';
import type { BuyerVariant } from './catalog-buyer-client';
import shared from '../administration/access.module.css';

/** Only an account that may order gets the buy box. A catalog-only account reads the page without asking for branches it
 *  could never order for. */
export function BuyBox({ item, actorId, actions, isCurrent }: { item: BuyerVariant; actorId: string; actions: readonly string[]; isCurrent: () => boolean }) {
  if (!actions.includes('order.submit')) return <p className={shared.help}>Akun Anda dapat melihat katalog. Hubungi pengelola akses di unit Anda jika perlu memesan untuk cabang.</p>;
  return <AddToCartPanel key={actorId + ':' + item.skuId + ':' + item.definition.id + ':' + (item.currentPrice?.id ?? 'none')} actorId={actorId} item={item} isCurrent={isCurrent} />;
}
