import { AccessFailure } from '../administration/workflow';

/** After an automatic load (OCR on open), only a lost session or a changed account ends the review desk. */
export function autoLoadEndsSession(failure: unknown): boolean {
  return failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed');
}
