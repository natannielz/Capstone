import type {Metadata} from 'next';
import {TemporaryDelegation} from '../../../modules/administration/TemporaryDelegation';

export const metadata: Metadata = {title: 'Delegasi sementara | Unit Toko', description: 'Delegasi izin berbatas waktu dan catatan serah-terima.', robots: {index: false, follow: false}};
export default function DelegationPage() {return <TemporaryDelegation />;}
