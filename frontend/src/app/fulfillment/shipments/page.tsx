import type { Metadata } from 'next';
import { ShipmentWorkspace } from '../../../modules/fulfillment/ShipmentWorkspace';
export const metadata: Metadata = { title: 'Kiriman | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ShipmentWorkspace />; }
