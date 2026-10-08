import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import {ownerKey, parseCatalogSku, type CatalogSku} from './catalog-workflow';

const idPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const id = z.string().regex(idPattern).refine(value => idPattern.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && !/\p{Cc}/u.test(value));
const time = z.string().datetime({offset: true}).refine(value => value.endsWith('Z') || value.endsWith('+00:00'));
const operation = z.enum(['catalog.identifier.assign', 'catalog.identifier.retire', 'catalog.identifier.correct-sku']);
const key = z.string().regex(/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/);
export type IdentifierOperation = z.infer<typeof operation>;
const invalid = (): never => {throw new AccessFailure(502, 'catalog_identifier_response_invalid');};
const badInput = (): never => {throw new RequestNotSentError(400, 'catalog_identifier_input_invalid');};

/** Validate supplied bytes only. A checksum never allocates or authenticates a GTIN. */
export function canonicalIdentifier(kind: 'skuCode' | 'gtin', raw: string): string {
  if (typeof raw !== 'string') return badInput();
  if (kind === 'skuCode') {
    if (/\p{Cc}/u.test(raw)) return badInput();
    const value = raw.trim(); if (!/^[A-Za-z0-9._-]{1,64}$(?![\s\S])/.test(value)) return badInput();
    return value.toUpperCase();
  }
  if (![8, 12, 13, 14].includes(raw.length) || !/^[0-9]+$(?![\s\S])/.test(raw)) return badInput();
  let sum = 0, weight = 3;
  for (let index = raw.length - 2; index >= 0; index--) {sum += Number(raw[index]) * weight; weight = weight === 3 ? 1 : 3;}
  if ((10 - sum % 10) % 10 !== Number(raw.at(-1))) return badInput();
  return raw.padStart(14, '0');
}
export const identifierNamespace = (sku: CatalogSku, kind: 'skuCode' | 'gtin') => kind === 'skuCode' ? 'internal' :
  `catalog:${sku.owner.shopId}:${sku.owner.channelId}:${sku.owner.warehouseId ?? 'shop'}`;
const identifierSchema = z.object({id, organizationId: id, skuId: id, kind: z.enum(['skuCode', 'gtin', 'supplier', 'omi', 'smart']),
  namespace: text(256), rawValue: z.string().min(1).max(128).refine(value => !/\p{Cc}/u.test(value)), canonicalValue: text(128), isPrimary: z.boolean(), effectiveAt: time,
  retiredAt: time.nullable(), revision: id, provenanceReference: text(500), definitionId: id.nullable()}).strict();
export type CatalogIdentifier = z.infer<typeof identifierSchema>;
export type CatalogIdentifierView = {catalog: CatalogSku; identifiers: CatalogIdentifier[]};
export function parseIdentifierView(value: unknown, expectedSkuId: string, expectedOwner?: string): CatalogIdentifierView {
  const parsed = z.object({catalog: z.unknown(), identifiers: z.array(identifierSchema)}).strict().safeParse(value);
  if (!parsed.success) return invalid();
  const catalog = parseCatalogSku(parsed.data.catalog), identifiers = parsed.data.identifiers;
  if (catalog.id !== expectedSkuId || expectedOwner && ownerKey(catalog.owner) !== expectedOwner || new Set(identifiers.map(row => row.id)).size !== identifiers.length) return invalid();
  for (const row of identifiers) {
    if (row.skuId !== catalog.id || row.organizationId !== catalog.organizationId || row.retiredAt !== null && Date.parse(row.retiredAt) < Date.parse(row.effectiveAt) ||
      row.isPrimary && (row.kind !== 'skuCode' || row.retiredAt !== null) || row.kind !== 'skuCode' && row.definitionId === null) return invalid();
    if (row.kind === 'skuCode' || row.kind === 'gtin') {
      try {if (canonicalIdentifier(row.kind, row.rawValue) !== row.canonicalValue || identifierNamespace(catalog, row.kind) !== row.namespace) return invalid();}
      catch {return invalid();}
    } else if (row.rawValue !== row.canonicalValue || !row.namespace.startsWith(identifierNamespace(catalog, 'gtin') + ':account:')) return invalid();
  }
  const primary = identifiers.filter(row => row.isPrimary);
  if (primary.length !== 1 || identifiers.filter(row => row.kind === 'skuCode' && row.canonicalValue === catalog.code).length !== 1) return invalid();
  return {catalog, identifiers};
}
const resultSchema = z.object({skuId: id, skuRevision: id, identifierId: id, identifierRevision: id, recordedAt: time}).strict();
export type IdentifierResult = z.infer<typeof resultSchema>;
const referenceSchema = z.object({actorId: id, operation, key, skuId: id, skuRevision: id, identifierId: id.nullable(), identifierRevision: id.nullable(), createdAt: time}).strict()
  .refine(value => value.operation === 'catalog.identifier.assign' ? value.identifierId === null && value.identifierRevision === null : value.identifierId !== null && value.identifierRevision !== null);
export type IdentifierReference = z.infer<typeof referenceSchema>;
export function parseIdentifierReference(value: unknown, actorId: string): IdentifierReference {
  const parsed = referenceSchema.safeParse(value); if (!parsed.success || parsed.data.actorId !== actorId) return invalid(); return parsed.data;
}
export function parseIdentifierResult(value: unknown, original: IdentifierReference): IdentifierResult {
  const parsed = resultSchema.safeParse(value); if (!parsed.success) return invalid(); const result = parsed.data;
  if (result.skuId !== original.skuId || BigInt(result.skuRevision) !== BigInt(original.skuRevision) + BigInt(1) ||
    (original.operation === 'catalog.identifier.retire' ? result.identifierId !== original.identifierId || BigInt(result.identifierRevision) !== BigInt(original.identifierRevision!) + BigInt(1) :
      result.identifierRevision !== '1' || result.identifierId === original.identifierId)) return invalid();
  return result;
}
type CommandBase = {skuId: string; skuRevision: string; reason: string; provenanceReference: string};
export type IdentifierCommand = CommandBase & (
  {operation: 'catalog.identifier.assign'; kind: 'skuCode' | 'gtin'; rawValue: string; canonicalValue: string; namespace: string; definitionId: string} |
  {operation: 'catalog.identifier.retire'; identifierId: string; identifierRevision: string} |
  {operation: 'catalog.identifier.correct-sku'; identifierId: string; identifierRevision: string; newCode: string; canonicalValue: string});
export function identifierCommand(view: CatalogIdentifierView, action: 'assign' | 'retire' | 'correct', fields: {
  kind?: 'skuCode' | 'gtin'; rawValue?: string; identifierId?: string; provenanceReference: string; reason: string;
}): IdentifierCommand {
  const sku = view.catalog;
  if (!sku.active || !text(500).safeParse(fields.provenanceReference).success || !text(1000).safeParse(fields.reason).success) return badInput();
  const base = {skuId: sku.id, skuRevision: sku.version, reason: fields.reason, provenanceReference: fields.provenanceReference};
  if (action === 'assign') {
    if (!fields.kind || typeof fields.rawValue !== 'string') return badInput();
    return {...base, operation: 'catalog.identifier.assign', kind: fields.kind, rawValue: fields.rawValue,
      canonicalValue: canonicalIdentifier(fields.kind, fields.rawValue), namespace: identifierNamespace(sku, fields.kind), definitionId: sku.currentDefinitionId};
  }
  const selected = view.identifiers.find(row => row.id === fields.identifierId);
  if (!selected || selected.retiredAt !== null || (action === 'retire' ? selected.isPrimary : !selected.isPrimary)) return badInput();
  const target = {...base, identifierId: selected.id, identifierRevision: selected.revision};
  if (action === 'retire') return {...target, operation: 'catalog.identifier.retire'};
  if (typeof fields.rawValue !== 'string' || canonicalIdentifier('skuCode', fields.rawValue) === sku.code) return badInput();
  return {...target, operation: 'catalog.identifier.correct-sku', newCode: fields.rawValue, canonicalValue: canonicalIdentifier('skuCode', fields.rawValue)};
}
export function identifierReference(actorId: string, command: IdentifierCommand, originalKey: string, createdAt: string): IdentifierReference {
  return parseIdentifierReference({actorId, operation: command.operation, key: originalKey, createdAt, skuId: command.skuId, skuRevision: command.skuRevision,
    identifierId: 'identifierId' in command ? command.identifierId : null, identifierRevision: 'identifierRevision' in command ? command.identifierRevision : null}, actorId);
}
type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
export const identifierJournalPrefix = (actorId: string) => 'unit-toko.catalog-identifier.pending.' + actorId + '.';
const storageKey = (entry: IdentifierReference) => identifierJournalPrefix(entry.actorId) + entry.operation + '.' + entry.key;
export class IdentifierJournal {
  constructor(private readonly storage: () => StoragePort) {}
  list(actorId: string): IdentifierReference[] {
    if (!id.safeParse(actorId).success) return invalid();
    const storage = this.storage();
    return Array.from({length: storage.length}, (_, index) => storage.key(index)).filter((value): value is string => value !== null && value.startsWith(identifierJournalPrefix(actorId)))
      .map(key => {const ref = parseIdentifierReference(JSON.parse(storage.getItem(key) ?? 'null'), actorId); if (storageKey(ref) !== key) return invalid(); return ref;})
      .sort((left, right) => left.createdAt.localeCompare(right.createdAt) || left.key.localeCompare(right.key));
  }
  remember(value: IdentifierReference): void {
    const ref = parseIdentifierReference(value, value.actorId), storage = this.storage(), key = storageKey(ref), bytes = JSON.stringify(ref), existing = storage.getItem(key);
    if (existing !== null && JSON.stringify(parseIdentifierReference(JSON.parse(existing), ref.actorId)) !== bytes) return invalid();
    storage.setItem(key, bytes); if (storage.getItem(key) !== bytes) return invalid();
  }
  confirmed(value: IdentifierReference): void {
    const ref = parseIdentifierReference(value, value.actorId), storage = this.storage(), key = storageKey(ref), existing = storage.getItem(key);
    if (existing !== null && JSON.stringify(parseIdentifierReference(JSON.parse(existing), ref.actorId)) !== JSON.stringify(ref)) return invalid();
    storage.removeItem(key); if (storage.getItem(key) !== null) return invalid();
  }
}
export type IdentifierContext = {actorId: string; isCurrent: () => boolean; signal?: AbortSignal};
export function createCatalogIdentifierGateway(fetcher: typeof globalThis.fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  async function request(path: string, body: unknown, context: IdentifierContext, command?: IdentifierReference): Promise<unknown> {
    const current = (sent = false) => {if (!context.isCurrent() || context.signal?.aborted) {
      if (sent && command) throw new AccessFailure(503, 'catalog_identifier_outcome_unknown');
      throw new RequestNotSentError(409, 'catalog_identifier_view_stale');
    }};
    current(); if (!id.safeParse(context.actorId).success) return badInput();
    const csrf = await fetchCsrfToken(csrfUrl, fetcher, context.signal); current();
    const headers = {'Accept': 'application/json', 'Content-Type': 'application/json', 'X-Expected-Actor-Id': context.actorId, 'X-CSRF-Token': csrf,
      ...(command ? {'Idempotency-Key': command.key, 'If-Match': '"' + command.skuRevision + '"'} : {})};
    const response = await fetcher('/api/v1/catalog/administration' + path, {method: 'POST', headers, body: JSON.stringify(body),
      credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal: context.signal});
    const data: unknown = await response.json(); current(true);
    if (!response.ok) {const error = data as {code?: unknown}; throw new AccessFailure(response.status, typeof error?.code === 'string' ? error.code : 'catalog_identifier_unavailable');}
    return data;
  }
  return {
    async read(skuId: string, context: IdentifierContext, expectedOwner?: string) {
      if (!id.safeParse(skuId).success) return badInput();
      return parseIdentifierView(await request('/skus/' + skuId + '/identifiers/query', {purpose: 'catalog-authoring'}, context), skuId, expectedOwner);
    },
    async submit(command: IdentifierCommand, original: IdentifierReference, context: IdentifierContext) {
      const ref = parseIdentifierReference(original, context.actorId);
      if (JSON.stringify(identifierReference(context.actorId, command, ref.key, ref.createdAt)) !== JSON.stringify(ref)) return badInput();
      const path = '/skus/' + command.skuId, common = {provenanceReference: command.provenanceReference, reason: command.reason};
      const suffix = command.operation === 'catalog.identifier.assign' ? '/identifiers' : command.operation === 'catalog.identifier.retire' ? '/identifiers/' + command.identifierId + '/retire' : '/code';
      const body = command.operation === 'catalog.identifier.assign' ? {...common, kind: command.kind, sourceAccount: null, rawValue: command.rawValue, definitionId: command.definitionId} :
        command.operation === 'catalog.identifier.retire' ? {...common, expectedIdentifierRevision: command.identifierRevision} :
          {...common, primaryIdentifierId: command.identifierId, expectedIdentifierRevision: command.identifierRevision, newCode: command.newCode};
      return parseIdentifierResult(await request(path + suffix, body, context, ref), ref);
    },
    async recover(original: IdentifierReference, context: IdentifierContext) {
      const ref = parseIdentifierReference(original, context.actorId), parsed = z.object({status: z.enum(['confirmed', 'not_confirmed']), operation,
        result: z.unknown()}).strict().safeParse(await request('/identifiers/operations/query', {purpose: 'operation-recovery', operation: ref.operation, key: ref.key}, context));
      if (!parsed.success || parsed.data.operation !== ref.operation || parsed.data.status === 'not_confirmed' && parsed.data.result !== null) return invalid();
      return parsed.data.status === 'confirmed' ? {status: 'confirmed' as const, result: parseIdentifierResult(parsed.data.result, ref)} : {status: 'not_confirmed' as const, result: null};
    },
  };
}
export type CatalogIdentifierGateway = ReturnType<typeof createCatalogIdentifierGateway>;
