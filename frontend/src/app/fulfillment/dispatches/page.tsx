import type { Metadata } from 'next';
import { ShipmentDispatchWorkspace } from '../../../modules/fulfillment/ShipmentDispatchWorkspace';
export const metadata: Metadata = { title: 'Keberangkatan kiriman | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ShipmentDispatchWorkspace />; }
