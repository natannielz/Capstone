import type { Metadata } from 'next';
import { ShipmentPreparationWorkspace } from '../../../modules/fulfillment/ShipmentPreparationWorkspace';
export const metadata: Metadata = { title: 'Persiapan kiriman | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ShipmentPreparationWorkspace />; }
