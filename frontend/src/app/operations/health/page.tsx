import type {Metadata} from 'next';
import {HealthWorkspace} from '../../../modules/operations/HealthWorkspace';

export const metadata: Metadata = {title: 'Kesehatan sistem | Unit Toko', robots: {index: false, follow: false}};
export default function OperatorHealthPage() {return <HealthWorkspace />;}
