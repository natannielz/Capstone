import {RequestNotSentError} from '@/api/client';
import type {OperationRecoveryAdapters, OperationReference} from '../administration/operation-journal';
import {type PaymentSupplementGateway, type PaymentSupplementOwner, paymentSupplementOwnerKey} from './payment-pdf-supplement-client';
import {isSupplementOperation, supplementKeyBinding, supplementOperations} from './payment-pdf-supplement-recovery';

/** Mount only on the requester screen with its freshly scoped primary-image submission. Never reconstruct evidence from journal metadata. */
export function paymentSupplementRecoveryAdapter(gateway: PaymentSupplementGateway, owner: PaymentSupplementOwner,
  current: () => boolean): NonNullable<OperationRecoveryAdapters['billing.payment.supplement']> {
  paymentSupplementOwnerKey(owner);
  const check = () => {if (!current()) throw new RequestNotSentError(409, 'payment_supplement_view_stale');};
  return {async lookup(entry: OperationReference) {
    check(); if (!isSupplementOperation(entry.operation)) throw new RequestNotSentError(400, 'payment_supplement_recovery_invalid');
    const binding = supplementKeyBinding(entry.operation, entry.key, owner.actorId);
    if (entry.actorId !== owner.actorId || binding.declarationId !== owner.declarationId || binding.submissionId !== owner.submissionId)
      throw new RequestNotSentError(409, 'payment_supplement_view_stale');
    const saved = binding.supplementId === null ? null : await gateway.detail('requester', owner, binding.supplementId, current); check();
    const result = entry.operation === supplementOperations.reserve
      ? await gateway.recoverReservation(owner, saved!, entry.key, current)
      : await gateway.recoverCommand(entry.operation, owner, entry.key, saved, current);
    check(); return result === null ? null : {state: 'committed', result};
  }};
}

/** Refresh-safe recovery starts from an authorized read of the original image declaration, never saved evidence/body data. */
export function paymentSupplementReloadRecoveryAdapter(gateway: PaymentSupplementGateway, actorId: string,
  primary: (declarationId: string) => Promise<PaymentSupplementOwner>, current: () => boolean): NonNullable<OperationRecoveryAdapters['billing.payment.supplement']> {
  const check = () => {if (!current()) throw new RequestNotSentError(409, 'payment_supplement_view_stale');};
  return {async lookup(entry) {
    check(); if (!isSupplementOperation(entry.operation) || entry.actorId !== actorId) throw new RequestNotSentError(400, 'payment_supplement_recovery_invalid');
    const binding = supplementKeyBinding(entry.operation, entry.key, actorId), owner = await primary(binding.declarationId); check();
    paymentSupplementOwnerKey(owner);
    if (owner.actorId !== actorId || owner.declarationId !== binding.declarationId || owner.submissionId !== binding.submissionId)
      throw new RequestNotSentError(409, 'payment_supplement_view_stale');
    return paymentSupplementRecoveryAdapter(gateway, owner, current).lookup(entry);
  }};
}
