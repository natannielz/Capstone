import type { Metadata } from 'next';
import { SupplierDocumentWorkspace } from '../../../modules/procurement/SupplierDocumentWorkspace';

export const metadata: Metadata = { title: 'Dokumen pemasok dan pencocokan | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <SupplierDocumentWorkspace />; }
