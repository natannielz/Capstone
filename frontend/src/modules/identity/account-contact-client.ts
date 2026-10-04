import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {OperationReference} from '../administration/operation-journal';

export const accountContactOperation = 'account.contact.update' as const;
export const accountContactPurpose = 'account-contact' as const;
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/u).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.string().regex(/^(?:0|[1-9][0-9]{0,2})$(?![\s\S])/u).refine(value => BigInt(value) <= BigInt(256));
const positiveRevision = revision.refine(value => value !== '0');
const timestamp = z.string().datetime({offset: true}).refine(value => /(?:Z|\+00:00)$/u.test(value) &&
  Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 19) === value.slice(0, 19));
const text = (limit: number) => z.string().min(1).refine(value => value.isWellFormed() && value === value.trim() &&
  value === value.normalize('NFC') && [...value].length <= limit && !/[\p{Cc}\p{Cf}\p{Cs}]/u.test(value));
const contactSchema = z.object({name: text(160), phone: text(40).nullable(), position: text(160).nullable(), address: text(1000).nullable()}).strict();
const currentSchema = z.object({userId: id, revision, name: text(160).nullable(), phone: text(40).nullable(),
  position: text(160).nullable(), address: text(1000).nullable(), updatedAt: timestamp.nullable()}).strict()
  .refine(value => value.revision === '0'
    ? value.name === null && value.phone === null && value.position === null && value.address === null && value.updatedAt === null
    : value.name !== null && value.updatedAt !== null);
const resultSchema = z.object({userId: id, revision: positiveRevision, historyId: id, recordedAt: timestamp}).strict();
const historyItemSchema = z.object({historyId: id, revision: positiveRevision, contact: contactSchema, recordedAt: timestamp, reason: text(1000)}).strict();
const historySchema = z.object({items: z.array(historyItemSchema).max(100), nextBeforeHistoryId: id.nullable()}).strict();
const recoverySchema = z.object({result: resultSchema.nullable()}).strict();
const entrySchema = z.object({actorId: id, operation: z.literal(accountContactOperation), key: z.string(), createdAt: timestamp}).strict();
const invalid = (): never => {throw new AccessFailure(502, 'account_contact_response_invalid');};
function parse<T>(schema: z.ZodType<T>, value: unknown): T {const parsed = schema.safeParse(value); return parsed.success ? parsed.data : invalid();}
export type AccountContactCurrent = z.infer<typeof currentSchema>;
export type AccountContactResult = z.infer<typeof resultSchema>;
export type AccountContactHistory = z.infer<typeof historySchema>;
export type AccountContactFields = Readonly<z.infer<typeof contactSchema>>;
export type AccountContactCommand = Readonly<{actorId: string; expectedRevision: string;
  body: Readonly<AccountContactFields & {purpose: typeof accountContactPurpose; reason: string}>}>;
const commandSchema = z.object({actorId: id, expectedRevision: revision.refine(value => BigInt(value) < BigInt(256)),
  body: contactSchema.extend({purpose: z.literal(accountContactPurpose), reason: text(1000)}).strict()}).strict();

export function parseAccountContactCurrent(value: unknown, actorId: string): AccountContactCurrent {
  const current = parse(currentSchema, value); return current.userId === parse(id, actorId) ? current : invalid();
}
export function parseAccountContactResult(value: unknown, actorId: string): AccountContactResult {
  const result = parse(resultSchema, value); return result.userId === parse(id, actorId) ? result : invalid();
}
export function parseAccountContactHistory(value: unknown, before: string | null, limit: number): AccountContactHistory {
  const history = parse(historySchema, value);
  if (!Number.isInteger(limit) || limit < 1 || limit > 100 || history.items.length > limit) return invalid();
  if (before !== null) parse(id, before);
  let previousId = before, previousRevision: string | null = null;
  for (const item of history.items) {
    if (previousId !== null && BigInt(item.historyId) >= BigInt(previousId) ||
      previousRevision !== null && BigInt(item.revision) >= BigInt(previousRevision)) return invalid();
    previousId = item.historyId; previousRevision = item.revision;
  }
  if (history.nextBeforeHistoryId !== null && (history.items.length !== limit || history.nextBeforeHistoryId !== previousId)) return invalid();
  return history;
}
export function accountContactKey(actorId: string, uuid = crypto.randomUUID()): string {
  const key = `account.contact.${parse(id, actorId)}.${uuid}`; accountContactKeyActor(key); return key;
}
export function accountContactKeyActor(key: string): string {
  const match = /^account\.contact\.([1-9][0-9]{0,18})\.[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/u.exec(key);
  return match ? parse(id, match[1]) : invalid();
}
function entry(value: unknown, actorId: string): OperationReference {
  const saved = parse(entrySchema, value);
  if (saved.actorId !== actorId || accountContactKeyActor(saved.key) !== actorId) throw new AccessFailure(409, 'actor_changed');
  return Object.freeze(saved);
}
function freezeCommand(value: AccountContactCommand): AccountContactCommand {
  const parsed = commandSchema.safeParse(value);
  if (!parsed.success) throw new RequestNotSentError(400, 'invalid_account_contact');
  return Object.freeze({...parsed.data, body: Object.freeze(parsed.data.body)});
}
/** The form is normalized locally; response parsing never silently repairs server data. */
export function prepareAccountContact(actorId: string, current: AccountContactCurrent,
  fields: {name: string; phone: string; position: string; address: string}, reason: string): AccountContactCommand {
  const source = parseAccountContactCurrent(current, actorId);
  const canonical = (value: string) => value.normalize('NFC').trim();
  const optional = (value: string) => canonical(value) || null;
  return freezeCommand({actorId, expectedRevision: source.revision, body: {purpose: accountContactPurpose,
    name: canonical(fields.name), phone: optional(fields.phone), position: optional(fields.position),
    address: optional(fields.address), reason: canonical(reason)}});
}

type Path = '/api/v1/identity/contact' | '/api/v1/identity/contact/query' | '/api/v1/identity/contact/history/query' | '/api/v1/identity/contact/operations/query';
export type AccountContactRequest = Readonly<{path: Path; headers: Readonly<{'X-Expected-Actor-Id': string; 'If-Match'?: string; 'Idempotency-Key'?: string}>;
  body: Readonly<Record<string, unknown>>; isCurrent: () => boolean; signal?: AbortSignal}>;
export type AccountContactTransport = {send(request: AccountContactRequest): Promise<Response>};
export function createAccountContactTransport(options: {fetch?: typeof globalThis.fetch; origin?: string} = {}): AccountContactTransport {
  const fetcher = options.fetch ?? globalThis.fetch;
  return {async send(request) {
    const origin = options.origin ?? window.location.origin, url = new URL(request.path, origin);
    if (url.origin !== new URL(origin).origin || typeof window !== 'undefined' && url.origin !== window.location.origin)
      throw new RequestNotSentError(400, 'invalid_origin');
    const before = () => {if (request.signal?.aborted || !request.isCurrent()) throw new RequestNotSentError(409, 'account_contact_view_changed');};
    before();
    const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, request.signal);
    before();
    return fetcher(new Request(url, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: request.signal,
      headers: {...request.headers, 'X-CSRF-Token': token, 'Content-Type': 'application/json'}, body: JSON.stringify(request.body)}));
  }};
}
async function json(response: Response): Promise<unknown> {
  const value: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const problem = value !== null && typeof value === 'object' ? value as Record<string, unknown> : {};
    throw new AccessFailure(response.status, typeof problem.code === 'string' ? problem.code : 'request_failed');
  }
  return value;
}
function stillCurrent(isCurrent: () => boolean, signal?: AbortSignal) {
  if (signal?.aborted || !isCurrent()) throw new AccessFailure(409, 'account_contact_view_changed');
}
export function createAccountContactGateway(transport: AccountContactTransport = createAccountContactTransport()) {
  return {
    async current(actorId: string, isCurrent: () => boolean, signal?: AbortSignal) {
      parse(id, actorId); stillCurrent(isCurrent, signal);
      const result = await json(await transport.send({path: '/api/v1/identity/contact/query', headers: {'X-Expected-Actor-Id': actorId},
        body: {purpose: accountContactPurpose}, isCurrent, signal}));
      stillCurrent(isCurrent, signal); return parseAccountContactCurrent(result, actorId);
    },
    async history(actorId: string, before: string | null, limit: number, isCurrent: () => boolean, signal?: AbortSignal) {
      parse(id, actorId); if (before !== null) parse(id, before);
      if (!Number.isInteger(limit) || limit < 1 || limit > 100) throw new RequestNotSentError(400, 'invalid_account_contact_history');
      stillCurrent(isCurrent, signal);
      const result = await json(await transport.send({path: '/api/v1/identity/contact/history/query', headers: {'X-Expected-Actor-Id': actorId},
        body: {purpose: accountContactPurpose, beforeHistoryId: before, limit}, isCurrent, signal}));
      stillCurrent(isCurrent, signal); return parseAccountContactHistory(result, before, limit);
    },
    async save(value: AccountContactCommand, key: string, isCurrent: () => boolean, signal?: AbortSignal) {
      const command = freezeCommand(value);
      if (accountContactKeyActor(key) !== command.actorId) throw new RequestNotSentError(400, 'invalid_account_contact');
      if (signal?.aborted || !isCurrent()) throw new RequestNotSentError(409, 'account_contact_view_changed');
      const response = await transport.send({path: '/api/v1/identity/contact', headers: {'X-Expected-Actor-Id': command.actorId,
        'If-Match': `"${command.expectedRevision}"`, 'Idempotency-Key': key}, body: command.body, isCurrent, signal});
      if (signal?.aborted || !isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const data = await json(response);
      if (signal?.aborted || !isCurrent()) throw new AccessFailure(503, 'commit_outcome_unknown');
      const result = parseAccountContactResult(data, command.actorId);
      return BigInt(result.revision) === BigInt(command.expectedRevision) + BigInt(1) ? result : invalid();
    },
    async recover(actorId: string, value: OperationReference, isCurrent: () => boolean, signal?: AbortSignal) {
      const saved = entry(value, actorId); stillCurrent(isCurrent, signal);
      const result = parse(recoverySchema, await json(await transport.send({path: '/api/v1/identity/contact/operations/query',
        headers: {'X-Expected-Actor-Id': actorId}, body: {purpose: accountContactPurpose, key: saved.key}, isCurrent, signal})));
      stillCurrent(isCurrent, signal); return result.result === null ? null : parseAccountContactResult(result.result, actorId);
    },
  };
}
export type AccountContactGateway = ReturnType<typeof createAccountContactGateway>;
type ContactJournal = {list(actorId: string): OperationReference[]; remember(actorId: string, operation: string, key: string): OperationReference; confirmed(value: OperationReference): void};
export function createAccountContactWorkflow(gateway: AccountContactGateway, journal: ContactJournal, uuid: () => string = () => crypto.randomUUID()) {
  const pending = (actorId: string) => journal.list(actorId).filter(value => value.operation === accountContactOperation).map(value => entry(value, actorId));
  const clear = (saved: OperationReference) => {try {journal.confirmed(saved);} catch { /* Keep lookup-only recovery if cleanup fails. */ }};
  return {
    pending,
    async save(value: AccountContactCommand, isCurrent: () => boolean, signal?: AbortSignal) {
      const command = freezeCommand(value);
      if (signal?.aborted || !isCurrent()) throw new RequestNotSentError(409, 'account_contact_view_changed');
      if (pending(command.actorId).length) throw new AccessFailure(409, 'resolve_previous_attempt');
      const key = accountContactKey(command.actorId, uuid()), saved = entry(journal.remember(command.actorId, accountContactOperation, key), command.actorId);
      if (saved.key !== key) throw new AccessFailure(400, 'recovery_storage_unavailable');
      try {const result = await gateway.save(command, key, isCurrent, signal); clear(saved); return result;}
      catch (error) {if (error instanceof RequestNotSentError || error instanceof AccessFailure && error.status >= 400 && error.status < 500) clear(saved); throw error;}
    },
    async recover(actorId: string, value: OperationReference, isCurrent: () => boolean, signal?: AbortSignal) {
      const saved = entry(value, actorId), result = await gateway.recover(actorId, saved, isCurrent, signal);
      if (result !== null) clear(saved);
      return result;
    },
  };
}
