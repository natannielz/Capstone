import type { Metadata } from 'next';
import { OrganizationAddressWorkspace } from '../../../../modules/organizations/OrganizationAddressWorkspace';

export const metadata: Metadata = {
  title: 'Alamat organisasi | Unit Toko',
  description: 'Pengajuan dan pemeriksaan independen alamat penagihan, pengiriman cabang dan keberangkatan gudang.',
  robots: { index: false, follow: false },
};

export default function OrganizationAddressPage() { return <OrganizationAddressWorkspace />; }
