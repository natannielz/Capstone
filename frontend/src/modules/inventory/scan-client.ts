import {z} from 'zod';
import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {AccessScope} from '../organizations/access-scope';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => /^[1-9][0-9]{0,18}$/.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && !/\p{Cc}/u.test(value) && value.isWellFormed());
const purpose = z.enum(['receiving', 'picking', 'dispatch', 'stocktake']);
const identifier = z.object({identifierId: id, identifierRevision: id, skuId: id, definitionId: id, definitionVersion: id,
  kind: z.enum(['skuCode', 'gtin']), namespace: text(256), canonicalValue: text(128)}).strict();
const approvedSource = z.object({orderId: id, orderVersion: id, inspectionId: id, inspectionRevision: id,
  inspectionLineNumber: z.number().int().min(1).max(100), skuId: id, definitionId: id, definitionVersion: id,
  sourceSystem: z.enum(['supplier', 'omi', 's_mart']), sourceAccount: text(128), sourceCode: text(128)}).strict();
export const operationalScanSelectionSchema = z.object({identifier: identifier.nullable(), source: approvedSource.nullable()}).strict()
  .refine(value => (value.identifier === null) !== (value.source === null));
export type OperationalScanSelection = z.infer<typeof operationalScanSelectionSchema>;
export function validateScanSelections(selections: readonly OperationalScanSelection[] | undefined,
  packages: readonly {skuId: string; definitionId: string; definitionVersion: string}[], allowSource = false): readonly OperationalScanSelection[] | undefined {
  if (!selections?.length) return undefined;
  if (selections.length > 100) throw new RequestNotSentError(400, 'invalid_operational_scan');
  const parsed = selections.map(value => operationalScanSelectionSchema.parse(value));
  const ids = parsed.map(value => {
    const selected = value.identifier ?? value.source;
    if (!selected || !allowSource && value.source !== null || !packages.some(item => item.skuId === selected.skuId &&
      item.definitionId === selected.definitionId && item.definitionVersion === selected.definitionVersion))
      throw new RequestNotSentError(409, 'operational_scan_context_changed');
    return selected.skuId;
  });
  if (new Set(ids).size !== ids.length) throw new RequestNotSentError(400, 'invalid_operational_scan');
  return Object.freeze(parsed.map(value => Object.freeze({identifier: value.identifier === null ? null : Object.freeze(value.identifier),
    source: value.source === null ? null : Object.freeze(value.source)})));
}
const candidate = z.object({targetReference: text(128), skuId: id, definitionId: id, definitionVersion: id, skuCode: text(64),
  packageLabel: text(160), packageKind: z.enum(['loose', 'sealed', 'other']), baseUnitCode: text(64), baseUnitsPerPackage: z.number().int().min(1).max(2147483647),
  selection: operationalScanSelectionSchema, currentPrimaryCode: text(64).nullable()}).strict().refine(value => {
    const selected = value.selection.identifier ?? value.selection.source;
    return selected !== null && selected.skuId === value.skuId && selected.definitionId === value.definitionId && selected.definitionVersion === value.definitionVersion;
  });
const result = z.object({kind: z.enum(['match', 'unknown', 'ambiguous', 'invalid']), reason: text(100), purpose,
  documentId: id.nullable(), documentRevision: id.nullable(), auxiliaryId: id.nullable(), auxiliaryRevision: id.nullable(),
  sourceReview: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/).nullable(), candidates: z.array(candidate).max(100)}).strict().superRefine((value, ctx) => {
  if (value.kind === 'match' ? value.candidates.length !== 1 : value.kind === 'ambiguous' ? value.candidates.length < 2 : value.candidates.length !== 0)
    ctx.addIssue({code: 'custom', message: 'Invalid resolution count'});
  if (new Set(value.candidates.map(item => item.targetReference)).size !== value.candidates.length)
    ctx.addIssue({code: 'custom', message: 'Duplicate resolution target'});
  if (value.candidates.some(item => item.selection.source !== null && (value.purpose !== 'receiving' ||
    item.selection.source.orderId !== value.documentId || item.selection.source.orderVersion !== value.documentRevision ||
    item.selection.source.inspectionId !== value.auxiliaryId || item.selection.source.inspectionRevision !== value.auxiliaryRevision)))
    ctx.addIssue({code: 'custom', message: 'Wrong approved source context'});
});
export type ScanCandidate = z.infer<typeof candidate>;
export type ScanResolution = z.infer<typeof result>;
export type ScanContext = {purpose: z.infer<typeof purpose>; location: AccessScope | null; documentId: string | null; documentRevision: string | null;
  auxiliaryId: string | null; auxiliaryRevision: string | null; sourceReview: string | null};
export type ScanCode = {codeKind: 'sku' | 'supplier' | 'gtin'; rawCode: string; sourceNamespace: 'supplier' | 'omi' | 's_mart' | null; sourceAccount: string | null};
export function parseScanResolution(value: unknown, context: ScanContext): ScanResolution {
  const parsed = result.safeParse(value);
  if (!parsed.success || ['purpose', 'documentId', 'documentRevision', 'auxiliaryId', 'auxiliaryRevision', 'sourceReview'].some(field =>
    parsed.data[field as keyof ScanResolution] !== context[field as keyof ScanContext])) throw new AccessFailure(502, 'operational_scan_invalid_response');
  return parsed.data;
}
export function createScanGateway(fetcher: typeof globalThis.fetch = globalThis.fetch, csrfUrl = '/api/v1/identity/csrf') {
  return {async resolve(actorId: string, context: ScanContext, code: ScanCode, current: () => boolean, signal?: AbortSignal) {
    const guard = () => {if (!current() || signal?.aborted) throw new RequestNotSentError(409, 'operational_scan_context_changed');};
    guard(); if (!id.safeParse(actorId).success || !text(256).safeParse(code.rawCode).success) throw new RequestNotSentError(400, 'invalid_operational_scan');
    const csrf = await fetchCsrfToken(csrfUrl, fetcher, signal); guard();
    const response = await fetcher('/api/v1/catalog/scans/query', {method: 'POST', credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal,
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': csrf, 'X-Expected-Actor-Id': actorId}, body: JSON.stringify({...context, ...code})});
    const value: unknown = await response.json(); guard();
    if (!response.ok) throw new AccessFailure(response.status, value !== null && typeof value === 'object' && 'code' in value && typeof value.code === 'string' ? value.code : 'operational_scan_unavailable');
    return parseScanResolution(value, context);
  }};
}
export function sameScanPackage(a: ScanCandidate, b: {skuId: string; definitionId: string; definitionVersion: string; skuCode: string; packageLabel: string; packageKind: string; baseUnitCode: string; baseUnitsPerPackage: number}) {
  return a.skuId === b.skuId && a.definitionId === b.definitionId && a.definitionVersion === b.definitionVersion && a.skuCode === b.skuCode &&
    a.packageLabel === b.packageLabel && a.packageKind === b.packageKind && a.baseUnitCode === b.baseUnitCode && a.baseUnitsPerPackage === b.baseUnitsPerPackage;
}
