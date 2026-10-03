import type { Metadata } from 'next';
import { ProcurementDisbursementWorkspace } from '../../../modules/procurement/ProcurementDisbursementWorkspace';

export const metadata: Metadata = { title: 'Bukti uang muka pemasok | Unit Toko', description: 'Pencatatan rincian dan bukti asli uang muka untuk pemeriksaan bank independen.', robots: { index: false, follow: false } };
export default function ProcurementDisbursementPage() { return <ProcurementDisbursementWorkspace />; }
