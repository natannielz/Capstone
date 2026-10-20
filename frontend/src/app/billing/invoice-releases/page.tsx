import type {Metadata} from 'next';
import {InvoiceReleaseWorkspace} from '../../../modules/invoice-releases/InvoiceReleaseWorkspace';

export const metadata: Metadata = {title: 'Persetujuan penerbitan tagihan | Unit Toko', robots: {index: false, follow: false}};
export default function InvoiceReleasesPage() {return <InvoiceReleaseWorkspace />;}
