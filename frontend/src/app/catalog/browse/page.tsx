import { redirect } from 'next/navigation';

/** One buyer catalog: the older browse route forwards to it. */
export default function Page() { redirect('/shop'); }
