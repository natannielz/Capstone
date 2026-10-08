import type {Metadata} from 'next';
import {ReplenishmentWorkspace} from '../../../modules/inventory/ReplenishmentWorkspace';
export const metadata: Metadata = {title: 'Kebutuhan pembelian | Unit Toko', description: 'Periksa kebutuhan pembelian berdasarkan sumber stok dan pasokan yang dapat dipastikan.', robots: {index: false, follow: false}};
export default function ReplenishmentPage() {return <ReplenishmentWorkspace />;}
