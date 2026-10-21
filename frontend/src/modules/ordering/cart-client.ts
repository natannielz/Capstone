import { RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { createOrderDraftGateway, prepareDraftCommand, type DraftCommand, type DraftResult, type OrderDraft } from './order-draft-client';
import { pickCart } from './cart-model';
import type { OrderContext, OrderSelection } from './order-quote-client';
import type { OrderDelivery } from './order-delivery';

/** The server may have saved the change but its answer never arrived. Sending the same command with the same key again
 *  replays the original result, so the page offers exactly that instead of a fresh attempt. */
export class CartOutcomeUnknown extends Error {
  constructor(readonly command: DraftCommand, readonly key: string) { super('cart_outcome_unknown'); this.name = 'CartOutcomeUnknown'; }
}

type Drafts = Pick<ReturnType<typeof createOrderDraftGateway>, 'list' | 'read' | 'submit' | 'discard'>;
const unknownOutcome = (error: unknown) => !(error instanceof RequestNotSentError) &&
  (!(error instanceof AccessFailure) || error.status === 0 || error.status >= 500);

/** With `submit`, saves go through the caller's tracked journal: an unconfirmed save survives a reload and is checked, never replayed silently. */
export function createCartGateway({ drafts = createOrderDraftGateway(), newKey = () => crypto.randomUUID(), submit }: {
  drafts?: Drafts; newKey?: () => string; submit?: (command: DraftCommand) => Promise<DraftResult> } = {}) {
  async function read(actorId: string, context: OrderContext, signal?: AbortSignal): Promise<OrderDraft | null> {
    return pickCart((await drafts.list(actorId, context, null, signal)).items);
  }
  /** One send and, if its answer is lost, one replay with the same key. */
  async function send(command: DraftCommand, key: string) {
    try { return await drafts.submit(command, key); }
    catch (first) {
      if (!unknownOutcome(first)) throw first;
      try { return await drafts.submit(command, key); }
      catch (second) { throw unknownOutcome(second) ? new CartOutcomeUnknown(command, key) : second; }
    }
  }
  const save = (command: DraftCommand) => submit ? submit(command) : send(command, newKey());
  return {
    read,
    /** Reads the cart, applies the buyer's change to what is saved now and saves it at that revision. If another tab changed
     *  the cart first, the same change is applied once more on top of the newer cart. A changed price is never retried. */
    async apply(actorId: string, context: OrderContext, change: (current: OrderSelection[]) => OrderSelection[], reason: string) {
      for (let attempt = 0; ; attempt++) {
        const cart = await read(actorId, context), next = change(cart?.input.selections ?? []);
        try {
          if (next.length === 0) {
            if (cart) await drafts.discard(actorId, cart.id, cart.revision, reason, newKey());
            return { draftId: null, revision: null };
          }
          const command = prepareDraftCommand(actorId, cart?.id ?? null, cart?.revision ?? '0', { context, selections: next,
            delivery: cart?.input.delivery ?? null, purchaseReference: cart?.input.purchaseReference ?? null }, reason);
          const result = await save(command);
          return { draftId: result.draftId, revision: result.revision };
        } catch (failure) {
          if (attempt === 0 && failure instanceof AccessFailure && failure.status === 409 && failure.code !== 'order_selection_changed') continue;
          throw failure;
        }
      }
    },
    /** Checkout's delivery and purchase reference, written into the same draft at its current revision; its packages stay as saved. */
    async saveDetails(actorId: string, draftId: string, delivery: OrderDelivery, purchaseReference: string | null) {
      for (let attempt = 0; ; attempt++) {
        const draft = await drafts.read(actorId, draftId);
        try {
          const command = prepareDraftCommand(actorId, draft.id, draft.revision, { ...draft.input, delivery, purchaseReference }, 'Lengkapi pengiriman untuk pengajuan');
          const result = await save(command);
          return { draftId: result.draftId, revision: result.revision };
        } catch (failure) {
          if (attempt === 0 && failure instanceof AccessFailure && failure.status === 409 && failure.code !== 'order_selection_changed') continue;
          throw failure;
        }
      }
    },
    /** "Coba lagi" after an unknown outcome: the identical command and key, so a save that did happen is not repeated. */
    async resend(unknown: CartOutcomeUnknown) {
      const result = await send(unknown.command, unknown.key);
      return { draftId: result.draftId, revision: result.revision };
    },
  };
}
export type CartGateway = ReturnType<typeof createCartGateway>;
