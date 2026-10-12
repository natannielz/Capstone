import { AccessFailure } from '../administration/workflow';

export function orderLineRevision(value: unknown): string {
  if (value === undefined || value === '0') return '0';
  if (typeof value === 'string' && /^(?:[2-9]|[1-9][0-9]|10[0-3])$(?![\s\S])/.test(value)) return value;
  throw new AccessFailure(502, 'order_line_identity_invalid');
}
type OrderLine = { skuId: string; lineRevision?: string };
export const orderLineKey = (line: OrderLine): string => `${line.skuId}:${orderLineRevision(line.lineRevision)}`;
export const sameOrderLine = (first: OrderLine, second: OrderLine): boolean => orderLineKey(first) === orderLineKey(second);
