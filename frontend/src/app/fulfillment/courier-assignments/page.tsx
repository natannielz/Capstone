import type {Metadata} from 'next';
import {CourierAssignmentWorkspace} from '../../../modules/fulfillment/CourierAssignmentWorkspace';

export const metadata: Metadata = {title: 'Penugasan kurir | Unit Toko', robots: {index: false, follow: false}};
export default function CourierAssignmentsPage() {return <CourierAssignmentWorkspace />;}
