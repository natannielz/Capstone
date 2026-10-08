import type {Metadata} from 'next';
import {AnomalyWorkspace} from '../../../modules/operations/AnomalyWorkspace';

export const metadata: Metadata = {title: 'Tinjauan operasional | Unit Toko', robots: {index: false, follow: false}};
export default function AnomaliesPage() {return <AnomalyWorkspace />;}
